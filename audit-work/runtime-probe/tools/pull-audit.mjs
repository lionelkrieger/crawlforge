// Pull all URLs, links, findings, summary, scores, hosts for an audit; plus URL details for trap URLs.
import { api } from './rest.mjs';
import fs from 'node:fs';
const id = process.argv[2];
async function all(path) {
  const rows = []; let offset = 0; const limit = 200;
  for (;;) {
    const r = await api('GET', `${path}${path.includes('?') ? '&' : '?'}offset=${offset}&limit=${limit}`);
    if (r.status !== 200) return { status: r.status, body: r.body, rows };
    const d = r.body.data ?? []; rows.push(...d);
    const total = r.body.pagination?.total ?? d.length;
    offset += limit; if (offset >= total || d.length === 0) break;
  }
  return rows;
}
const out = { id };
out.audit = (await api('GET', `/api/audits/${id}`)).body;
out.summary = (await api('GET', `/api/audits/${id}/summary`)).body;
out.scores = (await api('GET', `/api/audits/${id}/scores`)).body;
out.hosts = (await api('GET', `/api/audits/${id}/hosts`)).body;
out.urls = await all(`/api/audits/${id}/urls`);
out.links = await all(`/api/audits/${id}/links`);
out.findings = await all(`/api/audits/${id}/findings`);
out.fixtureRequests = await (await fetch('http://127.0.0.1:47311/__fixture/requests')).json();
const traps = JSON.parse(fs.readFileSync('E:/Crawlforge/app/tests/fixture-site/expected-results.json', 'utf8')).traps;
const trapUrls = [...new Set(traps.flatMap(t => t.urls))];
out.trapUrlDetails = {};
const urlRows = Array.isArray(out.urls) ? out.urls : out.urls.rows;
for (const u of trapUrls) {
  const full = 'http://127.0.0.1:47311' + u;
  const row = urlRows.find(r => (r.url ?? r.normalizedUrl) === full) ?? urlRows.find(r => (r.url ?? '').startsWith(full));
  out.trapUrlDetails[u] = row ? (await api('GET', `/api/audits/${id}/urls/${row.id}`)).body : null;
}
fs.writeFileSync(`../logs/audit-${id}-data.json`, JSON.stringify(out, null, 1));
const f = Array.isArray(out.findings) ? out.findings : out.findings.rows;
const byRule = {}; for (const x of f) byRule[x.ruleKey] = (byRule[x.ruleKey] ?? 0) + 1;
console.log(JSON.stringify({ urls: urlRows.length, links: (Array.isArray(out.links) ? out.links : out.links.rows).length, findings: f.length, byRule, fixture: { nonGet: out.fixtureRequests.nonGet.length, tagHits: out.fixtureRequests.tagHits.length }, sampleUrl: urlRows[0] }, null, 1));
