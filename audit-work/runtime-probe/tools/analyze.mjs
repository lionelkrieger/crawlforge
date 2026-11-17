import fs from 'node:fs';
const id = process.argv[2];
const d = JSON.parse(fs.readFileSync(`../logs/audit-${id}-data.json`, 'utf8'));
const urls = d.urls, links = d.links, f = d.findings;
const B = 'http://127.0.0.1:47311';
const show = (label, x) => console.log('=== ' + label + '\n' + JSON.stringify(x, null, 0).slice(0, 1500));
const byUrl = u => urls.filter(r => (r.canonicalUrl ?? '').startsWith(B + u));
const fBy = (urlIdSet) => f.filter(x => urlIdSet.has(String(x.urlId))).map(x => x.ruleKey + ':' + x.severity + (x.message ? ' ' + x.message.slice(0, 90) : ''));
const pat = process.argv[3] ? new RegExp(process.argv[3]) : null;
const traps = JSON.parse(fs.readFileSync('E:/Crawlforge/app/tests/fixture-site/expected-results.json', 'utf8')).traps;
for (const t of traps) {
  if (pat && !pat.test(t.id)) continue;
  for (const u of t.urls) {
    const rows = byUrl(u).slice(0, 4);
    const ids = new Set(rows.map(r => String(r.id)));
    show(t.id + ' ' + u, { rows: rows.map(r => ({ id: r.id, u: r.canonicalUrl, s: r.statusCode, idx: r.indexable, b: r.blockReason, t: r.title })), findings: [...new Set(fBy(ids).filter(k => !/color-contrast|landmark-main|content.metrics|mobile\.|indexability.indexable|canonical.missing/.test(k)))] , matchCount: byUrl(u).length });
  }
}
