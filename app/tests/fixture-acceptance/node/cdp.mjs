// WP-08-04 fixture-acceptance harness: raw CDP client over Node's built-in
// WebSocket + fetch (Node >= 22; no dependencies, no playwright).
//
// Usage:
//   node cdp.mjs --port 9223 --serve [--target route=hub]
//       Persistent JSON-lines server on stdin/stdout. Each stdin line is
//       {"id":1,"cmd":"eval","expression":"..."}; each stdout line is
//       {"id":1,"ok":true,"result":...} or {"id":1,"ok":false,"error":"..."}.
//       Closing stdin drops the CDP socket WITHOUT closing the app.
//   node cdp.mjs --port 9223 <cmd> [jsonArgs]
//       One-shot: run one command, print its JSON result, exit.
//
// Commands: ping, targets, dump, eval, shot, click, fill, key, type,
//           insertText, wait, waitFor, count, events, navigate, reload,
//           closeBrowser.
//
// This helper only OBSERVES and DRIVES the real renderer through the same
// input events a user produces; it never patches product state.

import fs from 'node:fs';
import readline from 'node:readline';

const arg = (n, d) => { const i = process.argv.indexOf(n); return i > -1 ? process.argv[i + 1] : d; };
const PORT = Number(arg('--port', 9222));
const SERVE = process.argv.includes('--serve');
const TARGET_HINT = arg('--target', 'index.html');
const BASE = `http://127.0.0.1:${PORT}`;

async function httpJson(path) {
  const r = await fetch(BASE + path, { signal: AbortSignal.timeout(5000) });
  return r.json();
}

async function pickPage() {
  const list = await httpJson('/json/list');
  const pages = list.filter(t => t.type === 'page' && t.webSocketDebuggerUrl);
  if (pages.length === 0) throw new Error('no page targets on CDP port ' + PORT);
  return pages.find(p => p.url.includes('route=hub'))
      ?? pages.find(p => p.url.includes(TARGET_HINT))
      ?? pages[0];
}

class Cdp {
  constructor(ws) {
    this.ws = ws; this.nextId = 1; this.pending = new Map();
    this.events = { console: [], exceptions: [], requests: [], responses: [] };
    ws.onmessage = (m) => {
      const msg = JSON.parse(typeof m.data === 'string' ? m.data : m.data.toString());
      if (msg.id !== undefined && this.pending.has(msg.id)) {
        const { resolve, reject, timer } = this.pending.get(msg.id);
        this.pending.delete(msg.id); clearTimeout(timer);
        if (msg.error) reject(new Error(msg.error.message)); else resolve(msg.result);
      } else if (msg.method) {
        this.onEvent(msg.method, msg.params ?? {});
      }
    };
  }
  static async connect() {
    const page = await pickPage();
    const ws = new WebSocket(page.webSocketDebuggerUrl);
    await new Promise((res, rej) => {
      const t = setTimeout(() => rej(new Error('CDP WS connect timeout')), 10000);
      ws.onopen = () => { clearTimeout(t); res(); };
      ws.onerror = (e) => { clearTimeout(t); rej(new Error('CDP WS error: ' + (e.message ?? 'unknown'))); };
    });
    const c = new Cdp(ws);
    c.pageUrl = page.url; c.pageId = page.id;
    await c.send('Runtime.enable'); await c.send('Page.enable'); await c.send('Network.enable');
    return c;
  }
  onEvent(method, p) {
    const cap = (arr, item) => { arr.push(item); if (arr.length > 3000) arr.splice(0, arr.length - 3000); };
    if (method === 'Runtime.consoleAPICalled') {
      cap(this.events.console, { type: p.type, text: (p.args ?? []).map(a => a.value ?? a.description ?? '').join(' ').slice(0, 500) });
    } else if (method === 'Runtime.exceptionThrown') {
      cap(this.events.exceptions, { text: (p.exceptionDetails?.exception?.description ?? p.exceptionDetails?.text ?? '').slice(0, 800) });
    } else if (method === 'Network.requestWillBeSent') {
      if (/^https?:/.test(p.request?.url ?? '')) cap(this.events.requests, { requestId: p.requestId, method: p.request.method, url: p.request.url, postData: (p.request.postData ?? '').slice(0, 2000), ts: p.timestamp });
    } else if (method === 'Network.responseReceived') {
      cap(this.events.responses, { requestId: p.requestId, url: p.response.url, status: p.response.status, mimeType: p.response.mimeType });
    }
  }
  send(method, params = {}, timeoutMs = 30000) {
    const id = this.nextId++;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => { this.pending.delete(id); reject(new Error(`CDP ${method} timeout after ${timeoutMs}ms`)); }, timeoutMs);
      this.pending.set(id, { resolve, reject, timer });
      this.ws.send(JSON.stringify({ id, method, params }));
    });
  }
  async evaluate(expression, awaitPromise = true) {
    const r = await this.send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise });
    if (r.exceptionDetails) throw new Error('page eval exception: ' + (r.exceptionDetails.exception?.description ?? r.exceptionDetails.text ?? '').slice(0, 800));
    return r.result?.value;
  }
  close() { try { this.ws.close(); } catch {} }
}

