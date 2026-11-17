// Poll an audit until terminal state; log state transitions and counters.
import { api } from './rest.mjs';
import fs from 'node:fs';
const id = process.argv[2]; const maxSec = Number(process.argv[3] ?? 900);
const t0 = Date.now(); let last = ''; const log = [];
while (true) {
  const r = await api('GET', `/api/audits/${id}`);
  const a = r.body?.data ?? r.body;
  const sig = JSON.stringify({ state: a?.state, phase: a?.phase });
  const elapsed = ((Date.now() - t0) / 1000).toFixed(1);
  if (sig !== last) { log.push({ t: elapsed, ...JSON.parse(sig), counters: a?.counters ?? a?.progress ?? null }); console.log(elapsed, sig, JSON.stringify(a?.counters ?? a?.progress ?? '')); last = sig; }
  if (['completed', 'failed', 'cancelled', 'canceled'].includes(String(a?.state).toLowerCase())) break;
  if ((Date.now() - t0) / 1000 > maxSec) { console.log('TIMEOUT'); log.push({ t: elapsed, timeout: true }); break; }
  await new Promise(r => setTimeout(r, 1000));
}
const final = await api('GET', `/api/audits/${id}`);
fs.writeFileSync(`../logs/audit-${id}-poll.json`, JSON.stringify({ log, final: final.body }, null, 2));
console.log(JSON.stringify(final.body, null, 2));
