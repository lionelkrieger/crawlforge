// @vitest-environment jsdom
/**
 * Full-shell integration tests: dark-first AppShell composition, navigation
 * across the window catalog, seeded dashboard rendering, audit open flow,
 * Ctrl+K palette, pane collapse, and offline graceful degradation.
 */

import { beforeEach, describe, expect, it } from 'vitest';
import { MantineProvider } from '@mantine/core';
import { spotlight } from '@mantine/spotlight';
import { CrawlForgeShell } from '../../../src/frontend/src/shell/CrawlForgeShell';
import { HubDashboard } from '../../../src/frontend/src/shell/HubDashboard';
import { ShellProvider } from '../../../src/frontend/src/shell/ShellContext';
import { crawlForgeTheme } from '../../../src/frontend/src/theme/theme';
import type { ProjectSummary } from '../../../src/frontend/src/contracts/api/projects';
import type { AuditSummary } from '../../../src/frontend/src/contracts/api/audits';
import { upsertEntities } from '../../../src/frontend/src/store/entityTable';
import { render, byTestId, flush, click, keyDown, type, ok, offlineServices, fakeServices, installDomStubs } from './helpers';

installDomStubs();

// Window state, the global spotlight store, and Mantine portal roots
// (Modal/Spotlight exit transitions outlive short flushes) persist across
// mounts within one jsdom file; reset all three so every test starts clean.
beforeEach(() => {
  localStorage.clear();
  spotlight.close();
  document.body.replaceChildren();
});

const project = (id: string, name: string): ProjectSummary => ({
  id,
  name,
  description: `${name} crawl target`,
  tags: ['production'],
  scopeSummary: null,
  createdAt: '2026-11-01T00:00:00Z',
  updatedAt: '2026-11-10T00:00:00Z'
});

const audit = (id: string, state: AuditSummary['state']): AuditSummary => ({
  id,
  projectId: '1',
  configurationId: '1',
  state,
  phase: state === 'running' ? 'fetching' : 'done',
  startedAt: '2026-11-14T09:00:00Z',
  completedAt: state === 'completed' ? '2026-11-14T10:00:00Z' : null,
  counters: { queued: 20, fetched: 80, error: 2, blocked: 1, wafBlocked: 1 }
});

function seed(services: ReturnType<typeof offlineServices>) {
  services.store.setState((state) => ({
    projects: {
      entities: upsertEntities(state.projects.entities, [project('1', 'Acme Site'), project('2', 'Beta Blog')]),
      listPage: {
        key: '{}',
        offset: 0,
        limit: 50,
        total: 2,
        items: [project('1', 'Acme Site'), project('2', 'Beta Blog')],
        fetchedAt: '2026-11-14T12:00:00Z'
      }
    },
    audits: {
      entities: upsertEntities(state.audits.entities, [audit('7', 'running'), audit('8', 'completed')]),
      activeAuditId: null,
      listPage: {
        key: '{}',
        offset: 0,
        limit: 50,
        total: 2,
        items: [audit('7', 'running'), audit('8', 'completed')],
        fetchedAt: '2026-11-14T12:00:00Z'
      }
    }
  }));
}

