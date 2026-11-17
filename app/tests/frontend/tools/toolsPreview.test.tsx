// @vitest-environment jsdom
/**
 * Preview + render tools: content-scope preview (with picker mount),
 * llms.txt generator/validator tabs, and the render tester (screenshot,
 * console, WAF banner, picker). The picker's capture/send wiring is proven
 * in picker.test.tsx; here we verify the tool-level integration.
 */

import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { ScopePreviewTool } from '../../../src/frontend/src/tools/ScopePreviewTool';
import { LlmsTool } from '../../../src/frontend/src/tools/LlmsTool';
import { RenderTesterTool } from '../../../src/frontend/src/tools/RenderTesterTool';
import {
  auditSummary,
  byTestId,
  click,
  flush,
  ok,
  renderTool,
  seedAudits,
  textOf,
  toolServices,
  typeInto,
  waitFor
} from './helpers';
import type { Mounted } from '../shell/helpers';

let mounted: Mounted | null = null;

beforeEach(() => {
  localStorage.clear();
  document.body.replaceChildren();
});

afterEach(() => {
  mounted?.unmount();
  mounted = null;
});

async function selectOption(container: HTMLElement, testId: string, label: string): Promise<void> {
  click(byTestId(container, testId) as HTMLInputElement);
  await flush();
  const option = Array.from(document.querySelectorAll<HTMLElement>('[role="option"]')).find((node) =>
    (node.textContent ?? '').includes(label)
  );
  expect(option, `option "${label}"`).toBeDefined();
  click(option!);
  await flush();
}

async function clickTab(container: HTMLElement, label: string): Promise<void> {
  const tab = Array.from(container.querySelectorAll<HTMLElement>('[role="tab"]')).find((node) =>
    (node.textContent ?? '').includes(label)
  );
  expect(tab, `tab "${label}"`).toBeDefined();
  click(tab!);
  await flush();
}

describe('ScopePreviewTool', () => {
  it('previews inclusion with matched selectors and scoped text', async () => {
    const { services, calls } = toolServices();
    mounted = await renderTool(<ScopePreviewTool />, services);
    click(byTestId(mounted.container, 'scope-run')!);
    await waitFor(() => byTestId(mounted!.container, 'scope-result') !== null);
    expect(byTestId(mounted.container, 'scope-verdict')!.getAttribute('data-verdict')).toBe('ok');
    expect(textOf(byTestId(mounted.container, 'scope-result'))).toContain('main article');
    expect(textOf(byTestId(mounted.container, 'scope-text-pre'))).toContain('Local-first crawling');
    const post = calls.find((call) => call.url.includes('/api/tools/scope-preview'))!;
    const body = JSON.parse(post.body!);
    expect(body.contentScope.includeSelectors).toContain('main article');
    expect(body.contentScope.excludeSelectors).toContain('nav');
  });

  it('loads a rendered artifact into the sandboxed picker', async () => {
    const { services } = toolServices();
    mounted = await renderTool(<ScopePreviewTool />, services);
    click(byTestId(mounted.container, 'scope-load-artifact')!);
    await waitFor(() => byTestId(mounted!.container, 'scope-picker-iframe') !== null);
    const iframe = byTestId(mounted.container, 'scope-picker-iframe') as HTMLIFrameElement;
    expect(iframe.getAttribute('sandbox')).toBe('allow-same-origin');
  });

  it('edits include/exclude lists live', async () => {
    const { services } = toolServices();
    mounted = await renderTool(<ScopePreviewTool />, services);
    click(byTestId(mounted.container, 'scope-include-add')!);
    await flush();
    const row = byTestId(mounted.container, 'scope-include-row-1') as HTMLInputElement;
    typeInto(row, 'article.post');
    await flush();
    expect(row.value).toBe('article.post');
    click(byTestId(mounted.container, 'scope-exclude-remove-0')!);
    await flush();
    expect(byTestId(mounted.container, 'scope-exclude-row-2')).toBeNull();
  });
});

describe('LlmsTool', () => {
  it('generates a local llms.txt from a selected audit', async () => {
    const { services, calls } = toolServices();
    seedAudits(services, [auditSummary('1')]);
    mounted = await renderTool(<LlmsTool />, services);
    await selectOption(mounted.container, 'llms-audit', 'Audit 1');
    click(byTestId(mounted.container, 'llms-generate')!);
    await waitFor(() => byTestId(mounted!.container, 'llms-generate-result') !== null);
    expect(textOf(byTestId(mounted.container, 'llms-filename'))).toBe('llms.txt');
    const post = calls.find((call) => call.url.includes('/api/tools/llms-generate'))!;
    expect(JSON.parse(post.body!).variant).toBe('llms-txt');
  });

  it('validates a body and lists every issue', async () => {
    const { services } = toolServices();
    mounted = await renderTool(<LlmsTool />, services);
    await clickTab(mounted.container, 'Validator');
    click(byTestId(mounted.container, 'llms-validate')!);
    await waitFor(() => byTestId(mounted!.container, 'llms-validate-result') !== null);
    expect(byTestId(mounted.container, 'llms-verdict')!.getAttribute('data-verdict')).toBe('fail');
    expect(textOf(byTestId(mounted.container, 'llms-issues'))).toContain('Missing H1 title.');
    expect(textOf(byTestId(mounted.container, 'llms-issues'))).toContain('Link is not absolute');
  });
});

describe('RenderTesterTool', () => {
  it('renders screenshot, console output, and the picker', async () => {
    const { services } = toolServices();
    mounted = await renderTool(<RenderTesterTool />, services);
    click(byTestId(mounted.container, 'render-run')!);
    await waitFor(() => byTestId(mounted!.container, 'render-result') !== null);
    const shot = byTestId(mounted.container, 'render-screenshot') as HTMLImageElement;
    expect(shot.getAttribute('src')).toBe('data:image/png;base64,aGVsbG8=');
    expect(textOf(byTestId(mounted.container, 'render-console'))).toContain('Deprecated API used');
    expect(byTestId(mounted.container, 'render-picker-iframe')).not.toBeNull();
  });

  it('surfaces a post-render WAF verdict banner', async () => {
    const { services } = toolServices({
      '/api/tools/render-test': () =>
        ok({
          screenshotBase64: null,
          renderedHtml: '<p>challenge</p>',
          consoleMessages: [],
          wafVerdict: { provider: 'Cloudflare', challengeClass: 'managed', confidence: 0.9 },
          pickerActive: true
        })
    });
    mounted = await renderTool(<RenderTesterTool />, services);
    click(byTestId(mounted.container, 'render-run')!);
    await waitFor(() => byTestId(mounted!.container, 'render-waf') !== null);
    expect(textOf(byTestId(mounted.container, 'render-waf'))).toContain('Cloudflare');
  });

  it('omits the picker when the backend reports it inactive', async () => {
    const { services } = toolServices({
      '/api/tools/render-test': () =>
        ok({ screenshotBase64: null, renderedHtml: '<p>x</p>', consoleMessages: [], wafVerdict: null, pickerActive: false })
    });
    mounted = await renderTool(<RenderTesterTool />, services);
    click(byTestId(mounted.container, 'render-run')!);
    await waitFor(() => byTestId(mounted!.container, 'render-result') !== null);
    expect(byTestId(mounted.container, 'render-picker-iframe')).toBeNull();
  });
});