// Status page: the panel refreshes itself every half minute while the page is in view, the list of problems can be
// narrowed down, and the commands that fix a problem are copied with one click.
(() => {
  const panel = document.querySelector('[data-status-panel]');
  if (!panel) return;
  let filter = 'all';

  const applyFilter = () => {
    panel.querySelectorAll('[data-problems] .problem-row').forEach(row => {
      const kinds = (row.dataset.kind || '').split(' ');
      row.hidden = filter !== 'all' && !kinds.includes(filter);
    });
    const radio = panel.querySelector(`[data-problems] input[value="${filter}"]`);
    if (radio) radio.checked = true;
  };

  panel.addEventListener('change', e => {
    if (e.target.name !== 'problems') return;
    filter = e.target.value;
    applyFilter();
  });

  panel.addEventListener('click', async e => {
    const button = e.target.closest('[data-copy]');
    if (!button) return;
    try {
      await navigator.clipboard.writeText(button.dataset.copy);
      const label = button.textContent;
      button.textContent = button.dataset.copied;
      setTimeout(() => { button.textContent = label; }, 1600);
    } catch {
      // No clipboard (an old browser or plain http): the command stays on the page to select by hand.
    }
  });

  const refresh = async () => {
    if (document.hidden) return;
    // Details opened by the reader stay open after the refresh.
    const open = [...panel.querySelectorAll('.problem-row details[open]')]
      .map(d => d.closest('.problem-row').querySelector('.problem-message').textContent);
    try {
      const r = await fetch(location.pathname + '?handler=Panel', { credentials: 'same-origin' });
      if (!r.ok) return;
      panel.innerHTML = await r.text();
      panel.querySelectorAll('.problem-row').forEach(row => {
        if (open.includes(row.querySelector('.problem-message').textContent)) row.querySelector('details')?.setAttribute('open', '');
      });
      applyFilter();
    } catch {
      // Offline for a moment: the next refresh tries again.
    }
  };

  setInterval(refresh, 30000);
  document.addEventListener('visibilitychange', () => { if (!document.hidden) refresh(); });
  applyFilter();
})();
