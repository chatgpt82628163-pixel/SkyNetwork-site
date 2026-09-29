// Home page: the "online now" figures follow the network every 15 s while the page is open.
(function () {
  const counts = document.querySelectorAll('[data-live-count]');
  if (!counts.length) return;
  const fmt = n => Number(n).toLocaleString(document.documentElement.lang || 'en');
  async function refresh() {
    if (document.hidden) return;
    try {
      const r = await fetch('/api/v1/online', { cache: 'no-store' });
      if (!r.ok) return;
      const d = await r.json();
      if (!d.available) return;
      const value = {
        pilots: d.pilots.length,
        controllers: d.controllers.filter(c => c.facility !== 'OBS' && c.facility !== 'ATIS').length,
      };
      counts.forEach(el => { el.textContent = fmt(value[el.dataset.liveCount]); });
      const dot = document.querySelector('.ph-live .dot');
      if (dot) dot.classList.toggle('off', value.pilots + value.controllers === 0);
    } catch { }
  }
  setInterval(refresh, 15000);
  document.addEventListener('visibilitychange', () => { if (!document.hidden) refresh(); });
})();
