// Pull URL details for each expected trap URL through the REST explorer.
import { api } from './rest.mjs';
import fs from 'node:fs';

const auditId = process.argv[2];
const source = JSON.parse(fs.readFileSync(`../logs/audit-${auditId}-data.json`, 'utf8'));
const traps = JSON.parse(fs.readFileSync('E:/Crawlforge/app/tests/fixture-site/expected-results.json', 'utf8')).traps;
const trapUrls = [...new Set(traps.flatMap(t => t.urls))];
const out = {};
for (const path of trapUrls) {
  const full = 'http://127.0.0.1:47311' + path;
  const row = source.urls.find(u => u.canonicalUrl === full || String(u.canonicalUrl || '').startsWith(full));
  if (!row) { out[path] = { found: false }; continue; }
  const detail = await api('GET', `/api/audits/${auditId}/urls/${row.id}`);
  out[path] = { found: true, summary: row, detail: detail.body?.data ?? detail.body };
}
fs.writeFileSync(`../logs/audit-${auditId}-trap-details.json`, JSON.stringify(out, null, 1));
console.log(JSON.stringify(Object.fromEntries(Object.entries(out).map(([k, v]) => [k, { found: v.found, keys: v.detail ? Object.keys(v.detail) : [] }])), null, 1));
