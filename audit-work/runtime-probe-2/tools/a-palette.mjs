export default async (h, [query]) => {
  const p = h.page;
  await p.keyboard.press('Control+K');
  await p.waitForTimeout(700);
  if (query) { await p.keyboard.type(query); await p.waitForTimeout(500); }
  await h.shot('palette' + (query ? '-' + query.replace(/\W+/g, '_') : ''));
  const items = await p.evaluate(() => [...document.querySelectorAll('[role=option],[role=listbox] li,[cmdk-item],[role=dialog] button,[role=dialog] li')].map(e => e.textContent.trim().replace(/\s+/g, ' ')));
  const d = await h.dump();
  await p.keyboard.press('Escape');
  return { items, text: d.text.slice(d.text.indexOf('Search'), 200) };
};
