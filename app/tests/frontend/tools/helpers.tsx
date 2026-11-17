/**
 * Shared helpers for the tools suites (WP-06-05). Reuses the accepted
 * shell harness (read-only) and adds tool-route fakes with believable
 * payloads, a Mantine+Shell render wrapper, and store seeding.
 */

import type { ReactNode } from 'react';
import { act } from 'react';
import { MantineProvider } from '@mantine/core';
import { ShellProvider, type ShellServices } from '../../../src/frontend/src/shell/ShellContext';
import { crawlForgeTheme } from '../../../src/frontend/src/theme/theme';
import { upsertEntities } from '../../../src/frontend/src/store/entityTable';
import type { AuditSummary } from '../../../src/frontend/src/contracts/api/audits';
import type { Destination } from '../../../src/frontend/src/contracts/api/destinations';
import {
  render,
  byTestId,
  flush,
  click,
  type,
  fire,
  keyDown,
  ok,
  err,
  fakeFetch,
  fakeServices,
  offlineServices,
  installDomStubs,
  type FetchCall,
  type Mounted
} from '../shell/helpers';

export {
  render,
  byTestId,
  flush,
  click,
  type,
  fire,
  keyDown,
  ok,
  err,
  fakeFetch,
  fakeServices,
  offlineServices,
  installDomStubs,
  type FetchCall,
  type Mounted
};

/** All matches for a test id. */
export function byTestIdAll(container: HTMLElement, id: string): HTMLElement[] {
  return Array.from(container.querySelectorAll<HTMLElement>(`[data-testid="${id}"]`));
}

/** Trimmed text content of a node (or ''). */
export function textOf(element: Element | null): string {
  return (element?.textContent ?? '').trim();
}

/**
 * Types into a controlled Mantine form control. In Mantine 8 the
 * `data-testid` lands on the inner <input>/<textarea> itself, so the node
 * passed here IS the control. Uses the matching native prototype setter so
 * both inputs and textareas drive React's onChange.
 */
export function typeInto(control: HTMLInputElement | HTMLTextAreaElement, value: string): void {
  const proto = control.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
  const setter = Object.getOwnPropertyDescriptor(proto, 'value')?.set;
  act(() => {
    if (setter) setter.call(control, value);
    else control.value = value;
    control.dispatchEvent(new Event('input', { bubbles: true }));
  });
}

/** Polls under act until the predicate holds (bounded; throws on timeout). */
export async function waitFor(predicate: () => boolean, timeoutMs = 2000): Promise<void> {
  const started = Date.now();
  while (!predicate()) {
    if (Date.now() - started > timeoutMs) {
      throw new Error('waitFor timed out');
    }
    await flush(10);
  }
}

/** Renders a tool inside the dark-first Mantine provider + shell context. */
export function renderTool(ui: ReactNode, services: ShellServices): Promise<Mounted> {
  return render(
    <MantineProvider theme={crawlForgeTheme} forceColorScheme="dark">
      <ShellProvider services={services}>{ui}</ShellProvider>
    </MantineProvider>
  );
}

/** A believable audit summary. */
export function auditSummary(id: string, state: AuditSummary['state'] = 'completed'): AuditSummary {
  return {
    id,
    projectId: '1',
    configurationId: '1',
    state,
    phase: state === 'running' ? 'fetching' : 'done',
    startedAt: '2026-11-14T09:00:00Z',
    completedAt: state === 'completed' ? '2026-11-14T10:00:00Z' : null,
    counters: { queued: 0, fetched: 120, error: 2, blocked: 1, wafBlocked: 0 }
  };
}

/** Seeds the store's audit slice (for sitemap/llms audit pickers). */
export function seedAudits(services: ShellServices, audits: readonly AuditSummary[]): void {
  services.store.setState((state) => ({
    audits: {
      entities: upsertEntities(state.audits.entities, audits),
      activeAuditId: null,
      listPage: {
        key: '{}',
        offset: 0,
        limit: 50,
        total: audits.length,
        items: audits,
        fetchedAt: '2026-11-14T12:00:00Z'
      }
    }
  }));
}

/** A believable destination row. */
export function destination(id: string, overrides: Partial<Destination> = {}): Destination {
  return {
    id,
    name: 'Nightly drop',
    kind: 'local-folder',
    target: 'C:\\exports\\nightly',
    username: null,
    secretMask: null,
    enabled: true,
    createdAt: '2026-11-01T00:00:00Z',
    updatedAt: '2026-11-10T00:00:00Z',
    ...overrides
  };
}

