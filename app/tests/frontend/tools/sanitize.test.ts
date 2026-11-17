// @vitest-environment jsdom
/**
 * Offline artifact sanitizer tests: the sandbox must never execute script,
 * load external subresources, or navigate — while preserving structure so
 * selectors stay meaningful.
 */

import { describe, expect, it } from 'vitest';
import { isDataUrl, sanitizeArtifact } from '../../../src/frontend/src/tools/picker/sanitize';

describe('isDataUrl', () => {
  it('accepts data: URLs case-insensitively and rejects remote schemes', () => {
    expect(isDataUrl('data:image/png;base64,AAA')).toBe(true);
    expect(isDataUrl('  DATA:text/plain,hi')).toBe(true);
    expect(isDataUrl('https://evil.example/x.png')).toBe(false);
    expect(isDataUrl('//evil.example/x.png')).toBe(false);
    expect(isDataUrl('javascript:alert(1)')).toBe(false);
  });
});

describe('sanitizeArtifact', () => {
  it('removes active content wholesale', () => {
    const html = `<div><script>alert(1)</script><iframe src="https://evil.example"></iframe>
      <object data="x"></object><embed src="x"><noscript>no</noscript>
      <p onclick="steal()">keep</p></div>`;
    const out = sanitizeArtifact(html);
    expect(out).not.toContain('<script');
    expect(out).not.toContain('<iframe');
    expect(out).not.toContain('<object');
    expect(out).not.toContain('<embed');
    expect(out).not.toContain('<noscript');
    expect(out).not.toContain('onclick');
    expect(out).toContain('keep');
  });

  it('strips every on* handler regardless of case', () => {
    const out = sanitizeArtifact('<p ONCLICK="x()" onmouseover="y()" onload="z()">text</p>');
    expect(out.toLowerCase()).not.toContain('onclick');
    expect(out.toLowerCase()).not.toContain('onmouseover');
    expect(out.toLowerCase()).not.toContain('onload');
    expect(out).toContain('text');
  });

  it('drops external media but keeps data: media sources', () => {
    const out = sanitizeArtifact(
      '<img src="https://evil.example/a.png"><img src="data:image/png;base64,AAA"><img srcset="https://evil.example/b.png 2x">'
    );
    expect(out).not.toContain('https://evil.example');
    expect(out).not.toContain('srcset');
    expect(out).toContain('data:image/png;base64,AAA');
  });

  it('defangs anchors and forms without losing readable text', () => {
    const out = sanitizeArtifact('<a href="https://evil.example/phish">Click</a><form action="https://evil.example/post"><input type="text"></form>');
    expect(out).toContain('data-cf-href="https://evil.example/phish"');
    expect(out).toContain('href="#"');
    expect(out).toContain('Click');
    expect(out).not.toContain('action="https://evil.example/post"');
    expect(out).toContain('action="#"');
  });

  it('neutralizes url() loads in inline styles but keeps other declarations', () => {
    const out = sanitizeArtifact('<div style="background: url(https://evil.example/bg.png); color: red;">x</div>');
    expect(out).not.toContain('https://evil.example');
    expect(out).toContain('color: red');
    expect(out).toContain('none');
  });

  it('strips @import and external urls from style blocks, keeps data: urls', () => {
    // The style must live inside a body element to survive the body-fragment
    // return (a leading <style> parses into <head>, which is dropped).
    const out = sanitizeArtifact(
      '<div><style>@import "https://evil.example/x.css"; .a { background: url(https://evil.example/y.png); } .b { background: url(data:image/png;base64,AAA); }</style><p>x</p></div>'
    );
    expect(out).not.toContain('@import');
    expect(out).not.toContain('https://evil.example');
    expect(out).toContain('data:image/png;base64,AAA');
  });

  it('preserves classes, ids, and structure so selectors stay meaningful', () => {
    const out = sanitizeArtifact('<main id="content"><article class="post"><h2 class="title">Hi</h2></article></main>');
    expect(out).toContain('id="content"');
    expect(out).toContain('class="post"');
    expect(out).toContain('class="title"');
    expect(out).toContain('Hi');
  });

  it('returns a fragment (no html/head/body wrappers)', () => {
    const out = sanitizeArtifact('<!doctype html><html><head><title>T</title></head><body><p>only</p></body></html>');
    expect(out).not.toContain('<html');
    expect(out).not.toContain('<body');
    expect(out).toContain('<p>only</p>');
  });
});