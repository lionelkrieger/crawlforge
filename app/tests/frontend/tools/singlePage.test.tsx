// @vitest-environment jsdom
/**
 * Single-Page Analyzer tests (criterion #10): audit-grade findings without a
 * project — severity-sorted findings with evidence, live severity filters,
 * element inventory, rendered-artifact inspection with the picker, WAF/scope
 * diagnostics, and offline degradation.
 */

import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { SinglePageAnalyzer } from '../../../src/frontend/src/surfaces/single-page/SinglePageAnalyzer';
import { byTestId, click, flush, ok, offlineServices, renderTool, textOf, toolServices, waitFor } from './helpers';
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

async function clickTab(container: HTMLElement, label: string): Promise<void> {
  const tab = Array.from(container.querySelectorAll<HTMLElement>('[role="tab"]')).find((node) =>
    (node.textContent ?? '').includes(label)
  );
  expect(tab, `tab "${label}"`).toBeDefined();
  click(tab!);
  await flush();
}

async function analyze(container: HTMLElement): Promise<void> {
  click(byTestId(container, 'sp-analyze')!);
  await waitFor(() => byTestId(container, 'sp-summary') !== null);
}

describe('analysis without a project', () => {
  it('reports findings, element total, and scope diagnostics', async () => {
    const services = toolServices().services;
    mounted = await renderTool(<SinglePageAnalyzer />, services);
    expect(byTestId(mounted.container, 'sp-empty')).not.toBeNull();
    await analyze(mounted.container);
    expect(textOf(byTestId(mounted.container, 'sp-finding-count'))).toBe('3');
    expect(textOf(byTestId(mounted.container, 'sp-element-total'))).toBe('26');
    expect(byTestId(mounted.container, 'sp-scope')).not.toBeNull();
    expect(byTestId(mounted.container, 'sp-waf')).toBeNull();
  });

  it('sorts findings by severity and renders evidence', async () => {
    const services = toolServices().services;
    mounted = await renderTool(<SinglePageAnalyzer />, services);
    await analyze(mounted.container);
    const rows = Array.from(mounted.container.querySelectorAll('[data-testid^="sp-finding-"]'))
      .map((node) => node.getAttribute('data-testid'))
      .filter((id) => /^sp-finding-\d+$/.test(id ?? ''));
    expect(rows).toEqual(['sp-finding-1', 'sp-finding-2', 'sp-finding-3']);
    expect(textOf(byTestId(mounted.container, 'sp-finding-severity-1'))).toBe('high');
    expect(textOf(byTestId(mounted.container, 'sp-finding-selector-1'))).toBe('head');
    expect(textOf(byTestId(mounted.container, 'sp-finding-snippet-1-pre'))).toContain('<head>');
  });

  it('filters findings by severity live', async () => {
    const services = toolServices().services;
    mounted = await renderTool(<SinglePageAnalyzer />, services);
    await analyze(mounted.container);
    click(byTestId(mounted.container, 'sp-filter-medium')!);
    await flush();
    expect(byTestId(mounted.container, 'sp-finding-2')).not.toBeNull();
    expect(byTestId(mounted.container, 'sp-finding-1')).toBeNull();
    click(byTestId(mounted.container, 'sp-filter-all')!);
    await flush();
    expect(mounted.container.querySelectorAll('[data-testid^="sp-finding-"][data-severity]').length).toBe(3);
  });

  it('shows the element inventory sorted by count', async () => {
    const services = toolServices().services;
    mounted = await renderTool(<SinglePageAnalyzer />, services);
    await analyze(mounted.container);
    await clickTab(mounted.container, 'Element inventory');
    const rows = Array.from(mounted.container.querySelectorAll('[data-testid^="sp-element-row-"]'));
    expect(rows[0].getAttribute('data-testid')).toBe('sp-element-row-links');
    expect(textOf(rows[0])).toContain('18');
  });

  it('surfaces a WAF verdict when present', async () => {
    const services = toolServices({
      '/api/analysis/single-page': () =>
        ok({
          findings: [],
          elementInventory: { counts: {}, totalElements: 0 },
          wafVerdict: { provider: 'Cloudflare', challengeClass: 'managed', confidence: 0.9 },
          scopeDiagnostics: null
        })
    }).services;
    mounted = await renderTool(<SinglePageAnalyzer />, services);
    await analyze(mounted.container);
    expect(textOf(byTestId(mounted.container, 'sp-waf'))).toContain('Cloudflare');
  });
});

describe('rendered artifact inspection', () => {
  it('renders the artifact and mounts the sandboxed picker', async () => {
    const services = toolServices().services;
    mounted = await renderTool(<SinglePageAnalyzer />, services);
    click(byTestId(mounted.container, 'sp-inspect-rendered')!);
    await waitFor(() => byTestId(mounted!.container, 'sp-artifact') !== null);
    expect((byTestId(mounted.container, 'sp-screenshot') as HTMLImageElement).getAttribute('src')).toContain('data:image/png;base64,');
    expect((byTestId(mounted.container, 'sp-picker-iframe') as HTMLIFrameElement).getAttribute('sandbox')).toBe('allow-same-origin');
  });
});

describe('offline degradation', () => {
  it('shows a disconnect message instead of throwing', async () => {
    const services = offlineServices();
    mounted = await renderTool(<SinglePageAnalyzer />, services);
    click(byTestId(mounted.container, 'sp-analyze')!);
    await waitFor(() => byTestId(mounted!.container, 'sp-error') !== null);
    expect(textOf(byTestId(mounted.container, 'sp-error'))).toContain('Backend disconnected');
  });
});