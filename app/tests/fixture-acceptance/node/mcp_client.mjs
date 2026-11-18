// WP-08-04 fixture-acceptance harness: MCP client over the product's stdio
// adapter, using the workspace-hoisted @modelcontextprotocol/sdk (resolved by
// walking up from this file to E:\Crawlforge\app\node_modules — NO install).
//
// Usage:
//   node mcp_client.mjs list
//   node mcp_client.mjs flow <projectId> <configurationId> [maxSec]
//   node mcp_client.mjs reaudit <auditId>
//   node mcp_client.mjs call <toolName> <jsonArgs>
//   node mcp_client.mjs read <resourceUri>
//
// The adapter does not read mcp-endpoint.json itself; this helper reads the
// descriptor and passes --endpoint/--token explicitly (CODEMAP §7).
// Output: one JSON document on stdout. Exit 0 on protocol success (tool-level
// errors are recorded in the document, not as exit failures).

import fs from 'node:fs';
import path from 'node:path';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';

const ADAPTER = 'E:/Crawlforge/app/src/mcp-stdio-adapter/dist/index.js';
const descriptorPath = path.join(process.env.APPDATA, 'CrawlForge', 'mcp-endpoint.json');

async function connect() {
  if (!fs.existsSync(descriptorPath)) throw new Error('mcp-endpoint.json not found — is the backend running? ' + descriptorPath);
  const ep = JSON.parse(fs.readFileSync(descriptorPath, 'utf8'));
  const transport = new StdioClientTransport({
    command: process.execPath,
    args: [ADAPTER, '--endpoint', ep.endpoint, '--token', ep.token],
    stderr: 'pipe'
  });
  const client = new Client({ name: 'fixture-acceptance-harness', version: '1.0.0' });
  await client.connect(transport);
  return { client, endpoint: ep.endpoint };
}

const txt = (r) => (r?.content ?? []).map(x => x.text ?? '').join('\n');

async function main() {
  const [cmd, ...args] = process.argv.slice(2);
  const { client, endpoint } = await connect();
  const out = { cmd, endpoint, steps: [] };
  const step = (name, v) => out.steps.push({ name, v });
  try {
    if (cmd === 'list') {
      out.server = client.getServerVersion();
      out.capabilities = client.getServerCapabilities();
      for (const [k, f] of [['tools', () => client.listTools()], ['resources', () => client.listResources()], ['resourceTemplates', () => client.listResourceTemplates()], ['prompts', () => client.listPrompts()]]) {
        try { out[k] = await f(); } catch (e) { out[k] = { error: String(e.message ?? e) }; }
      }
    } else if (cmd === 'flow') {
      const [projectId, configurationId, maxSec = '400'] = args;
      let start;
      try {
        start = await client.callTool({ name: 'start-audit', arguments: { projectId: Number(projectId), configurationId: Number(configurationId) } });
        step('start-audit', start);
      } catch (e) { step('start-audit-error', String(e.message ?? e)); }
      let auditId;
      try { const j = JSON.parse(txt(start)); auditId = j.auditId ?? j.data?.auditId; } catch {}
      if (!auditId) { const m = /"?auditId"?\s*[:=]\s*"?(\d+)/.exec(txt(start) ?? ''); auditId = m?.[1]; }
      out.auditId = auditId ? String(auditId) : null;
      step('auditId', out.auditId);
      const t0 = Date.now(); let last = '';
      while (auditId) {
        let p;
        try { p = await client.callTool({ name: 'get-audit-progress', arguments: { auditId: Number(auditId) } }); }
        catch (e) { step('progress-error', String(e.message ?? e)); break; }
        const s = txt(p) ?? '';
        const st = /"(?:state|status)"\s*:\s*"(\w+)"/i.exec(s)?.[1] ?? '?';
        if (st !== last) { step('progress ' + ((Date.now() - t0) / 1000).toFixed(1) + 's', s.slice(0, 1200)); last = st; }
        if (/completed|failed|cancel/i.test(st)) break;
        if ((Date.now() - t0) / 1000 > Number(maxSec)) { step('timeout', s.slice(0, 400)); break; }
        await new Promise(r => setTimeout(r, 2000));
      }
      for (const r of ['summary', 'high-severity-findings']) {
        try {
          const v = await client.readResource({ uri: `crawlforge://audits/${auditId}/${r}` });
          step('read ' + r, (v.contents ?? []).map(x => x.text ?? '').join('').slice(0, 6000));
        } catch (e) { step('read ' + r + ' error', String(e.message ?? e)); }
      }
      try {
        const v = await client.getPrompt({ name: 'audit-health-briefing', arguments: { auditId: String(auditId) } });
        step('prompt audit-health-briefing', JSON.stringify(v).slice(0, 2000));
      } catch (e) { step('prompt error', String(e.message ?? e)); }
    } else if (cmd === 'reaudit') {
      try {
        const v = await client.callTool({ name: 're-audit', arguments: { auditId: Number(args[0]) } });
        step('re-audit', v);
      } catch (e) { step('re-audit-error', String(e.message ?? e)); }
    } else if (cmd === 'call') {
      let a = {}; try { a = JSON.parse(args[1] ?? '{}'); } catch {}
      try { step(args[0], await client.callTool({ name: args[0], arguments: a })); }
      catch (e) { step(args[0] + '-error', String(e.message ?? e)); }
    } else if (cmd === 'read') {
      try { step(args[0], await client.readResource({ uri: args[0] })); }
      catch (e) { step(args[0] + '-error', String(e.message ?? e)); }
    } else {
      throw new Error('unknown command: ' + cmd);
    }
  } finally {
    try { await client.close(); } catch {}
  }
  console.log(JSON.stringify(out, null, 1));
  setTimeout(() => process.exit(0), 100);
}

main().catch(e => { console.error(JSON.stringify({ fatal: String(e.message ?? e) })); process.exit(1); });