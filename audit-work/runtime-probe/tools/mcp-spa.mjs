import { connect } from './mcp-list.mjs';
const c = await connect();
for (const render of [false, true]) {
  try { const r = await c.callTool({ name: 'analyze-single-page', arguments: { url: 'http://127.0.0.1:47311/pages/js-content.html', render } });
    const t = r.content.map(x => x.text).join(''); console.log('render', render, 'isError', r.isError, t.length, t.slice(0, 700)); }
  catch (e) { console.log('render', render, 'ERR', e.message); }
}
try { const r = await c.readResource({ uri: 'crawlforge://audits' }); console.log('audit-list', r.contents[0].text.slice(0, 600)); } catch (e) { console.log('audit-list ERR', e.message); }
await c.close(); process.exit(0);
