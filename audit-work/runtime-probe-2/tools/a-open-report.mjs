export default async (h, [auditId, shot]) => {
  const p = h.page;
  const calls = [];
  p.on('request', r => { if (r.url().includes('/api/')) calls.push(`${r.method()} ${r.url().replace(/^https?:\/\/[^/]+/, '')}`); });
  const row = p.locator('tr').filter({ has: p.locator('td').first().filter({ hasText: new RegExp('^\\s*' + auditId + '\\s*$') }) }).first();
  await row.locator('button').filter({ hasText: /Open/ }).click({ timeout: 5000 });
  await p.waitForTimeout(3500);
  if (shot) await h.shot(shot);
  return { calls, pages: h.ctx.pages().map(x => x.url()), ...(await h.dump()) };
};
