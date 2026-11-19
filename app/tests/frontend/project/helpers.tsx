// @vitest-environment jsdom
/**
 * Shared helpers + REAL REST DTO fixtures for the project surface suites
 * (WP-09-07). Fixture shapes are copied from the Api wire contracts
 * (ProjectWireContracts.cs: AuditConfigurationWire / ConfigurationSummaryWire
 * / ProjectSummaryWire) and the ContractConformance configuration seed —
 * real-producer → real-consumer, not invented shapes.
 */

import type { ReactNode } from 'react';
import { act } from 'react';
import { MantineProvider } from '@mantine/core';
import { crawlForgeTheme } from '../../../src/frontend/src/theme/theme';
import type { AuditConfiguration, ConfigurationSummary, ProjectSummary } from '../../../src/frontend/src/contracts/api/projects';
import type { AuditSummary } from '../../../src/frontend/src/contracts/api/audits';
import {
  render,
  byTestId,
  flush,
  click,
  type,
  fire,
  ok,
  err,
  fakeFetch,
  fakeApiClient,
  installDomStubs,
  type FetchCall,
  type Mounted
} from '../shell/helpers';

export { render, byTestId, flush, click, type, fire, ok, err, fakeFetch, fakeApiClient, installDomStubs, type FetchCall, type Mounted };

/** All matches for a test id. */
export function byTestIdAll(container: HTMLElement, id: string): HTMLElement[] {
  return Array.from(container.querySelectorAll<HTMLElement>(`[data-testid="${id}"]`));
}

/** Trimmed text content of a node (or ''). */
export function textOf(element: Element | null): string {
  return (element?.textContent ?? '').trim();
}

/** Types into a controlled Mantine input/textarea via the native setter. */
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
    if (Date.now() - started > timeoutMs) throw new Error('waitFor timed out');
    await flush(10);
  }
}

/** Renders a project surface inside the dark-first Mantine provider (no shell). */
export function renderProject(ui: ReactNode): Promise<Mounted> {
  return render(
    <MantineProvider theme={crawlForgeTheme} forceColorScheme="dark">
      {ui}
    </MantineProvider>
  );
}

/** Picks a Mantine Select option by its visible label (portal-rendered). */
export async function selectOption(container: HTMLElement, selectTestId: string, optionLabel: string): Promise<void> {
  const input = byTestId(container, selectTestId)!;
  click(input);
  await flush(20);
  const option = Array.from(document.querySelectorAll<HTMLElement>('[role="option"]'))
    .find((node) => textOf(node) === optionLabel);
  if (!option) throw new Error(`Select option not found: ${optionLabel}`);
  click(option);
  await flush(20);
}

/** Clicks a Mantine Tabs tab by its exact visible label. */
export async function clickTab(container: HTMLElement, label: string): Promise<void> {
  const tab = Array.from(container.querySelectorAll<HTMLElement>('[role="tab"]'))
    .find((node) => textOf(node) === label);
  if (!tab) throw new Error(`Tab not found: ${label}`);
  click(tab);
  await flush(20);
}

// ── Real REST DTO fixtures (Api wire contract shapes) ───────────────────────

/**
 * Full configuration DTO exactly as GET /api/configurations/{id} returns it
 * (AuditConfigurationWire, camelCase; limits/aimd/rendering per the
 * ContractConformance seed + ProjectWireMapper.DefaultAimd).
 */
export const configurationWireFixture: AuditConfiguration = {
  id: '7',
  projectId: '3',
  name: 'Full crawl',
  description: 'Fixture full crawl profile',
  createdAt: '2026-11-14T09:00:00Z',
  updatedAt: '2026-11-15T10:30:00Z',
  startUrls: ['http://127.0.0.1:47311/'],
  limits: { maxUrls: 2, maxDepth: 1, maxDurationSeconds: 30, maxResponseBytes: null },
  aimd: { initialRateR0: 2.0, alpha: 0.5, beta: 0.5, kappa: 4, ewmaTtfbWeight: 0.2 },
  rendering: { enabled: false, deviceProfile: null, infiniteScroll: false, captureConsole: false },
  respectRobotsTxt: true,
  virtualRobotsTxt: null
};

/** Configuration summary row (ConfigurationSummaryWire). */
export function configurationSummaryFixture(id: string, name: string): ConfigurationSummary {
  return {
    id,
    projectId: '3',
    name,
    description: null,
    createdAt: '2026-11-14T09:00:00Z',
    updatedAt: '2026-11-15T10:30:00Z'
  };
}

/** Project summary row (ProjectSummaryWire). */
export const projectSummaryFixture: ProjectSummary = {
  id: '3',
  name: 'Fixture project',
  description: 'WP-09-07 fixture project',
  tags: ['fixture'],
  scopeSummary: 'http://127.0.0.1:47311/',
  createdAt: '2026-11-14T08:00:00Z',
  updatedAt: '2026-11-15T10:30:00Z'
};

/** Audit summary row (AuditSummaryWire shape). */
export function auditSummaryFixture(id: string, state: AuditSummary['state'] = 'completed'): AuditSummary {
  return {
    id,
    projectId: '3',
    configurationId: '7',
    state,
    phase: state === 'running' ? 'fetching' : 'done',
    startedAt: '2026-11-14T09:00:00Z',
    completedAt: state === 'completed' ? '2026-11-14T10:00:00Z' : null,
    counters: { queued: 0, fetched: 120, error: 2, blocked: 1, wafBlocked: 0 }
  };
}

/**
 * Stateful in-memory configurations CRUD over fake fetch — the real route
 * paths and envelope shapes the Api serves.
 */
export function configurationRoutes(seed: ConfigurationSummary[] = [configurationSummaryFixture('7', 'Full crawl')]) {
  let items = [...seed];
  let nextId = 100;
  const full = (summary: ConfigurationSummary): AuditConfiguration => ({
    ...configurationWireFixture,
    id: summary.id,
    name: summary.name,
    description: summary.description
  });
  return {
    routes: {
      '/configurations': (call: FetchCall) => {
        const url = call.url;
        if (call.method === 'GET' && url.includes('/api/projects/')) {
          return ok(items, { offset: 0, limit: 200, total: items.length });
        }
        if (call.method === 'POST' && url.includes('/api/projects/')) {
          const body = JSON.parse(call.body ?? '{}') as Record<string, unknown>;
          const created = configurationSummaryFixture(String(nextId++), String(body.name ?? 'unnamed'));
          items = [...items, created];
          return ok({ ...full(created), ...(body.scopeRules ? { scopeRules: body.scopeRules } : {}) });
        }
        const id = url.split('/api/configurations/')[1]?.split('?')[0];
        if (call.method === 'GET') {
          const found = items.find((item) => item.id === id);
          return found ? ok(full(found)) : err('not-found', `Configuration ${id} not found.`);
        }
        if (call.method === 'PUT') {
          const body = JSON.parse(call.body ?? '{}') as Record<string, unknown>;
          items = items.map((item) => (item.id === id ? { ...item, name: String(body.name ?? item.name) } : item));
          const updated = items.find((item) => item.id === id)!;
          return ok({ ...full(updated), ...(body.scopeRules ? { scopeRules: body.scopeRules } : {}) });
        }
        if (call.method === 'DELETE') {
          items = items.filter((item) => item.id !== id);
          return ok({ id, deleted: true });
        }
        return err('bad-request', 'Unsupported configurations call.');
      }
    } as Record<string, (call: FetchCall) => unknown>,
    current: () => items
  };
}