// --- in-page helpers (serialized into the renderer) -----------------------

const FIND_ELEMENT = (how, value) => {
  const vis = el => { const r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0; };
  if (how === 'selector') return document.querySelector(value);
  if (how === 'testid') return document.querySelector(`[data-testid="${CSS.escape(value)}"]`);
  if (how === 'placeholder') return [...document.querySelectorAll('input,textarea')].find(i => i.placeholder === value && vis(i));
  if (how === 'text') {
    const sel = 'button,[role=button],[role=tab],a,[role=menuitem],[role=link],li,tr,span[role=link]';
    const norm = s => (s ?? '').trim().replace(/\s+/g, ' ');
    const all = [...document.querySelectorAll(sel)].filter(vis);
    return all.find(e => norm(e.textContent) === value)
        ?? all.find(e => norm(e.getAttribute('aria-label')) === value)
        ?? all.find(e => norm(e.textContent).includes(value));
  }
  return null;
};

const DUMP_EXPR = `(() => {
  const vis = el => { const r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0; };
  const buttons = [...document.querySelectorAll('button,[role=button],[role=tab],a,[role=menuitem]')].filter(vis)
    .map(b => (b.getAttribute('aria-label') || b.textContent || '').trim().replace(/\\s+/g, ' ').slice(0, 80));
  const inputs = [...document.querySelectorAll('input,select,textarea')].filter(vis)
    .map(i => ({ tag: i.tagName.toLowerCase(), type: i.type || '', testid: i.getAttribute('data-testid') || '', placeholder: i.placeholder || '', ariaLabel: i.getAttribute('aria-label') || '' }));
  const testids = [...document.querySelectorAll('[data-testid]')].filter(vis).map(e => e.getAttribute('data-testid'));
  return { title: document.title, url: location.href, text: document.body.innerText.slice(0, 8000), buttons, inputs, testids };
})()`;

// Expression that RE-FINDS the element on every evaluation (survives React
// re-renders between dispatches). Used by the hardened click/fill paths.
const EL_EXPR = (how, value) =>
  `(${FIND_ELEMENT.toString()})(${JSON.stringify(how)}, ${JSON.stringify(value)})`;

async function rectOf(c, how, value) {
  const expr = `(() => {
    const find = ${FIND_ELEMENT.toString()};
    const el = find(${JSON.stringify(how)}, ${JSON.stringify(value)});
    if (!el) return null;
    el.scrollIntoView({ block: 'center', inline: 'center' });
    const r = el.getBoundingClientRect();
    return { x: r.x + r.width / 2, y: r.y + r.height / 2, w: r.width, h: r.height, tag: el.tagName, text: (el.textContent || '').trim().slice(0, 120) };
  })()`;
  return c.evaluate(expr);
}

async function mouseClick(c, x, y, clickCount = 1) {
  await c.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x, y });
  await c.send('Input.dispatchMouseEvent', { type: 'mousePressed', x, y, button: 'left', clickCount });
  await c.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x, y, button: 'left', clickCount });
}

const KEY_DEFS = {
  Enter: { key: 'Enter', code: 'Enter', vk: 13 },
  Escape: { key: 'Escape', code: 'Escape', vk: 27 },
  Tab: { key: 'Tab', code: 'Tab', vk: 9 },
  Backspace: { key: 'Backspace', code: 'Backspace', vk: 8 },
  ArrowDown: { key: 'ArrowDown', code: 'ArrowDown', vk: 40 },
  ArrowUp: { key: 'ArrowUp', code: 'ArrowUp', vk: 38 },
  k: { key: 'k', code: 'KeyK', vk: 75 },
  a: { key: 'a', code: 'KeyA', vk: 65 }
};

