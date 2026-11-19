/**
 * Export surface test harness (WP-09-10). Reuses the shell + tools helpers
 * (read-only) and adds export-route fakes with REAL wire JSON shapes:
 * ExportCreateResponse {exportId, state}, non-enveloped file Blob,
 * ExtractedContentResponse {columns, rows}, stateful destinations.
 *
 * @module tests/frontend/export/helpers
 */

import { byTestId, click, fakeServices, flush, ok, type FetchCall } from '../shell/helpers';
import { destinationRoutes, textOf } from '../tools/helpers';

export * from '../shell/helpers';
export { renderTool, typeInto, waitFor, byTestIdAll, textOf, destination, destinationRoutes } from '../tools/helpers';

/**
 * Non-enveloped export file route: returns HTTP 404 until `readyAfter`
 * attempts (default 1 = immediately ready), then a real Blob response.
 */
export function exportFileRoutes(readyAfter = 1): Record<string, (call: FetchCall) => unknown> {
  let attempts = 0;
  return {
    '/file': () => {
      attempts += 1;
      if (attempts < readyAfter) {
        return new Response(JSON.stringify({ error: { code: 'not-found', message: 'Export not ready.' } }), {
          status: 404,
          headers: { 'Content-Type': 'application/json' }
        });
      }
      return new Response(new Blob(['fake-export-bytes']), {
        status: 200,
        headers: { 'Content-Type': 'application/octet-stream' }
      });
    }
  };
}

/** Export routes: file first (substring matching is order-sensitive). */
export function exportRoutes(readyAfter = 1): Record<string, (call: FetchCall) => unknown> {
  return {
    ...exportFileRoutes(readyAfter),
    '/api/exports': () => ok({ exportId: '90', state: 'queued' }),
    '/extracted-content': () => ok({ columns: ['url', 'custom.Price.0', 'custom.Price.1'], rows: [] }),
    ...destinationRoutes()
  };
}

/** Full export service graph (fake fetch + fake hub). */
export function exportServices(extra: Record<string, (call: FetchCall) => unknown> = {}, readyAfter = 1) {
  return fakeServices({ ...exportRoutes(readyAfter), ...extra });
}

/** Stubs URL.createObjectURL/revokeObjectURL; records created object URLs. */
export function stubObjectUrl(): { created: string[]; restore: () => void } {
  const created: string[] = [];
  const originalCreate = URL.createObjectURL;
  const originalRevoke = URL.revokeObjectURL;
  URL.createObjectURL = () => {
    const url = `blob:fake-${created.length}`;
    created.push(url);
    return url;
  };
  URL.revokeObjectURL = () => undefined;
  return {
    created,
    restore: () => {
      URL.createObjectURL = originalCreate;
      URL.revokeObjectURL = originalRevoke;
    }
  };
}

/** Picks a Mantine Select option by its visible label (portal-rendered). */
export async function selectOption(container: HTMLElement, selectTestId: string, optionLabel: string): Promise<void> {
  const input = byTestId(container, selectTestId)!;
  click(input);
  await flush(20);
  const option = Array.from(document.querySelectorAll<HTMLElement>('[role="option"]')).find((node) => textOf(node) === optionLabel);
  if (!option) throw new Error(`Select option not found: ${optionLabel}`);
  click(option);
  await flush(20);
}