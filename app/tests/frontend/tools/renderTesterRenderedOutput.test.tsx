// @vitest-environment jsdom
/**
 * UI-19 render tester × WP-10-03 real rendered output: after a successful
 * `tools.renderTest` call the tool mounts `RenderedOutputPanel` (default
 * testid `rendered-output`) over the REAL response — screenshot, network
 * waterfall (response.networkWaterfall), console messages and rendered DOM.
 * A failing call keeps the tool's existing error state and renders no panel.
 */

import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { RenderTesterTool } from '../../../src/frontend/src/tools/RenderTesterTool';
import type { RenderTestResponse } from '../../../src/frontend/src/contracts/api/tools';
import {
  byTestId,
  click,
  err,
  ok,
  renderTool,
  textOf,
  toolServices,
  waitFor,
  type Mounted
} from './helpers';

let mounted: Mounted | null = null;

beforeEach(() => {
  localStorage.clear();
  document.body.replaceChildren();
});

afterEach(() => {
  mounted?.unmount();
  mounted = null;
});

/**
 * One real-shaped `tools.renderTest` response: a screenshot, two
 * `networkWaterfall` entries carrying `transferredBytes`, one console message.
 */
const RESPONSE: RenderTestResponse = {
  screenshotBase64: 'aGVsbG8=',
  renderedHtml:
    '<html><head><title>Rendered title after script</title></head><body><h1>Rendered</h1></body></html>',
  consoleMessages: [{ level: 'warn', text: 'Deprecated API used', timestamp: null }],
  networkWaterfall: [
    {
      url: 'https://acme.example.com/',
      resourceType: 'document',
      method: 'GET',
      statusCode: 200,
      mimeType: 'text/html',
      transferredBytes: 512,
      encodedDataLength: 400,
      fromCache: false,
      fromServiceWorker: false
    },
    {
      url: 'https://acme.example.com/app.js',
      resourceType: 'script',
      method: 'GET',
      statusCode: 200,
      mimeType: 'application/javascript',
      transferredBytes: 2048,
      encodedDataLength: 1800,
      fromCache: false,
      fromServiceWorker: false
    }
  ],
  wafVerdict: null,
  pickerActive: true
};

/** Mounts the render tester and clicks Render against the given responder. */
async function runRender(responder: () => unknown): Promise<Mounted> {
  const { services } = toolServices({ '/api/tools/render-test': responder });
  const view = await renderTool(<RenderTesterTool />, services);
  click(byTestId(view.container, 'render-run')!);
  return view;
}

describe('RenderTesterTool — real rendered output panel (UI-19)', () => {
  it('shows rendered-output with the screenshot, waterfall count 2, the console row and the rendered DOM', async () => {
    mounted = await runRender(() => ok(RESPONSE));
    await waitFor(() => byTestId(mounted!.container, 'rendered-output') !== null);

    // Screenshot img.
    const img = byTestId(mounted.container, 'rendered-output-screenshot-img') as HTMLImageElement;
    expect(img).not.toBeNull();
    expect(img.getAttribute('src')).toBe('data:image/png;base64,aGVsbG8=');

    // Network waterfall: exactly two entries, count badge reads 2, bytes shown.
    expect(textOf(byTestId(mounted.container, 'rendered-output-waterfall-count'))).toContain('2 network requests');
    expect(byTestId(mounted.container, 'rendered-output-waterfall-row-0')).not.toBeNull();
    expect(byTestId(mounted.container, 'rendered-output-waterfall-row-1')).not.toBeNull();
    expect(byTestId(mounted.container, 'rendered-output-waterfall-row-2')).toBeNull();
    expect(textOf(byTestId(mounted.container, 'rendered-output-waterfall-bytes-0'))).toBe('512 B');
    expect(textOf(byTestId(mounted.container, 'rendered-output-waterfall-bytes-1'))).toBe('2.0 KB');

    // Console row.
    const row = byTestId(mounted.container, 'rendered-output-console-row-0');
    expect(row).not.toBeNull();
    expect(row?.getAttribute('data-level')).toBe('warn');
    expect(textOf(byTestId(mounted.container, 'rendered-output-console-text-0'))).toBe('Deprecated API used');

    // Rendered DOM.
    expect(textOf(byTestId(mounted.container, 'rendered-output-dom-pre'))).toContain('Rendered title after script');
  });

  it('keeps the existing render-screenshot / render-console testids alongside the panel', async () => {
    mounted = await runRender(() => ok(RESPONSE));
    await waitFor(() => byTestId(mounted!.container, 'rendered-output') !== null);
    const shot = byTestId(mounted.container, 'render-screenshot') as HTMLImageElement;
    expect(shot.getAttribute('src')).toBe('data:image/png;base64,aGVsbG8=');
    expect(textOf(byTestId(mounted.container, 'render-console'))).toContain('Deprecated API used');
    expect(byTestId(mounted.container, 'render-result')).not.toBeNull();
  });

  it('renders no rendered-output and shows the existing error state when the render fails', async () => {
    mounted = await runRender(() => err('render-failed', 'Render failed: the target refused the connection.'));
    await waitFor(() => byTestId(mounted!.container, 'render-error') !== null);
    expect(textOf(byTestId(mounted.container, 'render-error'))).toContain('Render failed');
    expect(byTestId(mounted.container, 'render-result')).toBeNull();
    expect(byTestId(mounted.container, 'rendered-output')).toBeNull();
  });
});
