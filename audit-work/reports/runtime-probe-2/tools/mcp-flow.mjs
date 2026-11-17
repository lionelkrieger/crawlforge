// MCP delegated flow: start-audit -> poll get-audit-progress -> read audit-summary + high-severity-findings.
import { connect } from './mcp-list.mjs';
import fs from 'node:fs';
const [projectId, configurationId, maxSec = '700', tag = 'mcp'] = process.argv.slice(2);
const c = await connect();
const out = { steps: [] };
const step = (name, v) => { out.steps.push({ name, v }); console.log('##', name, JSON.stringify(v).slice(0, 1500)); };
const txt = r => r?.content?.map(x => x.text).join('\n');
let start;
try { start = await c.callTool({ name: 'start-audit', arguments: { projectId: Number(projectId), configurationId: Number(configurationId) } }); step('start-audit', start); }
catch (e) { step('start-audit-error', e.message); }
let auditId; try { auditId = JSON.parse(txt(start)).auditId ?? JSON.parse(txt(start)).data?.auditId; } catch {}
if (!auditId) { const m = /"?auditId"?\s*[:=]\s*"?(\d+)/.exec(txt(start) ?? ''); auditId = m?.[1]; }
step('auditId', auditId);
const t0 = Date.now(); let last = '';
while (auditId) {
  let p; try { p = await c.callTool({ name: 'get-audit-progress', arguments: { auditId: Number(auditId) } }); } catch (e) { step('progress-error', e.message); break; }
  const s = txt(p) ?? '';
  const st = /"(?:state|status)"\s*:\s*"(\w+)"/i.exec(s)?.[1] ?? '?';
  if (st !== last) { step('progress ' + ((Date.now() - t0) / 1000).toFixed(1) + 's', s.slice(0, 600)); last = st; }
  if (/completed|failed|cancel/i.test(st)) break;
  if ((Date.now() - t0) / 1000 > Number(maxSec)) { step('timeout', s.slice(0, 300)); break; }
  await new Promise(r => setTimeout(r, 2000));
}
for (const r of ['summary', 'high-severity-findings']) {
  try { const v = await c.readResource({ uri: `crawlforge://audits/${auditId}/${r}` }); step('read ' + r, v.contents?.map(x => x.text).join('').slice(0, 4000)); }
  catch (e) { step('read ' + r + ' error', e.message); }
}
try { const v = await c.getPrompt({ name: 'audit-health-briefing', arguments: { auditId: String(auditId) } }); step('prompt audit-health-briefing', JSON.stringify(v).slice(0, 1500)); } catch (e) { step('prompt error', e.message); }
fs.writeFileSync(`../logs/mcp-flow-${tag}.json`, JSON.stringify(out, null, 2));
await c.close();
process.exit(0);
