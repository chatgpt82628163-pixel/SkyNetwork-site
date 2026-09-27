// Live traffic map, radar style: aircraft with their flight plans and routes, controllers with their sectors, airports.
(function () {
  const el = document.getElementById('map');
  if (!el || !window.L) return;
  const compact = el.dataset.compact === '1';
  const root = document.documentElement;
  // The site theme (theme.js): the header switch, otherwise the system setting.
  const theme = () => window.skyTheme ? window.skyTheme() : root.dataset.theme === 'dark' ? 'dark' : 'light';
  // Texts in the visitor's language, from the page (see MapTexts); English when missing.
  const texts = (() => { try { return JSON.parse(el.dataset.text || '{}'); } catch { return {}; } })();
  const t = (key, ...args) => (texts[key] ?? key).replace(/\{(\d)\}/g, (_, i) => args[i]);
  const css = name => getComputedStyle(root).getPropertyValue(name).trim();

  const map = L.map(el, { zoomControl: false, worldCopyJump: true, scrollWheelZoom: !compact })
    .setView([55.75, 37.6], compact ? 4 : 5);
  if (!compact) L.control.zoom({ position: 'bottomright' }).addTo(map);
  // Leaflet's default prefix carries a flag; just the name here.
  map.attributionControl.setPrefix('<a href="https://leafletjs.com">Leaflet</a>');

  // Tiles come through the site (see TileProxy); the base map follows the site theme, labels sit above the sectors.
  map.createPane('labels').classList.add('labels-pane');
  map.getPane('labels').style.zIndex = 450;
  const tileOptions = { maxZoom: 18, maxNativeZoom: 16 };
  const base = L.tileLayer(`/tiles/${theme()}/{z}/{x}/{y}.png`, {
    ...tileOptions, attribution: '&copy; Esri, HERE, Garmin, &copy; OpenStreetMap'
  }).addTo(map);
  // Place names stop at 15: closer in they would only be blown up and blurred over the airport diagram.
  const labels = L.tileLayer(`/tiles/${theme()}-labels/{z}/{x}/{y}.png`, { ...tileOptions, maxZoom: 15, pane: 'labels' }).addTo(map);

  const firOutline = L.layerGroup();              // every sector border (switchable)
  const sectors = L.layerGroup().addTo(map);      // staffed sectors and approach areas
  const traffic = L.layerGroup().addTo(map);      // controller labels, airport badges
  const planeLayer = L.layerGroup().addTo(map);   // aircraft: kept between refreshes, so they glide instead of jumping
  const routeLayer = L.layerGroup().addTo(map);   // the selected flight
  const calm = matchMedia('(prefers-reduced-motion: reduce)');
  const coarse = matchMedia('(pointer: coarse)');   // touch device: no hover popover

  const card = document.getElementById('map-card');
  const search = document.getElementById('map-search');
  const firToggle = document.getElementById('fir-toggle');
  let data = null, selected = null, fitted = false;
  let pendingHash = compact ? '' : decodeURIComponent(location.hash.slice(1));
  let route = null;                 // the selected aircraft's route points, extras and flown track
  let follow = false;               // keep the selected aircraft centred
  const sections = { graph: false, plan: true };   // which card sections are open (kept across refreshes)

  // ---- helpers ----
  const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const prefix = cs => String(cs).split('_')[0].toUpperCase();
  const pad = (n, w) => String(n).padStart(w, '0');
  const hm = min => !(min > 0) ? '—' : min < 60 ? t('{0} min', min) : t('{0} h {1} min', Math.floor(min / 60), pad(min % 60, 2));
  const utc = d => d.toISOString().slice(11, 16) + 'z';
  const onlineFor = iso => hm(Math.max(1, Math.round((Date.now() - new Date(iso)) / 60000)));
  const feet = ft => `${Math.round(ft).toLocaleString(root.lang || 'en')} ft`;
  const hhmm = s => /^\d{4}$/.test(s || '') && s !== '0000' ? `${s.slice(0, 2)}:${s.slice(2)}z` : '—';
  const RAD = Math.PI / 180;
  const isAirway = s => /^[A-Z]{1,2}\d{1,4}[A-Z]?$/.test(s);

  function distNm(a, b) {
    const dLat = (b[0] - a[0]) * RAD, dLon = (b[1] - a[1]) * RAD;
    const h = Math.sin(dLat / 2) ** 2 + Math.cos(a[0] * RAD) * Math.cos(b[0] * RAD) * Math.sin(dLon / 2) ** 2;
    return 2 * 3440.065 * Math.asin(Math.sqrt(Math.min(1, h)));
  }

  function bearing(a, b) {
    const p1 = a[0] * RAD, p2 = b[0] * RAD, dl = (b[1] - a[1]) * RAD;
    return (Math.atan2(Math.sin(dl) * Math.cos(p2), Math.cos(p1) * Math.sin(p2) - Math.sin(p1) * Math.cos(p2) * Math.cos(dl)) / RAD + 360) % 360;
  }

  // Great-circle arc, the way a long flight actually goes; longitudes unwrapped so the line never jumps across the map.
  function arc(a, b) {
    const [p1, l1, p2, l2] = [a[0] * RAD, a[1] * RAD, b[0] * RAD, b[1] * RAD];
    const d = 2 * Math.asin(Math.sqrt(Math.sin((p2 - p1) / 2) ** 2 + Math.cos(p1) * Math.cos(p2) * Math.sin((l2 - l1) / 2) ** 2));
    if (d < 0.01) return [a, b];
    const n = Math.ceil(d / 0.02), points = [];
    let prev = null;
    for (let i = 0; i <= n; i++) {
      const A = Math.sin((1 - i / n) * d) / Math.sin(d), B = Math.sin(i / n * d) / Math.sin(d);
      const x = A * Math.cos(p1) * Math.cos(l1) + B * Math.cos(p2) * Math.cos(l2);
      const y = A * Math.cos(p1) * Math.sin(l1) + B * Math.cos(p2) * Math.sin(l2);
      const z = A * Math.sin(p1) + B * Math.sin(p2);
      let lon = Math.atan2(y, x) / RAD;
      if (prev !== null) { while (lon - prev > 180) lon -= 360; while (lon - prev < -180) lon += 360; }
      prev = lon;
      points.push([Math.atan2(z, Math.hypot(x, y)) / RAD, lon]);
    }
    return points;
  }

  // Great-circle arcs through all points, kept continuous across the date line.
  function path(points) {
    const out = [];
    for (let i = 1; i < points.length; i++) {
      let seg = arc(points[i - 1], points[i]);
      if (out.length) {
        const shift = Math.round((out[out.length - 1][1] - seg[0][1]) / 360) * 360;
        seg = seg.slice(1).map(([la, lo]) => [la, lo + shift]);
      }
      out.push(...seg);
    }
    return out.length ? out : points;
  }

  // The flown track is a fix every 5 s. On the ground those are 30–50 m apart, so straight legs cut the
  // corners of taxiways; a centripetal Catmull-Rom curve through the fixes follows them instead (no loops or
  // overshoot, unlike the uniform spline). Long legs (cruise) stay straight.
  function flat(a, b) { return Math.hypot(b[0] - a[0], (b[1] - a[1]) * Math.cos(a[0] * Math.PI / 180)); }
  function catmull(p0, p1, p2, p3, t0, t1, t2, t3, t) {
    const lerp = (a, b, ta, tb) => tb - ta < 1e-12 ? a : [((tb - t) * a[0] + (t - ta) * b[0]) / (tb - ta), ((tb - t) * a[1] + (t - ta) * b[1]) / (tb - ta)];
    const a1 = lerp(p0, p1, t0, t1), a2 = lerp(p1, p2, t1, t2), a3 = lerp(p2, p3, t2, t3);
    const b1 = lerp(a1, a2, t0, t2), b2 = lerp(a2, a3, t1, t3);
    return lerp(b1, b2, t1, t2);
  }
  function smooth(pts) {
    if (pts.length < 3) return pts;
    const out = [pts[0]];
    for (let i = 0; i < pts.length - 1; i++) {
      const p0 = pts[Math.max(0, i - 1)], p1 = pts[i], p2 = pts[i + 1], p3 = pts[Math.min(pts.length - 1, i + 2)];
      const leg = distNm(p1, p2);
      if (leg > 3 || leg < 0.003) { out.push(p2); continue; }
      const t1 = Math.sqrt(flat(p0, p1)), t2 = t1 + Math.sqrt(flat(p1, p2)), t3 = t2 + Math.sqrt(flat(p2, p3));
      const n = leg > 0.5 ? 3 : 6;
      for (let k = 1; k < n; k++) out.push(catmull(p0, p1, p2, p3, 0, t1, t2, t3, t1 + (t2 - t1) * k / n));
      out.push(p2);
    }
    return out;
  }

  // Index of the route point the aircraft is flying to: the leg it is closest to lying on.
  function nextPoint(points, at) {
    if (!at) return 1;
    let best = 1, cost = Infinity;
    for (let i = 1; i < points.length; i++) {
      const c = distNm(points[i - 1], at) + distNm(at, points[i]) - distNm(points[i - 1], points[i]);
      if (c < cost) { cost = c; best = i; }
    }
    return best;
  }

  const label = (html, onClick) => {
    const m = L.marker([0, 0], { icon: L.divIcon({ className: '', iconSize: null, html }), riseOnHover: true });
    if (onClick) m.on('click', onClick);
    return m;
  };
  const open = (kind, key) => () => compact ? location.href = '/map#' + encodeURIComponent(key) : select(kind, key, false);

  // Always the aircraft symbol (pointing north when the heading is unknown) with the callsign underneath.
  const planeClass = (p, isSelected) =>
    'plane' + ((p.onGround ?? p.groundspeed < 40) ? ' ground' : '') + (isSelected ? ' selected' : '') + (p.heading == null ? ' nohdg' : '');
  const plane = (p, isSelected, turn, born) => L.divIcon({
    className: planeClass(p, isSelected) + (born ? ' born' : ''),
    iconSize: [22, 22], iconAnchor: [11, 11],
    html: `<span class="cs">${esc(p.callsign)}</span><svg width="22" height="22" viewBox="0 0 24 24" style="transform:rotate(${turn}deg)"><path fill="currentColor" d="M12 2c.8 0 1.3.7 1.3 1.6v5.6l7.7 4.6v2l-7.7-2.3v4.4l2.2 1.7v1.6L12 20.2l-3.5 1v-1.6l2.2-1.7v-4.4L3 15.8v-2l7.7-4.6V3.6C10.7 2.7 11.2 2 12 2z"/></svg>`
  });

  // ---- aircraft motion ----
  // Positions come every 5 s. Each aircraft glides from where it is on screen now to the new position over the
  // same 5 s, so it moves steadily instead of jumping (a new glide starts from the on-screen position, never from
  // the old target). A jump of more than 20 nm (a reconnect or a slew) is not animated; neither is anything when
  // the visitor asked for less motion.
  const GLIDE_MS = 5000;
  const planes = new Map();        // callsign → { marker, look, turn, tip }
  const glides = new Map();        // marker → { from, to, start }
  let glideFrame = 0, flying = false;
  function glide(marker, to) {
    const cur = marker.getLatLng();
    let lng = to[1];
    while (lng - cur.lng > 180) lng -= 360;
    while (lng - cur.lng < -180) lng += 360;
    const g = glides.get(marker);
    if (g && g.to[0] === to[0] && g.to[1] === lng) return;           // already on its way there
    if (cur.lat === to[0] && cur.lng === lng) return;
    if (calm.matches || distNm([cur.lat, cur.lng], [to[0], lng]) > 20) {
      glides.delete(marker);
      marker.setLatLng([to[0], lng]);
      return;
    }
    glides.set(marker, { from: [cur.lat, cur.lng], to: [to[0], lng], start: performance.now() });
    if (!glideFrame) glideFrame = requestAnimationFrame(step);
  }
  function step(now) {
    glideFrame = 0;
    for (const [m, g] of glides) {
      const k = Math.min(1, (now - g.start) / GLIDE_MS);
      m.setLatLng([g.from[0] + (g.to[0] - g.from[0]) * k, g.from[1] + (g.to[1] - g.from[1]) * k]);
      if (k >= 1) glides.delete(m);
    }
    // Following: the map moves with the aircraft on screen (not while it flies to it).
    if (follow && !flying && selected?.kind === 'pilot') {
      const m = planes.get(selected.key)?.marker;
      if (m && glides.has(m)) map.panTo(m.getLatLng(), { animate: false });
    }
    if (glides.size) glideFrame = requestAnimationFrame(step);
  }
  // The shortest turn to the new heading (350° → 10° turns 20° right, not 340° left).
  const turnTo = (from, heading) => from + ((((heading - from) % 360) + 540) % 360 - 180);

  // ---- reference data ----
  // Sector borders (see data/firs.LICENSE.txt): features by id, callsign prefixes → sector, upper sectors → several FIRs.
  let firs = { features: [], prefixes: {}, uirs: {} }, firById = new Map();
  fetch('/data/firs.json').then(r => r.json()).then(d => {
    firs = d;
    firById = new Map(d.features.map(f => [f.properties.id, f]));
    drawOutline();
    render();
  }).catch(() => { });

  // Sector of a CTR/FSS position, the longest callsign prefix first: UUWV_N_CTR → UUWV-N, UUWV_CTR → UUWV, RU-WRC_FSS → UMMV+UUWV+UWWW.
  // A part of a sector with no border of its own (EYVL_S_CTR when only EYVL is known) is not the whole sector:
  // null, so it is drawn around the controller's position. Digits are not a part (UUWV_1_CTR is UUWV).
  function sectorOf(c) {
    const parts = String(c.callsign).toUpperCase().split('_').slice(0, -1).filter(p => !/^\d+$/.test(p));
    for (let n = parts.length; n > 0; n--) {
      if (n < parts.length && c.latitude != null) return null;
      const key = parts.slice(0, n).join('_');
      const fir = firs.prefixes[key], f = fir && firById.get(fir.b);
      if (f) return { id: fir.b, name: fir.n, features: [f], label: f.properties.lat != null ? [f.properties.lat, f.properties.lon] : null };
      const uir = firs.uirs[key], list = uir ? uir.b.map(id => firById.get(id)).filter(Boolean) : [];
      if (list.length) return { id: key, name: uir.n, features: list, label: null };
    }
    return null;
  }

  // Canvas: hundreds of borders draw much faster there than as SVG.
  const outlineRenderer = L.canvas({ padding: .5 });
  function drawOutline() {
    firOutline.clearLayers();
    L.geoJSON(firs.features.filter(f => f.properties.top), {
      interactive: false, renderer: outlineRenderer, style: { color: css('--map-fir'), weight: 1, fill: false }
    }).addTo(firOutline);
  }
  // Layer switches in the panel (the home page map follows the same choices), remembered in this browser.
  const layerOn = {};
  function layerToggle(id, key, apply) {
    const box = document.getElementById(id);
    let on = true;
    try { on = localStorage.getItem('map-' + key) !== '0'; } catch { }
    if (box) box.checked = on;
    layerOn[key] = on;
    apply(on);
    box?.addEventListener('change', () => {
      layerOn[key] = box.checked;
      try { localStorage.setItem('map-' + key, box.checked ? '1' : '0'); } catch { }
      apply(box.checked);
    });
  }
  layerToggle('fir-toggle', 'firs', on => on ? firOutline.addTo(map) : firOutline.remove());
  layerToggle('layer-labels', 'labels', on => on ? labels.addTo(map) : labels.remove());
  document.addEventListener('click', e => {
    document.querySelectorAll('details.layers-menu[open]').forEach(d => { if (!d.contains(e.target)) d.open = false; });
  });

  // Airport coordinates and names (OurAirports), airline names by ICAO code (OpenFlights): loaded when first needed.
  let airports = null, airportsLoad = null, airlines = null, airlinesLoad = null;
  const loadAirports = () => airportsLoad ??= fetch('/data/airports.json').then(r => r.json()).then(a => airports = a).catch(() => airports = {});
  const loadAirlines = () => airlinesLoad ??= fetch('/data/airlines.json').then(r => r.json()).then(a => airlines = a).catch(() => airlines = {});
  const airport = code => airports?.[String(code || '').toUpperCase()] ?? null;
  // Just the position: Leaflet takes a [lat, lon] pair, not the whole airport row.
  const aptLL = code => { const a = airport(code); return a ? [a[0], a[1]] : null; };
  const airlineOf = cs => { const m = /^([A-Z]{3})\d/.exec(String(cs).toUpperCase()); return m ? airlines?.[m[1]] ?? null : null; };

  // The selected aircraft's route points and flown track, refreshed with the live data.
  async function loadRoute(cs) {
    try {
      const r = await fetch(`/api/v1/pilots/${encodeURIComponent(cs)}/route`, { cache: 'no-store' });
      if (!r.ok) return;
      const d = await r.json();
      if (selected?.kind === 'pilot' && selected.key === cs) { route = { callsign: cs, ...d }; updateCard(); }
    } catch { }
  }

  // METAR for airport cards, at most one request per airport every 5 minutes.
  const metars = new Map();
  function metarOf(code) {
    const m = metars.get(code);
    if (!m || Date.now() - m.at > 300000) {
      metars.set(code, { at: Date.now(), text: m?.text ?? null });
      fetch(`/api/v1/metar/${encodeURIComponent(code)}`).then(r => r.ok ? r.json() : null)
        .then(d => { metars.set(code, { at: Date.now(), text: d?.metar ?? '' }); if (selected?.key === code) updateCard(); })
        .catch(() => { });
    }
    return metars.get(code).text;
  }

  // ---- airport diagrams (OpenStreetMap) when zoomed in, like a ground radar ----
  const LAYOUT_ZOOM = 12;
  map.createPane('layout').style.zIndex = 350;
  map.createPane('layoutLabels').style.zIndex = 460;
  map.getPane('layoutLabels').style.pointerEvents = 'none';
  const layoutRenderer = L.canvas({ pane: 'layout', padding: .5 });
  const layoutLayer = L.layerGroup().addTo(map);
  const layouts = new Map();   // ICAO → diagram, or 'loading'

  async function loadLayouts() {
    if (compact || map.getZoom() < LAYOUT_ZOOM || !layerOn.layouts) return drawLayouts();
    await loadAirports();
    const view = map.getBounds().pad(.2), c = map.getCenter();
    const near = Object.entries(airports).filter(([, a]) => view.contains([a[0], a[1]]))
      .sort((x, y) => map.distance(c, [x[1][0], x[1][1]]) - map.distance(c, [y[1][0], y[1][1]])).slice(0, 4);
    for (const [code] of near) {
      if (layouts.has(code)) continue;
      layouts.set(code, 'loading');
      fetch(`/api/v1/airports/${code}/layout`).then(r => { if (!r.ok) throw r; return r.json(); })
        .then(d => { layouts.set(code, d); drawLayouts(); })
        .catch(() => setTimeout(() => layouts.delete(code), 60000));   // try again in a minute
    }
    drawLayouts();
  }

  function drawLayouts() {
    layoutLayer.clearLayers();
    const z = map.getZoom();
    if (compact || z < LAYOUT_ZOOM || !layerOn.layouts) return;
    const near = map.getBounds().pad(.5), view = map.getBounds().pad(.1);
    const col = { apron: css('--apt-apron'), building: css('--apt-building'), runway: css('--apt-runway'), taxiway: css('--apt-taxiway'),
      stand: css('--apt-label'), gate: css('--accent') };
    const tag = (at, text, kind) => L.marker(at, { pane: 'layoutLabels', interactive: false, keyboard: false,
      icon: L.divIcon({ className: '', iconSize: null, html: `<span class="apt-lbl ${kind}">${esc(text)}</span>` }) }).addTo(layoutLayer);
    for (const [code, d] of layouts) {
      const home = airport(code);
      if (!d || d === 'loading' || !home || !near.contains([home[0], home[1]])) continue;
      // Real widths: metres → pixels at this zoom.
      const mpp = 40075016.686 * Math.cos(home[0] * RAD) / (256 * 2 ** z);
      const px = m => Math.max(1, m / mpp);
      const shape = { renderer: layoutRenderer, interactive: false };
      for (const a of d.areas)
        L.polygon(a.ring, { ...shape, stroke: false, fillColor: a.kind === 'apron' ? col.apron : col.building, fillOpacity: 1 }).addTo(layoutLayer);
      for (const tw of d.taxiways)
        L.polyline(tw.line, { ...shape, color: col.taxiway, weight: px(tw.width), lineCap: 'round', lineJoin: 'round' }).addTo(layoutLayer);
      for (const r of d.runways)
        L.polyline(r.line, { ...shape, color: col.runway, weight: px(r.width), lineCap: 'butt' }).addTo(layoutLayer);

      // A runway number sits at the end aircraft roll from on that heading (06 at the south-west end of 06/24);
      // OpenStreetMap draws runways in either direction, so the order of the ends is checked against the heading.
      for (const r of d.runways) {
        let [one, two] = String(r.ref).split('/').map(x => x.trim());
        const start = r.line[0], end = r.line[r.line.length - 1];
        const heading = parseInt(one, 10) * 10, course = bearing(start, end);
        if (!isNaN(heading) && Math.abs(((course - heading + 540) % 360) - 180) > 90) [one, two] = [two, one];
        if (one) tag(start, one, 'rwy');
        if (two) tag(end, two, 'rwy');
      }
      if (z >= 14) {
        // One name per taxiway piece, not repeated within 300 m.
        const placed = new Map();
        for (const tw of d.taxiways) {
          const name = String(tw.ref).replace(/^TWY\s*/i, '');
          if (!name || tw.lane) continue;
          const mid = tw.line[Math.floor(tw.line.length / 2)];
          if (!view.contains(mid) || (placed.get(name) ?? []).some(p => map.distance(p, mid) < 300)) continue;
          placed.set(name, [...(placed.get(name) ?? []), mid]);
          tag(mid, name, 'twy');
        }
      }
      if (z >= 15)
        for (const st of d.stands) {
          if (!view.contains(st.at)) continue;
          L.circleMarker(st.at, { ...shape, radius: z >= 16 ? 3 : 2, stroke: false, fillColor: st.gate ? col.gate : col.stand, fillOpacity: .9 }).addTo(layoutLayer);
          if (z >= 16 && st.ref) tag(st.at, st.ref, st.gate ? 'gate' : 'stand');
        }
    }
  }
  map.on('moveend', loadLayouts);
  layerToggle('layer-layouts', 'layouts', () => loadLayouts());

  // ---- airport codes (ICAO): big airports from zoom 5, medium from 7, small from 9 ----
  map.createPane('airportCodes').style.zIndex = 455;
  const codesLayer = L.layerGroup().addTo(map);
  let staffedAirports = new Set();   // these already have a controller badge
  function drawCodes() {
    codesLayer.clearLayers();
    if (compact || !airports || !layerOn.codes) return;
    const z = map.getZoom();
    const maxRank = z >= 9 ? 2 : z >= 7 ? 1 : z >= 5 ? 0 : -1;
    if (maxRank < 0) return;
    const view = map.getBounds().pad(.1);
    // Bigger airports first; a code that would overlap one already placed is left out, as on a radar screen.
    const candidates = Object.entries(airports)
      .filter(([code, a]) => (a[3] ?? 2) <= maxRank && !staffedAirports.has(code) && view.contains([a[0], a[1]]))
      .sort((x, y) => (x[1][3] ?? 2) - (y[1][3] ?? 2));
    const placed = [];
    for (const [code, a] of candidates) {
      const p = map.latLngToContainerPoint([a[0], a[1]]);
      if (placed.some(q => Math.abs(q.x - p.x) < 46 && Math.abs(q.y - p.y) < 18)) continue;
      placed.push(p);
      if (placed.length > 300) break;
      L.marker([a[0], a[1]], { pane: 'airportCodes', keyboard: false,
        icon: L.divIcon({ className: '', iconSize: null, html: `<span class="apt-code r${a[3] ?? 2}" title="${esc(a[2])}">${code}</span>` }) })
        .on('click', () => select('airport', code, false)).addTo(codesLayer);
    }
  }
  map.on('moveend', drawCodes);
  // Airport positions place approach circles and tower badges: drawn again once known.
  loadAirports().then(() => { if (!compact) drawCodes(); render(); });
  layerToggle('layer-codes', 'codes', on => { if (on) { codesLayer.addTo(map); drawCodes(); } else codesLayer.remove(); });

  // ---- hover popover ----
  // A small card anchored to airport badges, sector labels, and approach circles.
  // Only shown on pointer devices; touch users tap to open the full card as before.
  const popEl = (() => {
    const d = document.createElement('div');
    d.className = 'map-pop';
    d.hidden = true;
    (el.closest('.map-page') ?? el.parentElement).appendChild(d);
    return d;
  })();

  let hov = null;        // { kind: 'apt'|'atc'|'app', key, at:[lat,lon] } or null
  let hovTimer = 0;

  function showPop(kind, key, at) {
    clearTimeout(hovTimer);
    const content = buildPop(kind, key);
    if (!content) return;
    const same = hov && hov.kind === kind && hov.key === key;
    hov = { kind, key, at };
    popEl.innerHTML = content;
    placePop();
    if (!same) {
      // Re-trigger the grow-in animation for a new element.
      popEl.hidden = true;
      void popEl.offsetHeight;  // flush
    }
    popEl.hidden = false;
  }

  function hidePop() {
    hovTimer = setTimeout(() => {
      hov = null;
      popEl.hidden = true;
    }, 100);
  }

  function keepPop() { clearTimeout(hovTimer); }

  function placePop() {
    if (!hov) return;
    const pt = map.latLngToContainerPoint(hov.at);
    const above = pt.y > 160;
    popEl.style.left = Math.round(pt.x) + 'px';
    popEl.style.top = Math.round(above ? pt.y - 6 : pt.y + 6) + 'px';
    popEl.style.transform = above ? 'translate(-50%,-100%)' : 'translate(-50%,0)';
  }

  function updatePop() {
    if (!hov || popEl.hidden) return;
    const content = buildPop(hov.kind, hov.key);
    if (!content) { hov = null; popEl.hidden = true; return; }
    popEl.innerHTML = content;
    placePop();
  }

  popEl.addEventListener('mouseenter', keepPop);
  popEl.addEventListener('mouseleave', hidePop);
  map.on('move', placePop);

  function buildPop(kind, key) {
    if (!data) return '';
    if (kind === 'apt') return popAirport(key);
    if (kind === 'atc') return popAtc(key);
    if (kind === 'app') return popApp(key);
    return '';
  }

  const popChip = c => {
    const f = c.facility;
    if (f === 'ATIS') return `<i class="ATIS">${esc(c.atisCode || 'i')}</i>`;
    if (f === 'FSS')  return `<i class="FSS">F</i>`;
    return `<i class="${f}">${f[0]}</i>`;
  };

  function popRow(c) {
    const atisLine = c.facility === 'ATIS' && c.textAtis?.length
      ? `<div class="pop-atis">${esc(c.textAtis[0].slice(0, 72))}</div>` : '';
    return `<div class="pop-row">` +
      `<div class="pop-head">${popChip(c)}<span class="pop-cs">${esc(c.callsign)}</span><span class="pop-freq">${esc(c.frequency)}</span></div>` +
      `<div class="pop-who">${esc(c.name)} · ${esc(c.rating)} · ${onlineFor(c.logonTime)}</div>` +
      atisLine + `</div>`;
  }

  function popAirport(code) {
    const order = ['DEL', 'GND', 'TWR', 'APP', 'ATIS'];
    const list = order.flatMap(f => data.controllers.filter(c => prefix(c.callsign) === code && c.facility === f));
    if (!list.length) return '';
    const deps = data.pilots.filter(p => p.flightPlan?.departure?.toUpperCase() === code).length;
    const arrs = data.pilots.filter(p => p.flightPlan?.destination?.toUpperCase() === code).length;
    const foot = deps + arrs > 0
      ? `<div class="pop-foot"><span>${t('Departures')} ${deps}</span><span>${t('Arrivals')} ${arrs}</span></div>` : '';
    return list.map(popRow).join('') + foot;
  }

  function popAtc(cs) {
    const c = atcOf(cs);
    if (!c) return '';
    const sector = (c.facility === 'CTR' || c.facility === 'FSS') ? sectorOf(c) : null;
    let ctrls;
    if (sector) {
      ctrls = data.controllers.filter(x => {
        if (x.facility !== 'CTR' && x.facility !== 'FSS') return false;
        const s = sectorOf(x);
        return s && s.id === sector.id;
      });
      if (!ctrls.length) ctrls = [c];
    } else {
      ctrls = [c];
    }
    return ctrls.map(popRow).join('');
  }

  function popApp(code) {
    const list = data.controllers.filter(c => prefix(c.callsign) === code && (c.facility === 'APP' || c.facility === 'ATIS'));
    return list.map(popRow).join('');
  }

  // Keyboard: focusin on a badge or label shows the popover (tabbed into it).
  map.getContainer().addEventListener('focusin', e => {
    if (coarse.matches) return;
    const node = e.target.closest('[data-popkind]');
    if (!node) return;
    const at = [parseFloat(node.dataset.lat), parseFloat(node.dataset.lon)];
    if (isNaN(at[0]) || isNaN(at[1])) return;
    showPop(node.dataset.popkind, node.dataset.popkey, at);
  });
  map.getContainer().addEventListener('focusout', e => {
    if (e.target.closest('[data-popkind]')) hidePop();
  });

  // Everyone on the map at once.
  let lastPoints = [];
  document.getElementById('fit-all')?.addEventListener('click', () => {
    if (lastPoints.length) map.fitBounds(lastPoints, { maxZoom: 7, padding: [30, 30] });
    document.querySelector('details.layers-menu')?.removeAttribute('open');
  });

  // ---- drawing ----
  function render() {
    if (!data) return [];
    sectors.clearLayers();
    traffic.clearLayers();
    const points = [];
    const ctr = css('--f-ctr'), app = css('--f-app');
    const staffed = new Map();   // sector id → { features, controllers }
    const towers = new Map();    // airport → controllers (DEL/GND/TWR/APP)

    for (const c of data.controllers) {
      if (c.facility === 'OBS') continue;
      const at = c.latitude != null ? [c.latitude, c.longitude] : null;
      if (at) points.push(at);
      if (c.facility === 'CTR' || c.facility === 'FSS') {
        const sector = sectorOf(c);
        if (sector) {
          const s = staffed.get(sector.id) ?? { ...sector, controllers: [] };
          s.controllers.push(c);
          staffed.set(sector.id, s);
        } else if (at) {
          // No known border for this callsign: a circle instead, smaller for a part of a sector.
          const part = String(c.callsign).split('_').filter(p => !/^\d+$/.test(p)).length > 2;
          const ctrAt = at;
          L.circle(ctrAt, { radius: (c.facility === 'FSS' ? 250 : part ? 90 : 180) * 1852, color: ctr, weight: 1.2, fillOpacity: .06, bubblingMouseEvents: false })
            .on('click', open('atc', c.callsign))
            .on('mouseover', () => !coarse.matches && showPop('atc', c.callsign, ctrAt))
            .on('mouseout', () => hidePop())
            .addTo(sectors);
          label(
            `<span class="atc-label" title="${esc(c.name)} · ${esc(c.frequency)}" ` +
            `data-popkind="atc" data-popkey="${esc(c.callsign)}" data-lat="${ctrAt[0]}" data-lon="${ctrAt[1]}" ` +
            `style="transform:translate(-50%,-50%);display:inline-block">${esc(c.callsign)}</span>`,
            open('atc', c.callsign))
            .on('mouseover', () => !coarse.matches && showPop('atc', c.callsign, ctrAt))
            .on('mouseout', () => hidePop())
            .setLatLng(ctrAt).addTo(traffic);
        }
        continue;
      }
      const code = prefix(c.callsign);
      towers.set(code, [...(towers.get(code) ?? []), c]);
      // Around the airport: a controller's own position is often the middle of their whole sector file.
      const airportAt = aptLL(code) ?? at;
      if (c.facility === 'APP' && airportAt) {
        const appAt = airportAt;
        L.circle(appAt, { radius: 45 * 1852, color: app, weight: 1, dashArray: '4 4', fillOpacity: .05, bubblingMouseEvents: false })
          .on('click', open('airport', code))
          .on('mouseover', () => !coarse.matches && showPop('app', code, appAt))
          .on('mouseout', () => hidePop())
          .addTo(sectors);
      }
    }

    for (const [, s] of staffed) {
      const cs = s.controllers[0].callsign;
      const shape = L.geoJSON(s.features, { style: { color: ctr, weight: 1.6, fillColor: ctr, fillOpacity: .1 }, bubblingMouseEvents: false })
        .on('click', open('atc', cs)).addTo(sectors);
      const names = s.controllers.map(c => esc(c.callsign)).join('<br>');
      const hint = esc(s.name) + ' · ' + s.controllers.map(c => esc(c.frequency)).join(', ');
      const rawCenter = s.label ?? shape.getBounds().getCenter();
      const sectAt = Array.isArray(rawCenter) ? rawCenter : [rawCenter.lat, rawCenter.lng];
      shape.on('mouseover', () => !coarse.matches && showPop('atc', cs, sectAt))
           .on('mouseout', () => hidePop());
      label(
        `<span class="atc-label" title="${hint}" ` +
        `data-popkind="atc" data-popkey="${esc(cs)}" data-lat="${sectAt[0]}" data-lon="${sectAt[1]}" ` +
        `style="transform:translate(-50%,-50%);display:inline-block;text-align:center">${names}</span>`,
        open('atc', cs))
        .on('mouseover', () => !coarse.matches && showPop('atc', cs, sectAt))
        .on('mouseout', () => hidePop())
        .setLatLng(sectAt).addTo(traffic);
    }

    const order = ['DEL', 'GND', 'TWR', 'APP', 'ATIS'];
    const staffedBefore = [...staffedAirports].join();
    staffedAirports = new Set(towers.keys());
    if (staffedBefore !== [...staffedAirports].join()) drawCodes();
    for (const [code, list] of towers) {
      const c = list.find(x => x.latitude != null);
      const at = aptLL(code) ?? (c ? [c.latitude, c.longitude] : null);
      if (!at) continue;
      // The ATIS chip shows its current letter.
      const chips = order.filter(f => list.some(x => x.facility === f)).map(f => f === 'ATIS'
        ? `<i class="ATIS">${esc(list.find(x => x.facility === 'ATIS').atisCode || 'i')}</i>` : `<i class="${f}">${f[0]}</i>`).join('');
      const hint = list.map(x => `${esc(x.callsign)} ${esc(x.frequency)}`).join('&#10;');
      const aptAt = at;
      label(
        `<span class="apt-badge" title="${hint}" ` +
        `data-popkind="apt" data-popkey="${esc(code)}" data-lat="${aptAt[0]}" data-lon="${aptAt[1]}" ` +
        `style="transform:translate(-50%,-50%)">${esc(code)}${chips}</span>`,
        open('airport', code))
        .on('mouseover', () => !coarse.matches && showPop('apt', code, aptAt))
        .on('mouseout', () => hidePop())
        .setLatLng(aptAt).addTo(traffic);
    }

    // Aircraft stay on the map between refreshes: a new one fades in, a known one glides to its new position and
    // turns to its new heading, one that left fades out.
    const online = new Set();
    for (const p of data.pilots) {
      if (p.latitude == null) continue;
      const at = [p.latitude, p.longitude];
      points.push(at);
      online.add(p.callsign);
      const isSelected = selected?.kind === 'pilot' && selected.key === p.callsign;
      const fp = p.flightPlan;
      const look = planeClass(p, isSelected), tip = esc(p.callsign) + (fp?.aircraft ? ' · ' + esc(fp.aircraft) : '');
      const e = planes.get(p.callsign);
      if (!e) {
        const turn = p.heading ?? 0;
        const marker = L.marker(at, { icon: plane(p, isSelected, turn, !calm.matches), zIndexOffset: isSelected ? 1000 : 0 })
          .bindTooltip(tip, { direction: 'top', offset: [0, -12] })
          .on('click', open('pilot', p.callsign))
          .addTo(planeLayer);
        planes.set(p.callsign, { marker, look, turn, tip });
        continue;
      }
      if (p.heading != null) e.turn = turnTo(e.turn, p.heading);
      if (e.look !== look) {
        // Selected or not, on the ground or not: a new symbol (the selection ring plays once).
        e.look = look;
        e.marker.setIcon(plane(p, isSelected, e.turn, false));
        e.marker.setZIndexOffset(isSelected ? 1000 : 0);
      } else {
        const svg = e.marker.getElement()?.querySelector('svg');
        if (svg) svg.style.transform = `rotate(${e.turn}deg)`;
      }
      if (e.tip !== tip) { e.tip = tip; e.marker.setTooltipContent(tip); }
      glide(e.marker, at);
    }
    for (const [cs, e] of planes) {
      if (online.has(cs)) continue;
      planes.delete(cs);
      glides.delete(e.marker);
      const node = e.marker.getElement();
      if (node && !calm.matches) {
        node.classList.add('gone');
        setTimeout(() => planeLayer.removeLayer(e.marker), 400);
      } else planeLayer.removeLayer(e.marker);
    }
    updateCard();
    updatePop();
    return points;
  }

  // ---- selection ----
  function select(kind, key, fly) {
    if (!(selected?.kind === kind && selected.key === key)) { route = null; follow = false; }
    selected = { kind, key };
    history.replaceState(null, '', '#' + encodeURIComponent(key));
    render();
    if (kind === 'pilot') loadRoute(key);
    if (fly) flyTo();
  }
  function deselect() {
    if (!selected) return;
    selected = null;
    route = null;
    follow = false;
    history.replaceState(null, '', location.pathname);
    render();
  }
  map.on('click', deselect);
  // Dragging the map stops following, the way map apps do (otherwise the map would pull straight back).
  map.on('dragstart', () => { if (follow) { follow = false; updateCard(); } });
  map.on('moveend', () => { flying = false; });
  document.addEventListener('keydown', e => { if (e.key === 'Escape') deselect(); });
  card?.addEventListener('click', e => {
    if (e.target.closest('.close')) return deselect();
    const action = e.target.closest('[data-action]');
    if (action) {
      e.preventDefault();
      if (action.dataset.action === 'center') flyTo();
      else if (action.dataset.action === 'follow') { follow = !follow; if (follow) flyTo(); updateCard(); }
      else if (action.dataset.action === 'share') {
        const link = location.origin + '/map#' + encodeURIComponent(selected?.key ?? '');
        (navigator.clipboard?.writeText(link) ?? Promise.reject()).then(() => {
          action.querySelector('span').textContent = t('Link copied');
          setTimeout(updateCard, 2000);
        }).catch(() => prompt(t('Share link'), link));
      }
      return;
    }
    const target = e.target.closest('[data-select]');
    if (!target) return;
    e.preventDefault();
    const [kind, key] = target.dataset.select.split('|');
    select(kind, key, true);
  });
  // A section opened by hand is remembered across refreshes; the graph draws itself from left to right.
  let byHand = false;
  card?.addEventListener('pointerdown', e => { if (e.target.closest('summary')) byHand = true; });
  card?.addEventListener('keydown', e => { if (e.target.closest('summary')) byHand = true; });
  card?.addEventListener('toggle', e => {
    const s = e.target.dataset?.sec;
    if (!s) return;
    sections[s] = e.target.open;
    if (byHand && s === 'graph' && e.target.open && !calm.matches) e.target.querySelector('.mc-chart')?.classList.add('draw');
    byHand = false;
  }, true);

  const pilotOf = cs => data?.pilots.find(p => p.callsign === cs);
  const atcOf = cs => data?.controllers.find(c => c.callsign === cs);

  async function flyTo() {
    if (!selected) return;
    const { kind, key } = selected;
    if (kind === 'pilot') {
      const p = pilotOf(key);
      // Where the aircraft is on screen (it glides a few seconds behind the last position).
      const shown = planes.get(key)?.marker.getLatLng();
      if (p?.latitude != null) { flying = true; map.flyTo(shown ?? [p.latitude, p.longitude], Math.max(map.getZoom(), 6), { duration: .8 }); }
    } else if (kind === 'atc') {
      const c = atcOf(key), sector = c && sectorOf(c);
      if (sector) map.flyToBounds(L.geoJSON(sector.features).getBounds(), { padding: [40, 40], duration: .8 });
      else if (c?.latitude != null) map.flyTo([c.latitude, c.longitude], Math.max(map.getZoom(), 6), { duration: .8 });
    } else {
      await loadAirports();
      const c = data?.controllers.find(x => prefix(x.callsign) === key && x.latitude != null);
      const at = aptLL(key) ?? (c ? [c.latitude, c.longitude] : null);
      // Close enough for the airport diagram.
      if (at) map.flyTo(at, Math.max(map.getZoom(), 13), { duration: .8 });
    }
  }

  // ---- the route on the map ----
  let drawn = null;   // what drawRoute last drew, redrawn on zoom for the label spacing
  map.on('zoomend', () => { if (drawn) drawRoute(drawn); });
  // Callsign labels only when zoomed in enough for them not to pile up.
  const labelsByZoom = () => map.getContainer().classList.toggle('no-cs', map.getZoom() < 5);
  map.on('zoomend', labelsByZoom);
  labelsByZoom();

  // The flown part in one colour, the rest of the plan in another, every point a small arrow along the route with its
  // name where there is room, airway names along the legs — like a radar's route display.
  function drawRoute(d) {
    drawn = d;
    routeLayer.clearLayers();
    const { at, dep, arr, points, track } = d;
    const colRoute = css('--map-route'), colFlown = css('--map-flown');
    const ll = points ? points.map(w => [w[1], w[2]]) : null;
    const i = ll ? nextPoint(ll, at) : 1;
    const line = (pts, opts) => L.polyline(path(pts), { interactive: false, ...opts }).addTo(routeLayer);

    if (ll) line(ll, { color: colRoute, weight: 1.5, opacity: .3, dashArray: '4 6' });
    else if (dep && arr) line([dep, arr], { color: colRoute, weight: 1.5, opacity: .3, dashArray: '4 6' });
    if (at) {
      const flown = track.length > 1 ? [...track.map(q => [q[0], q[1]]), at] : ll ? [...ll.slice(0, i), at] : dep ? [dep, at] : null;
      if (flown && flown.length > 1) line(smooth(flown), { color: colFlown, weight: 2.5 });
      if (ll) line([at, ...ll.slice(i)], { color: colRoute, weight: 2 });
      else if (arr) line([at, arr], { color: colRoute, weight: 2, dashArray: '6 6' });
    }

    if (ll) {
      // Names only where they do not pile up; airway names once per run of points on that airway.
      let lastLabel = null;
      const minGap = 34;
      for (let k = 1; k < ll.length - 1; k++) {
        const p = map.latLngToContainerPoint(ll[k]);
        const show = !lastLabel || Math.hypot(p.x - lastLabel.x, p.y - lastLabel.y) >= minGap;
        if (show) lastLabel = p;
        const dir = bearing(ll[k], ll[Math.min(k + 1, ll.length - 1)]);
        L.marker(ll[k], { interactive: false, keyboard: false, icon: L.divIcon({
          className: '', iconSize: [10, 10], iconAnchor: [5, 5],
          html: `<div class="wp${k < i ? ' passed' : ''}"><svg viewBox="0 0 10 10" style="transform:rotate(${Math.round(dir)}deg)"><path d="M5 0 10 10 5 7.5 0 10z"/></svg>${show ? `<i>${esc(points[k][0])}</i>` : ''}</div>`,
        }) }).addTo(routeLayer);
      }
      for (let k = 1; k < points.length;) {
        const aw = points[k][3];
        if (!aw) { k++; continue; }
        let end = k;
        while (end + 1 < points.length && points[end + 1][3] === aw) end++;
        const a = ll[k - 1], b = ll[end];
        const pa = map.latLngToContainerPoint(a), pb = map.latLngToContainerPoint(b);
        if (Math.hypot(pb.x - pa.x, pb.y - pa.y) >= 70) {
          const m = Math.floor((k - 1 + end) / 2), s1 = ll[m], s2 = ll[m + 1];
          let angle = bearing(s1, s2) - 90;
          if (angle > 90) angle -= 180; else if (angle <= -90) angle += 180;
          L.marker([(s1[0] + s2[0]) / 2, (s1[1] + s2[1]) / 2], { interactive: false, keyboard: false, icon: L.divIcon({
            className: '', iconSize: null, html: `<span class="aw-lbl${end < i ? ' passed' : ''}" style="transform:translate(-50%,-50%) rotate(${angle.toFixed(0)}deg)">${esc(aw)}</span>`,
          }) }).addTo(routeLayer);
        }
        k = end + 1;
      }
    }
    for (const [code, a] of [[d.depCode, dep], [d.arrCode, arr]])
      if (a) L.marker(a, { icon: L.divIcon({ className: '', iconSize: [10, 10], html: '<div class="apt-dot"></div>' }) })
        .bindTooltip(esc(code), { permanent: true, direction: 'right', offset: [8, 0] })
        .on('click', () => select('airport', code, false)).addTo(routeLayer);
    return i;
  }

  // ---- cards ----
  const head = (title, who, chips) => `
    <div class="mc-head">
      <div>
        <div class="cs">${title}</div>
        ${who ? `<div class="who">${who}</div>` : ''}
        ${chips ? `<div class="mc-chips">${chips}</div>` : ''}
      </div>
      <button class="close" type="button" aria-label="${t('Close')}">×</button>
    </div>`;
  const cell = (name, value) => `<div><span>${name}</span><b>${value}</b></div>`;
  const aptLink = (code, info) => code
    ? `<a href="#" data-select="airport|${esc(code)}"><b>${esc(code)}</b></a><span>${esc(info?.[2] ?? '')}</span>`
    : '<b>—</b>';
  const section = (key, title, body) => `<details class="mc-sec" data-sec="${key}"${sections[key] ? ' open' : ''}><summary>${title}<i></i></summary>${body}</details>`;

  // The card slides in from its edge (from below on a phone) and goes back the same way. Every animation starts
  // from what is on screen, so a card grabbed again while it closes simply turns round. The card is redrawn on
  // every refresh; only the card itself animates, so a redraw never restarts anything.
  let cardKey = '', cardMotion = null;
  const OUT = 'cubic-bezier(.22, 1, .36, 1)';
  const away = () => matchMedia('(max-width: 760px)').matches ? 'translateY(40px)' : 'translateX(28px) scale(.98)';
  const onScreen = () => { const cs = getComputedStyle(card); return { opacity: cs.opacity, transform: cs.transform }; };
  function showCard() {
    card.classList.remove('closing');
    const from = cardMotion?.playState === 'running' ? onScreen() : { opacity: 0, transform: calm.matches ? 'none' : away() };
    cardMotion?.cancel();
    cardMotion = card.animate([from, { opacity: 1, transform: 'none' }], { duration: calm.matches ? 160 : 460, easing: OUT });
  }
  function hideCard() {
    if (card.hidden || card.classList.contains('closing')) return;
    cardKey = '';
    const from = onScreen();
    cardMotion?.cancel();
    card.classList.add('closing');
    const motion = cardMotion = card.animate([from, { opacity: 0, transform: calm.matches ? 'none' : away() }],
      { duration: calm.matches ? 120 : 220, easing: 'cubic-bezier(.4, 0, 1, 1)', fill: 'forwards' });
    motion.onfinish = () => {
      if (cardMotion !== motion) return;
      card.hidden = true;
      card.classList.remove('closing');
      motion.cancel();
    };
  }
  function swapCard() {
    if (cardMotion?.playState === 'running' || calm.matches) return;
    cardMotion = card.animate([{ opacity: .4, transform: 'translateY(6px)' }, { opacity: 1, transform: 'none' }], { duration: 300, easing: OUT });
  }
  // The flight's progress bar moves on from where it is on screen (a newly opened flight fills up from the start).
  function growProgress(fromPct) {
    const bar = card.querySelector('.mc-progress i'), dot = card.querySelector('.mc-progress b');
    if (!bar || !dot || calm.matches) return;
    const to = bar.style.width;
    if (Math.abs(parseFloat(to) - fromPct) < 0.5) return;
    bar.style.width = dot.style.left = fromPct + '%';
    void bar.offsetWidth;
    bar.style.width = dot.style.left = to;
  }

  function updateCard() {
    if (!card) return;
    if (!selected || !data) { routeLayer.clearLayers(); drawn = null; hideCard(); return; }
    const { kind, key } = selected;
    if (kind !== 'pilot') { routeLayer.clearLayers(); drawn = null; }
    const id = kind + '|' + key;
    const opening = card.hidden || card.classList.contains('closing');
    const swapping = !opening && id !== cardKey;
    const oldBar = opening || swapping ? null : card.querySelector('.mc-progress i');
    const shownPct = oldBar ? oldBar.getBoundingClientRect().width / Math.max(1, oldBar.parentElement.getBoundingClientRect().width) * 100 : 0;
    const scroll = card.scrollTop;
    card.innerHTML = kind === 'pilot' ? pilotCard(key) : kind === 'atc' ? atcCard(key) : airportCard(key);
    cardKey = id;
    card.hidden = false;
    card.scrollTop = opening || swapping ? 0 : scroll;
    growProgress(shownPct);
    if (opening) showCard(); else if (swapping) swapCard();
  }

  function pilotCard(cs) {
    const p = pilotOf(cs);
    if (!p) { routeLayer.clearLayers(); drawn = null; return head(esc(cs), t('Offline')); }
    const fp = p.flightPlan;
    if (!airports) loadAirports().then(updateCard);
    if (!airlines) loadAirlines().then(updateCard);
    const at = p.latitude != null ? [p.latitude, p.longitude] : null;
    const dep = aptLL(fp?.departure), arr = aptLL(fp?.destination);
    const mine = route?.callsign === p.callsign ? route : null;
    const points = mine?.waypoints?.length > 1 ? mine.waypoints : null;
    const track = mine?.track ?? [];
    const extras = mine?.extras ?? null;
    const gs = p.groundspeed, alt = p.altitude;

    const i = drawRoute({ at, dep, arr, points, track, depCode: fp?.departure, arrCode: fp?.destination });

    // Distance flown and left, along the plan when there is one.
    let flown = null, left = null, next = null;
    if (at && points) {
      const ll = points.map(w => [w[1], w[2]]);
      next = points[i];
      left = distNm(at, ll[i]);
      for (let k = i + 1; k < ll.length; k++) left += distNm(ll[k - 1], ll[k]);
      let total = 0;
      for (let k = 1; k < ll.length; k++) total += distNm(ll[k - 1], ll[k]);
      flown = Math.max(0, total - left);
    } else if (at && dep && arr) { flown = distNm(dep, at); left = distNm(at, arr); }

    // Where the flight is: from the last few minutes of the track and the distance to the airports.
    const now = Date.now() / 1000;
    let vs = 0;
    if (at) {
      const recent = [...track.filter(q => now - q[4] < 240), [at[0], at[1], alt, gs, now]];
      const a = recent[0], b = recent[recent.length - 1], dt = (b[4] - a[4]) / 60;
      if (dt >= 0.5) vs = Math.round((b[2] - a[2]) / dt / 50) * 50;
    }
    const dDep = at && dep ? distNm(at, dep) : null, dArr = at && arr ? distNm(at, arr) : null;
    let phase;
    if (p.onGround ?? gs < 40) phase = dArr != null && dArr < 5 && track.length > 3 ? 'Arrived' : 'On the ground';
    else if (dArr != null && dArr < 30 && vs <= 200 && alt < 12000) phase = 'Arriving';
    else if (dDep != null && dDep < 30 && vs > 200) phase = 'Departing';
    else if (vs > 300) phase = 'Climbing';
    else if (vs < -300) phase = 'Descending';
    else phase = 'Cruising';

    // Departure: when the aircraft first moved, otherwise the planned time; arrival from the speed and distance left.
    const off = track.find(q => q[3] >= 50);
    const depTime = off ? utc(new Date(off[4] * 1000)) : hhmm(fp?.departureTime);
    const eta = gs > 50 && left > 1 ? utc(new Date(Date.now() + left / gs * 3600000)) : extras?.on ? utc(new Date(extras.on * 1000)) : '—';
    const pct = flown != null ? Math.min(100, Math.round(flown / Math.max(1, flown + left) * 100)) : 0;
    const airline = airlineOf(p.callsign);

    const chips = `<span class="badge phase ${phase.toLowerCase().replace(/ /g, '-')}">${t(phase)}</span>` +
      (fp?.aircraft ? `<span class="badge accent">${esc(fp.aircraft)}</span>` : '') +
      (fp?.rules ? `<span class="badge">${esc(fp.rules)}</span>` : '');
    const who = (airline ? `<b>${esc(airline)}</b> · ` : '') + `<a href="/members/${p.cid}">${esc(p.name)}</a> · CID ${p.cid}`;

    const flight = fp ? `
      <div class="mc-flight">
        <div class="mc-route"><div class="apt">${aptLink(fp.departure, airport(fp.departure))}</div><div class="arrow">→</div><div class="apt">${aptLink(fp.destination, airport(fp.destination))}</div></div>
        <div class="mc-progress"><i style="width:${pct}%"></i><b style="left:${pct}%"></b></div>
        <div class="mc-times">
          <span><small>${off ? t('Departed') : t('Planned')}</small>${depTime}</span>
          <span><small>${t('Time online')}</small>${onlineFor(p.logonTime)}</span>
          <span><small>${t('ETA')}</small>${eta}</span>
        </div>
        ${flown != null ? `<div class="mc-progress-text"><span>${t('Distance flown')} ${Math.round(flown)} nm</span><span>${t('Remaining')} ${Math.round(left)} nm</span></div>` : ''}
      </div>` : `<div class="muted small">${t('No flight plan filed')}</div>`;

    const grid = `
      <div class="mc-grid">
        ${cell(t('Ground speed'), gs + ' kt')}
        ${cell(t('Altitude'), feet(alt))}
        ${cell(t('Heading'), p.heading != null ? pad(Math.round(p.heading) % 360, 3) + '°' : '—')}
        ${cell(t('Squawk'), esc(p.transponder || '—'))}
        ${cell(t('Vertical speed'), (p.onGround ?? gs < 40) ? '—' : (vs > 0 ? '+' : '') + vs + ' ft/min')}
        ${cell(t('Next point'), next && gs >= 40 ? `${esc(next[0])} <small class="muted">${Math.round(distNm(at, [next[1], next[2]]))} nm</small>` : '—')}
      </div>`;

    const steps = extras?.steps ? extras.steps.split(/\s+/).map(s => {
      const [fix, lvl] = s.split('/');
      return lvl ? `<span class="badge">FL${lvl.replace(/^0+/, '')} <b class="mono">${esc(fix)}</b></span>` : '';
    }).join('') : '';
    const source = mine?.source === 'simbrief' ? t('Route from SimBrief (AIRAC {0})', esc(extras?.airac || '—'))
      : mine?.source === 'route' ? t('Route worked out from the flight plan') : '';
    const unresolved = mine?.unresolved?.length ? `<div class="muted small">${t('Not found in the database: {0}', esc(mine.unresolved.join(', ')))}</div>` : '';
    const routeText = fp?.route ? esc(fp.route).split(' ').map(w => isAirway(w) ? `<b>${w}</b>` : w).join(' ') : '';
    const plan = fp ? `
      <div class="mc-grid three">
        ${cell(t('Aircraft type'), esc(extras?.type && extras.type !== fp.aircraft ? `${fp.aircraft} · ${extras.type}` : fp.aircraft || '—'))}
        ${cell(t('Cruise TAS'), fp.cruiseSpeed ? fp.cruiseSpeed + ' kt' + (extras?.mach ? ` · M${esc(extras.mach)}` : '') : '—')}
        ${cell(t('Cruise altitude'), esc(fp.cruiseAltitude || '—'))}
        ${cell(t('Aircraft registration'), esc(extras?.reg || '—'))}
        ${cell(t('Alternate'), fp.alternate ? `<a href="#" data-select="airport|${esc(fp.alternate)}">${esc(fp.alternate)}</a>` : '—')}
        ${cell(t('Route distance'), extras?.dist ? extras.dist + ' nm' : left != null && flown != null ? Math.round(flown + left) + ' nm' : '—')}
        ${cell(t('Departure'), hhmm(fp.departureTime))}
        ${cell(t('En route'), hm(fp.enrouteMinutes))}
        ${cell(t('Fuel'), hm(fp.fuelMinutes))}
      </div>
      ${steps ? `<div class="mc-block"><div class="eyebrow">${t('Step climbs')}</div><div class="mc-chips">${steps}</div></div>` : ''}
      ${routeText ? `<div class="mc-block"><div class="eyebrow">${t('Route')}</div><div class="mc-text mc-route-text">${routeText}</div>
        ${source ? `<div class="muted small">${source}</div>` : ''}${unresolved}</div>` : ''}
      ${fp.remarks ? `<div class="mc-block"><div class="eyebrow">${t('Remarks')}</div><div class="mc-text">${esc(fp.remarks)}</div></div>` : ''}` : '';

    return head(esc(p.callsign), who, chips) + `
      <div class="mc-body">
        ${flight}
        ${grid}
        ${section('graph', t('Speed & altitude graph'), chart(track, at, alt, gs))}
        ${fp ? section('plan', t('Flight plan'), plan) : ''}
      </div>
      <div class="mc-tools">
        <button type="button" data-action="center"><svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="3"/><path d="M12 2v4M12 18v4M2 12h4M18 12h4"/></svg><span>${t('Center on aircraft')}</span></button>
        <button type="button" data-action="follow" class="${follow ? 'on' : ''}"><svg viewBox="0 0 24 24"><path d="M3 11l18-8-8 18-2-8z"/></svg><span>${t(follow ? 'Following' : 'Follow')}</span></button>
        <button type="button" data-action="share"><svg viewBox="0 0 24 24"><path d="M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1.5 1.5"/><path d="M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1.5-1.5"/></svg><span>${t('Share link')}</span></button>
      </div>`;
  }

  // Altitude and ground speed since the aircraft connected.
  function chart(track, at, alt, gs) {
    const pts = at ? [...track, [at[0], at[1], alt, gs, Date.now() / 1000]] : track;
    if (pts.length < 3) return `<div class="muted small">${t('Not enough data yet')}</div>`;
    const W = 320, H = 90, padT = 6, padB = 6;
    const t0 = pts[0][4], span = Math.max(300, pts[pts.length - 1][4] - t0);
    const maxAlt = Math.max(2000, ...pts.map(q => q[2])), maxGs = Math.max(100, ...pts.map(q => q[3]));
    const x = q => ((q[4] - t0) / span * W).toFixed(1);
    const yA = q => (padT + (1 - q[2] / maxAlt) * (H - padT - padB)).toFixed(1);
    const yS = q => (padT + (1 - q[3] / maxGs) * (H - padT - padB)).toFixed(1);
    return `
      <svg class="mc-chart" viewBox="0 0 ${W} ${H}" preserveAspectRatio="none" aria-hidden="true">
        <polyline class="alt" points="${pts.map(q => `${x(q)},${yA(q)}`).join(' ')}"/>
        <polyline class="gs" points="${pts.map(q => `${x(q)},${yS(q)}`).join(' ')}"/>
      </svg>
      <div class="mc-legend"><span class="alt">${t('Altitude')} · ${feet(alt)}</span><span class="gs">${t('Ground speed')} · ${gs} kt</span>
        <span class="muted">${utc(new Date(t0 * 1000))} – ${utc(new Date())}</span></div>`;
  }

  function atcCard(cs) {
    const c = atcOf(cs);
    if (!c) return head(esc(cs), t('Offline'));
    const sector = (c.facility === 'CTR' || c.facility === 'FSS') ? sectorOf(c) : null;
    const code = prefix(c.callsign);
    return head(esc(c.callsign), `<a href="/members/${c.cid}">${esc(c.name)}</a> · CID ${c.cid}`,
      `<span class="badge accent">${esc(c.facility)}</span><span class="badge">${esc(c.rating)}</span>`) + `
      <div class="mc-body">
        <div class="mc-grid">
          ${cell(t('Frequency'), esc(c.frequency || '—'))}
          ${cell(t('Rating'), esc(c.rating || '—'))}
          ${cell(t('Online for'), onlineFor(c.logonTime))}
        </div>
        ${sector ? `<div class="small"><span class="muted">${t('Sector:')}</span> ${esc(sector.name)}</div>`
                 : `<div class="small"><span class="muted">${t('Airport:')}</span> <a href="#" data-select="airport|${esc(code)}">${esc(code)}</a></div>`}
        ${atisText(c)}
      </div>`;
  }

  // The ATIS text (or a controller's info lines) as the server last got it.
  function atisText(c) {
    const lines = c.textAtis ?? [];
    if (!lines.length) return '';
    const title = c.facility === 'ATIS' ? 'ATIS' + (c.atisCode ? ' · ' + esc(c.atisCode) : '') : t('Controller info');
    return `<div class="mc-block"><div class="eyebrow">${title}</div><div class="atis-text">${lines.map(esc).join('\n')}</div></div>`;
  }

  function airportCard(code) {
    if (!airports) loadAirports().then(updateCard);
    const info = airport(code);
    const atc = data.controllers.filter(c => prefix(c.callsign) === code && c.facility !== 'OBS');
    const deps = data.pilots.filter(p => p.flightPlan?.departure?.toUpperCase() === code);
    const arrs = data.pilots.filter(p => p.flightPlan?.destination?.toUpperCase() === code);
    const strips = atc.map(c => `<a class="strip ${esc(c.facility)}" href="#" data-select="atc|${esc(c.callsign)}">
        <span class="cs">${esc(c.callsign)}</span><span class="who">${esc(c.name)}</span><span class="freq">${esc(c.frequency)}</span></a>`).join('');
    const flights = list => list.length
      ? `<div class="mc-chips">${list.map(p => `<a class="badge" href="#" data-select="pilot|${esc(p.callsign)}">${esc(p.callsign)}</a>`).join('')}</div>`
      : `<div class="muted small">${t('none')}</div>`;
    const metar = metarOf(code);
    return head(esc(code), esc(info?.[2] ?? '')) + `
      <div class="mc-body">
        <div class="mc-block"><div class="eyebrow">METAR</div>${metar ? `<div class="mc-text">${esc(metar)}</div>`
          : `<div class="muted small">${metar === '' ? t('no data') : t('Loading…')}</div>`}</div>
        <div class="mc-block"><div class="eyebrow">${t('Controllers')}</div>${strips ? `<div class="mc-list">${strips}</div>` : `<div class="muted small">${t('nobody')}</div>`}</div>
        ${atc.filter(c => c.facility === 'ATIS').map(atisText).join('')}
        <div class="mc-block"><div class="eyebrow">${t('Departures')} · ${deps.length}</div>${flights(deps)}</div>
        <div class="mc-block"><div class="eyebrow">${t('Arrivals')} · ${arrs.length}</div>${flights(arrs)}</div>
      </div>`;
  }

  // ---- search ----
  async function find(q) {
    q = q.trim().toUpperCase();
    if (!q || !data) return;
    // Exact callsign or CID first, then an airport code, then the start of a callsign.
    const exact = list => list.find(x => x.callsign.toUpperCase() === q || String(x.cid) === q);
    const starts = list => list.find(x => x.callsign.toUpperCase().startsWith(q));
    const p = exact(data.pilots), c = exact(data.controllers);
    if (p) return select('pilot', p.callsign, true);
    if (c) return select('atc', c.callsign, true);
    await loadAirports();
    if (airport(q)) return select('airport', q, true);
    const sp = starts(data.pilots), sc = starts(data.controllers);
    if (sp) return select('pilot', sp.callsign, true);
    if (sc) return select('atc', sc.callsign, true);
    if (search) { search.value = ''; search.placeholder = t('Not found: {0}', q); }
  }
  search?.addEventListener('change', () => find(search.value));

  // ---- theme ----
  window.addEventListener('themechange', () => {
    base.setUrl(`/tiles/${theme()}/{z}/{x}/{y}.png`);
    labels.setUrl(`/tiles/${theme()}-labels/{z}/{x}/{y}.png`);
    drawOutline();
    drawLayouts();
    render();
  });

  // ---- live data ----
  async function refresh() {
    try {
      const r = await fetch('/api/v1/online', { cache: 'no-store' });
      data = await r.json();
    } catch { return; }
    const pc = document.getElementById('pilot-count'), ac = document.getElementById('atc-count'), up = document.getElementById('map-updated');
    if (pc) pc.textContent = data.pilots.length;
    if (ac) ac.textContent = data.controllers.length;
    if (up) up.textContent = data.available ? t('Updated') + ' ' + utc(new Date(data.updated)) : t('Server not responding');
    const list = document.getElementById('map-callsigns');
    if (list) list.innerHTML = [...data.pilots, ...data.controllers].map(x => `<option value="${esc(x.callsign)}">`).join('');

    const points = render();
    lastPoints = points;
    if (selected?.kind === 'pilot') {
      loadRoute(selected.key);
      const p = pilotOf(selected.key);
      // With motion the map moves with the gliding aircraft (see step); without it, it jumps with the aircraft.
      if (follow && p?.latitude != null && calm.matches) map.panTo([p.latitude, p.longitude]);
    }
    if (pendingHash) {
      const q = pendingHash;
      pendingHash = '';
      fitted = true;
      find(q);
    } else if (!fitted && points.length > 0) {
      map.fitBounds(points, { maxZoom: 7, padding: [30, 30] });
      fitted = true;
    }
  }
  refresh();
  // Every 5 s: the client reports every 5 s and the site reads the server every 5 s, so the map lags by 15 s at most.
  setInterval(refresh, 5000);
})();
