// @vitest-environment jsdom
/**
 * ExportDestinationsSection proofs (WP-09-10, UI-21): exact heading, CRUD
 * with subset-merge PUT semantics, enable/disable toggle, secret masking +
 * clear, test-connection verdicts, and confirmed delete — all over the
 * stateful destinations fake with real wire shapes.
 */

import { describe, expect, it } from 'vitest';
import { ExportDestinationsSection } from '../../../src/frontend/src/surfaces/export/ExportDestinationsSection';
import { byTestId, click, exportServices, flush, renderTool, textOf, typeInto, waitFor, type FetchCall } from './helpers';

function findCall(calls: FetchCall[], method: string, fragment: string): FetchCall | undefined {
  return calls.find((call) => call.method === method && call.url.includes(fragment));
}

describe('ExportDestinationsSection', () => {
  it('renders the exact "Export destinations" heading with masked secrets', async () => {
    const { services } = exportServices();
    const mounted = await renderTool(<ExportDestinationsSection />, services);
    await waitFor(() => Boolean(byTestId(mounted.container, 'export-destination-row-3')));

    const section = byTestId(mounted.container, 'export-destinations-section')!;
    expect(textOf(section.querySelector('h3'))).toBe('Export destinations');
    expect(byTestId(mounted.container, 'export-destination-row-3')!.getAttribute('data-enabled')).toBe('true');
    expect(byTestId(mounted.container, 'export-destination-row-4')!.getAttribute('data-enabled')).toBe('false');
    expect(textOf(byTestId(mounted.container, 'export-destination-mask-4'))).toBe('********');
    mounted.unmount();
  });

  it('toggles enablement with a subset PUT carrying only {enabled}', async () => {
    const { services, calls } = exportServices();
    const mounted = await renderTool(<ExportDestinationsSection />, services);
    await waitFor(() => Boolean(byTestId(mounted.container, 'export-destination-enabled-4')));

    click(byTestId(mounted.container, 'export-destination-enabled-4')!);
    await waitFor(() => Boolean(findCall(calls, 'PUT', '/api/destinations/4')));

    const put = findCall(calls, 'PUT', '/api/destinations/4')!;
    expect(JSON.parse(put.body!)).toEqual({ enabled: true });
    await waitFor(() => byTestId(mounted.container, 'export-destination-row-4')!.getAttribute('data-enabled') === 'true');
    mounted.unmount();
  });

  it('creates a destination with write-only secret', async () => {
    const { services, calls } = exportServices();
    const mounted = await renderTool(<ExportDestinationsSection />, services);
    await waitFor(() => Boolean(byTestId(mounted.container, 'export-destinations-new')));

    click(byTestId(mounted.container, 'export-destinations-new')!);
    await flush(20);
    typeInto(byTestId(mounted.container, 'export-destination-name') as HTMLInputElement, 'Fixture drop');
    typeInto(byTestId(mounted.container, 'export-destination-target') as HTMLInputElement, 'C:\\exports\\fixture');
    typeInto(byTestId(mounted.container, 'export-destination-secret') as HTMLInputElement, 's3cret');
    click(byTestId(mounted.container, 'export-destination-save')!);
    await waitFor(() => Boolean(findCall(calls, 'POST', '/api/destinations')));

    const post = findCall(calls, 'POST', '/api/destinations')!;
    expect(JSON.parse(post.body!)).toEqual({
      name: 'Fixture drop',
      kind: 'local-folder',
      target: 'C:\\exports\\fixture',
      secret: 's3cret'
    });
    // The created row shows a mask, never the clear secret.
    await waitFor(() => textOf(byTestId(mounted.container, 'export-destinations-table')).includes('Fixture drop'));
    expect(textOf(byTestId(mounted.container, 'export-destinations-table'))).not.toContain('s3cret');
    mounted.unmount();
  });

  it('edits with subset merge: only changed fields travel', async () => {
    const { services, calls } = exportServices();
    const mounted = await renderTool(<ExportDestinationsSection />, services);
    await waitFor(() => Boolean(byTestId(mounted.container, 'export-destination-edit-3')));

    click(byTestId(mounted.container, 'export-destination-edit-3')!);
    await flush(20);
    typeInto(byTestId(mounted.container, 'export-destination-name') as HTMLInputElement, 'Renamed drop');
    click(byTestId(mounted.container, 'export-destination-save')!);
    await waitFor(() => Boolean(findCall(calls, 'PUT', '/api/destinations/3')));

    expect(JSON.parse(findCall(calls, 'PUT', '/api/destinations/3')!.body!)).toEqual({ name: 'Renamed drop' });
    mounted.unmount();
  });

  it('clears the stored secret with an empty-string secret field', async () => {
    const { services, calls } = exportServices();
    const mounted = await renderTool(<ExportDestinationsSection />, services);
    await waitFor(() => Boolean(byTestId(mounted.container, 'export-destination-edit-4')));

    click(byTestId(mounted.container, 'export-destination-edit-4')!);
    await flush(20);
    click(byTestId(mounted.container, 'export-destination-clear-secret')!);
    click(byTestId(mounted.container, 'export-destination-save')!);
    await waitFor(() => Boolean(findCall(calls, 'PUT', '/api/destinations/4')));

    expect(JSON.parse(findCall(calls, 'PUT', '/api/destinations/4')!.body!)).toEqual({ secret: '' });
    await waitFor(() => byTestId(mounted.container, 'export-destination-mask-4') === null);
    mounted.unmount();
  });

  it('runs test-connection and shows both verdicts', async () => {
    const { services, calls } = exportServices();
    const mounted = await renderTool(<ExportDestinationsSection />, services);
    await waitFor(() => Boolean(byTestId(mounted.container, 'export-destination-test-3')));

    click(byTestId(mounted.container, 'export-destination-test-3')!);
    await waitFor(() => Boolean(byTestId(mounted.container, 'export-destination-reachable-3')));

    expect(findCall(calls, 'POST', '/api/destinations/3/test')).toBeDefined();
    expect(byTestId(mounted.container, 'export-destination-reachable-3')!.getAttribute('data-verdict')).toBe('ok');
    expect(byTestId(mounted.container, 'export-destination-creds-3')!.getAttribute('data-verdict')).toBe('ok');
    mounted.unmount();
  });

  it('deletes only after explicit confirmation', async () => {
    const { services, calls } = exportServices();
    const mounted = await renderTool(<ExportDestinationsSection />, services);
    await waitFor(() => Boolean(byTestId(mounted.container, 'export-destination-delete-3')));

    click(byTestId(mounted.container, 'export-destination-delete-3')!);
    await flush(20);
    expect(findCall(calls, 'DELETE', '/api/destinations/3')).toBeUndefined(); // not yet
    click(byTestId(mounted.container, 'export-destination-confirm-3')!);
    await waitFor(() => Boolean(findCall(calls, 'DELETE', '/api/destinations/3')));
    await waitFor(() => byTestId(mounted.container, 'export-destination-row-3') === null);
    mounted.unmount();
  });
});