// REST helper using the backend's loopback handshake descriptor (same discovery as the shell).
// Usage: node rest.mjs METHOD /api/path [jsonBody]
import fs from 'node:fs';
const hs = JSON.parse(fs.readFileSync(process.env.APPDATA + '/CrawlForge/loopback-handshake.json', 'utf8'));
export const base = `http://127.0.0.1:${hs.port}`;
export async function api(method, path, body) {
  const r = await fetch(base + path, {
    method,
    headers: { 'X-CrawlForge-Session': hs.sessionToken, 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : (typeof body === 'string' ? body : JSON.stringify(body)),
  });
  const t = await r.text();
  let j; try { j = JSON.parse(t); } catch { j = t; }
  return { status: r.status, body: j };
}
if (process.argv[1] && process.argv[1].endsWith('rest.mjs') && process.argv[2]) {
  const r = await api(process.argv[2], process.argv[3], process.argv[4]);
  console.log(r.status); console.log(JSON.stringify(r.body, null, 2));
}
