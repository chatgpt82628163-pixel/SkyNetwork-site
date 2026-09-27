// Home page: reveal-on-scroll, numbers that count up and roll when they change, live counts, the radar sweep and
// voice waveform, and the sphere that leans towards the pointer. Safe without JS: nothing here hides content.
(function () {
  const root = document.documentElement;
  const motion = root.classList.contains('motion');   // set by the inline head snippet (no reduced motion, observer present)
  const fmt = n => Number(n).toLocaleString(root.lang || 'en');

  // ---- reveals ----
  const reveals = [...document.querySelectorAll('[data-reveal]')];
  const shown = new Set();
  const show = el => {
    if (shown.has(el)) return;
    shown.add(el);
    el.classList.add('in');
    el.dispatchEvent(new CustomEvent('reveal'));
    // After the transition the attribute goes, so the element's own hover transitions apply again.
    const i = parseInt(el.style.getPropertyValue('--i')) || 0;
    setTimeout(() => el.removeAttribute('data-reveal'), 650 + i * 70);
  };
  const showAll = () => reveals.forEach(show);
  if (motion && reveals.length) {
    // A group staggers its children 70 ms apart; its value is a base offset.
    document.querySelectorAll('[data-reveal-group]').forEach(g => {
      const base = parseInt(g.getAttribute('data-reveal-group')) || 0;
      [...g.children].forEach((c, i) => { if (c.hasAttribute('data-reveal')) c.style.setProperty('--i', Math.min(base + i, 8)); });
    });
    const io = new IntersectionObserver(entries => {
      entries.forEach(e => { if (e.isIntersecting) { show(e.target); io.unobserve(e.target); } });
    }, { rootMargin: '0px 0px -10% 0px', threshold: 0 });
    reveals.forEach(el => io.observe(el));
    // Belt and braces: what is already on screen shows after a slow load; everything shows on a bfcache restore or when leaving.
    setTimeout(() => reveals.forEach(el => { if (el.getBoundingClientRect().top < innerHeight) show(el); }), 3000);
    addEventListener('pageshow', e => { if (e.persisted) showAll(); });
    addEventListener('pagehide', showAll);
  } else {
    showAll();
  }

  // ---- numbers count up when they come into view (fast at first, settling at the value) ----
  if (motion) {
    document.querySelectorAll('.hm-stat').forEach(cell => {
      const b = cell.querySelector('[data-count]');
      const target = Number(b?.dataset.count);
      if (!b || !(target > 0)) return;
      b.textContent = fmt(0);
      cell.addEventListener('reveal', () => {
        const start = performance.now(), dur = Math.min(1600, 700 + Math.log10(target + 1) * 260);
        const tick = now => {
          const k = Math.min(1, (now - start) / dur), e = 1 - Math.pow(1 - k, 4);
          b.textContent = fmt(Math.round(target * e));
          if (k < 1) requestAnimationFrame(tick);
        };
        requestAnimationFrame(tick);
      }, { once: true });
    });
  }

  // ---- a changed value rolls: the old one leaves upwards, the new one comes up from below ----
  function roll(el, text) {
    if (el.textContent === text) return;
    if (!motion) { el.textContent = text; return; }
    el.classList.add('hm-roll');
    el.classList.remove('in');
    el.classList.add('out');
    setTimeout(() => {
      el.textContent = text;
      el.classList.remove('out');
      void el.offsetWidth;
      el.classList.add('in');
    }, 220);
  }

  // ---- live counts: who is on the network, every 15 s ----
  const liveCounts = document.querySelectorAll('[data-live-count]');
  const liveCalls = document.querySelector('[data-live-calls]');
  async function refresh() {
    if (document.hidden) return;
    try {
      const r = await fetch('/api/v1/online', { cache: 'no-store' });
      if (!r.ok) return;
      const d = await r.json();
      if (!d.available) return;
      const onPosition = d.controllers.filter(c => c.facility !== 'OBS' && c.facility !== 'ATIS');
      const counts = { pilots: d.pilots.length, controllers: onPosition.length };
      liveCounts.forEach(el => roll(el, fmt(counts[el.dataset.liveCount])));
      if (liveCalls && onPosition.length) roll(liveCalls, onPosition.slice(0, 4).map(c => c.callsign).join(' · '));
    } catch { }
  }
  if (liveCounts.length) {
    setInterval(refresh, 15000);
    document.addEventListener('visibilitychange', () => { if (!document.hidden) refresh(); });
  }

  if (!motion) return;

  // ---- the radar turns only while it is on screen ----
  const radar = document.querySelector('.hm-radar');
  if (radar) new IntersectionObserver(([e]) => radar.classList.toggle('on', e.isIntersecting)).observe(radar);

  // ---- the voice card "speaks" once when it appears, and again on hover ----
  const voice = document.querySelector('.hm-card-sky');
  if (voice) {
    const talk = () => {
      voice.classList.remove('talk');
      void voice.offsetWidth;
      voice.classList.add('talk');
    };
    voice.addEventListener('reveal', () => setTimeout(talk, 400), { once: true });
    voice.addEventListener('pointerenter', talk);
    voice.addEventListener('animationend', e => { if (e.target === voice.querySelector('.hm-wave i:last-child')) voice.classList.remove('talk'); });
  }

  // ---- the sphere leans a little towards the pointer, on a critically damped spring ----
  const sphere = document.querySelector('[data-tilt]');
  if (sphere && matchMedia('(hover: hover) and (pointer: fine)').matches) {
    const hero = sphere.closest('.hm-hero');
    let tx = 0, ty = 0, x = 0, y = 0, vx = 0, vy = 0, frame = 0, last = 0;
    const MAX = 12;                       // px at most
    const step = now => {
      const dt = Math.min(0.032, (now - (last || now)) / 1000 || 0.016);
      last = now;
      // Spring with a 0.5 s response and damping 1 (no overshoot).
      const w = 2 * Math.PI / 0.5, k = w * w, c = 2 * w;
      vx += (k * (tx - x) - c * vx) * dt; x += vx * dt;
      vy += (k * (ty - y) - c * vy) * dt; y += vy * dt;
      sphere.style.setProperty('--tx', x.toFixed(2) + 'px');
      sphere.style.setProperty('--ty', y.toFixed(2) + 'px');
      if (Math.abs(tx - x) + Math.abs(ty - y) + Math.abs(vx) + Math.abs(vy) > 0.05) frame = requestAnimationFrame(step);
      else { frame = 0; last = 0; }
    };
    const kick = () => { if (!frame) frame = requestAnimationFrame(step); };
    hero.addEventListener('pointermove', e => {
      const r = sphere.getBoundingClientRect();
      const dx = (e.clientX - (r.left + r.width / 2)) / innerWidth, dy = (e.clientY - (r.top + r.height / 2)) / innerHeight;
      tx = Math.max(-1, Math.min(1, dx * 2)) * MAX;
      ty = Math.max(-1, Math.min(1, dy * 2)) * MAX;
      kick();
    });
    hero.addEventListener('pointerleave', () => { tx = 0; ty = 0; kick(); });
  }
})();