describe('shell composition', () => {
  it('renders the dark-first AppShell with header, rail, panes, and status bar', async () => {
    const services = offlineServices();
    const mounted = await render(<CrawlForgeShell services={services} />);
    await flush(10);

    expect(document.documentElement.getAttribute('data-mantine-color-scheme')).toBe('dark');
    expect(byTestId(mounted.container, 'cf-appshell')).not.toBeNull();
    expect(mounted.container.textContent).toContain('CrawlForge');
    expect(byTestId(mounted.container, 'cf-nav-rail')).not.toBeNull();
    expect(byTestId(mounted.container, 'cf-statusbar')).not.toBeNull();
    expect(byTestId(mounted.container, 'cf-split')).not.toBeNull();
    expect(byTestId(mounted.container, 'connection-pill')!.textContent).toContain('Offline');
    expect(byTestId(mounted.container, 'hub-dashboard')).not.toBeNull();
    mounted.unmount();
  });

  it('renders seeded projects and audits on the hub', async () => {
    const services = offlineServices();
    seed(services);
    const mounted = await render(<CrawlForgeShell services={services} />);
    await flush(10);

    expect(byTestId(mounted.container, 'project-row-1')!.textContent).toContain('Acme Site');
    expect(byTestId(mounted.container, 'project-row-2')!.textContent).toContain('Beta Blog');
    expect(byTestId(mounted.container, 'audit-row-7')!.textContent).toContain('running');
    expect(byTestId(mounted.container, 'audit-state-8')!.textContent).toContain('completed');
    // Stat cards reflect the aggregates.
    expect(byTestId(mounted.container, 'hub-dashboard')!.textContent).toContain('160'); // 80+80 fetched
    mounted.unmount();
  });

  it('navigates the window catalog through the rail', async () => {
    const services = offlineServices();
    const mounted = await render(<CrawlForgeShell services={services} />);
    await flush(10);

    click(byTestId(mounted.container, 'nav-audits')!);
    await flush();
    expect(byTestId(mounted.container, 'audits-view')).not.toBeNull();

    click(byTestId(mounted.container, 'nav-tools')!);
    await flush();
    expect(byTestId(mounted.container, 'tools-view')).not.toBeNull();
    // The tools frame mounts the full instrument bench (WP-06-05), opening
    // on the Single-Page Analyzer; prove the mounted catalog navigates too.
    expect(byTestId(mounted.container, 'tools-workspace')).not.toBeNull();
    expect(byTestId(mounted.container, 'single-page-analyzer')).not.toBeNull();
    click(byTestId(mounted.container, 'tools-rail-rewrite')!);
    await flush();
    expect(byTestId(mounted.container, 'tool-rewrite')).not.toBeNull();

    click(byTestId(mounted.container, 'nav-settings')!);
    await flush();
    expect(byTestId(mounted.container, 'settings-empty')).not.toBeNull(); // offline

    click(byTestId(mounted.container, 'nav-hub')!);
    await flush();
    expect(byTestId(mounted.container, 'hub-dashboard')).not.toBeNull();
    mounted.unmount();
  });

  it('opens an audit into the report frame and sets it active', async () => {
    const services = offlineServices();
    seed(services);
    const mounted = await render(<CrawlForgeShell services={services} />);
    await flush(10);

    click(byTestId(mounted.container, 'open-audit-7')!);
    await flush();
    expect(byTestId(mounted.container, 'report-view')).not.toBeNull();
    expect(byTestId(mounted.container, 'report-state')!.textContent).toContain('running');
    expect(services.store.getState().audits.activeAuditId).toBe('7');
    // Lifecycle controls are live for a running audit.
    expect(byTestId(mounted.container, 'audit-pause')).not.toBeNull();
    expect(byTestId(mounted.container, 'audit-cancel')).not.toBeNull();
    mounted.unmount();
  });

  it('collapses and restores the activity pane', async () => {
    const services = offlineServices();
    const mounted = await render(<CrawlForgeShell services={services} />);
    await flush(10);

    const second = byTestId(mounted.container, 'cf-split-second')!;
    expect(second.style.display).not.toBe('none');
    click(byTestId(mounted.container, 'toggle-secondary-pane')!);
    await flush();
    expect(byTestId(mounted.container, 'cf-split-second')!.style.display).toBe('none');
    click(byTestId(mounted.container, 'toggle-secondary-pane')!);
    await flush();
    expect(byTestId(mounted.container, 'cf-split-second')!.style.display).not.toBe('none');
    mounted.unmount();
  });
});

describe('command palette (Ctrl+K)', () => {
  it('opens via the trigger and via the keyboard shortcut', async () => {
    const services = offlineServices();
    seed(services);
    const mounted = await render(<CrawlForgeShell services={services} />);
    await flush(10);

    click(byTestId(mounted.container, 'palette-trigger')!);
    await flush(250);
    const search = document.querySelector('input[placeholder^="Search navigation"]') as HTMLInputElement | null;
    expect(search).not.toBeNull();
    // Seeded audits/projects appear as actions.
    const paletteRoot = document.querySelector('.mantine-Spotlight-content') ?? document.body;
    expect(paletteRoot.textContent).toContain('Audit 7');

    // Close, then re-open with Ctrl+K (dispatch on body: Mantine's capture
    // handlers call getAttribute on the target, which Document lacks).
    keyDown(document.body, 'Escape');
    await flush(250);
    keyDown(document.body, 'k', { ctrlKey: true });
    await flush(250);
    expect(document.querySelector('input[placeholder^="Search navigation"]')).not.toBeNull();
    mounted.unmount();
  });

  it('navigates through a palette action', async () => {
    const services = offlineServices();
    const mounted = await render(<CrawlForgeShell services={services} />);
    await flush(10);

    click(byTestId(mounted.container, 'palette-trigger')!);
    await flush(250);
    const settingsAction = [...document.querySelectorAll('.mantine-Spotlight-action')].find((node) =>
      node.textContent?.includes('Settings')
    ) as HTMLElement | undefined;
    expect(settingsAction).toBeDefined();
    click(settingsAction!);
    await flush(250);
    expect(byTestId(mounted.container, 'settings-empty')).not.toBeNull();
    mounted.unmount();
  });
});