/** A small rendered artifact for picker tests (structure over styling). */
export const ARTIFACT_HTML = `<!doctype html>
<html><head><title>Acme</title></head>
<body>
  <nav class="site-nav"><a href="/about">About</a></nav>
  <main>
    <article class="post">
      <h2 class="post-title">Local-first crawling</h2>
      <p class="lead">CrawlForge audits entire websites on your machine.</p>
      <p>Second paragraph with <strong>emphasis</strong>.</p>
    </article>
    <aside class="sidebar">Sidebar noise</aside>
  </main>
  <footer>Footer noise</footer>
</body></html>`;

/** Standard fake routes for the tools endpoints (believable payloads). */
export function toolRouteFakes(): Record<string, (call: FetchCall) => unknown> {
  // Stateful saved-query store so create/update/delete round-trip realistically.
  let queryItems: Array<{ id: string; name: string; ruleTree: unknown; createdAt: string; updatedAt: string }> = [
    {
      id: '5',
      name: 'Indexable success pages',
      ruleTree: { kind: 'group', operator: 'and', children: [{ kind: 'condition', field: 'statusClass', operator: 'eq', value: '2xx' }] },
      createdAt: '2026-11-01T00:00:00Z',
      updatedAt: '2026-11-10T00:00:00Z'
    }
  ];
  return {
    '/api/tools/robots-test': () =>
      ok({
        results: [
          { url: 'https://acme.example.com/', allowed: true, matchedRule: null },
          { url: 'https://acme.example.com/admin/settings', allowed: false, matchedRule: 'Disallow: /admin/' },
          { url: 'https://acme.example.com/admin/public-docs', allowed: true, matchedRule: 'Allow: /admin/public-docs' }
        ]
      }),
    '/api/tools/url-check': () =>
      ok({
        statusCode: 301,
        statusClass: '3xx',
        redirectChain: [{ url: 'https://acme.example.com/old-page', statusCode: 301, location: 'https://acme.example.com/new-page' }],
        headers: { 'content-type': 'text/html', location: 'https://acme.example.com/new-page' },
        indexable: false,
        wafVerdict: null,
        scopeDiagnostics: { included: true, matchedIncludeSelectors: ['main'], matchedExcludeSelectors: [], scopedWordCount: 320, totalWordCount: 400 }
      }),
    '/api/tools/structured-data-check': () =>
      ok({
        blocks: [
          {
            format: 'json-ld',
            type: 'Article',
            properties: { headline: 'Local-first crawling' },
            valid: true,
            richResultEligible: true,
            issues: []
          },
          {
            format: 'microdata',
            type: 'BreadcrumbList',
            properties: {},
            valid: false,
            richResultEligible: false,
            issues: ['Missing itemListElement.']
          }
        ]
      }),
    '/api/tools/render-test': () =>
      ok({
        screenshotBase64: 'aGVsbG8=',
        renderedHtml: ARTIFACT_HTML,
        consoleMessages: [
          { level: 'warn', text: 'Deprecated API used', timestamp: null },
          { level: 'error', text: 'Failed to load resource', timestamp: null }
        ],
        wafVerdict: null,
        pickerActive: true
      }),
    '/api/tools/sitemap-generate': () =>
      ok({ fileName: 'sitemap.xml', filePath: 'C:\\exports\\sitemap.xml', urlCount: 96, isIndex: false }),
    '/api/tools/scope-preview': () =>
      ok({
        included: true,
        matchedIncludeSelectors: ['main article'],
        matchedExcludeSelectors: ['nav'],
        scopedTextPreview: 'Local-first crawling CrawlForge audits entire websites on your machine.'
      }),
    '/api/tools/rewrite-preview': () =>
      ok({
        results: [
          {
            originalUrl: 'https://old.acme.example.com/docs/intro?utm_source=newsletter',
            rewrittenUrl: 'https://acme.example.com/docs/intro',
            appliedRulePatterns: ['^https://old\\.acme\\.example\\.com', '\\?utm_[^=]+=[^&]*&?']
          }
        ]
      }),
    '/api/tools/llms-generate': () =>
      ok({ variant: 'llms-txt', fileName: 'llms.txt', filePath: 'C:\\exports\\llms.txt' }),
    '/api/tools/llms-validate': () => ok({ valid: false, issues: ['Missing H1 title.', 'Link is not absolute: /docs'] }),
    '/api/analysis/single-page': () =>
      ok({
        findings: [
          {
            id: '1',
            ruleKey: 'meta.title.missing',
            severity: 'high',
            category: 'metadata',
            urlId: null,
            message: 'The page has no title element.',
            evidence: { snippet: '<head></head>', selector: 'head' }
          },
          {
            id: '2',
            ruleKey: 'content.thin',
            severity: 'medium',
            category: 'content',
            urlId: null,
            message: 'Visible word count is below the threshold.',
            evidence: null
          },
          {
            id: '3',
            ruleKey: 'links.broken',
            severity: 'low',
            category: 'links',
            urlId: null,
            message: 'One outbound link returned 404.',
            evidence: null
          }
        ],
        elementInventory: { counts: { headings: 4, links: 18, images: 3, document_head: 1 }, totalElements: 26 },
        wafVerdict: null,
        scopeDiagnostics: { included: true, matchedIncludeSelectors: [], matchedExcludeSelectors: [], scopedWordCount: 210, totalWordCount: 240 }
      }),
    '/api/queries': (call: FetchCall) => {
      if (call.method === 'GET') return ok(queryItems);
      if (call.method === 'POST') {
        const body = JSON.parse(call.body ?? '{}') as { name: string; ruleTree: unknown };
        const created = { id: String(queryItems.length + 10), name: body.name, ruleTree: body.ruleTree, createdAt: '2026-11-14T00:00:00Z', updatedAt: '2026-11-14T00:00:00Z' };
        queryItems = [...queryItems, created];
        return ok(created);
      }
      if (call.method === 'PUT') {
        const body = JSON.parse(call.body ?? '{}') as { id: string; name?: string; ruleTree?: unknown };
        queryItems = queryItems.map((item) =>
          item.id === body.id
            ? { ...item, name: body.name ?? item.name, ruleTree: body.ruleTree ?? item.ruleTree, updatedAt: '2026-11-14T00:00:00Z' }
            : item
        );
        return ok(queryItems.find((item) => item.id === body.id));
      }
      // DELETE carries the id in the query string (no path segment).
      const id = new URL(call.url).searchParams.get('id') ?? '';
      queryItems = queryItems.filter((item) => item.id !== id);
      return ok({ id, deleted: true });
    }
  };
}