async function pressKey(c, name, modifiers = 0) {
  const def = KEY_DEFS[name] ?? { key: name, code: '', vk: name.toUpperCase().charCodeAt(0) };
  const base = { key: def.key, code: def.code, windowsVirtualKeyCode: def.vk, nativeVirtualKeyCode: def.vk, modifiers };
  await c.send('Input.dispatchKeyEvent', { type: 'rawKeyDown', ...base });
  await c.send('Input.dispatchKeyEvent', { type: 'keyUp', ...base });
}

async function typeText(c, text) {
  for (const ch of text) {
    const vk = ch.toUpperCase().charCodeAt(0);
    const base = { key: ch, code: '', windowsVirtualKeyCode: vk, nativeVirtualKeyCode: vk, text: ch, unmodifiedText: ch };
    await c.send('Input.dispatchKeyEvent', { type: 'keyDown', ...base });
    await c.send('Input.dispatchKeyEvent', { type: 'char', ...base });
    await c.send('Input.dispatchKeyEvent', { type: 'keyUp', ...base });
  }
}

async function fillInput(c, how, value, text) {
  const expr = `(() => {
    const find = ${FIND_ELEMENT.toString()};
    const el = find(${JSON.stringify(how)}, ${JSON.stringify(value)});
    if (!el) return { ok: false, error: 'input not found' };
    el.focus();
    const proto = el.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
    const setter = Object.getOwnPropertyDescriptor(proto, 'value').set;
    setter.call(el, ${JSON.stringify(text)});
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
    return { ok: true, value: el.value };
  })()`;
  return c.evaluate(expr);
}

// --- command dispatch ------------------------------------------------------

