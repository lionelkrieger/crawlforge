// Visit each standalone tool; screenshot; record its text.
export default async (h) => {
  const p = h.page;
  // caller navigates to Tools first
  await p.waitForTimeout(800);
  const names = ['Single-Page Analyzer', 'SERP & Social Preview', 'Render Tester + Picker', 'Robots.txt Tester', 'Sitemap Generator', 'Content Scope Preview', 'Rewrite & Host Mapping', 'llms.txt Generate / Validate', 'URL Check', 'Structured Data Checker', 'Query Builder', 'Export Destinations'];
  const out = {};
  for (const n of names) {
    try {
      await p.locator('button').filter({ hasText: n }).first().click({ timeout: 3000 });
      await p.waitForTimeout(700);
      const f = await h.shot('UI-19-tool-' + n.replace(/\W+/g, '_'));
      const txt = await p.evaluate(() => { const m = document.querySelector('main') || document.body; return m.innerText; });
      const i = txt.indexOf('Export Destinations');
      out[n] = { shot: f, text: txt.slice(i + 25, i + 900).replace(/\n+/g, ' | ') };
    } catch (e) { out[n] = { error: e.message.split('\n')[0] }; }
  }
  return out;
};
