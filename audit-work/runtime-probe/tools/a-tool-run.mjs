// Open a tool, fill its first visible text input in the tool panel, click an action button, capture result + API calls.
export default async (h, [tool, url, action, shot, waitMs]) => {
  const p = h.page;
  const calls = [];
  p.on('response', async r => { if (r.url().includes('/api/')) { let b = ''; try { b = (await r.text()).slice(0, 600); } catch {} calls.push(`${r.request().method()} ${r.url().replace(/^https?:\/\/[^/]+/, '')} -> ${r.status()} ${b}`); } });
  await p.locator('button').filter({ hasText: tool }).first().click({ timeout: 3000 });
  await p.waitForTimeout(600);
  const inputs = p.locator('main input[type=text], main input:not([type]), main input[type=url]');
  const n = await inputs.count();
  let filled = false;
  for (let i = 0; i < n; i++) { const el = inputs.nth(i); if (await el.isVisible()) { await el.fill(url); filled = true; break; } }
  await p.locator('main button').filter({ hasText: new RegExp('^\\s*' + action + '\\s*$') }).first().click({ timeout: 3000 });
  await p.waitForTimeout(Number(waitMs ?? 6000));
  await h.shot(shot);
  const txt = await p.evaluate(() => (document.querySelector('main') || document.body).innerText);
  const i = txt.indexOf('Export Destinations');
  return { filled, calls, text: txt.slice(i + 25, i + 2500).replace(/\n+/g, ' | ') };
};