async function runCommand(c, cmd, a) {
  switch (cmd) {
    case 'ping': return { ok: true, pageUrl: c.pageUrl, pageId: c.pageId };
    case 'targets': return httpJson('/json/list');
    case 'dump': return c.evaluate(DUMP_EXPR);
    case 'eval': return c.evaluate(a.expression, a.awaitPromise !== false);
    case 'count': return c.evaluate(`document.querySelectorAll(${JSON.stringify(a.selector)}).length`);
    case 'shot': {
      const r = await c.send('Page.captureScreenshot', { format: 'png' }, 60000);
      fs.writeFileSync(a.path, Buffer.from(r.data, 'base64'));
      return { path: a.path };
    }
    case 'click': {
      // Animation-race hardening (lead send-back 2026-11-18): re-read a FRESH
      // rect immediately before each dispatch, verify the click landed via
      // focus, and fall back to real DOM click events if input events keep
      // missing a moving target (e.g. Mantine modal animation). Failure is
      // reported to the caller, never swallowed.
      const how = a.testid ? 'testid' : a.selector ? 'selector' : a.placeholder ? 'placeholder' : 'text';
      const val = a.testid ?? a.selector ?? a.placeholder ?? a.text;
      const elExpr = EL_EXPR(how, val);
      const exists = await c.evaluate(`!!(${elExpr})`);
      if (!exists) return { clicked: false, error: `element not found: ${how}=${val}` };
      await c.evaluate(`(() => { const el = ${elExpr}; if (el) el.scrollIntoView({ block: 'center', inline: 'center' }); return !!el; })()`);
      await new Promise(r => setTimeout(r, 100));
      let rect = null;
      let method = 'input';
      for (let attempt = 0; attempt < 3; attempt++) {
        rect = await rectOf(c, how, val); // fresh find + fresh rect each attempt
        if (!rect) return { clicked: false, error: `element detached or invisible: ${how}=${val}` };
        await mouseClick(c, rect.x, rect.y, 1);
        if (a.double) { await mouseClick(c, rect.x, rect.y, 2); }
        await new Promise(r => setTimeout(r, 80));
        const after = await c.evaluate(`(() => { const el = ${elExpr};
          return { present: !!el, focused: !!el && document.activeElement === el }; })()`);
        if (after.focused) break;
        if (!after.present) {
          // The element existed before dispatch and is gone now: its own
          // action most likely removed it (e.g. submit closes the modal).
          // Report success; the caller's EVIDENCE wait verifies the effect.
          return { clicked: true, rect, method: 'input', vanishedAfterDispatch: true };
        }
        if (attempt === 2) {
          // DOM fallback: dispatch the same events a user click produces.
          await c.evaluate(`(() => { const el = ${elExpr}; if (!el) return false; el.click();
            ${a.double ? `el.dispatchEvent(new MouseEvent('dblclick', { bubbles: true, cancelable: true, view: window }));` : ''}
            return true; })()`);
          method = 'dom-fallback';
          await new Promise(r => setTimeout(r, 80));
        }
      }
      return { clicked: true, rect, method };
    }
    case 'fill': {
      const how = a.testid ? 'testid' : a.selector ? 'selector' : 'placeholder';
      const val = a.testid ?? a.selector ?? a.placeholder;
      const r = await fillInput(c, how, val, a.value ?? '');
      if (!r || r.ok === false) return r ?? { ok: false, error: 'input not found' };
      await new Promise(res => setTimeout(res, 60)); // controlled-input settle
      const settled = await c.evaluate(`(() => { const el = ${EL_EXPR(how, val)}; return el ? el.value : null; })()`);
      return { ok: settled === (a.value ?? ''), value: settled, expected: a.value ?? '' };
    }
    case 'key': await pressKey(c, a.key, a.modifiers ?? 0); return { pressed: a.key };
    case 'type': await typeText(c, a.text ?? ''); return { typed: (a.text ?? '').length };
    case 'insertText': await c.send('Input.insertText', { text: a.text ?? '' }); return { inserted: (a.text ?? '').length };
    case 'wait': await new Promise(r => setTimeout(r, Math.min(Number(a.ms ?? 500), 120000))); return { waited: a.ms };
    case 'waitFor': {
      const deadline = Date.now() + Math.min(Number(a.timeoutMs ?? 15000), 120000);
      const how = a.testid ? 'testid' : 'selector';
      const val = a.testid ?? a.selector;
      const expr = `(() => { const find = ${FIND_ELEMENT.toString()}; return !!find(${JSON.stringify(how)}, ${JSON.stringify(val)}); })()`;
      for (;;) {
        if (await c.evaluate(expr)) return { found: true };
        if (Date.now() > deadline) return { found: false, timeout: true };
        await new Promise(r => setTimeout(r, 250));
      }
    }
    case 'events': {
      const out = { ...c.events };
      if (a.clear) c.events = { console: [], exceptions: [], requests: [], responses: [] };
      return out;
    }
    case 'navigate': { await c.send('Page.navigate', { url: a.url }); return { navigated: a.url }; }
    case 'reload': { await c.send('Page.reload', {}); return { reloaded: true }; }
    case 'closeBrowser': {
      const ver = await httpJson('/json/version');
      const ws = new WebSocket(ver.webSocketDebuggerUrl);
      await new Promise((res, rej) => { const t = setTimeout(() => rej(new Error('browser WS timeout')), 8000); ws.onopen = () => { clearTimeout(t); res(); }; ws.onerror = () => { clearTimeout(t); rej(new Error('browser WS error')); }; });
      const result = await new Promise((res, rej) => {
        const t = setTimeout(() => rej(new Error('Browser.close timeout')), 15000);
        ws.onmessage = (m) => { const msg = JSON.parse(m.data); if (msg.id === 1) { clearTimeout(t); res(msg); } };
        // The browser often terminates BEFORE replying; a closed socket is success.
        ws.onclose = () => { clearTimeout(t); res({ note: 'browser socket closed (process exiting)' }); };
        ws.send(JSON.stringify({ id: 1, method: 'Browser.close' }));
      });
      try { ws.close(); } catch {}
      return { closed: true, result };
    }
    default: throw new Error('unknown command: ' + cmd);
  }
}

// --- entry points ----------------------------------------------------------

if (SERVE) {
  const c = await Cdp.connect();
  const rl = readline.createInterface({ input: process.stdin, terminal: false });
  const write = (obj) => process.stdout.write(JSON.stringify(obj) + '\n');
  write({ id: 0, ok: true, result: { ready: true, pageUrl: c.pageUrl, pageId: c.pageId } });
  rl.on('line', async (line) => {
    let req; try { req = JSON.parse(line); } catch { return write({ id: null, ok: false, error: 'bad JSON line' }); }
    try {
      const result = await runCommand(c, req.cmd, req);
      write({ id: req.id, ok: true, result });
    } catch (e) {
      write({ id: req.id, ok: false, error: String(e.message ?? e) });
    }
  });
  rl.on('close', () => { c.close(); process.exit(0); });
} else {
  const cmd = process.argv[2];
  if (!cmd || cmd.startsWith('--')) { console.error('usage: node cdp.mjs --port <p> --serve | <cmd> [jsonArgs]'); process.exit(2); }
  let a = {}; try { a = JSON.parse(process.argv[3] ?? '{}'); } catch { a = {}; }
  const c = await Cdp.connect();
  try {
    const result = await runCommand(c, cmd, a);
    console.log(JSON.stringify(result));
  } finally {
    c.close();
    setTimeout(() => process.exit(0), 100);
  }
}