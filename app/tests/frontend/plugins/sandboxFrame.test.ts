// @vitest-environment jsdom
/**
 * Sandboxed frame tests (FS-CRAWL-002 §6.4, E5, #39): allow-scripts-only
 * sandbox (opaque origin), strict CSP with connect-src 'none' without a
 * grant, local-only srcdoc composition, and script-terminator escaping.
 */

import { beforeEach, describe, expect, it } from 'vitest';
import {
  buildCsp,
  buildSrcDoc,
  createSandboxFrame,
  SANDBOX_ATTRIBUTE_VALUE
} from '../../../src/frontend/src/plugins/sandboxFrame';

beforeEach(() => {
  document.body.replaceChildren();
});

describe('CSP', () => {
  it('locks everything down without a grant', () => {
    const csp = buildCsp([]);
    expect(csp).toContain("default-src 'none'");
    expect(csp).toContain("connect-src 'none'");
    expect(csp).toContain("object-src 'none'");
    expect(csp).toContain("form-action 'none'");
    expect(csp).toContain("base-uri 'none'");
    expect(csp).toContain("script-src 'unsafe-inline'");
  });

  it('opens connect-src only to granted destination origins', () => {
    const csp = buildCsp(['https://warehouse.internal.example:8443']);
    expect(csp).toContain('connect-src https://warehouse.internal.example:8443');
    expect(csp).not.toContain("connect-src 'none'");
    expect(csp).toContain("default-src 'none'");
  });
});

describe('srcdoc composition', () => {
  it('embeds the CSP meta, the bridge shim, and the local entry code', () => {
    const srcdoc = buildSrcDoc({
      pluginId: 'com.example.widget',
      entryCode: 'window.cf.onHook(function (hook) { });',
      networkOrigins: []
    });
    expect(srcdoc).toContain('Content-Security-Policy');
    expect(srcdoc).toContain('crawlforge-plugin-v1');
    expect(srcdoc).toContain('window.cf');
    expect(srcdoc).toContain('window.cf.onHook');
    expect(srcdoc).not.toContain('http://');
    expect(srcdoc).not.toContain('https://');
  });

  it('includes local styles when provided', () => {
    const srcdoc = buildSrcDoc({ pluginId: 'p', entryCode: '', styles: '.tab { color: red; }', networkOrigins: [] });
    expect(srcdoc).toContain('<style>.tab { color: red; }</style>');
  });

  it('escapes inline script terminators in entry code', () => {
    const srcdoc = buildSrcDoc({ pluginId: 'p', entryCode: 'var s = "</script><script>alert(1)</script>";', networkOrigins: [] });
    expect(srcdoc).not.toContain('</script><script>alert(1)');
    expect(srcdoc).toContain('<\\/script');
  });

  it('granted origins appear only in the CSP connect-src', () => {
    const srcdoc = buildSrcDoc({ pluginId: 'p', entryCode: '', networkOrigins: ['https://warehouse.internal.example:8443'] });
    expect(srcdoc).toContain('connect-src https://warehouse.internal.example:8443');
  });
});

describe('frame element', () => {
  it('uses allow-scripts only — no same-origin, forms, popups, or navigation', () => {
    expect(SANDBOX_ATTRIBUTE_VALUE).toBe('allow-scripts');
    const frame = createSandboxFrame(document, { pluginId: 'com.example.widget', entryCode: '', networkOrigins: [] });
    expect(frame.getAttribute('sandbox')).toBe('allow-scripts');
    expect(frame.getAttribute('sandbox')).not.toContain('allow-same-origin');
    expect(frame.getAttribute('data-testid')).toBe('plugin-frame-com.example.widget');
    expect(frame.srcdoc).toContain('Content-Security-Policy');
  });

  it('attaches and detaches cleanly', () => {
    const frame = createSandboxFrame(document, { pluginId: 'p', entryCode: '', networkOrigins: [] });
    document.body.appendChild(frame);
    expect(document.querySelector('[data-testid="plugin-frame-p"]')).toBe(frame);
    frame.remove();
    expect(document.querySelector('[data-testid="plugin-frame-p"]')).toBeNull();
  });
});