describe('offline degradation', () => {
  it('keeps every surface usable without a backend', async () => {
    const services = offlineServices();
    const mounted = await render(<CrawlForgeShell services={services} />);
    await flush(10);

    expect(byTestId(mounted.container, 'projects-empty')).not.toBeNull();
    expect(byTestId(mounted.container, 'audits-empty')).not.toBeNull();
    expect(byTestId(mounted.container, 'activity-empty')).not.toBeNull();
    expect(byTestId(mounted.container, 'status-ws')!.textContent).toContain('—');

    click(byTestId(mounted.container, 'nav-report')!);
    await flush();
    expect(byTestId(mounted.container, 'report-empty')).not.toBeNull();
    mounted.unmount();
  });
});

describe('project creation (locked two-call flow)', () => {
  it('creates the project then its default configuration with the seed URL', async () => {
    const created: ProjectSummary = {
      id: '99',
      name: 'Gamma Docs',
      description: null,
      tags: [],
      scopeSummary: null,
      createdAt: '2026-11-14T12:00:00Z',
      updatedAt: '2026-11-14T12:00:00Z'
    };
    // Specific fragment first: fakeFetch matches by substring in order.
    const { services, calls } = fakeServices({
      '/api/projects/99/configurations': () => ok({
        id: '501',
        projectId: '99',
        name: 'Default configuration',
        description: null,
        createdAt: '2026-11-14T12:00:00Z',
        updatedAt: '2026-11-14T12:00:00Z'
      }),
      '/api/projects': (call) =>
        call.method === 'POST' ? ok(created) : ok([created], { offset: 0, limit: 50, total: 1 })
    });

    const mounted = await render(
      <MantineProvider theme={crawlForgeTheme} defaultColorScheme="dark" forceColorScheme="dark">
        <ShellProvider services={services}>
          <HubDashboard />
        </ShellProvider>
      </MantineProvider>
    );
    await flush(10);

    click(byTestId(mounted.container, 'new-project-button')!);
    await flush(20);
    const nameInput = document.querySelector('[data-testid="project-name-input"]') as HTMLInputElement;
    const urlInput = document.querySelector('[data-testid="project-url-input"]') as HTMLInputElement;
    expect(nameInput).not.toBeNull();
    type(nameInput, 'Gamma Docs');
    type(urlInput, 'https://gamma.example.com');
    click(document.querySelector('[data-testid="create-project-submit"]')!);
    await flush(250);

    const projectPost = calls.find((c) => c.method === 'POST' && c.url.endsWith('/api/projects'));
    expect(projectPost).toBeDefined();
    expect(JSON.parse(projectPost!.body!)).toEqual({ name: 'Gamma Docs' });

    const configPost = calls.find((c) => c.method === 'POST' && c.url.includes('/configurations'));
    expect(configPost).toBeDefined();
    expect(configPost!.url).toBe('http://127.0.0.1:5000/api/projects/99/configurations');
    expect(JSON.parse(configPost!.body!)).toEqual({
      name: 'Default configuration',
      startUrls: ['https://gamma.example.com'],
      respectRobotsTxt: true
    });

    // Reload landed visibly: the new project row renders on the hub.
    expect(byTestId(mounted.container, 'project-row-99')!.textContent).toContain('Gamma Docs');
    // Modal content unmounts after its exit transition completes.
    await flush(500);
    expect(document.querySelector('[data-testid="create-project-submit"]')).toBeNull();
    mounted.unmount();
  });

  it('surfaces field-level validation errors from the create envelope', async () => {
    const { services } = fakeServices({
      '/api/projects': () => ({
        data: null,
        error: { code: 'validation', message: 'Invalid project', fieldErrors: { name: ['must not be empty'] } }
      })
    });
    const mounted = await render(
      <MantineProvider theme={crawlForgeTheme} defaultColorScheme="dark" forceColorScheme="dark">
        <ShellProvider services={services}>
          <HubDashboard />
        </ShellProvider>
      </MantineProvider>
    );
    await flush(10);
    click(byTestId(mounted.container, 'new-project-button')!);
    await flush(20);
    type(document.querySelector('[data-testid="project-name-input"]') as HTMLInputElement, 'X');
    type(document.querySelector('[data-testid="project-url-input"]') as HTMLInputElement, 'https://x.example.com');
    click(document.querySelector('[data-testid="create-project-submit"]')!);
    await flush(20);
    const error = document.querySelector('[data-testid="create-project-error"]')!;
    expect(error.textContent).toContain('name: must not be empty');
    mounted.unmount();
  });
});
