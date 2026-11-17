/**
 * Normalized store tests: entity tables, LRU window caches, ring buffer
 * bounds, immutability, selectors, and revision accounting.
 */

import { describe, expect, it } from 'vitest';
import {
  createCrawlForgeStore,
  createInitialState,
  createStoreActions,
  createStoreHook,
  getStoreRevision,
  auditPageKey,
  refreshFrontendHeap
} from '../../../src/frontend/src/store/crawlForgeStore';
import {
  emptyTable,
  upsertEntity,
  upsertEntities,
  removeEntity,
  tableToList
} from '../../../src/frontend/src/store/entityTable';
import {
  canonicalQueryKey,
  emptyWindowedSlice,
  putPage,
  touchPage,
  getPage,
  pageFromResponse,
  MAX_WINDOWED_PAGES
} from '../../../src/frontend/src/store/windowedCache';
import {
  emptyActivityBuffer,
  pushActivity
} from '../../../src/frontend/src/store/ringBuffer';
import {
  selectActiveAudit,
  selectAudits,
  selectProjects,
  selectSettings,
  selectSystemHealth,
  selectUrlPage,
  selectors
} from '../../../src/frontend/src/store/selectors';
import { isEntityTable, isRecentActivityBuffer, isWindowedPage } from '../../../src/frontend/src/contracts/store/guards';
import { isString } from '../../../src/frontend/src/contracts/common/guards';
import type { ProjectSummary } from '../../../src/frontend/src/contracts/api/projects';
import type { AuditSummary } from '../../../src/frontend/src/contracts/api/audits';
import type { WindowedPage } from '../../../src/frontend/src/contracts/store/state';
import { ok, err, fakeApiClient } from './helpers';

const project = (id: string, name = `Project ${id}`): ProjectSummary => ({
  id,
  name,
  description: null,
  tags: [],
  scopeSummary: null,
  createdAt: '2026-11-01T00:00:00Z',
  updatedAt: '2026-11-01T00:00:00Z'
});

const audit = (id: string, state: AuditSummary['state'] = 'running'): AuditSummary => ({
  id,
  projectId: '1',
  configurationId: '1',
  state,
  phase: 'fetching',
  startedAt: '2026-11-14T10:00:00Z',
  completedAt: null,
  counters: { queued: 10, fetched: 5, error: 1, blocked: 0 }
});

describe('entity tables', () => {
  it('upserts immutably and preserves order', () => {
    const table = emptyTable<ProjectSummary>();
    const first = upsertEntity(table, project('1'));
    const second = upsertEntity(first, project('2'));
    const updated = upsertEntity(second, project('1', 'Renamed'));

    expect(table.allIds).toEqual([]);
    expect(second.allIds).toEqual(['1', '2']);
    expect(updated.allIds).toEqual(['1', '2']);
    expect(updated.byId['1'].name).toBe('Renamed');
    expect(second.byId['1'].name).toBe('Project 1'); // original untouched
    expect(updated).not.toBe(second);
    expect(isEntityTable(updated, (item): item is ProjectSummary => typeof item === 'object' && item !== null && 'id' in item)).toBe(true);
  });

  it('removes absent ids without copying', () => {
    const table = upsertEntities(emptyTable<ProjectSummary>(), [project('1')]);
    expect(removeEntity(table, '99')).toBe(table);
    const removed = removeEntity(table, '1');
    expect(removed.allIds).toEqual([]);
    expect(tableToList(table)).toHaveLength(1);
  });
});

describe('windowed cache (LRU)', () => {
  const page = (key: string): WindowedPage<string> => ({
    key,
    offset: 0,
    limit: 10,
    total: 1,
    items: ['x'],
    fetchedAt: '2026-11-14T12:00:00Z'
  });

  it('canonicalizes query keys independent of field order', () => {
    expect(canonicalQueryKey({ limit: 10, offset: 0, sort: 'url' })).toBe(
      canonicalQueryKey({ sort: 'url', offset: 0, limit: 10 })
    );
    expect(canonicalQueryKey(undefined)).toBe('{}');
  });

  it('evicts least-recently-used pages under the bound', () => {
    let slice = emptyWindowedSlice<string>();
    for (let i = 0; i < MAX_WINDOWED_PAGES + 5; i += 1) {
      slice = putPage(slice, page(`k${i}`));
    }
    expect(Object.keys(slice.pages)).toHaveLength(MAX_WINDOWED_PAGES);
    expect(getPage(slice, 'k0')).toBeNull(); // oldest evicted
    expect(getPage(slice, `k${MAX_WINDOWED_PAGES + 4}`)).not.toBeNull();
    expect(slice.activeKey).toBe(`k${MAX_WINDOWED_PAGES + 4}`);
  });

  it('touch refreshes recency without duplication', () => {
    let slice = putPage(emptyWindowedSlice<string>(), page('a'));
    slice = putPage(slice, page('b'));
    slice = touchPage(slice, 'a');
    slice = putPage(slice, page('c'), 2);
    expect(getPage(slice, 'b')).toBeNull(); // b was least recent after touch
    expect(getPage(slice, 'a')).not.toBeNull();
    expect(Object.keys(slice.pages)).toHaveLength(2);
  });

  it('builds pages from envelope pagination and clamps items to limit', () => {
    const built = pageFromResponse('k', ['a', 'b', 'c'], { offset: 0, limit: 2, total: 3 }, 'now', 50);
    expect(built.items).toEqual(['a', 'b']);
    expect(built.total).toBe(3);
    expect(isWindowedPage(built, isString)).toBe(true);
  });
});