/**
 * Stateful destination routes: list/create/update/delete/test with
 * subset-merge semantics (absent keeps, '' clears secret, enabled toggles).
 */
export function destinationRoutes(initial?: readonly Destination[]): Record<string, (call: FetchCall) => unknown> {
  let items: Destination[] = [
    ...(initial ?? [
      destination('3'),
      destination('4', { name: 'Backup SFTP', kind: 'sftp', target: 'files.acme.example.com:/backups', username: 'backup', secretMask: '********', enabled: false })
    ])
  ];
  return {
    '/api/destinations': (call: FetchCall) => {
      const idMatch = /\/api\/destinations\/([^/?]+)/.exec(call.url);
      const id = idMatch?.[1] ?? null;
      if (call.method === 'GET') return ok(items);
      if (call.method === 'POST' && call.url.includes('/test')) {
        return ok({ reachable: true, credentialsValid: true, message: 'Connected.' });
      }
      if (call.method === 'POST') {
        const body = JSON.parse(call.body ?? '{}') as Partial<Destination> & { secret?: string };
        const created = destination(String(items.length + 10), {
          name: body.name ?? 'New',
          kind: body.kind ?? 'local-folder',
          target: body.target ?? '',
          username: body.username ?? null,
          secretMask: body.secret ? '********' : null
        });
        items = [...items, created];
        return ok(created);
      }
      if (call.method === 'PUT' && id) {
        const body = JSON.parse(call.body ?? '{}') as Record<string, unknown>;
        items = items.map((item) => {
          if (item.id !== id) return item;
          return {
            ...item,
            ...(typeof body.name === 'string' ? { name: body.name } : {}),
            ...(typeof body.kind === 'string' ? { kind: body.kind } : {}),
            ...(typeof body.target === 'string' ? { target: body.target } : {}),
            ...('username' in body ? { username: (body.username as string | null) ?? null } : {}),
            ...(body.secret === '' ? { secretMask: null } : typeof body.secret === 'string' ? { secretMask: '********' } : {}),
            ...(typeof body.enabled === 'boolean' ? { enabled: body.enabled } : {})
          };
        });
        return ok(items.find((item) => item.id === id));
      }
      if (call.method === 'DELETE' && id) {
        items = items.filter((item) => item.id !== id);
        return ok({ id, deleted: true });
      }
      return err('bad-request', 'Unsupported destinations call.');
    }
  };
}

/** Full tool service graph: tool routes + stateful destinations. */
export function toolServices(extra: Record<string, (call: FetchCall) => unknown> = {}) {
  return fakeServices({ ...toolRouteFakes(), ...destinationRoutes(), ...extra });
}