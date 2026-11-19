// @vitest-environment jsdom
/**
 * ExportDialog proofs (WP-09-10, UI-20): format/dataset/column selection,
 * dynamic extraction columns, enabled-only destination offering, the exact
 * create wire body, bounded file polling + local download, and error paths.
 * All REST interactions run against fake-fetch routes with real wire shapes.
 *
 * The dialog renders through a Mantine Modal portal on document.body, so
 * queries target the portal root rather than the mount container.
 */

import { afterEach, describe, expect, it } from 'vitest';
import { ExportDialog } from '../../../src/frontend/src/surfaces/export/ExportDialog';
import {
  byTestId,
  click,
  exportServices,
  flush,
  offlineServices,
  renderTool,
  selectOption,
  stubObjectUrl,
  textOf,
  waitFor,
  type FetchCall,
  type Mounted
} from './helpers';

const AUDIT_ID = '7';

/** Dialog content lives in the Modal portal on document.body. */
function dlg(id: string): HTMLElement | null {
  return byTestId(document.body, id);
}

function findCall(calls: FetchCall[], method: string, fragment: string): FetchCall | undefined {
  return calls.find((call) => call.method === method && call.url.includes(fragment));
}

describe('ExportDialog', () => {
  let objectUrls: ReturnType<typeof stubObjectUrl> | null = null;
  let mounted: Mounted | null = null;

  // Deterministic cleanup: a failed assertion must never leak a Modal portal
  // into the next test (portals live on document.body, outside the container).
  afterEach(() => {
    mounted?.unmount();
    mounted = null;
    objectUrls?.restore();
    objectUrls = null;
  });

  it('offers baseline + additive formats and defaults to the baseline datasets', async () => {
    const { services, calls } = exportServices();
    mounted = await renderTool(<ExportDialog auditId={AUDIT_ID} open onClose={() => undefined} />, services);
    await flush(20);

    expect(dlg('export-dialog')).not.toBeNull();
    for (const key of ['summary', 'urls', 'findings', 'links']) {
      const checkbox = dlg(`export-dataset-${key}`) as HTMLInputElement;
      expect(checkbox.checked).toBe(true);
    }
    const ngrams = dlg('export-dataset-ngrams') as HTMLInputElement;
    expect(ngrams.checked).toBe(false);

    // Additive formats are offered in the format select.
    await selectOption(document.body, 'export-format', 'JSON Lines — agent bulk (.jsonl)');
    click(dlg('export-start')!);
    await waitFor(() => Boolean(findCall(calls, 'POST', '/api/exports')));
    expect(JSON.parse(findCall(calls, 'POST', '/api/exports')!.body!).format).toBe('jsonl');
  });

  it('sends the exact create wire body (auditId/format/datasets; no destination when local)', async () => {
    const { services, calls } = exportServices();
    mounted = await renderTool(<ExportDialog auditId={AUDIT_ID} open onClose={() => undefined} />, services);
    await flush(20);

    click(dlg('export-start')!);
    await waitFor(() => Boolean(findCall(calls, 'POST', '/api/exports')));

    const create = findCall(calls, 'POST', '/api/exports')!;
    expect(JSON.parse(create.body!)).toEqual({
      auditId: AUDIT_ID,
      format: 'xlsx',
      datasets: ['summary', 'urls', 'findings', 'links']
    });
  });

  it('honors format + dataset selection in the create body', async () => {
    const { services, calls } = exportServices();
    mounted = await renderTool(<ExportDialog auditId={AUDIT_ID} open onClose={() => undefined} />, services);
    await flush(20);

    await selectOption(document.body, 'export-format', 'JSON — agent dataset (.json)');
    click(dlg('export-dataset-findings')!); // toggle off
    click(dlg('export-dataset-ngrams')!); // toggle on
    click(dlg('export-start')!);
    await waitFor(() => Boolean(findCall(calls, 'POST', '/api/exports')));

    const body = JSON.parse(findCall(calls, 'POST', '/api/exports')!.body!);
    expect(body.format).toBe('json');
    expect(body.datasets).toEqual(['summary', 'urls', 'links', 'ngrams']);
  });

  it('fetches dynamic extraction columns when custom-extraction is selected', async () => {
    const { services, calls } = exportServices();
    mounted = await renderTool(<ExportDialog auditId={AUDIT_ID} open onClose={() => undefined} />, services);
    await flush(20);

    expect(dlg('export-column-custom-extraction-custom.Price.0')).toBeNull();
    click(dlg('export-dataset-custom-extraction')!);
    await waitFor(() => Boolean(dlg('export-column-custom-extraction-custom.Price.0')));

    const fetch = findCall(calls, 'GET', '/extracted-content')!;
    expect(fetch.url).toContain(`/api/audits/${AUDIT_ID}/extracted-content`);
    expect(dlg('export-column-custom-extraction-custom.Price.1')).not.toBeNull();
  });

  it('sends an explicit column selection map when columns are deselected', async () => {
    const { services, calls } = exportServices();
    mounted = await renderTool(<ExportDialog auditId={AUDIT_ID} open onClose={() => undefined} />, services);
    await flush(20);

    click(dlg('export-column-urls-url')!); // deselect the url column
    click(dlg('export-start')!);
    await waitFor(() => Boolean(findCall(calls, 'POST', '/api/exports')));

    const body = JSON.parse(findCall(calls, 'POST', '/api/exports')!.body!);
    expect(body.columns.urls).toContain('host');
    expect(body.columns.urls).not.toContain('url');
    expect(body.columns.findings).toBeUndefined(); // untouched datasets stay "all"
  });

  it('offers only ENABLED destinations and sends destinationId when chosen', async () => {
    const { services, calls } = exportServices({}, 999); // file never ready: delivery phase stays visible
    mounted = await renderTool(<ExportDialog auditId={AUDIT_ID} open onClose={() => undefined} />, services);
    await flush(20);

    // Disabled "Backup SFTP" (id 4) must not be offered.
    click(dlg('export-destination')!);
    await flush(20);
    const options = Array.from(document.querySelectorAll<HTMLElement>('[role="option"]')).map((node) => textOf(node));
    expect(options).toContain('Nightly drop (local-folder)');
    expect(options).not.toContain('Backup SFTP (sftp)');
    click(Array.from(document.querySelectorAll<HTMLElement>('[role="option"]')).find((node) => textOf(node) === 'Nightly drop (local-folder)')!);
    await flush(20);

    click(dlg('export-start')!);
    await waitFor(() => Boolean(findCall(calls, 'POST', '/api/exports')));
    expect(JSON.parse(findCall(calls, 'POST', '/api/exports')!.body!).destinationId).toBe('3');
    await waitFor(() => textOf(dlg('export-status')).includes('Delivering to Nightly drop'));
  });

  it('polls the file route until ready and saves the blob locally', async () => {
    objectUrls = stubObjectUrl();
    const { services, calls } = exportServices({}, 2); // first file attempt 404
    mounted = await renderTool(<ExportDialog auditId={AUDIT_ID} open onClose={() => undefined} />, services);
    await flush(20);

    click(dlg('export-start')!);
    await waitFor(() => textOf(dlg('export-status')).includes('Ready'), 4000);

    const fileCalls = calls.filter((call) => call.url.includes('/api/exports/90/file'));
    expect(fileCalls.length).toBeGreaterThanOrEqual(2); // bounded retry happened
    expect(objectUrls.created.length).toBe(1);
    expect(textOf(dlg('export-status'))).toContain('Saved to your downloads');
    expect(dlg('export-download')).not.toBeNull();
  });

  it('page-content formats skip dataset selection and send an empty dataset list', async () => {
    const { services, calls } = exportServices();
    mounted = await renderTool(<ExportDialog auditId={AUDIT_ID} open onClose={() => undefined} />, services);
    await flush(20);

    await selectOption(document.body, 'export-format', 'Full page Markdown (.zip)');
    expect(dlg('export-page-content-note')).not.toBeNull();
    expect(dlg('export-dataset-urls')).toBeNull();

    click(dlg('export-start')!);
    await waitFor(() => Boolean(findCall(calls, 'POST', '/api/exports')));
    const body = JSON.parse(findCall(calls, 'POST', '/api/exports')!.body!);
    expect(body.format).toBe('markdown');
    expect(body.datasets).toEqual([]);
  });

  it('surfaces envelope errors from create', async () => {
    const { services } = exportServices({
      '/api/exports': () => ({ error: { code: 'validation', message: 'Unknown format.', fieldErrors: null }, data: null })
    });
    mounted = await renderTool(<ExportDialog auditId={AUDIT_ID} open onClose={() => undefined} />, services);
    await flush(20);

    click(dlg('export-start')!);
    await waitFor(() => Boolean(dlg('export-error')));
    expect(textOf(dlg('export-error'))).toContain('Unknown format.');
  });

  it('degrades to an offline message without the backend', async () => {
    mounted = await renderTool(<ExportDialog auditId={AUDIT_ID} open onClose={() => undefined} />, offlineServices());
    await flush(20);
    expect(dlg('export-offline')).not.toBeNull();
    expect((dlg('export-start') as HTMLButtonElement).disabled).toBe(true);
  });
});