import { connect } from './mcp-list.mjs';
const c = await connect();
const r = await c.callTool({ name: 're-audit', arguments: { auditId: 3, refresh: false } });
console.log(JSON.stringify(r).slice(0, 500));
const id = /"auditId\?"?\s*:\s*(\d+)/.exec(JSON.stringify(r))?.[1] ?? /(\d+)/.exec(r.content[0].text)?.[1];
for (let i = 0; i < 90; i++) { const p = await c.callTool({ name: 'get-audit-progress', arguments: { auditId: Number(id) } }); const t = p.content[0].text; if (!/Running|Queued/i.test(t) || i === 89) { console.log(t.slice(0, 400)); break; } await new Promise(r => setTimeout(r, 2000)); }
await c.close(); process.exit(0);
