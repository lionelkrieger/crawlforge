// @vitest-environment jsdom
/**
 * Real rendered output proofs (WP-10-03, UI-18 single-page analyzer + UI-19
 * render tester; probe-2 G01 "render testing inert"). The shared
 * `RenderedOutputPanel` binds the locked `RenderTestResponse` (screenshot,
 * rendered DOM, console, network waterfall, WAF) produced by the live CDP
 * pool; the single-page analyzer mounts the real-output viewers end to end.
 */

import { afterEach, describe, expect, it } from 'vitest';
import { RenderedOutputPanel } from '../../../src/frontend/src/surfaces/explorers/rendered/RenderedOutputPanel';
import { SinglePageAnalyzer } from '../../../src/frontend/src/surfaces/single-page/SinglePageAnalyzer';
import {
  renderTool,
  toolServices,
  waitFor,
  byTestId,
  click,
  flush,
  textOf,
  ok,
  fakeServices,
  type Mounted
} from '../tools/helpers';
import { renderTestResponse } from './helpers';

afterEach(() => {
  document.body.innerHTML = '';
});

describe('RenderedOutputPanel — binds the real render-test output (UI-19)', () => {
  it('renders screenshot, DOM, console, waterfall and the no-WAF verdict', async () => {
    const view = await renderTool(<RenderedOutputPanel result={renderTestResponse()} />, fakeServices().services);
    expect(byTestId(view.container, 'rendered-output')).not.toBeNull();
    expect((byTestId(view.container, 'rendered-output-screenshot-img') as HTMLImageElement).getAttribute('src')).toContain('data:image/png;base64,');
    expect(textOf(byTestId(view.container, 'rendered-output-dom-pre'))).toContain('Rendered title after script');
    expect(byTestId(view.container, 'rendered-output-console-row-0')).not.toBeNull();
    expect(byTestId(view.container, 'rendered-output-waterfall-row-0')).not.toBeNull();
    expect(byTestId(view.container, 'rendered-output-waterfall-row-2')).not.toBeNull();
    expect(byTestId(view.container, 'rendered-output-waf')?.getAttribute('data-detected')).toBe('false');
    expect(textOf(byTestId(view.container, 'rendered-output-waterfall-count'))).toContain('3 network requests');
    expect(textOf(byTestId(view.container, 'rendered-output-console-count'))).toContain('3 console messages');
  });

  it('treats a null waterfall as a baseline render (honest empty state)', async () => {
    const view = await renderTool(<RenderedOutputPanel result={renderTestResponse({ networkWaterfall: null })} />, fakeServices().services);
    expect(byTestId(view.container, 'rendered-output-waterfall-empty')).not.toBeNull();
    expect(textOf(byTestId(view.container, 'rendered-output-waterfall-count'))).toContain('No waterfall (baseline)');
  });

  it('treats an absent waterfall field as baseline too', async () => {
    const view = await renderTool(<RenderedOutputPanel result={renderTestResponse({ networkWaterfall: undefined })} />, fakeServices().services);
    expect(byTestId(view.container, 'rendered-output-waterfall-empty')).not.toBeNull();
  });

  it('surfaces a detected WAF verdict with provider and challenge class', async () => {
    const view = await renderTool(
      <RenderedOutputPanel
        result={renderTestResponse({ wafVerdict: { provider: 'cloudflare', challengeClass: 'managed-challenge', confidence: 0.9 } })}
      />,
      fakeServices().services
    );
    const waf = byTestId(view.container, 'rendered-output-waf');
    expect(waf?.getAttribute('data-detected')).toBe('true');
    expect(textOf(waf)).toContain('cloudflare');
    expect(textOf(waf)).toContain('managed-challenge');
  });
});

describe('SinglePageAnalyzer — real rendered output (UI-18)', () => {
  it('shows the network waterfall, console and DOM after rendering a page', async () => {
    const services = toolServices({ '/api/tools/render-test': () => ok(renderTestResponse()) }).services;
    const view: Mounted = await renderTool(<SinglePageAnalyzer />, services);
    click(byTestId(view.container, 'sp-inspect-rendered')!);
    await waitFor(() => byTestId(view.container, 'sp-artifact') !== null);
    await flush(10);
    // Backward-compatible artifact affordances remain.
    expect((byTestId(view.container, 'sp-screenshot') as HTMLImageElement).getAttribute('src')).toContain('data:image/png;base64,');
    // NEW real rendered output from the live pool.
    expect(byTestId(view.container, 'sp-waterfall-row-0')).not.toBeNull();
    expect(byTestId(view.container, 'sp-waterfall-row-2')).not.toBeNull();
    expect(byTestId(view.container, 'sp-console-row-0')).not.toBeNull();
    expect(textOf(byTestId(view.container, 'sp-dom-pre'))).toContain('Rendered title after script');
  });

  it('renders an honest empty waterfall for a baseline render (no captured traffic)', async () => {
    // Default tool fake returns a render-test response WITHOUT networkWaterfall.
    const services = toolServices().services;
    const view: Mounted = await renderTool(<SinglePageAnalyzer />, services);
    click(byTestId(view.container, 'sp-inspect-rendered')!);
    await waitFor(() => byTestId(view.container, 'sp-artifact') !== null);
    await flush(10);
    expect(byTestId(view.container, 'sp-waterfall-empty')).not.toBeNull();
    expect(byTestId(view.container, 'sp-console-row-0')).not.toBeNull();
  });
});
