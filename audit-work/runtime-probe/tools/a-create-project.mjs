// Fill the New project dialog (must be open) and submit; record network calls it makes.
export default async (h, [name, url]) => {
  const p = h.page;
  const calls = [];
  p.on('request', r => { if (r.url().includes('/api/')) calls.push(`${r.method()} ${r.url().replace(/^https?:\/\/[^/]+/, '')} ${r.postData() ?? ''}`.slice(0, 400)); });
  await p.getByPlaceholder('Example Corp — production site').fill(name);
  await p.getByPlaceholder('https://example.com').fill(url);
  await p.getByPlaceholder('What this project covers').fill('Runtime probe against fixture site');
  await h.shot('UI-01-new-project-filled');
  await p.locator('button').filter({ hasText: /^Create project$/ }).click();
  await p.waitForTimeout(2500);
  await h.shot('UI-01-after-create');
  return { calls, ...(await h.dump()) };
};
