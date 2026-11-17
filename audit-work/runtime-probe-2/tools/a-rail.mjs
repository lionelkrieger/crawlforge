// Click a control by visible text / aria-label and dump the view.
export default async (h, [label, shotName]) => {
  const p = h.page;
  const sel = 'button,[role=button],[role=tab],a,[role=menuitem],[role=link],li';
  let loc = p.locator(`[aria-label="${label}"]`).first();
  if (!(await loc.count())) loc = p.locator(sel).filter({ hasText: new RegExp(`^\\s*${label.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\s*$`) }).first();
  await loc.click({ timeout: 5000 });
  await p.waitForTimeout(1500);
  if (shotName) await h.shot(shotName);
  return await h.dump();
};
