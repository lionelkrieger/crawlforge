/**
 * Explorer data sources over the typed loopback client (FS-CRAWL-002 §7.1).
 *
 * Each explorer binds one frozen route key + fixed path params; the grid
 * engine only ever requests offset/limit windows through this surface. The
 * adapters normalize the three locked list-response shapes:
 *   - plain arrays (urls/findings/links/ngrams/waf-blocks/…): data = TRow[]
 *   - dynamic-column payloads (extracted-content/snippet-results):
 *     data = { columns, rows }
 *   - analytics views: data = { tags, consent, findings }
 *
 * Envelope errors become error results (never throws); transport failures
 * reject and are caught by the page cache. Multi-column sort travels as
 * repeated sort/order fields — the sanctioned wire expression documented in
 * contracts/common/pagination.
 *
 * @module grid/dataSource
 */

import type { LoopbackApiClient, ApiRequestInit } from '../contracts/api/client';
import type { ApiRouteKey } from '../contracts/api/routes';
import type { ApiRouteParams, ApiRouteResponse } from '../contracts/api/contracts-map';
import type { ApiEnvelope } from '../contracts/common/envelope';
import type { GridDataSource, GridFetchResult, GridPage, ServerListQuery } from './types';
import { GRID_PAGE_SIZE } from './types';

/** Extracts rows + optional dynamic columns/meta from a route response. */
export type ResponseAdapter<TRow, K extends ApiRouteKey> = (
  data: ApiRouteResponse<K>,
  query: ServerListQuery
) => {
  readonly rows: readonly TRow[];
  readonly columns?: readonly string[];
  readonly meta?: Readonly<Record<string, unknown>>;
};

/** Identity adapter for plain-array list routes. */
export function arrayAdapter<TRow, K extends ApiRouteKey>(): ResponseAdapter<TRow, K> {
  return (data) => ({ rows: (data ?? []) as readonly TRow[] });
}

/** Adapter for { columns, rows } dynamic-column payloads. */
export function dynamicColumnsAdapter<TRow, K extends ApiRouteKey>(): ResponseAdapter<TRow, K> {
  return (data) => {
    const payload = (data ?? { columns: [], rows: [] }) as { columns?: readonly string[]; rows?: readonly TRow[] };
    return { rows: payload.rows ?? [], columns: payload.columns ?? [] };
  };
}

/** Options binding a data source to one frozen route. */
export interface ExplorerSourceOptions<TRow, K extends ApiRouteKey> {
  readonly api: LoopbackApiClient;
  readonly route: K;
  readonly params: ApiRouteParams<K>;
  /** Fixed domain filters merged under every window request. */
  readonly baseQuery?: Record<string, unknown>;
  readonly adapter: ResponseAdapter<TRow, K>;
  readonly pageSize?: number;
}

/**
 * Creates a typed server-windowed data source. The single controlled cast
 * at the client boundary widens sort/order to the repeated-field wire form
 * (documented in contracts/common/pagination); every other type flows from
 * the frozen contract map.
 */
export function createExplorerDataSource<TRow, K extends ApiRouteKey>(
  options: ExplorerSourceOptions<TRow, K>
): GridDataSource<TRow> {
  const { api, route, params, baseQuery, adapter } = options;
  const pageSize = options.pageSize ?? GRID_PAGE_SIZE;
  const sourceKey = `${route}|${JSON.stringify(params)}`;

  return {
    sourceKey,
    async fetchPage(query: ServerListQuery, signal?: AbortSignal): Promise<GridFetchResult<TRow>> {
      const merged: Record<string, unknown> = {
        ...baseQuery,
        ...query,
        limit: query.limit ?? pageSize
      };
      // Drop undefined/null entries so omitted filters stay omitted on the wire.
      for (const key of Object.keys(merged)) {
        if (merged[key] === undefined || merged[key] === null) delete merged[key];
      }
      const init = {
        params,
        query: merged,
        ...(signal ? { signal } : {})
      } as unknown as ApiRequestInit<K>;
      let envelope: ApiEnvelope<ApiRouteResponse<K>>;
      try {
        envelope = (await api.request(route, init)) as ApiEnvelope<ApiRouteResponse<K>>;
      } catch (error) {
        return {
          ok: false,
          errorCode: 'transport',
          errorMessage: error instanceof Error ? error.message : 'Backend unreachable'
        };
      }
      if (envelope.error || envelope.data === null || envelope.data === undefined) {
        return {
          ok: false,
          errorCode: envelope.error?.code ?? 'internal',
          errorMessage: envelope.error?.message ?? 'Empty response'
        };
      }
      const adapted = adapter(envelope.data, merged as ServerListQuery);
      const page: GridPage<TRow> = {
        offset: envelope.pagination?.offset ?? (query.offset ?? 0),
        limit: envelope.pagination?.limit ?? pageSize,
        total: envelope.pagination?.total ?? adapted.rows.length,
        rows: adapted.rows,
        ...(adapted.columns ? { columns: adapted.columns } : {}),
        ...(adapted.meta ? { meta: adapted.meta } : {}),
        fetchedAt: Date.now()
      };
      return { ok: true, page };
    }
  };
}

/** Offline data source: honest empty windows, never fabricates rows. */
export function createOfflineDataSource<TRow>(sourceKey: string): GridDataSource<TRow> {
  return {
    sourceKey,
    async fetchPage(): Promise<GridFetchResult<TRow>> {
      return { ok: false, errorCode: 'offline', errorMessage: 'Backend disconnected — explorer data is unavailable offline.' };
    }
  };
}

/** In-memory data source for tests and plugin previews (bounded windows). */
export function createMemoryDataSource<TRow>(
  sourceKey: string,
  allRows: readonly TRow[],
  columns?: readonly string[]
): GridDataSource<TRow> {
  return {
    sourceKey,
    async fetchPage(query: ServerListQuery): Promise<GridFetchResult<TRow>> {
      const offset = query.offset ?? 0;
      const limit = query.limit ?? GRID_PAGE_SIZE;
      const page: GridPage<TRow> = {
        offset,
        limit,
        total: allRows.length,
        rows: allRows.slice(offset, offset + limit),
        ...(columns ? { columns } : {}),
        fetchedAt: Date.now()
      };
      return { ok: true, page };
    }
  };
}
