// Probe helper: connect to the running Electron renderer over CDP and run an action module.
// Usage: node cdp.mjs <action.mjs> [args...]
import { chromium } from 'playwright-core';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const browser = await chromium.connectOverCDP('http://127.0.0.1:9232');
const ctx = browser.contexts()[0];
const pages = ctx.pages();
const page = pages.find(p => p.url().includes('route=hub')) ?? pages[0];
const shotDir = 'E:/Crawlforge/audit-work/runtime-probe-2/screenshots';

const helpers = {
  page, ctx, browser,
  async shot(name) { const f = path.join(shotDir, name + '.png'); await page.screenshot({ path: f }); return f; },
  async dump(p = page) {
    return p.evaluate(() => {
      const vis = el => { const r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0; };
      const buttons = [...document.querySelectorAll('button,[role=button],[role=tab],a,[role=menuitem]')].filter(vis)
        .map(b => (b.getAttribute('aria-label') || b.textContent || '').trim().replace(/\s+/g, ' ').slice(0, 60));
      const inputs = [...document.querySelectorAll('input,select,textarea')].filter(vis)
        .map(i => `${i.tagName.toLowerCase()}[${i.type || ''}] name=${i.name || ''} label=${i.getAttribute('aria-label') || i.placeholder || ''}`);
      return { title: document.title, url: location.href, text: document.body.innerText.slice(0, 3000), buttons, inputs };
    });
  },
};

const mod = await import(pathToFileURL(path.resolve(process.argv[2])).href);
try {
  const out = await mod.default(helpers, process.argv.slice(3));
  if (out !== undefined) console.log(typeof out === 'string' ? out : JSON.stringify(out, null, 2));
} catch (e) { console.error('ACTION ERROR', e.message); }
process.exit(0); // drop the CDP socket without closing the app
