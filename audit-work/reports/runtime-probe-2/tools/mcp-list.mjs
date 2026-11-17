import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';
import fs from 'node:fs';
const ep = JSON.parse(fs.readFileSync(process.env.APPDATA + '/CrawlForge/mcp-endpoint.json', 'utf8'));
export async function connect() {
  const transport = new StdioClientTransport({ command: 'node', args: ['E:/Crawlforge/app/src/mcp-stdio-adapter/dist/index.js', '--endpoint', ep.endpoint, '--token', ep.token], stderr: 'pipe' });
  const client = new Client({ name: 'runtime-probe', version: '0.0.1' });
  await client.connect(transport);
  return client;
}
if (process.argv[1].endsWith('mcp-list.mjs')) {
  const c = await connect();
  const out = { server: c.getServerVersion(), capabilities: c.getServerCapabilities() };
  for (const [k, f] of [['tools', () => c.listTools()], ['resources', () => c.listResources()], ['resourceTemplates', () => c.listResourceTemplates()], ['prompts', () => c.listPrompts()]]) {
    try { out[k] = await f(); } catch (e) { out[k] = { error: e.message }; }
  }
  fs.writeFileSync('../logs/mcp-list.json', JSON.stringify(out, null, 2));
  console.log(JSON.stringify(out, null, 1).slice(0, 12000));
  await c.close();
}
