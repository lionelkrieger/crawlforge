// Click the project row by name, then optionally its Start audit button; capture API calls.
export default async (h, [name, mode, shot]) => {
  const p = h.page;
  const calls = [];
  p.on('request', r => { if (r.url().includes('/api/')) calls.push(`${r.method()} ${r.url().replace(/^https?:\/\/[^/]+/, '')} ${r.postData() ?? ''}`.slice(0, 300)); });
  const row = p.locator('tr').filter({ hasText: name }).first();
  if (mode === 'start') await row.locator('button').filter({ hasText: 'Start audit' }).click();
  else if (mode === 'dbl') await row.dblclick();
  else await row.click();
  await p.waitForTimeout(3000);
  if (shot) await h.shot(shot);
  return { calls, pages: h.ctx.pages().map(x => x.url()), ...(await h.dump()) };
};
