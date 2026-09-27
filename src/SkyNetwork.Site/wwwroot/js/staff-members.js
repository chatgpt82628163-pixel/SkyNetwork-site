// Staff member list: "tick all", the number of ticked members, and the suspend and delete buttons waiting for a tick.
(() => {
  const bar = document.querySelector('[data-bulk]');
  const all = document.querySelector('[data-bulk-all]');
  if (!bar || !all) return;
  const count = bar.querySelector('[data-bulk-count]');
  const go = [...bar.querySelectorAll('[data-bulk-go]')];
  const boxes = () => [...document.querySelectorAll('input[name=cids]')];

  const update = () => {
    const list = boxes(), ticked = list.filter(b => b.checked).length;
    count.textContent = count.dataset.template.replace('{0}', ticked);
    for (const b of go) b.disabled = ticked === 0;
    all.checked = ticked > 0 && ticked === list.length;
    all.indeterminate = ticked > 0 && ticked < list.length;
  };
  all.addEventListener('change', () => {
    for (const b of boxes()) b.checked = all.checked;
    update();
  });
  document.addEventListener('change', e => {
    if (e.target.name === 'cids') update();
  });
  update();
})();
