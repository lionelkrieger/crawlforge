// CrawlForge fixture site: page catalogue (CR-015). Each trap is a named page with documented expectations
// in expected-results.json. Pages are generated here so the whole site is one reviewable module.
const esc = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;');
export const page = ({ title = 'Fixture page', head = '', body = '', lang = 'en' }) =>
  `<!doctype html><html lang="${lang}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>${esc(title)}</title>${head}</head><body>${body}</body></html>`;

const words = (n, seed = 'crawl') => Array.from({ length: n }, (_, i) => `${seed}${i % 17}`).join(' ');
const para = (n, seed) => `<p>${words(n, seed)}</p>`;

export const PAGES = {
  '/': page({
    title: 'CrawlForge fixture home',
    body: `<h1>CrawlForge fixture site</h1>${para(120, 'home')}
<nav><ul>
<li><a href="/redirect/chain-1">Redirect chain</a></li>
<li><a href="/redirect/loop-a">Redirect loop</a></li>
<li><a href="/pages/noindex.html">Noindex page</a></li>
<li><a href="/private/blocked.html">Robots-blocked page</a></li>
<li><a href="/pages/canonical-conflict.html">Canonical conflict</a></li>
<li><a href="/pages/broken-links.html">Broken links</a></li>
<li><a href="/pages/js-link.html">JavaScript-only link</a></li>
<li><a href="/pages/js-content.html">Script-injected content</a></li>
<li><a href="/pages/late-meta.html">Late meta change</a></li>
<li><a href="/pages/soft-404.html">Soft 404 (server)</a></li>
<li><a href="/spa/">SPA (click-only navigation)</a></li>
<li><a href="/hash-app/">Hash-routed app</a></li>
<li><a href="/pages/js-redirect.html">Client-side redirect</a></li>
<li><a href="/pages/postback.aspx">ASP.NET postback page</a></li>
<li><a href="/pages/viewstate-large.aspx">Large ViewState</a></li>
<li><a href="/trap/filter?color=red">Faceted filter trap</a></li>
<li><a href="/trap/calendar/2026/11">Calendar trap</a></li>
<li><a href="/cookie-gate/">Cookie-dependent redirect</a></li>
<li><a href="/pages/consent.html">Consent banner</a></li>
<li><a href="/sw-app/">Service worker app</a></li>
<li><a href="/pages/structured-data.html">Structured data errors</a></li>
<li><a href="/pages/images.html">Images without alt</a></li>
<li><a href="/pages/mixed-content.html">Mixed content</a></li>
<li><a href="/pages/hreflang-en.html">Hreflang without return tag</a></li>
<li><a href="/pages/dup-title-a.html">Duplicate title A</a></li>
<li><a href="/pages/dup-title-b.html">Duplicate title B</a></li>
<li><a href="/pages/thin.html">Thin page</a></li>
<li><a href="/live/odds.html">Live odds (websocket)</a></li>
</ul></nav>`,
  }),
  '/pages/redirect-target.html': page({ title: 'Redirect chain target', body: `<h1>Redirect target</h1>${para(150, 'target')}` }),
  '/pages/noindex.html': page({ title: 'Noindex page', head: '<meta name="robots" content="noindex, follow">', body: `<h1>Not for the index</h1>${para(150, 'noidx')}` }),
  '/private/blocked.html': page({ title: 'Robots-blocked page', body: `<h1>Blocked by robots.txt</h1>${para(150, 'blocked')}` }),
  '/pages/canonical-conflict.html': page({
    title: 'Canonical conflict',
    head: '<link rel="canonical" href="/pages/canonical-target-html.html">',
    body: `<h1>Canonical conflict</h1>${para(150, 'canon')}`,
  }),
  '/pages/canonical-target-html.html': page({ title: 'Canonical target (HTML)', body: `<h1>HTML canonical target</h1>${para(150, 'canhtml')}` }),
  '/pages/canonical-target-header.html': page({ title: 'Canonical target (header)', body: `<h1>Header canonical target</h1>${para(150, 'canhdr')}` }),
  '/pages/broken-links.html': page({
    title: 'Broken links',
    body: `<h1>Broken links</h1>${para(150, 'broken')}<p><a href="/pages/does-not-exist.html">Broken internal link</a></p><p><a href="https://broken.example.invalid/page">Broken external link</a></p>`,
  }),
  '/pages/orphan.html': page({ title: 'Orphan page (sitemap only)', body: `<h1>Orphan</h1>${para(150, 'orphan')}` }),
  '/pages/js-link.html': page({
    title: 'JavaScript-only link',
    body: `<h1>JavaScript-only link</h1>${para(150, 'jslink')}<div id="slot"></div>
<script>document.getElementById('slot').innerHTML = '<a href="/pages/js-only-target.html">Reachable only after rendering</a>';</script>`,
  }),
  '/pages/js-only-target.html': page({ title: 'JS-only link target', body: `<h1>Found only by rendering</h1>${para(150, 'jstarget')}` }),
  '/pages/js-content.html': page({
    title: 'Script-injected content',
    body: `<h1>Script-injected content</h1><div id="content"></div>
<script>document.getElementById('content').innerHTML = '<p>${words(200, 'injected')}</p>';</script>`,
  }),
  '/pages/late-meta.html': page({
    title: 'Raw title before script',
    head: '<link rel="canonical" href="/pages/late-meta.html"><meta name="description" content="Raw description">',
    body: `<h1>Late meta change</h1>${para(150, 'latemeta')}
<script>
document.title = 'Rendered title after script';
document.querySelector('link[rel=canonical]').href = '/pages/late-meta-other.html';
var m = document.createElement('meta'); m.name = 'robots'; m.content = 'noindex'; document.head.appendChild(m);
</script>`,
  }),
  '/pages/soft-404.html': page({ title: 'Page not found', body: '<h1>Page not found</h1><p>Sorry, the page you are looking for does not exist.</p>' }),
  '/pages/js-redirect.html': page({
    title: 'Client-side redirect',
    body: `<h1>Redirecting</h1>${para(60, 'jsred')}<script>setTimeout(function(){ location.replace('/pages/js-redirect-target.html'); }, 300);</script>`,
  }),
  '/pages/js-redirect-target.html': page({ title: 'Client-side redirect target', body: `<h1>Arrived by script redirect</h1>${para(150, 'jsredt')}` }),
  '/pages/structured-data.html': page({
    title: 'Structured data errors',
    head: `<script type="application/ld+json">{"@context":"https://schema.org","@type":"Product","description":"Missing name and offers"}</script>
<script type="application/ld+json">{"@context":"https://schema.org","@type":"Organization", "name": "Broken block"</script>`,
    body: `<h1>Structured data errors</h1>${para(150, 'sd')}`,
  }),
  '/pages/images.html': page({
    title: 'Images without alt text',
    body: `<h1>Images</h1>${para(150, 'img')}<img src="/static/pixel.png" width="1" height="1"><img src="/static/pixel.png" alt="">`,
  }),
  '/pages/mixed-content.html': page({
    title: 'Mixed content',
    body: `<h1>Mixed content</h1>${para(150, 'mixed')}<img src="http://127.0.0.1:47312/static/pixel.png" alt="insecure image">`,
  }),
  '/pages/hreflang-en.html': page({
    title: 'Hreflang English',
    head: '<link rel="alternate" hreflang="en" href="/pages/hreflang-en.html"><link rel="alternate" hreflang="fr" href="/pages/hreflang-fr.html">',
    body: `<h1>English</h1>${para(150, 'en')}<a href="/pages/hreflang-fr.html">Français</a>`,
  }),
  '/pages/hreflang-fr.html': page({ title: 'Hreflang French', lang: 'fr', body: `<h1>Français</h1>${para(150, 'fr')}` }),
  '/pages/dup-title-a.html': page({ title: 'Duplicate title', body: `<h1>Duplicate A</h1>${para(150, 'dupa')}` }),
  '/pages/dup-title-b.html': page({ title: 'Duplicate title', body: `<h1>Duplicate B</h1>${para(150, 'dupb')}` }),
  '/pages/thin.html': page({ title: 'Thin page', body: '<h1>Thin</h1><p>Only a few words here.</p>' }),
  '/pages/consent.html': page({
    title: 'Consent banner',
    head: '<script src="/static/fake-tag.js"></script>',
    body: `<div id="consent-banner" class="fixture-consent" role="dialog" style="position:fixed;inset:0;background:#fff">
<p>We use cookies.</p><button id="fixture-consent-accept">Accept all</button> <button id="fixture-consent-reject">Reject all</button></div>
<h1>Consent banner</h1>${para(150, 'consent')}
<script>
document.getElementById('fixture-consent-accept').onclick = function(){ document.cookie='fixture_consent=granted;path=/'; document.getElementById('consent-banner').remove(); };
document.getElementById('fixture-consent-reject').onclick = function(){ document.cookie='fixture_consent=denied;path=/'; document.getElementById('consent-banner').remove(); };
</script>`,
  }),
  '/pages/postback.aspx': page({
    title: 'ASP.NET postback navigation',
    body: `<form method="post" action="/pages/postback.aspx" id="form1">
<input type="hidden" name="__VIEWSTATE" id="__VIEWSTATE" value="${'dDw'.repeat(300)}">
<input type="hidden" name="__EVENTVALIDATION" id="__EVENTVALIDATION" value="${'ZXY'.repeat(50)}">
<h1>Postback navigation</h1>${para(150, 'postback')}
<a id="lnkProducts" href="javascript:__doPostBack('ctl00$lnkProducts','')">Products (postback)</a>
<a id="lnkAbout" href="javascript:WebForm_DoPostBackWithOptions(new WebForm_PostBackOptions('ctl00$lnkAbout', '', true, '', '', false, true))">About (postback)</a>
<a href="/(S(abc123xyz))/pages/session.aspx">Session page (cookieless session URL)</a>
<script>function __doPostBack(t,a){ var f=document.getElementById('form1'); f.submit(); }</script>
</form>`,
  }),
  '/pages/session.aspx': page({ title: 'Cookieless session page', body: `<h1>Session page</h1>${para(150, 'session')}` }),
  // SPA with history routing: navigation only by click (no hrefs). Server returns the shell for every /spa/* path.
  '/spa/': null,
  '/hash-app/': page({
    title: 'Hash-routed app',
    body: `<h1 id="h">Hash app</h1><nav><a href="#/products">Products</a> <a href="#!/about">About</a></nav><div id="view"></div>
<script>
function render(){ var r = location.hash || '#/'; var v = document.getElementById('view');
 if (r.indexOf('products') > -1) v.innerHTML = '<h2>Products</h2><p>${words(150, 'hprod')}</p>';
 else if (r.indexOf('about') > -1) v.innerHTML = '<h2>About</h2><p>${words(150, 'habout')}</p>';
 else v.innerHTML = '<p>${words(150, 'hhome')}</p>'; }
window.addEventListener('hashchange', render); render();
</script>`,
  }),
  '/sw-app/': page({
    title: 'Service worker app',
    body: `<h1>Service worker app</h1>${para(150, 'sw')}<script>if ('serviceWorker' in navigator) navigator.serviceWorker.register('/sw-app/sw.js');</script>`,
  }),
  '/live/odds.html': page({
    title: 'Live odds',
    body: `<h1>Live odds</h1><div id="odds">Connecting...</div>
<p>${words(120, 'odds')}</p>
<button id="place-bet" onclick="fetch('/api/bet',{method:'POST',body:'{}'})">Place bet</button>
<button id="deposit" onclick="fetch('/api/deposit',{method:'POST',body:'{}'})">Deposit</button>
<a href="/spa/markets">Markets</a>
<script>
var ws = new WebSocket((location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + '/ws/odds');
ws.onmessage = function(e){ document.getElementById('odds').textContent = 'Home ' + e.data + ' Away'; };
</script>`,
  }),
};

