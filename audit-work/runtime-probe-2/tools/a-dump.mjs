export default async (h, [name]) => {
  if (name) await h.shot(name);
  return { pages: h.ctx.pages().map(p => p.url()), ...(await h.dump()) };
};
