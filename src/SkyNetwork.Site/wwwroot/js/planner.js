// Flight planner studio: the plan from /api/v1/planner/plan drawn on the map and laid out in panels —
// runways by wind, weather, load and fuel, route with variants, navigation log, ICAO fuel, OFP and exports.
(function () {
  const root = document.getElementById('ps');
  if (!root || !window.L) return;
  const doc = document.documentElement;
  const RAD = Math.PI / 180;
  const texts = (() => { try { return JSON.parse(root.dataset.text || '{}'); } catch { return {}; } })();
  const t = (key, ...args) => (texts[key] ?? key).replace(/\{(\d)\}/g, (_, i) => args[i]);
  const ru = (doc.lang || '').startsWith('ru');
  const $ = (sel, el = root) => el.querySelector(sel);
  const $$ = (sel, el = root) => [...el.querySelectorAll(sel)];
  const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const num = n => n == null || isNaN(n) ? '—' : Math.round(n).toLocaleString(ru ? 'ru-RU' : 'en-US');
  const hm = m => { if (m == null) return '—'; m = Math.round(m); return `${Math.floor(m / 60)}:${String(m % 60).padStart(2, '0')}`; };
  const hhmm = iso => iso ? iso.substring(11, 16) + 'Z' : '—';
  const pad3 = n => String(Math.round(n) % 360 || 360).padStart(3, '0');
  const store = { get(k, d) { try { return localStorage.getItem('planner-' + k) ?? d; } catch { return d; } },
                  set(k, v) { try { localStorage.setItem('planner-' + k, v); } catch { /* private window */ } } };

  // ── units ────────────────────────────────────────────────────────────────
  let unit = store.get('unit', 'kg');
  const w = kg => num(unit === 'lb' ? kg * 2.20462 : kg);

  // ── map ──────────────────────────────────────────────────────────────────
  const theme = () => window.skyTheme ? window.skyTheme() : doc.dataset.theme === 'dark' ? 'dark' : 'light';
  const map = L.map('ps-map', { zoomControl: false, worldCopyJump: true }).setView([55.75, 37.6], 4);
  L.control.zoom({ position: 'bottomright' }).addTo(map);
  map.attributionControl.setPrefix('<a href="https://leafletjs.com">Leaflet</a>');
  map.createPane('labels').style.zIndex = 450;
  map.getPane('labels').style.pointerEvents = 'none';
  const tileOptions = { maxZoom: 18, maxNativeZoom: 16 };
  const base = L.tileLayer(`/tiles/${theme()}/{z}/{x}/{y}.png`, { ...tileOptions, attribution: '&copy; Esri, HERE, Garmin, &copy; OpenStreetMap' }).addTo(map);
  const labels = L.tileLayer(`/tiles/${theme()}-labels/{z}/{x}/{y}.png`, { ...tileOptions, maxZoom: 15, pane: 'labels' }).addTo(map);
  window.addEventListener('themechange', () => {
    base.setUrl(`/tiles/${theme()}/{z}/{x}/{y}.png`);
    labels.setUrl(`/tiles/${theme()}-labels/{z}/{x}/{y}.png`);
  });
  const routeLayer = L.layerGroup().addTo(map);
  const fixLayer = L.layerGroup().addTo(map);
  const aptLayer = L.layerGroup().addTo(map);
  // On a phone the map sits in a page that scrolls: the wheel scrolls the page, not the map.
  const wheel = () => { if (innerWidth >= 1100) map.scrollWheelZoom.enable(); else map.scrollWheelZoom.disable(); };
  wheel(); addEventListener('resize', wheel);
  // A long route zoomed out is a string of labels on top of each other: then only the dots (TOC and TOD keep theirs).
  let fixCount = 0;
  const declutter = () => root.classList.toggle('ps-dense', fixCount > 25 && map.getZoom() < 6);
  map.on('zoomend', declutter);

  function arc(a, b) {
    const [p1, l1, p2, l2] = [a[0] * RAD, a[1] * RAD, b[0] * RAD, b[1] * RAD];
    const d = 2 * Math.asin(Math.sqrt(Math.sin((p2 - p1) / 2) ** 2 + Math.cos(p1) * Math.cos(p2) * Math.sin((l2 - l1) / 2) ** 2));
    if (d < 0.01) return [a, b];
    const n = Math.ceil(d / 0.02), pts = [];
    let prev = null;
    for (let i = 0; i <= n; i++) {
      const A = Math.sin((1 - i / n) * d) / Math.sin(d), B = Math.sin(i / n * d) / Math.sin(d);
      const x = A * Math.cos(p1) * Math.cos(l1) + B * Math.cos(p2) * Math.cos(l2);
      const y = A * Math.cos(p1) * Math.sin(l1) + B * Math.cos(p2) * Math.sin(l2);
      const z = A * Math.sin(p1) + B * Math.sin(p2);
      let lon = Math.atan2(y, x) / RAD;
      if (prev !== null) { while (lon - prev > 180) lon -= 360; while (lon - prev < -180) lon += 360; }
      prev = lon;
      pts.push([Math.atan2(z, Math.hypot(x, y)) / RAD, lon]);
    }
    return pts;
  }
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

  function drawMap(p) {
    routeLayer.clearLayers(); fixLayer.clearLayers(); aptLayer.clearLayers();
    const pts = p.navLog.map(r => [r.lat, r.lon]);
    const line = path(pts);
    L.polyline(line, { color: getComputedStyle(doc).getPropertyValue('--ps-route').trim() || '#5aa8f7', weight: 3, dashArray: '10 7', opacity: .95 }).addTo(routeLayer);
    // Keep longitudes continuous with the drawn line so labels sit on it across the date line.
    let prev = null;
    p.navLog.forEach((r, i) => {
      let lon = r.lon;
      if (prev !== null) { while (lon - prev > 180) lon -= 360; while (lon - prev < -180) lon += 360; }
      prev = lon;
      if (r.kind === 'apt') return;
      const cls = r.kind === 'toc' || r.kind === 'tod' ? 'ps-fix ps-prof' : 'ps-fix';
      const icon = L.divIcon({ className: cls, html: `<i></i><span>${esc(r.ident)}</span>`, iconSize: null });
      L.marker([r.lat, lon], { icon, keyboard: false }).bindTooltip(
        `<b>${esc(r.ident)}</b>${r.airway ? ' · ' + esc(r.airway) : ''}<br>${fl(r.altitudeFt)} · ${r.gsKt} ${t('kt')} · ${hhmm(r.eta)}`,
        { direction: 'top', offset: [0, -8] }).addTo(fixLayer);
      void i;
    });
    const apt = (a, cls, title) => L.marker([a.lat, a.lon], {
      icon: L.divIcon({ className: 'ps-apt ' + cls, html: `<span>${esc(a.icao)}</span><i></i>`, iconSize: null }), zIndexOffset: 500,
    }).bindTooltip(`<b>${esc(a.icao)}</b> ${esc(a.name)}<br>${title}`, { direction: 'top', offset: [0, -14] }).addTo(aptLayer);
    apt(p.departureAirport, 'dep', t('Departure'));
    apt(p.destinationAirport, 'dep', t('Destination'));
    for (const a of p.alternateOptions) {
      const m = apt(a, a.icao === p.alternate ? 'alt sel' : 'alt', a.icao === p.alternate ? t('Alternate') : t('Click to take as the alternate'));
      m.on('click', () => { $('#ps-alt').value = a.icao; plan(); });
    }
    if (p.alternateAirport && !p.alternateOptions.some(a => a.icao === p.alternate)) apt(p.alternateAirport, 'alt sel', t('Alternate'));
    fixCount = p.navLog.length;
    const bounds = L.latLngBounds(line);
    if (bounds.isValid()) {
      map.invalidateSize();
      map.fitBounds(bounds, { paddingTopLeft: [padLeft(), wide() ? 90 : 30], paddingBottomRight: [padRight(), padBottom()], maxZoom: 10 });
    }
    declutter();
  }
  // Room the panels take over the map; never so much that the route has no place left to be drawn.
  const wide = () => innerWidth >= 1100;
  const mapW = () => $('#ps-map').offsetWidth, mapH = () => $('#ps-map').offsetHeight;
  const padLeft = () => wide() ? Math.min($('.ps-left').offsetWidth + 40, mapW() * 0.3) : 30;
  const padRight = () => wide() ? Math.min($('.ps-right').offsetWidth + 40, mapW() * 0.3) : 30;
  const padBottom = () => wide() && !$('.ps-bottom').classList.contains('collapsed')
    ? Math.min($(".ps-bottom").offsetHeight + 30, mapH() * 0.6) : 30;
  const fl = ft => ft >= 10000 ? 'FL' + String(Math.round(ft / 100)).padStart(3, '0') : num(ft) + ' ' + t('ft');

  // ── request ──────────────────────────────────────────────────────────────
  const fields = ['dep', 'dest', 'alt', 'type', 'callsign', 'time', 'ci', 'fl', 'pax', 'bag', 'cargo', 'extra'];
  let current = null, variant = 0, depRwy = '', arrRwy = '', busy = 0;

  function query() {
    const q = new URLSearchParams();
    for (const f of fields) {
      const el = $('#ps-' + f);
      let v = (el?.value || '').trim();
      if (!v) continue;
      if (f === 'time') { v = new Date(v).toISOString(); }
      if (['dep', 'dest', 'alt', 'type', 'callsign'].includes(f)) v = v.toUpperCase();
      if (['bag', 'cargo', 'extra'].includes(f) && unit === 'lb') v = String(Math.round(Number(v) / 2.20462));
      q.set(f, v);
    }
    if (variant) q.set('variant', variant);
    if (depRwy) q.set('deprwy', depRwy);
    if (arrRwy) q.set('arrrwy', arrRwy);
    return q;
  }

  async function plan() {
    const q = query();
    if (!q.get('dep') || !q.get('dest') || !q.get('type')) { $('#ps-dep').focus(); return; }
    const my = ++busy;
    root.classList.add('loading');
    $('#ps-error').hidden = true;
    try {
      const r = await fetch('/api/v1/planner/plan?' + q);
      const body = await r.json().catch(() => ({}));
      if (my !== busy) return;
      if (!r.ok) throw new Error((ru ? body.errorRu : body.error) || (r.status === 429 ? t('Too many plans, wait a minute') : t('The plan could not be made')));
      current = body;
      history.replaceState(null, '', '/planner?' + q);
      render(body);
      $('.ps-flight').open = false;
    } catch (e) {
      $('#ps-error').textContent = e.message;
      $('#ps-error').hidden = false;
    } finally {
      if (my === busy) root.classList.remove('loading');
    }
  }

  // ── render ───────────────────────────────────────────────────────────────
  function render(p) {
    root.classList.add('has-plan');
    renderTop(p); renderRunways(p); renderWeather(p); renderLoad(p);
    renderRoute(p); renderNavlog(p); renderFuel(p); renderSummary(p); renderOfp(p);
    // Last: the panels have their final size, the route is fitted into what is left of the map.
    drawMap(p);
    // The fields show what the plan was made with (the level and pax when left automatic).
    $('#ps-fl').placeholder = 'FL' + p.cruiseLevel;
    $('#ps-pax').placeholder = p.weights.pax;
    if (!$('#ps-alt').value) $('#ps-alt').placeholder = p.alternate || '';
  }

  function renderTop(p) {
    $('#ps-top-route').innerHTML = `${esc(p.departure)} <span class="arr">→</span> ${esc(p.destination)}`;
    $('#ps-top-type').textContent = 'IFR ' + p.aircraftIcao;
    const warn = $('#ps-top-warn');
    warn.hidden = p.warnings.length === 0;
    warn.querySelector('b').textContent = p.warnings.length;
    warn.title = p.warnings.map(x => ru ? x.ru : x.en).join('\n');
    $('#ps-top-stats').innerHTML = `
      <span>${t('{0} routes', p.routes.length)}</span>
      <span>${t('Block')} <b>${hm(p.blockMin)}</b></span>
      <span>${t('Distance')} <b>${num(p.distanceNm)} nm</b></span>
      <span>${t('Passengers')} <b>${p.weights.pax}</b></span>`;
    $('#ps-charts').href = `https://chartfox.org/${encodeURIComponent(p.departure)}`;
  }

  // Runways: the airport's runways from OpenStreetMap drawn to scale, the wind across them, the one chosen.
  let rwyTab = 'dep';
  function renderRunways(p) {
    const r = rwyTab === 'dep' ? p.departureRunways : p.arrivalRunways;
    $$('#ps-rwy-tabs button').forEach(b => {
      b.textContent = b.dataset.tab === 'dep' ? p.departure : p.destination;
      b.classList.toggle('on', b.dataset.tab === rwyTab);
    });
    const sel = r.runways.find(x => x.ident === r.selected);
    const wind = r.windKt == null ? t('no METAR') : r.windVariable || r.windDir == null
      ? t('Wind variable {0} kt', Math.round(r.windKt)) : t('Wind {0}° {1} kt', pad3(r.windDir), Math.round(r.windKt)) + (r.gustKt ? ` G${Math.round(r.gustKt)}` : '');
    let comp = '';
    if (sel) {
      const head = sel.headwindKt, cross = sel.crosswindKt;
      const hw = head >= 0 ? t('headwind {0}', Math.round(head)) : t('tailwind {0}', Math.round(-head));
      const cw = t('crosswind {0} {1}', Math.round(Math.abs(cross)), cross > 0.5 ? t('from the right') : cross < -0.5 ? t('from the left') : '');
      comp = `<div class="${head < -0.5 || Math.abs(cross) > 20 ? 'warn' : 'muted'} small">${head < -0.5 ? '⚠ ' : ''}${esc(hw)} · ${esc(cw)}</div>`;
    }
    $('#ps-rwy-head').innerHTML = `
      <div class="ps-rwy-sel"><small>${rwyTab === 'dep' ? t('Take-off') : t('Landing')}</small><b>${esc(r.selected || '—')}</b></div>
      <div><div>${esc(wind)}</div>${comp}</div>`;
    $('#ps-rwy-svg').innerHTML = r.available ? runwaySvg(r) : `<p class="muted small ps-empty">${t('No runway data for {0} yet: OpenStreetMap is being asked, try again in a minute.', esc(r.icao))}</p>`;
    $('#ps-rwy-list').innerHTML = r.runways.map(x => `
      <button type="button" class="ps-rwy-chip ${x.ident === r.selected ? 'on' : ''} ${x.usable ? '' : 'short'}" data-rwy="${esc(x.ident)}"
        title="${t('{0} m, true heading {1}°', num(x.lengthM), pad3(x.headingTrue))}">
        <b>${esc(x.ident)}</b><span class="${x.headwindKt < -0.5 ? 'bad' : 'good'}">${x.headwindKt >= 0 ? '↓' : '↑'}${Math.abs(Math.round(x.headwindKt))}</span>
        <span>⇄${Math.abs(Math.round(x.crosswindKt))}</span><small>${num(x.lengthM)} m</small>
      </button>`).join('');
  }
  $('#ps-rwy-tabs').addEventListener('click', e => {
    const b = e.target.closest('button'); if (!b || !current) return;
    rwyTab = b.dataset.tab; renderRunways(current);
  });
  $('#ps-rwy-list').addEventListener('click', e => {
    const b = e.target.closest('[data-rwy]'); if (!b) return;
    if (rwyTab === 'dep') depRwy = b.dataset.rwy; else arrRwy = b.dataset.rwy;
    plan();
  });

  function runwaySvg(r) {
    const W = 340, H = 200, P = 26;
    const all = r.lines.flat();
    if (!all.length) return '';
    const lat0 = all.reduce((s, q) => s + q[0], 0) / all.length, k = Math.cos(lat0 * RAD);
    const xy = q => [q[1] * k, -q[0]];
    const ps = all.map(xy);
    const minX = Math.min(...ps.map(q => q[0])), maxX = Math.max(...ps.map(q => q[0]));
    const minY = Math.min(...ps.map(q => q[1])), maxY = Math.max(...ps.map(q => q[1]));
    const s = Math.min((W - 2 * P) / Math.max(maxX - minX, 1e-6), (H - 2 * P) / Math.max(maxY - minY, 1e-6));
    const ox = (W - (maxX - minX) * s) / 2, oy = (H - (maxY - minY) * s) / 2;
    const T = q => { const [x, y] = xy(q); return [ox + (x - minX) * s, oy + (y - minY) * s]; };
    // A field of small arrows blowing with the wind, like a windsock chart.
    let field = '';
    if (r.windDir != null && r.windKt > 0.5 && !r.windVariable) {
      const rot = (r.windDir + 180) % 360;
      for (let y = 16; y < H; y += 30) for (let x = 14 + (y / 30 % 2) * 15; x < W; x += 30)
        field += `<path d="M0 -6 L0 6 M-3 3 L0 6 L3 3" transform="translate(${x} ${y}) rotate(${rot})"/>`;
    }
    const sel = r.runways.find(x => x.ident === r.selected);
    const lines = r.lines.map(l => `<polyline points="${l.map(q => T(q).join(',')).join(' ')}"/>`).join('');
    let selLine = '', tags = '';
    if (sel) {
      const [x1, y1] = T(sel.threshold), [x2, y2] = T(sel.end);
      selLine = `<line class="sel" x1="${x1}" y1="${y1}" x2="${x2}" y2="${y2}"/><circle class="sel" cx="${x1}" cy="${y1}" r="4"/>`;
    }
    const placed = [];
    for (const x of r.runways) {
      const [x1, y1] = T(x.threshold), [x2, y2] = T(x.end);
      const len = Math.hypot(x2 - x1, y2 - y1) || 1;
      const ux = (x2 - x1) / len, uy = (y2 - y1) / len;
      let lx = x1 - ux * 16, ly = y1 - uy * 16;
      // Parallel runways put their numbers side by side: move a tag sideways until it is clear of the others.
      for (let k = 1; k < 6 && placed.some(([px, py]) => Math.abs(px - lx) < 32 && Math.abs(py - ly) < 20); k++) {
        const side = k % 2 ? 1 : -1, step = Math.ceil(k / 2) * 20;
        lx = x1 - ux * 16 - uy * step * side; ly = y1 - uy * 16 + ux * step * side;
      }
      placed.push([lx, ly]);
      tags += `<g class="tag ${x.ident === r.selected ? 'on' : ''}" transform="translate(${lx} ${ly})"><rect x="-15" y="-9" width="30" height="18" rx="5"/><text y="4">${esc(x.ident)}</text></g>`;
    }
    return `<svg viewBox="0 0 ${W} ${H}" role="img" aria-label="${t('Runways')}"><g class="field">${field}</g><g class="rwys">${lines}</g>${selLine}${tags}</svg>`;
  }

  // Weather: METAR and TAF, as sent or in words.
  let wxKind = store.get('wx-kind', 'metar'), wxRaw = store.get('wx-raw', 'raw');
  function renderWeather(p) {
    const list = [p.departureWeather, p.destinationWeather, p.alternateWeather].filter(Boolean);
    const role = [t('Departure'), t('Destination'), t('Alternate')];
    $$('#ps-wx-kind button').forEach(b => b.classList.toggle('on', b.dataset.v === wxKind));
    $$('#ps-wx-raw button').forEach(b => b.classList.toggle('on', b.dataset.v === wxRaw));
    $('#ps-wx-list').innerHTML = list.map((x, i) => {
      const raw = wxKind === 'metar' ? x.metar : x.taf;
      const words = wxKind === 'metar' ? (ru ? x.metarRu : x.metarEn) : (ru ? x.tafRu : x.tafEn);
      const text = wxRaw === 'raw' ? raw : words || raw;
      const cat = x.category ? `<span class="ps-cat ${x.category.toLowerCase()}">${esc(x.category)}</span>` : '';
      return `<div class="ps-wx">
        <div class="ps-wx-head"><b>${esc(x.icao)}</b><span class="muted small">${role[i]}</span>${cat}<span class="muted small">${x.observedAt ? hhmm(x.observedAt) : ''}</span></div>
        <div class="${wxRaw === 'raw' ? 'mono' : ''} ps-wx-text">${text ? esc(text) : `<span class="muted">${wxKind === 'metar' ? t('no METAR') : t('no TAF')}</span>`}</div>
      </div>`;
    }).join('');
  }
  $('#ps-wx-kind').addEventListener('click', e => { const b = e.target.closest('button'); if (!b) return; wxKind = b.dataset.v; store.set('wx-kind', wxKind); current && renderWeather(current); });
  $('#ps-wx-raw').addEventListener('click', e => { const b = e.target.closest('button'); if (!b) return; wxRaw = b.dataset.v; store.set('wx-raw', wxRaw); current && renderWeather(current); });

  // Load: fuel at a glance and the weights against their limits.
  function renderLoad(p) {
    const f = p.fuel, m = p.weights;
    $('#ps-ac-name').textContent = p.aircraftName;
    $$('#ps-units button').forEach(b => b.classList.toggle('on', b.dataset.v === unit));
    const cap = f.tankCapacityKg || 0;
    $('#ps-fuel').innerHTML = `
      <div class="ps-fuel-total"><small>${t('Block fuel')}</small><b>${w(f.blockKg)}</b><span>${unit}</span></div>
      <dl class="ps-fuel-grid">
        <div><dt><i class="dot trip"></i>${t('Trip')}</dt><dd>${w(f.tripKg)}</dd></div>
        <div><dt><i class="dot res"></i>${t('Reserves')}</dt><dd>${w(f.contingencyKg + f.alternateKg + f.finalReserveKg + f.extraKg)}</dd></div>
        <div><dt>${t('Air time')}</dt><dd>${hm(p.airMin)}</dd></div>
        <div><dt>${t('Distance')}</dt><dd>${num(p.distanceNm)} nm</dd></div>
      </dl>
      ${cap ? `<div class="ps-tank" title="${t('Tanks: {0} of {1}', w(f.blockKg), w(cap))}"><i class="trip" style="width:${Math.min(100, f.tripKg / cap * 100)}%"></i><i class="res" style="width:${Math.min(100, Math.max(0, (f.blockKg - f.tripKg) / cap * 100))}%"></i></div>` : ''}`;
    const bar = (code, label, v, max) => {
      const pct = max ? v / max * 100 : 0;
      const cls = pct > 100 ? 'over' : pct >= 98 ? 'near' : '';
      return `<div class="ps-bar ${cls}">
        <div class="ps-bar-head"><span class="code">${code}</span><span>${label}</span>
          ${cls ? `<span class="pct">⚠ ${Math.round(pct)}%</span>` : ''}<b>${w(v)}</b><span class="muted">/ ${w(max)}</span></div>
        <div class="ps-bar-track"><i style="width:${Math.min(pct, 100)}%"></i></div></div>`;
    };
    $('#ps-weights').innerHTML = `
      <div class="ps-kv"><span>${t('Empty aircraft')}</span><b>${w(m.emptyKg)}</b><span>${t('Payload')}</span><b>${w(m.payloadKg)}</b></div>
      ${bar('ZFW', t('Zero fuel'), m.zeroFuelKg, m.maxZeroFuelKg)}
      ${bar('TOW', t('Take-off'), m.takeoffKg, m.maxTakeoffKg)}
      ${bar('LW', t('Landing'), m.landingKg, m.maxLandingKg)}`;
    $('#ps-warnings').innerHTML = p.warnings.map(x => `<li>${esc(ru ? x.ru : x.en)}</li>`).join('');
    $$('.ps-unit').forEach(el => { el.textContent = unit; });
  }
  $('#ps-units').addEventListener('click', e => {
    const b = e.target.closest('button'); if (!b || b.dataset.v === unit) return;
    // The mass fields keep their meaning: convert what is typed.
    const k = b.dataset.v === 'lb' ? 2.20462 : 1 / 2.20462;
    for (const f of ['bag', 'cargo', 'extra']) { const el = $('#ps-' + f); if (el.value) el.value = Math.round(Number(el.value) * k); }
    unit = b.dataset.v; store.set('unit', unit);
    if (current) { renderLoad(current); renderNavlog(current); renderFuel(current); renderSummary(current); renderOfp(current); }
  });

  // Route: the string, the variants, what to do with it.
  function renderRoute(p) {
    const route = p.route.split(' ').map((tok, i, a) => {
      if (i === 0 || i === a.length - 1) return `<b>${esc(tok)}</b>`;
      if (/^[A-Z]{1,2}\d{1,4}[A-Z]?$/.test(tok)) return `<span class="awy">${esc(tok)}</span>`;
      if (tok === 'DCT') return `<span class="muted">${tok}</span>`;
      return esc(tok);
    }).join(' ');
    $('#ps-route-text').innerHTML = route;
    $('#ps-variants').innerHTML = p.routes.map((r, i) => `
      <button type="button" class="${i === p.variant ? 'on' : ''}" data-v="${i}">
        ${i === 0 ? t('Basic') : t('Variant {0}', i)}<small>${num(r.distanceNm)} nm</small></button>`).join('');
    const rte = p.fieldRoute === 'DCT' ? '' : p.fieldRoute;
    $('#ps-simbrief').href = 'https://dispatch.simbrief.com/options/custom?' + new URLSearchParams({
      orig: p.departure, dest: p.destination, type: p.aircraftIcao, route: rte, ...(p.alternate ? { altn: p.alternate } : {}),
      fl: String(p.cruiseLevel * 100), cruise: 'CI', civalue: String(p.costIndex) });
    $('#ps-file').href = '/flightplan?' + new URLSearchParams({
      dep: p.departure, dest: p.destination, type: p.aircraftIcao, route: rte, level: 'FL' + p.cruiseLevel,
      alt: p.alternate || '', speed: 'N' + String(p.cruiseTasKt).padStart(4, '0') });
    $('#ps-chart-dep').href = `https://chartfox.org/${encodeURIComponent(p.departure)}`;
    $('#ps-chart-dest').href = `https://chartfox.org/${encodeURIComponent(p.destination)}`;
    $('#ps-chart-dep').textContent = p.departure;
    $('#ps-chart-dest').textContent = p.destination;
  }
  $('#ps-variants').addEventListener('click', e => {
    const b = e.target.closest('button'); if (!b) return;
    variant = Number(b.dataset.v); plan();
  });
  $('#ps-copy').addEventListener('click', async () => {
    if (!current) return;
    try { await navigator.clipboard.writeText(current.route); flash($('#ps-copy'), t('Copied')); } catch { /* no clipboard */ }
  });
  $('#ps-share').addEventListener('click', async () => {
    try { await navigator.clipboard.writeText(location.href); flash($('#ps-share'), t('Link copied')); } catch { /* no clipboard */ }
  });
  function flash(el, text) { const old = el.textContent; el.textContent = text; setTimeout(() => { el.textContent = old; }, 1400); }

  // Navigation log: every fix with course, distance, level, wind, speeds, times and fuel.
  function renderNavlog(p) {
    const rows = p.navLog.map((r, i) => `
      <tr class="${r.kind}">
        <td><b>${esc(r.ident)}</b></td><td>${esc(r.airway || (i ? 'DCT' : ''))}</td>
        <td>${i ? pad3(r.courseTrue) : ''}</td><td>${i ? r.legNm.toFixed(0) : ''}</td><td>${num(r.cumNm)}</td><td>${num(r.remainingNm)}</td>
        <td>${r.kind === 'apt' ? '' : fl(r.altitudeFt)}</td>
        <td>${r.windDir != null ? pad3(r.windDir) + '/' + String(r.windKt).padStart(2, '0') : ''}</td>
        <td>${r.tempC != null ? r.tempC : ''}</td><td>${r.isaDev != null ? (r.isaDev > 0 ? 'P' : r.isaDev < 0 ? 'M' : '') + Math.abs(r.isaDev) : ''}</td>
        <td>${i ? r.tasKt : ''}</td><td>${i ? r.gsKt : ''}</td>
        <td>${i ? hm(r.legMin) : ''}</td><td>${hm(r.cumMin)}</td><td>${hhmm(r.eta)}</td>
        <td>${i ? w(r.legFuelKg) : ''}</td><td>${w(r.fuelUsedKg)}</td><td><b>${w(r.fuelRemainingKg)}</b></td>
      </tr>`).join('');
    $('#ps-navlog').innerHTML = `<table class="ps-table">
      <thead><tr><th>${t('Fix')}</th><th>${t('Airway')}</th><th>${t('Crs')}</th><th>${t('Leg')}</th><th>${t('Dist')}</th><th>${t('To go')}</th>
        <th>${t('Level')}</th><th>${t('Wind')}</th><th>°C</th><th>ISA</th><th>TAS</th><th>GS</th>
        <th>${t('Leg time')}</th><th>${t('Time')}</th><th>ETA</th><th>${t('Leg fuel')}</th><th>${t('Used')}</th><th>${t('Remaining')}</th></tr></thead>
      <tbody>${rows}</tbody></table>`;
  }

  // ICAO fuel, item by item.
  function renderFuel(p) {
    const f = p.fuel;
    const row = (name, note, kg, min, cls = '') => `<tr class="${cls}"><td>${name}</td><td class="muted">${note}</td><td>${min == null ? '' : hm(min)}</td><td><b>${w(kg)}</b></td></tr>`;
    $('#ps-fueltab').innerHTML = `<table class="ps-table ps-fuel-table">
      <thead><tr><th>${t('Item')}</th><th></th><th>${t('Time')}</th><th>${unit}</th></tr></thead><tbody>
      ${row(t('Taxi'), t('15 min'), f.taxiKg, 15)}
      ${row(t('Trip'), `${esc(p.departure)} → ${esc(p.destination)}, FL${p.cruiseLevel}`, f.tripKg, f.tripMin)}
      ${row(t('Contingency'), f.contingencyRule === '5%' ? t('5 % of trip') : t('5 min holding (more than 5 %)'), f.contingencyKg, f.contingencyMin)}
      ${row(t('Alternate'), p.alternate ? esc(p.destination) + ' → ' + esc(p.alternate) : t('none'), f.alternateKg, f.alternateMin)}
      ${row(t('Final reserve'), t('30 min holding at 1500 ft'), f.finalReserveKg, f.finalReserveMin)}
      ${row(t('Minimum take-off fuel'), '', f.minimumTakeoffKg, null, 'sub')}
      ${row(t('Extra'), t('at the captain\'s discretion'), f.extraKg, f.extraMin)}
      ${row(t('Block fuel'), t('taxi included'), f.blockKg, null, 'total')}
      ${row(t('Landing fuel'), t('at {0}', esc(p.destination)), f.landingKg, null, 'sub')}
      </tbody></table>`;
  }

  // Summary, the way an OFP starts: the flight and the plan in two grids.
  function renderSummary(p) {
    const cell = (k, v) => `<div><dt>${k}</dt><dd>${v}</dd></div>`;
    const wind = p.avgWindComponentKt >= 0 ? 'P' + String(Math.round(p.avgWindComponentKt)).padStart(3, '0') : 'M' + String(Math.round(-p.avgWindComponentKt)).padStart(3, '0');
    const isa = (p.avgIsaDev >= 0 ? 'P' : 'M') + String(Math.round(Math.abs(p.avgIsaDev))).padStart(2, '0');
    $('#ps-summary').innerHTML = `
      <h4>${t('Flight')}</h4><dl class="ps-grid">
        ${cell(t('Callsign'), esc(p.callsign || '—'))}${cell(t('Departure'), esc(p.departure))}${cell(t('Destination'), esc(p.destination))}
        ${cell(t('Alternate'), esc(p.alternate || '—'))}${cell(t('Aircraft type'), esc(p.aircraftIcao))}${cell(t('Date'), p.offBlock.substring(0, 10))}
        ${cell(t('Off block'), hhmm(p.offBlock))}${cell(t('Take-off'), hhmm(p.takeoff))}${cell(t('Landing'), hhmm(p.landing))}
        ${cell(t('On block'), hhmm(p.onBlock))}${cell(t('Air time'), hm(p.airMin))}${cell(t('Block time'), hm(p.blockMin))}
      </dl>
      <h4>${t('Plan')}</h4><dl class="ps-grid">
        ${cell(t('Cruise level'), 'FL' + p.cruiseLevel)}${cell(t('Cost index'), p.costIndex)}${cell(t('Cruise speed'), `M${p.cruiseMach.toFixed(2).substring(1)} / ${p.cruiseTasKt} kt`)}
        ${cell(t('Route distance'), num(p.distanceNm) + ' nm')}${cell(t('Great circle'), num(p.greatCircleNm) + ' nm')}
        ${cell(t('Average wind'), p.avgWindDir != null ? pad3(p.avgWindDir) + '/' + p.avgWindKt : '—')}
        ${cell(t('Wind component'), wind)}${cell(t('ISA deviation'), isa)}${cell('TOC / TOD', `${num(p.tocNm)} / ${num(p.todNm)} nm`)}
        ${cell('AIRAC', esc(p.airac || '—'))}${cell(t('Units'), unit.toUpperCase())}${cell(t('Winds aloft'), p.windsAloftAvailable ? 'Open-Meteo GFS' : t('none (calm, ISA)'))}
      </dl>
      <h4>${t('Load sheet')}</h4><dl class="ps-grid">
        ${cell(t('Trip fuel'), w(p.fuel.tripKg))}${cell(t('Passengers'), p.weights.pax)}${cell(t('Empty weight'), w(p.weights.emptyKg))}
        ${cell(t('Baggage'), w(p.weights.baggageKg))}${cell(t('Cargo'), w(p.weights.cargoKg))}${cell(t('Payload'), w(p.weights.payloadKg))}
        ${cell('ZFW', w(p.weights.zeroFuelKg))}${cell('TOW', w(p.weights.takeoffKg))}${cell('LW', w(p.weights.landingKg))}
        ${cell('Max ZFW', w(p.weights.maxZeroFuelKg))}${cell('Max TOW', w(p.weights.maxTakeoffKg))}${cell('Max LW', w(p.weights.maxLandingKg))}
      </dl>`;
  }

  // Operational flight plan as text, for printing (PDF) — the whole plan on paper.
  function renderOfp(p) {
    const pad = (s, n) => String(s ?? '').padEnd(n).substring(0, n);
    const lpad = (s, n) => String(s ?? '').padStart(n).substring(0, n);
    const f = p.fuel, m = p.weights;
    const L = [];
    L.push(`SKYNETWORK OFP  ${p.callsign || ''}  ${p.departure}-${p.destination}  ${p.aircraftIcao}  ${p.offBlock.substring(0, 10)}  AIRAC ${p.airac || '-'}`);
    L.push(t('For the simulator only. Not for real-world navigation.'));
    L.push('');
    L.push(`OFF BLK ${hhmm(p.offBlock)}  T/O ${hhmm(p.takeoff)}  LDG ${hhmm(p.landing)}  ON BLK ${hhmm(p.onBlock)}  AIR ${hm(p.airMin)}  BLOCK ${hm(p.blockMin)}`);
    L.push(`FL${p.cruiseLevel}  CI ${p.costIndex}  M${p.cruiseMach.toFixed(2).substring(1)}  TAS ${p.cruiseTasKt}  DIST ${num(p.distanceNm)} NM  GC ${num(p.greatCircleNm)} NM  ALTN ${p.alternate || '-'}`);
    L.push('');
    L.push(`FUEL (${unit.toUpperCase()})`);
    const fr = (n, v, min) => L.push(`  ${pad(n, 22)}${lpad(w(v), 9)}  ${min == null ? '' : hm(min)}`);
    fr('TAXI', f.taxiKg, 15); fr('TRIP', f.tripKg, f.tripMin); fr('CONT ' + f.contingencyRule, f.contingencyKg, f.contingencyMin);
    fr('ALTN ' + (p.alternate || ''), f.alternateKg, f.alternateMin); fr('FINAL RESERVE', f.finalReserveKg, 30);
    fr('MIN TAKEOFF FUEL', f.minimumTakeoffKg); fr('EXTRA', f.extraKg, f.extraMin); fr('BLOCK FUEL', f.blockKg); fr('LANDING FUEL', f.landingKg);
    L.push('');
    L.push(`WEIGHTS (${unit.toUpperCase()})  PAX ${m.pax}  PAYLOAD ${w(m.payloadKg)}`);
    L.push(`  ZFW ${lpad(w(m.zeroFuelKg), 9)}  MAX ${lpad(w(m.maxZeroFuelKg), 9)}`);
    L.push(`  TOW ${lpad(w(m.takeoffKg), 9)}  MAX ${lpad(w(m.maxTakeoffKg), 9)}`);
    L.push(`  LW  ${lpad(w(m.landingKg), 9)}  MAX ${lpad(w(m.maxLandingKg), 9)}`);
    L.push('');
    L.push('ROUTE');
    L.push('  ' + p.route);
    L.push('');
    L.push('FIX      AWY     CRS  LEG   DIST  ALT    WIND    OAT  TAS  GS   TIME   ETA    FUEL REM');
    for (const r of p.navLog)
      L.push(`${pad(r.ident, 9)}${pad(r.airway, 8)}${lpad(r.legNm ? pad3(r.courseTrue) : '', 3)}  ${lpad(r.legNm ? Math.round(r.legNm) : '', 4)}  ${lpad(num(r.cumNm), 5)}  ${pad(r.kind === 'apt' ? '' : fl(r.altitudeFt), 6)} ${pad(r.windDir != null ? pad3(r.windDir) + '/' + r.windKt : '', 7)} ${lpad(r.tempC ?? '', 4)}  ${lpad(r.tasKt || '', 3)}  ${lpad(r.gsKt || '', 3)}  ${lpad(hm(r.cumMin), 5)}  ${pad(hhmm(r.eta), 6)} ${lpad(w(r.fuelRemainingKg), 8)}`);
    L.push('');
    for (const x of [p.departureWeather, p.destinationWeather, p.alternateWeather].filter(Boolean)) {
      L.push(`WX ${x.icao}`);
      if (x.metar) L.push('  ' + x.metar);
      if (x.taf) L.push('  ' + x.taf);
    }
    if (p.warnings.length) { L.push(''); L.push(t('Warnings')); for (const x of p.warnings) L.push('  - ' + (ru ? x.ru : x.en)); }
    $('#ps-ofp').textContent = L.join('\n');
  }
  $('#ps-pdf').addEventListener('click', () => { if (current) window.print(); });

  // ── exports ──────────────────────────────────────────────────────────────
  function download(name, text, type) {
    const a = document.createElement('a');
    a.href = URL.createObjectURL(new Blob([text], { type }));
    a.download = name; document.body.appendChild(a); a.click();
    setTimeout(() => { URL.revokeObjectURL(a.href); a.remove(); }, 1000);
  }
  const fixes = p => p.navLog.filter(r => r.kind !== 'toc' && r.kind !== 'tod');
  function dms(v, pos, neg) {
    const h = v >= 0 ? pos : neg; v = Math.abs(v);
    const d = Math.floor(v), mf = (v - d) * 60, mm = Math.floor(mf), s = ((mf - mm) * 60).toFixed(2);
    return `${h}${d}° ${mm}' ${s}"`;
  }
  const lla = (lat, lon, alt) => `${dms(lat, 'N', 'S')},${dms(lon, 'E', 'W')},${alt >= 0 ? '+' : '-'}${String(Math.abs(alt).toFixed(2)).padStart(9, '0')}`;
  const xml = s => esc(s);
  function pln(p) {
    const f = fixes(p), dep = f[0], dest = f[f.length - 1];
    const wps = f.map((r, i) => {
      const type = i === 0 || i === f.length - 1 ? 'Airport' : 'Intersection';
      return `        <ATCWaypoint id="${xml(r.ident)}">
            <ATCWaypointType>${type}</ATCWaypointType>
            <WorldPosition>${lla(r.lat, r.lon, r.altitudeFt)}</WorldPosition>${r.airway ? `\n            <ATCAirway>${xml(r.airway)}</ATCAirway>` : ''}
            <ICAO>
                <ICAOIdent>${xml(r.ident)}</ICAOIdent>
            </ICAO>
        </ATCWaypoint>`;
    }).join('\n');
    return `<?xml version="1.0" encoding="UTF-8"?>
<SimBase.Document Type="AceXML" version="1,0">
    <Descr>AceXML Document</Descr>
    <FlightPlan.FlightPlan>
        <Title>${xml(p.departure)} to ${xml(p.destination)}</Title>
        <FPType>IFR</FPType>
        <RouteType>HighAlt</RouteType>
        <CruisingAlt>${p.cruiseLevel * 100}</CruisingAlt>
        <DepartureID>${xml(p.departure)}</DepartureID>
        <DepartureLLA>${lla(dep.lat, dep.lon, 0)}</DepartureLLA>
        <DestinationID>${xml(p.destination)}</DestinationID>
        <DestinationLLA>${lla(dest.lat, dest.lon, 0)}</DestinationLLA>
        <Descr>${xml(p.departure)}, ${xml(p.destination)} (SkyNetwork planner)</Descr>${p.departureRunways.selected ? `\n        <DeparturePosition>${xml(p.departureRunways.selected)}</DeparturePosition>` : ''}
        <DepartureName>${xml(p.departureAirport.name)}</DepartureName>
        <DestinationName>${xml(p.destinationAirport.name)}</DestinationName>
        <AppVersion>
            <AppVersionMajor>11</AppVersionMajor>
            <AppVersionBuild>282174</AppVersionBuild>
        </AppVersion>
${wps}
    </FlightPlan.FlightPlan>
</SimBase.Document>
`;
  }
  function fms(p) {
    const f = fixes(p);
    const lines = ['I', '1100 Version', `CYCLE ${p.airac || '2609'}`, `ADEP ${p.departure}`];
    if (p.departureRunways.selected) lines.push(`DEPRWY RW${p.departureRunways.selected}`);
    lines.push(`ADES ${p.destination}`);
    if (p.arrivalRunways.selected) lines.push(`DESRWY RW${p.arrivalRunways.selected}`);
    lines.push(`NUMENR ${f.length}`);
    f.forEach((r, i) => {
      const first = i === 0, last = i === f.length - 1;
      const code = first || last ? 1 : 11;
      const via = first ? 'ADEP' : last ? 'ADES' : r.airway || 'DRCT';
      lines.push(`${code} ${r.ident} ${via} ${(first || last ? 0 : r.altitudeFt).toFixed(6)} ${r.lat.toFixed(6)} ${r.lon.toFixed(6)}`);
    });
    return lines.join('\n') + '\n';
  }
  $('#ps-export').addEventListener('click', e => {
    const b = e.target.closest('[data-fmt]'); if (!b || !current) return;
    const p = current, name = `${p.departure}${p.destination}`;
    if (b.dataset.fmt === 'pln') download(name + '.pln', pln(p), 'application/xml');
    if (b.dataset.fmt === 'fms') download(name + '.fms', fms(p), 'text/plain');
    if (b.dataset.fmt === 'txt') download(name + '.txt', $('#ps-ofp').textContent, 'text/plain');
    b.closest('details')?.removeAttribute('open');
  });

  // ── panels ───────────────────────────────────────────────────────────────
  $('#ps-tabs').addEventListener('click', e => {
    const b = e.target.closest('button[data-tab]'); if (!b) return;
    $$('#ps-tabs button[data-tab]').forEach(x => x.classList.toggle('on', x === b));
    $$('.ps-tab').forEach(x => { x.hidden = x.id !== 'ps-tab-' + b.dataset.tab; });
    $('.ps-bottom').classList.remove('collapsed');
    store.set('tab', b.dataset.tab);
    if (current) drawMap(current);
  });
  $('#ps-collapse').addEventListener('click', () => { $('.ps-bottom').classList.toggle('collapsed'); if (current) drawMap(current); });
  const savedTab = store.get('tab', 'route');
  $(`#ps-tabs button[data-tab="${savedTab}"]`)?.click();

  // Phone: one panel at a time from the dock.
  $('#ps-dock').addEventListener('click', e => {
    const b = e.target.closest('button[data-panel]'); if (!b) return;
    const target = document.getElementById(b.dataset.panel);
    if (b.dataset.panel === 'ps-new') { $('.ps-flight').open = true; }
    target?.scrollIntoView({ behavior: 'smooth', block: 'start' });
  });
  $('#ps-new-btn').addEventListener('click', () => { $('.ps-flight').open = true; $('#ps-dep').focus(); });

  // ── form ─────────────────────────────────────────────────────────────────
  $('#ps-form').addEventListener('submit', e => { e.preventDefault(); variant = 0; depRwy = ''; arrRwy = ''; plan(); });
  // Load changes are applied at once (a short pause after typing).
  let timer = 0;
  $('#ps-load-form').addEventListener('input', () => { clearTimeout(timer); timer = setTimeout(() => current && plan(), 700); });
  $('#ps-load-form').addEventListener('submit', e => { e.preventDefault(); plan(); });
  for (const id of ['ps-dep', 'ps-dest']) $('#' + id).addEventListener('change', () => { variant = 0; depRwy = ''; arrRwy = ''; });

  // From the address: /planner?dep=UUEE&dest=ULLI&type=A320 plans at once.
  const params = new URLSearchParams(location.search);
  const aliases = { departure: 'dep', destination: 'dest', aircrafttype: 'type', alternate: 'alt' };
  for (const [k, v] of params) {
    const f = aliases[k.toLowerCase()] || k.toLowerCase();
    const el = $('#ps-' + f);
    if (!el || !v) continue;
    if (f === 'time') { const d = new Date(v); if (!isNaN(d)) el.value = new Date(d - d.getTimezoneOffset() * 60000).toISOString().substring(0, 16); }
    else el.value = v;
  }
  variant = Number(params.get('variant')) || 0;
  depRwy = params.get('deprwy') || ''; arrRwy = params.get('arrrwy') || '';
  if (unit === 'lb') $$('.ps-unit').forEach(el => { el.textContent = unit; });
  if ($('#ps-dep').value && $('#ps-dest').value) plan(); else $('.ps-flight').open = true;
})();