export const SPA_SHELL = page({
  title: 'Fixture SPA',
  body: `<header><h1 id="t">Fixture SPA</h1>
<nav><button id="nav-markets" data-route="/spa/markets">Markets</button>
<button id="nav-promos" data-route="/spa/promotions">Promotions</button>
<span role="link" tabindex="0" id="nav-results" data-route="/spa/results">Results</span>
<button id="nav-logout" onclick="fetch('/api/logout',{method:'POST'})">Logout</button></nav></header>
<main id="view"></main>
<script>
var views = {
 '/spa/': '<p>${words(150, 'spahome')}</p>',
 '/spa/markets': '<h2>Markets</h2><p>${words(150, 'markets')}</p><button id="spa-place-bet" onclick="fetch(\\'/api/bet\\',{method:\\'POST\\',body:\\'{}\\'})">Place bet</button>',
 '/spa/promotions': '<h2>Promotions</h2><p>${words(150, 'promos')}</p>',
 '/spa/results': '<h2>Results</h2><p>${words(150, 'results')}</p>'
};
function show(path){ var v = views[path]; document.getElementById('view').innerHTML = v || '<h2>Page not found</h2><p>Sorry, this page does not exist.</p>';
 document.title = v ? 'Fixture SPA ' + path : 'Page not found'; }
document.querySelectorAll('[data-route]').forEach(function(el){ el.addEventListener('click', function(){ history.pushState({}, '', el.dataset.route); show(el.dataset.route); }); });
window.addEventListener('popstate', function(){ show(location.pathname); });
show(location.pathname);
</script>`,
});

export const VIEWSTATE_LARGE = page({
  title: 'Large ViewState',
  body: `<form method="post" action="/pages/viewstate-large.aspx"><input type="hidden" name="__VIEWSTATE" id="__VIEWSTATE" value="${'QUJD'.repeat(38000)}">
<h1>Large ViewState</h1>${para(150, 'vs')}</form>`,
});
