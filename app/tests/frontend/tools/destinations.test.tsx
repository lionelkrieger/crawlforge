// @vitest-environment jsdom
/**
 * Export destinations management tests (WP-06-A3): the enabled switch
 * round-trips a subset PUT ({enabled} only), credentials stay masked,
 * secret clearing sends "", and create/edit/delete/test all land visibly.
 */

import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { DestinationsManager } from '../../../src/frontend/src/tools/DestinationsManager';
import { byTestId, click, flush, renderTool, textOf, toolServices, typeInto, waitFor } from './helpers';
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

async function renderManager() {
  const { services, calls } = toolServices();
  mounted = await renderTool(<DestinationsManager />, services);
  await waitFor(() => byTestId(mounted!.container, 'destination-row-3') !== null);
  return { container: mounted.container, calls };
}

describe('list + enablement', () => {
  it('lists destinations with enabled state and masked credentials', async () => {
    const { container } = await renderManager();
    expect(byTestId(container, 'destination-row-3')!.getAttribute('data-enabled')).toBe('true');
    expect(byTestId(container, 'destination-row-4')!.getAttribute('data-enabled')).toBe('false');
    expect(textOf(byTestId(container, 'destination-mask-4'))).toBe('********');
    expect(byTestId(container, 'destination-mask-3')).toBeNull();
  });

  it('toggles delivery with a subset PUT carrying only {enabled}', async () => {
    const { container, calls } = await renderManager();
    click(byTestId(container, 'destination-enabled-4') as HTMLInputElement);
    await waitFor(() => byTestId(mounted!.container, 'destination-row-4')!.getAttribute('data-enabled') === 'true');
    const put = calls.find((call) => call.url.includes('/api/destinations/4') && call.method === 'PUT')!;
    expect(JSON.parse(put.body!)).toEqual({ enabled: true });
  });
});

describe('create + edit', () => {
  it('creates a destination and shows it in the list', async () => {
    const { container, calls } = await renderManager();
    click(byTestId(container, 'destinations-new')!);
    await flush();
    typeInto(byTestId(container, 'destination-name') as HTMLInputElement, 'Webhook sink');
    typeInto(byTestId(container, 'destination-target') as HTMLInputElement, 'https://hooks.acme.example.com/ingest');
    click(byTestId(container, 'destination-save')!);
    await waitFor(() => byTestId(mounted!.container, 'destination-row-12') !== null);
    const post = calls.find((call) => call.url.endsWith('/api/destinations') && call.method === 'POST')!;
    expect(JSON.parse(post.body!).name).toBe('Webhook sink');
  });

  it('edits with subset merge — blank secret keeps the stored credential', async () => {
    const { container, calls } = await renderManager();
    click(byTestId(container, 'destination-edit-3')!);
    await flush();
    expect((byTestId(container, 'destination-name') as HTMLInputElement).value).toBe('Nightly drop');
    typeInto(byTestId(container, 'destination-name') as HTMLInputElement, 'Renamed drop');
    click(byTestId(container, 'destination-save')!);
    await flush();
    const put = calls.find((call) => call.url.includes('/api/destinations/3') && call.method === 'PUT')!;
    const body = JSON.parse(put.body!);
    expect(body.name).toBe('Renamed drop');
    expect('secret' in body).toBe(false);
    expect('enabled' in body).toBe(false);
  });

  it('clears a stored secret by sending an empty string', async () => {
    const { container, calls } = await renderManager();
    click(byTestId(container, 'destination-edit-4')!);
    await flush();
    click(byTestId(container, 'destination-clear-secret') as HTMLInputElement);
    click(byTestId(container, 'destination-save')!);
    await flush();
    const put = calls.find((call) => call.url.includes('/api/destinations/4') && call.method === 'PUT')!;
    expect(JSON.parse(put.body!).secret).toBe('');
  });
});

describe('test + delete', () => {
  it('runs a connection test and shows reachability + credential verdicts', async () => {
    const { container } = await renderManager();
    click(byTestId(container, 'destination-test-3')!);
    await waitFor(() => byTestId(mounted!.container, 'destination-reachable-3') !== null);
    expect(byTestId(container, 'destination-reachable-3')!.getAttribute('data-verdict')).toBe('ok');
    expect(byTestId(container, 'destination-creds-3')!.getAttribute('data-verdict')).toBe('ok');
  });

  it('deletes only after explicit confirmation', async () => {
    const { container, calls } = await renderManager();
    click(byTestId(container, 'destination-delete-3')!);
    await flush();
    expect(byTestId(container, 'destination-confirm-3')).not.toBeNull();
    expect(byTestId(container, 'destination-row-3')).not.toBeNull(); // still present pre-confirm
    click(byTestId(container, 'destination-confirm-3')!);
    await waitFor(() => byTestId(mounted!.container, 'destination-row-3') === null);
    expect(calls.some((call) => call.url.includes('/api/destinations/3') && call.method === 'DELETE')).toBe(true);
  });
});