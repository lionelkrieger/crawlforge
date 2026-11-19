// @vitest-environment jsdom
/**
 * ConfigurationsList component tests (WP-09-07, UI-03): REST CRUD through the
 * typed loopback client (mock fetch), the "Configurations" heading, the
 * exact-text "New configuration" / "Edit" affordances, duplicate and
 * two-step delete, and the embedded editor save path.
 */

import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { ConfigurationsList } from '../../../src/frontend/src/surfaces/project/ConfigurationsList';
import {
  byTestId,
  click,
  configurationRoutes,
  configurationSummaryFixture,
  fakeApiClient,
  flush,
  renderProject,
  textOf,
  typeInto,
  waitFor,
  type Mounted
} from './helpers';

let mounted: Mounted | null = null;

beforeEach(() => {
  document.body.replaceChildren();
});

afterEach(() => {
  mounted?.unmount();
  mounted = null;
});

async function renderList(seed?: ReturnType<typeof configurationSummaryFixture>[]) {
  const { routes, current } = configurationRoutes(seed);
  const { api, calls } = fakeApiClient(routes);
  mounted = await renderProject(<ConfigurationsList projectId="3" api={api} />);
  await waitFor(() => byTestId(mounted!.container, 'configuration-row-7') !== null || byTestId(mounted!.container, 'configurations-empty') !== null);
  return { container: mounted.container, calls, current };
}

describe('list surface', () => {
  it('renders the Configurations heading and loads rows via REST', async () => {
    const { container, calls } = await renderList();
    expect(textOf(byTestId(container, 'configurations-heading'))).toBe('Configurations');
    expect(byTestId(container, 'configuration-row-7')).not.toBeNull();
    expect(textOf(byTestId(container, 'configuration-name-7'))).toBe('Full crawl');
    const list = calls.find((call) => call.method === 'GET' && call.url.includes('/api/projects/3/configurations'));
    expect(list).toBeDefined();
  });

  it('carries the exact-text New configuration affordance', async () => {
    const { container } = await renderList();
    expect(textOf(byTestId(container, 'configurations-new'))).toBe('New configuration');
  });

  it('filters rows by the search input', async () => {
    const { container } = await renderList([configurationSummaryFixture('7', 'Full crawl'), configurationSummaryFixture('8', 'Quick sample')]);
    await waitFor(() => byTestId(container, 'configuration-row-8') !== null);
    typeInto(byTestId(container, 'configurations-search') as HTMLInputElement, 'quick');
    await flush();
    expect(byTestId(container, 'configuration-row-7')).toBeNull();
    expect(byTestId(container, 'configuration-row-8')).not.toBeNull();
  });

  it('shows the offline note without an API client', async () => {
    mounted = await renderProject(<ConfigurationsList projectId="3" api={null} />);
    await flush();
    expect(byTestId(mounted.container, 'configurations-offline')).not.toBeNull();
  });
});

describe('CRUD', () => {
  it('creates through POST with the engine scopeRules document', async () => {
    const { container, calls, current } = await renderList();
    click(byTestId(container, 'configurations-new')!);
    await waitFor(() => byTestId(container, 'configuration-editor') !== null);
    // The embedded editor satisfies the UI-04 tab contract.
    expect(container.querySelectorAll('[role="tab"]').length).toBe(13);
    typeInto(byTestId(container, 'config-name') as HTMLInputElement, 'Quick sample');
    typeInto(byTestId(container, 'config-start-urls') as HTMLTextAreaElement, 'http://127.0.0.1:47311/');
    click(byTestId(container, 'configuration-save')!);
    await waitFor(() => current().some((item) => item.name === 'Quick sample'));
    const post = calls.find((call) => call.method === 'POST' && call.url.includes('/api/projects/3/configurations'))!;
    const body = JSON.parse(post.body!) as Record<string, unknown>;
    expect(body.name).toBe('Quick sample');
    expect(body.startUrls).toEqual(['http://127.0.0.1:47311/']);
    expect((body.scopeRules as Record<string, unknown>).cookieJar).toEqual({ enabled: true });
    expect(body.limits).toEqual({ maxUrls: null, maxDepth: null, maxDurationSeconds: null, maxResponseBytes: null });
    // Back on the list with the new row.
    await waitFor(() => byTestId(container, 'configurations-new') !== null);
    expect(current().length).toBe(2);
  });

  it('opens an existing configuration via GET and saves via PUT', async () => {
    const { container, calls } = await renderList();
    click(byTestId(container, 'configuration-edit-7')!);
    await waitFor(() => byTestId(container, 'configuration-editor') !== null);
    expect((byTestId(container, 'config-name') as HTMLInputElement).value).toBe('Full crawl');
    const get = calls.find((call) => call.method === 'GET' && call.url.includes('/api/configurations/7'));
    expect(get).toBeDefined();
    typeInto(byTestId(container, 'config-name') as HTMLInputElement, 'Full crawl v2');
    click(byTestId(container, 'configuration-save')!);
    await waitFor(() => calls.some((call) => call.method === 'PUT' && call.url.includes('/api/configurations/7')));
    const put = calls.find((call) => call.method === 'PUT' && call.url.includes('/api/configurations/7'))!;
    const body = JSON.parse(put.body!) as Record<string, unknown>;
    expect(body.name).toBe('Full crawl v2');
    expect(body.scopeRules).toBeDefined();
    await flush();
  });

  it('duplicates via GET + POST with a (copy) name', async () => {
    const { container, calls, current } = await renderList();
    click(byTestId(container, 'configuration-duplicate-7')!);
    await waitFor(() => current().some((item) => item.name === 'Full crawl (copy)'));
    const post = calls.find((call) => call.method === 'POST' && call.url.includes('/api/projects/3/configurations'))!;
    expect(JSON.parse(post.body!).name).toBe('Full crawl (copy)');
  });

  it('deletes with a two-step confirm via DELETE', async () => {
    const { container, calls, current } = await renderList();
    click(byTestId(container, 'configuration-delete-7')!);
    await flush();
    const confirm = byTestId(container, 'configuration-delete-confirm-7')!;
    expect(textOf(confirm)).toBe('Confirm delete');
    click(confirm);
    await waitFor(() => current().length === 0);
    const del = calls.find((call) => call.method === 'DELETE' && call.url.includes('/api/configurations/7'));
    expect(del).toBeDefined();
    await waitFor(() => byTestId(container, 'configurations-empty') !== null);
  });

  it('surfaces envelope errors', async () => {
    const { api } = fakeApiClient({
      '/configurations': () => ({ data: null, error: { code: 'internal', message: 'Storage unavailable.' } })
    });
    mounted = await renderProject(<ConfigurationsList projectId="3" api={api} />);
    await waitFor(() => byTestId(mounted!.container, 'configurations-error') !== null);
    expect(textOf(byTestId(mounted.container, 'configurations-error'))).toContain('Storage unavailable.');
  });

  it('blocks saving an invalid configuration with a descriptive error', async () => {
    const { container, calls } = await renderList();
    click(byTestId(container, 'configurations-new')!);
    await waitFor(() => byTestId(container, 'configuration-editor') !== null);
    // No start URLs → validation rejects before any POST.
    click(byTestId(container, 'configuration-save')!);
    await flush();
    expect(textOf(byTestId(container, 'configurations-error'))).toContain('start URL');
    expect(calls.some((call) => call.method === 'POST')).toBe(false);
  });
});
