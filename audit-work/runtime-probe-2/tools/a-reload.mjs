export default async (h, [shot]) => { await h.page.reload(); await h.page.waitForTimeout(4000); if (shot) await h.shot(shot); const d = await h.dump(); return d.text.slice(0, 600); };
