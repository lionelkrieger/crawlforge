/**
 * Sandboxed plugin frame construction (FS-CRAWL-002 §6.4, E5, #39).
 *
 * Each plugin runs in an iframe with sandbox="allow-scripts" ONLY — opaque
 * origin, no same-origin access, no forms, no popups, no top navigation. A
 * strict CSP meta restricts the frame: default-src 'none', inline local code
 * only, and connect-src 'none' unless the user granted destination rules.
 * The entry module is local text injected by the shell; there is no remote
 * code channel of any kind (E1).
 *
 * @module plugins/sandboxFrame
 */

import { SANDBOX_PROTOCOL } from './sandboxProtocol';

/** The only sandbox token granted: scripts run at an opaque origin. */
export const SANDBOX_ATTRIBUTE_VALUE = 'allow-scripts';

/** Frame construction inputs (all content is local artifact text). */
export interface SandboxFrameOptions {
  readonly pluginId: string;
  readonly entryCode: string;
  readonly styles?: string;
  /** Granted destination origins; empty means connect-src 'none'. */
  readonly networkOrigins: readonly string[];
}

/** Builds the strict CSP policy for one plugin frame. */
export function buildCsp(networkOrigins: readonly string[]): string {
  const connectSrc = networkOrigins.length > 0 ? networkOrigins.join(' ') : "'none'";
  return [
    "default-src 'none'",
    "script-src 'unsafe-inline'",
    "style-src 'unsafe-inline'",
    `connect-src ${connectSrc}`,
    "img-src 'none'",
    "font-src 'none'",
    "object-src 'none'",
    "frame-src 'none'",
    "form-action 'none'",
    "base-uri 'none'"
  ].join('; ');
}

/** Escapes inline-script terminators so local code cannot break out. */
function escapeInlineScript(code: string): string {
  return code.replace(/<\/script/gi, '<\\/script');
}

/** The in-frame bridge shim: capability RPC + hook dispatch + error trap. */
const BRIDGE_SHIM = `(function () {
  var PROTOCOL = '${SANDBOX_PROTOCOL}';
  var nextId = 1;
  var pending = {};
  var hookHandlers = [];
  function reportError(error) {
    try {
      parent.postMessage({ protocol: PROTOCOL, type: 'error', message: String((error && error.message) || error) }, '*');
    } catch (ignored) { /* frame teardown */ }
  }
  window.addEventListener('message', function (event) {
    var msg = event.data;
    if (!msg || msg.protocol !== PROTOCOL) return;
    if (msg.type === 'response') {
      var entry = pending[msg.requestId];
      if (!entry) return;
      delete pending[msg.requestId];
      if (msg.ok) entry.resolve(msg.data); else entry.reject(new Error(msg.error));
    } else if (msg.type === 'hook') {
      for (var i = 0; i < hookHandlers.length; i += 1) {
        try { hookHandlers[i](msg.hook, msg.context); } catch (error) { reportError(error); }
      }
    }
  });
  window.addEventListener('error', function (event) { reportError(event.error || event.message); });
  window.cf = {
    request: function (capability, payload) {
      return new Promise(function (resolve, reject) {
        var requestId = nextId;
        nextId += 1;
        pending[requestId] = { resolve: resolve, reject: reject };
        parent.postMessage({ protocol: PROTOCOL, type: 'request', requestId: requestId, capability: capability, payload: payload }, '*');
      });
    },
    onHook: function (handler) { hookHandlers.push(handler); },
    getState: function () { return window.cf.request('data.getState'); },
    getSlice: function (slice) { return window.cf.request('data.getSlice', { slice: slice }); },
    subscribe: function () { return window.cf.request('data.subscribe'); },
    registerTab: function (id) { return window.cf.request('ui.registerTab', { id: id }); },
    registerColumnFormatter: function (target, columnKey) {
      return window.cf.request('ui.registerColumnFormatter', { target: target, columnKey: columnKey });
    },
    registerFilterPreset: function (name) { return window.cf.request('ui.registerFilterPreset', { name: name }); },
    fetch: function (url, options) {
      return window.cf.request('network.fetch', {
        url: url,
        method: options && options.method,
        body: options && options.body
      });
    }
  };
})();`;

/** Builds the complete srcdoc document for one plugin frame. */
export function buildSrcDoc(options: SandboxFrameOptions): string {
  const csp = buildCsp(options.networkOrigins);
  const styles = options.styles ? `<style>${options.styles}</style>` : '';
  const code = `${BRIDGE_SHIM}\n${escapeInlineScript(options.entryCode)}`;
  return [
    '<!DOCTYPE html>',
    '<html>',
    '<head>',
    `<meta http-equiv="Content-Security-Policy" content="${csp}">`,
    '<meta charset="utf-8">',
    styles,
    '</head>',
    '<body>',
    `<script>${code}</script>`,
    '</body>',
    '</html>'
  ].join('\n');
}

/**
 * Creates (does not attach) the sandboxed iframe for one plugin. The caller
 * appends it to a host container; removal detaches the plugin completely.
 */
export function createSandboxFrame(doc: Document, options: SandboxFrameOptions): HTMLIFrameElement {
  const frame = doc.createElement('iframe');
  frame.setAttribute('sandbox', SANDBOX_ATTRIBUTE_VALUE);
  frame.setAttribute('title', `Plugin ${options.pluginId}`);
  frame.setAttribute('data-testid', `plugin-frame-${options.pluginId}`);
  frame.style.width = '100%';
  frame.style.height = '100%';
  frame.style.border = '0';
  frame.srcdoc = buildSrcDoc(options);
  return frame;
}