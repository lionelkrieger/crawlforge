#!/usr/bin/env node
// CrawlForge fixture site server (architecture 07-quality-and-verification/03-fixture-site.md, CR-015).
// Dependency-free. Usage: node server.mjs [--port 47311] [--host 127.0.0.1] [--tls-cert cert.pem --tls-key key.pem]
// Recorder: GET /__fixture/requests returns every non-GET request and every tag hit; GET /__fixture/reset clears it.
import http from 'node:http';
import https from 'node:https';
import crypto from 'node:crypto';
import fs from 'node:fs';
import { PAGES, SPA_SHELL, VIEWSTATE_LARGE, page } from './pages.mjs';

const arg = (n, d) => { const i = process.argv.indexOf(n); return i > -1 ? process.argv[i + 1] : d; };
const PORT = Number(arg('--port', 47311)), HOST = arg('--host', '127.0.0.1');
const log = { nonGet: [], tagHits: [] };
const PIXEL = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==', 'base64');

const send = (res, status, body, type = 'text/html; charset=utf-8', headers = {}) => {
  res.writeHead(status, { 'Content-Type': type, ...headers }); res.end(body);
};
const redirect = (res, status, to, headers = {}) => send(res, status, '', 'text/plain', { Location: to, ...headers });

function trapFilter(url) {
  const p = url.searchParams, opts = { color: ['red', 'blue', 'green'], size: ['s', 'm', 'l'], sort: ['price', 'name'], page: ['1', '2', '3'] };
  const links = [];
  for (const [k, vals] of Object.entries(opts)) for (const v of vals) { const q = new URLSearchParams(p); q.set(k, v); links.push(`<a href="/trap/filter?${q}">${k}=${v}</a>`); }
  return page({ title: 'Product filter', body: `<h1>Products</h1><p>The same eight products in a different order.</p>${links.join(' ')}` });
}
function trapCalendar(y, m) {
  const n = new Date(Date.UTC(y, m, 1)), pv = new Date(Date.UTC(y, m - 2, 1));
  const f = (d) => `/trap/calendar/${d.getUTCFullYear()}/${d.getUTCMonth() + 1}`;
  return page({ title: `Events ${y}-${m}`, body: `<h1>Events calendar</h1><p>No events this month.</p><a href="${f(pv)}">Previous month</a> <a href="${f(n)}">Next month</a>` });
}
const SITEMAP = ['/', '/pages/orphan.html', '/pages/redirect-target.html', '/pages/noindex.html', '/redirect/chain-1', '/spa/', '/hash-app/', '/live/odds.html']
  .map((p) => `<url><loc>http://${HOST}:${PORT}${p}</loc></url>`).join('');

function handler(req, res) {
  const url = new URL(req.url, `http://${req.headers.host || HOST}`), path = url.pathname, cookies = req.headers.cookie || '';
  if (req.method !== 'GET' && req.method !== 'HEAD') log.nonGet.push({ at: new Date().toISOString(), method: req.method, path: url.pathname + url.search });
  if (path === '/__fixture/requests') return send(res, 200, JSON.stringify(log, null, 2), 'application/json');
  if (path === '/__fixture/reset') { log.nonGet = []; log.tagHits = []; return send(res, 200, '{"reset":true}', 'application/json'); }
  if (path.startsWith('/api/')) return send(res, 200, '{"ok":true}', 'application/json');
  if (path === '/robots.txt') return send(res, 200, `User-agent: *\nDisallow: /private/\nSitemap: http://${HOST}:${PORT}/sitemap.xml\n`, 'text/plain');
  if (path === '/sitemap.xml') return send(res, 200, `<?xml version="1.0" encoding="UTF-8"?><urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">${SITEMAP}</urlset>`, 'application/xml');
  if (path === '/static/pixel.png') return send(res, 200, PIXEL, 'image/png');
  if (path === '/static/fake-tag.js') return send(res, 200, "(new Image()).src='/collect?ev=page_view&consent='+(document.cookie.indexOf('fixture_consent=granted')>-1?'granted':'none');", 'application/javascript');
  if (path === '/collect') { log.tagHits.push({ at: new Date().toISOString(), query: url.search }); return send(res, 204, ''); }
  if (path === '/sw-app/sw.js') return send(res, 200, "self.addEventListener('fetch', function(){});", 'application/javascript');
  if (path === '/redirect/chain-1') return redirect(res, 301, '/redirect/chain-2');
  if (path === '/redirect/chain-2') return redirect(res, 302, '/redirect/chain-3');
  if (path === '/redirect/chain-3') return redirect(res, 301, '/pages/redirect-target.html');
  if (path === '/redirect/loop-a') return redirect(res, 301, '/redirect/loop-b');
  if (path === '/redirect/loop-b') return redirect(res, 301, '/redirect/loop-a');
  if (path === '/cookie-gate/') {
    if (!/fixture_gate=1/.test(cookies)) return redirect(res, 302, '/cookie-gate/', { 'Set-Cookie': 'fixture_gate=1; Path=/' });
    return send(res, 200, page({ title: 'Cookie gate passed', body: '<h1>Cookie gate passed</h1><p>Reachable only when cookies are kept between requests.</p>' }));
  }
  if (path === '/trap/filter') return send(res, 200, trapFilter(url));
  const cal = path.match(/^\/trap\/calendar\/(\d{4})\/(\d{1,2})$/);
  if (cal) return send(res, 200, trapCalendar(Number(cal[1]), Number(cal[2])));
  if (/^\/\([SAF]\([^)]*\)\)\/pages\/session\.aspx$/.test(path)) return send(res, 200, PAGES['/pages/session.aspx']);
  if (path === '/pages/viewstate-large.aspx') return send(res, 200, VIEWSTATE_LARGE);
  if (path.startsWith('/spa/')) return send(res, 200, SPA_SHELL);
  if (path === '/pages/canonical-conflict.html') return send(res, 200, PAGES[path], undefined, { Link: '</pages/canonical-target-header.html>; rel="canonical"' });
  if (PAGES[path]) return send(res, 200, PAGES[path]);
  return send(res, 404, page({ title: 'Not found', body: '<h1>404</h1>' }));
}

function upgrade(req, socket) {
  if (!req.url.startsWith('/ws/odds')) return socket.destroy();
  const accept = crypto.createHash('sha1').update(req.headers['sec-websocket-key'] + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64');
  socket.write(`HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ${accept}\r\n\r\n`);
  const timer = setInterval(() => {
    const msg = Buffer.from(`${(1 + Math.random() * 3).toFixed(2)} - ${(1 + Math.random() * 3).toFixed(2)}`);
    socket.write(Buffer.concat([Buffer.from([0x81, msg.length]), msg]));
  }, 300);
  socket.on('close', () => clearInterval(timer)); socket.on('error', () => clearInterval(timer));
}

const cert = arg('--tls-cert'), key = arg('--tls-key');
const server = cert && key ? https.createServer({ cert: fs.readFileSync(cert), key: fs.readFileSync(key) }, handler) : http.createServer(handler);
server.on('upgrade', upgrade);
server.listen(PORT, HOST, () => console.log(`fixture site on ${cert ? 'https' : 'http'}://${HOST}:${PORT}`));
