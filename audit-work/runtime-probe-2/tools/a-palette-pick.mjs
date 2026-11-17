export default async (h, [query, shot]) => {
  const p = h.page;
  await p.keyboard.press('Control+K');
  await p.waitForTimeout(600);
  await p.keyboard.type(query);
  await p.waitForTimeout(500);
  await p.keyboard.press('Enter');
  await p.waitForTimeout(2000);
  if (shot) await h.shot(shot);
  return { pages: h.ctx.pages().map(x => x.url()), ...(await h.dump()) };
};
