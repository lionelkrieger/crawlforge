// @vitest-environment jsdom
/**
 * Core standalone tools: robots tester, sitemap generator, rewrite preview,
 * URL check, structured-data checker. Each runs end-to-end against the fake
 * loopback API with believable payloads; validation errors surface through
 * the envelope field-error banner.
 */

import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { RobotsTesterTool } from '../../../src/frontend/src/tools/RobotsTesterTool';
import { SitemapGeneratorTool } from '../../../src/frontend/src/tools/SitemapGeneratorTool';
import { RewritePreviewTool } from '../../../src/frontend/src/tools/RewritePreviewTool';
import { UrlCheckTool } from '../../../src/frontend/src/tools/UrlCheckTool';
import { StructuredDataTool } from '../../../src/frontend/src/tools/StructuredDataTool';
import {
  auditSummary,
  byTestId,
  click,
  err,
  flush,
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

/** Picks a Mantine Select option from its portal dropdown. */
async function selectOption(container: HTMLElement, testId: string, label: string): Promise<void> {
  const input = byTestId(container, testId) as HTMLInputElement;
  click(input);
  await flush();
  const option = Array.from(document.querySelectorAll<HTMLElement>('[role="option"]')).find((node) =>
    (node.textContent ?? '').includes(label)
  );
  expect(option, `option "${label}"`).toBeDefined();
  click(option!);
  await flush();
}

describe('RobotsTesterTool', () => {
  it('runs verdicts and summarizes allow/disallow with matched rules', async () => {
    const { services, calls } = toolServices();
    mounted = await renderTool(<RobotsTesterTool />, services);
    click(byTestId(mounted.container, 'robots-run')!);
    await waitFor(() => byTestId(mounted!.container, 'robots-results') !== null);

    expect(textOf(byTestId(mounted.container, 'robots-summary'))).toBe('2 allowed · 1 disallowed');
    const disallowed = byTestId(mounted.container, 'robots-row-https://acme.example.com/admin/settings')!;
    expect(disallowed.querySelector('[data-verdict="fail"]')).not.toBeNull();
    expect(textOf(disallowed)).toContain('Disallow: /admin/');

    const post = calls.find((call) => call.url.includes('/api/tools/robots-test') && call.method === 'POST')!;
    const body = JSON.parse(post.body!);
    expect(body.userAgent).toBe('CrawlForge');
    expect(body.robotsBody).toContain('Disallow: /admin/');
    expect(body.testUrls.length).toBe(4);
  });

  it('surfaces envelope field errors in the banner', async () => {
    const { services } = toolServices({
      '/api/tools/robots-test': () => err('validation', 'Invalid request.', { testUrls: ['At least one URL is required.'] })
    });
    mounted = await renderTool(<RobotsTesterTool />, services);
    click(byTestId(mounted.container, 'robots-run')!);
    await waitFor(() => byTestId(mounted!.container, 'robots-error') !== null);
    expect(textOf(byTestId(mounted.container, 'robots-error'))).toContain('Invalid request.');
    expect(textOf(byTestId(mounted.container, 'robots-error-field-testUrls'))).toContain('At least one URL is required.');
  });
});

describe('SitemapGeneratorTool', () => {
  it('generates from a selected audit and reports the local file', async () => {
    const { services, calls } = toolServices();
    seedAudits(services, [auditSummary('1')]);
    mounted = await renderTool(<SitemapGeneratorTool />, services);
    await selectOption(mounted.container, 'sitemap-audit', 'Audit 1');
    click(byTestId(mounted.container, 'sitemap-generate')!);
    await waitFor(() => byTestId(mounted!.container, 'sitemap-result') !== null);

    expect(textOf(byTestId(mounted.container, 'sitemap-filename'))).toBe('sitemap.xml');
    expect(textOf(byTestId(mounted.container, 'sitemap-urlcount'))).toBe('96 URLs');
    const post = calls.find((call) => call.url.includes('/api/tools/sitemap-generate'))!;
    const body = JSON.parse(post.body!);
    expect(body.auditId).toBe('1');
    expect(body.filters.statusClass).toBe('2xx');
    expect(body.filters.indexable).toBe(true);
    expect(body.changeFrequency).toBe('weekly');
    expect(body.priority).toBe(0.5);
  });

  it('disables generation until an audit is chosen', async () => {
    const { services } = toolServices();
    seedAudits(services, [auditSummary('1')]);
    mounted = await renderTool(<SitemapGeneratorTool />, services);
    expect((byTestId(mounted.container, 'sitemap-generate') as HTMLButtonElement).disabled).toBe(true);
  });
});

describe('RewritePreviewTool', () => {
  it('previews before/after URLs with applied rule patterns', async () => {
    const { services } = toolServices();
    mounted = await renderTool(<RewritePreviewTool />, services);
    click(byTestId(mounted.container, 'rewrite-run')!);
    await waitFor(() => byTestId(mounted!.container, 'rewrite-results') !== null);
    const row = byTestId(mounted.container, 'rewrite-row-https://old.acme.example.com/docs/intro?utm_source=newsletter')!;
    expect(textOf(row)).toContain('https://acme.example.com/docs/intro');
    expect(row.querySelectorAll('[data-testid], .mantine-Badge-root').length).toBeGreaterThan(0);
  });

  it('adds and removes rule rows live', async () => {
    const { services } = toolServices();
    mounted = await renderTool(<RewritePreviewTool />, services);
    expect(byTestId(mounted.container, 'rewrite-rule-pattern-0')).not.toBeNull();
    expect(byTestId(mounted.container, 'rewrite-rule-pattern-1')).not.toBeNull();
    click(byTestId(mounted.container, 'rewrite-add-rule')!);
    await flush();
    expect(byTestId(mounted.container, 'rewrite-rule-pattern-2')).not.toBeNull();
    click(byTestId(mounted.container, 'rewrite-rule-remove-2')!);
    await flush();
    expect(byTestId(mounted.container, 'rewrite-rule-pattern-2')).toBeNull();
  });
});

describe('UrlCheckTool', () => {
  it('reports status, indexability, redirect chain, and headers', async () => {
    const { services } = toolServices();
    mounted = await renderTool(<UrlCheckTool />, services);
    click(byTestId(mounted.container, 'urlcheck-run')!);
    await waitFor(() => byTestId(mounted!.container, 'urlcheck-result') !== null);
    expect(textOf(byTestId(mounted.container, 'urlcheck-status'))).toBe('301 · 3xx');
    expect(byTestId(mounted.container, 'urlcheck-indexable')!.getAttribute('data-verdict')).toBe('fail');
    expect(byTestId(mounted.container, 'urlcheck-chain')).not.toBeNull();
    expect(textOf(byTestId(mounted.container, 'urlcheck-headers'))).toContain('content-type');
    expect(byTestId(mounted.container, 'urlcheck-scope')).not.toBeNull();
  });
});

describe('StructuredDataTool', () => {
  it('lists blocks with validation and rich-result verdicts', async () => {
    const { services } = toolServices();
    mounted = await renderTool(<StructuredDataTool />, services);
    click(byTestId(mounted.container, 'structured-run')!);
    await waitFor(() => byTestId(mounted!.container, 'structured-result') !== null);
    expect(byTestId(mounted.container, 'structured-block-0')).not.toBeNull();
    expect(byTestId(mounted.container, 'structured-block-1')).not.toBeNull();
    expect(textOf(byTestId(mounted.container, 'structured-rich-0'))).toBe('Rich result eligible');
    expect(textOf(byTestId(mounted.container, 'structured-issues-1'))).toContain('Missing itemListElement.');
  });

  it('accepts a pasted markup body in the request', async () => {
    const { services, calls } = toolServices();
    mounted = await renderTool(<StructuredDataTool />, services);
    typeInto(byTestId(mounted.container, 'structured-markup') as HTMLTextAreaElement, '<script type="application/ld+json">{}</script>');
    click(byTestId(mounted.container, 'structured-run')!);
    await waitFor(() => byTestId(mounted!.container, 'structured-result') !== null);
    const post = calls.find((call) => call.url.includes('/api/tools/structured-data-check'))!;
    expect(JSON.parse(post.body!).markup).toContain('application/ld+json');
  });
});