describe('recent-activity ring buffer', () => {
  it('bounds entries and accounts drops', () => {
    let buffer = emptyActivityBuffer(3);
    for (let i = 0; i < 5; i += 1) {
      buffer = pushActivity(buffer, { event: 'crawl-progress', receivedAt: `t${i}`, auditId: '1' });
    }
    expect(buffer.entries).toHaveLength(3);
    expect(buffer.droppedCount).toBe(2);
    expect(buffer.entries[0].receivedAt).toBe('t2');
    expect(isRecentActivityBuffer(buffer)).toBe(true);
  });
});

describe('normalized store', () => {
  it('starts empty and offline-safe', () => {
    const store = createCrawlForgeStore();
    const state = store.getState();
    expect(state.settings.loaded).toBe(false);
    expect(state.hub.connectionState).toBe('Disconnected');
    expect(state.hub.recentActivity.entries).toHaveLength(0);
    expect(getStoreRevision(store)).toBe(0);
  });

  it('bumps revision once per committed update', () => {
    const store = createCrawlForgeStore();
    const actions = createStoreActions(store, null);
    actions.setConnectionState('Connected');
    actions.setConnectionState('Connected'); // same value still commits a new hub object
    expect(getStoreRevision(store)).toBe(2);
  });

  it('loads projects through the envelope and normalizes them', async () => {
    const { api } = fakeApiClient({ '/api/projects': () => ok([project('1'), project('2')], { offset: 0, limit: 50, total: 2 }) });
    const store = createCrawlForgeStore();
    const actions = createStoreActions(store, api);
    const outcome = await actions.loadProjects();
    expect(outcome.ok).toBe(true);
    expect(store.getState().projects.entities.allIds).toEqual(['1', '2']);
    expect(store.getState().projects.listPage?.total).toBe(2);
    expect(selectProjects(store.getState())).toHaveLength(2);
  });

  it('surfaces envelope errors as outcomes, never throws', async () => {
    const { api } = fakeApiClient({ '/api/projects': () => err('internal', 'boom') });
    const store = createCrawlForgeStore();
    const actions = createStoreActions(store, api);
    const outcome = await actions.loadProjects();
    expect(outcome.ok).toBe(false);
    expect(outcome.errorCode).toBe('internal');
    expect(store.getState().projects.entities.allIds).toEqual([]);
  });

  it('loads windowed URL pages under the audit-scoped key', async () => {
    const { api } = fakeApiClient({
      '/api/audits/7/urls': () => ok([{ urlId: 'u1' }], { offset: 0, limit: 25, total: 100 })
    });
    const store = createCrawlForgeStore();
    const actions = createStoreActions(store, api);
    const outcome = await actions.loadUrlPage('7', { limit: 25 });
    expect(outcome.ok).toBe(true);
    const key = auditPageKey('7', { limit: 25 });
    const page = selectUrlPage(key)(store.getState());
    expect(page?.total).toBe(100);
    expect(page?.items).toHaveLength(1);
  });

  it('selects the active audit and settings', async () => {
    const { api } = fakeApiClient({
      '/api/audits': () => ok([audit('7')]),
      '/api/settings': () => ok({ theme: 'dark' })
    });
    const store = createCrawlForgeStore();
    const actions = createStoreActions(store, api);
    await actions.loadAudits();
    await actions.loadSettings();
    actions.setActiveAudit('7');
    expect(selectActiveAudit(store.getState())?.id).toBe('7');
    expect(selectAudits(store.getState())).toHaveLength(1);
    expect(selectSettings(store.getState())).toEqual({ theme: 'dark' });
    expect(selectSystemHealth(store.getState())).toBeNull();
    actions.setActiveAudit(null);
    expect(selectActiveAudit(store.getState())).toBeNull();
  });

  it('exposes the full locked selector contract', () => {
    expect(Object.keys(selectors).sort()).toEqual(
      [
        'selectSettings', 'selectProjects', 'selectProjectById', 'selectAudits',
        'selectActiveAudit', 'selectAuditById', 'selectUrlPage', 'selectFindingPage',
        'selectLinkPage', 'selectNgramPage', 'selectHostStates', 'selectSystemHealth',
        'selectFrontendJsHeapBytes', 'selectPlugins'
      ].sort()
    );
  });

  it('binds a callable store hook with StoreApi methods', () => {
    const store = createCrawlForgeStore();
    const hook = createStoreHook(store);
    expect(typeof hook).toBe('function');
    expect(hook.getState).toBe(store.getState);
    expect(hook.getInitialState().hub.connectionState).toBe('Disconnected');
    expect(createInitialState().urlPages.activeKey).toBeNull();
  });

  it('refreshes the frontend heap without committing when unchanged', () => {
    const store = createCrawlForgeStore();
    const before = getStoreRevision(store);
    refreshFrontendHeap(store);
    refreshFrontendHeap(store);
    // Node has no performance.memory → null both times; both are no-ops.
    expect(getStoreRevision(store)).toBe(before);
    expect(store.getState().systemHealth.frontendJsHeapBytes).toBeNull();
  });
});
