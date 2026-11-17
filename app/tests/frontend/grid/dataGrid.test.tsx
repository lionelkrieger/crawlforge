// @vitest-environment jsdom
/**
 * DataGrid + engine runtime proofs (criterion #40 mechanism, §8.3 checklist):
 * ≤50 DOM rows at 500k total with NO client-side dataset materialization,
 * ARIA grid semantics, window sliding on scroll, server multi-sort, debounced
 * instant filter, pinning budget, selection + export handoff, keyboard
 * navigation with focus retention, density modes, and honest error states.
 *
 * Real-browser 60 FPS proof is phase-07 E2E; this suite proves the mechanism
 * that makes it possible (row cap, recycling slots, bounded window fetches).
 */

import { beforeEach, describe, expect, it } from 'vitest';
import { useRef } from 'react';
import type { UrlRecordSummary, HttpStatusClass } from '../../../src/frontend/src/contracts/api/audits';
import type { GridDataSource, GridFetchResult, ServerListQuery } from '../../../src/frontend/src/grid/types';
import { MAX_DOM_ROWS, MAX_PINNED_COLUMNS } from '../../../src/frontend/src/grid/types';
import { useGridEngine, type GridEngine } from '../../../src/frontend/src/grid/useGridEngine';
import { DataGrid } from '../../../src/frontend/src/grid/DataGrid';
import { makeField } from '../../../src/frontend/src/surfaces/explorers/columnUtils';
import {
  render, flush, byTestId, fire, click, type, keyDown,
  recordingSource, seedUrls, testUrlColumns, setScrollGeometry, installGridStubs, primeGrid
} from './helpers';

const TOTAL = 500_000;

/**
 * Synthetic 500k-row source: rows are generated per requested window, so the
 * test itself never materializes the dataset — exactly the server contract.
 */
function syntheticSource(total = TOTAL) {
  const queries: ServerListQuery[] = [];
  const source: GridDataSource<UrlRecordSummary> = {
    sourceKey: 'synthetic-500k',
    async fetchPage(query): Promise<GridFetchResult<UrlRecordSummary>> {
      queries.push({ ...query });
      const offset = query.offset ?? 0;
      const limit = query.limit ?? 200;
      const classes: HttpStatusClass[] = ['2xx', '3xx', '4xx', '5xx'];
      const rows: UrlRecordSummary[] = [];
      for (let i = offset; i < Math.min(offset + limit, total); i += 1) {
        rows.push({
          id: `url-${i}`,
          canonicalUrl: `https://example.com/page/${i}`,
          statusCode: 200,
          statusClass: classes[i % classes.length],
          contentType: 'text/html',
          depth: i % 9,
          indexable: i % 2 === 0,
          title: `Page ${i}`
        });
      }
      return { ok: true, page: { offset, limit, total, rows, fetchedAt: Date.now() } };
    }
  };
  return { source, queries };
}

const queryFields = [
  makeField('statusClass', 'Status class', 'enum', { serverFilter: 'statusClass', enumValues: ['2xx', '4xx'] }),
  makeField('depth', 'Depth', 'number', { serverFilter: 'depth' })
];

interface HarnessHandle {
  engine: GridEngine<UrlRecordSummary> | null;
}

function Harness({ source, handle, onExport, onActivate }: {
  source: GridDataSource<UrlRecordSummary>;
  handle: HarnessHandle;
  onExport?: (payload: ReturnType<GridEngine<UrlRecordSummary>['exportHandoff']>) => void;
  onActivate?: (row: UrlRecordSummary, index: number) => void;
}) {
  const engine = useGridEngine<UrlRecordSummary>({
    source,
    columns: testUrlColumns(),
    rowKey: (row) => row.id,
    queryFields,
    explorerId: 'urls',
    onActivateRow: onActivate
  });
  handle.engine = engine;
  const exportRef = useRef(onExport);
  exportRef.current = onExport;
  return (
    <DataGrid
      engine={engine}
      label="URL Explorer"
      onExport={onExport ? (payload) => exportRef.current?.(payload) : undefined}
      onActivateRow={onActivate}
      height={600}
    />
  );
}

beforeEach(() => {
  document.body.replaceChildren();
  installGridStubs();
});

/** Renders the harness, then gives the grid body a real jsdom viewport. */
async function renderHarness(node: React.ReactElement) {
  const mounted = await render(node);
  await flush(20);
  await primeGrid(mounted.container);
  return mounted;
}

describe('criterion #40 — DOM row cap at 500k rows', () => {
  it('mounts ≤50 DOM rows for a 500,000-row dataset without materializing it', async () => {
    const { source, queries } = syntheticSource();
    const handle: HarnessHandle = { engine: null };
    const mounted = await renderHarness(<Harness source={source} handle={handle} />);
    await flush(20);

    const body = byTestId(mounted.container, 'cf-grid-body')!;
    const dataRows = body.querySelectorAll('[data-row-index]');
    expect(dataRows.length).toBeGreaterThan(0);
    expect(dataRows.length).toBeLessThanOrEqual(MAX_DOM_ROWS);
    expect(handle.engine!.totalRows).toBe(TOTAL);
    expect(byTestId(mounted.container, 'cf-grid-total')!.textContent).toContain('500,000');
    // Only the first window(s) were fetched — never the dataset.
    expect(queries.length).toBeLessThanOrEqual(2);
    expect(queries.every((query) => (query.limit ?? 200) <= 200)).toBe(true);
    mounted.unmount();
  });

  it('keeps the cap while sliding the window across the dataset', async () => {
    const { source } = syntheticSource();
    const handle: HarnessHandle = { engine: null };
    const mounted = await renderHarness(<Harness source={source} handle={handle} />);
    await flush(20);
    const body = byTestId(mounted.container, 'cf-grid-body')!;

    for (const rowIndex of [1_000, 25_000, 250_000, 499_900]) {
      await flush(0);
      setScrollGeometry(body, { clientHeight: 600, scrollTop: rowIndex * 36 });
      await flush(30);
      const dataRows = body.querySelectorAll('[data-row-index]');
      expect(dataRows.length).toBeLessThanOrEqual(MAX_DOM_ROWS);
      const indices = [...dataRows].map((row) => Number(row.getAttribute('data-row-index')));
      expect(Math.min(...indices)).toBeLessThanOrEqual(rowIndex);
      expect(Math.max(...indices)).toBeGreaterThanOrEqual(rowIndex);
    }
    mounted.unmount();
  });

  it('recycles row slots by global index (stable keys, bounded DOM)', async () => {
    const { source } = syntheticSource();
    const handle: HarnessHandle = { engine: null };
    const mounted = await renderHarness(<Harness source={source} handle={handle} />);
    await flush(20);
    const body = byTestId(mounted.container, 'cf-grid-body')!;
    const before = body.querySelectorAll('[data-row-index]').length;
    setScrollGeometry(body, { clientHeight: 600, scrollTop: 5_000 * 36 });
    await flush(30);
    const after = body.querySelectorAll('[data-row-index]').length;
    expect(after).toBeLessThanOrEqual(MAX_DOM_ROWS);
    // Edge windows carry one-sided overscan; mid-dataset windows carry both
    // sides — the delta is bounded by the full overscan budget.
    expect(Math.abs(after - before)).toBeLessThanOrEqual(2 * 6);
    // Old rows are gone from the DOM — slots were reused, not accumulated.
    expect(body.querySelector('[data-row-index="0"]')).toBeNull();
    mounted.unmount();
  });

  it('reuses the SAME row DOM nodes across a scroll sweep (slot-keyed recycling)', async () => {
    const { source } = syntheticSource();
    const handle: HarnessHandle = { engine: null };
    const mounted = await renderHarness(<Harness source={source} handle={handle} />);
    await flush(20);
    const body = byTestId(mounted.container, 'cf-grid-body')!;

    // Capture live DOM node references for the first ten mounted slots.
    const slotNodes = [...body.querySelectorAll('[data-row-index]')].slice(0, 10) as HTMLElement[];
    expect(slotNodes.length).toBe(10);
    expect(slotNodes[0].getAttribute('data-row-index')).toBe('0');
    expect(slotNodes[0].textContent).toContain('https://example.com/page/0');

    // Slide the window well past the overscan so every slot shifts.
    setScrollGeometry(body, { clientHeight: 600, scrollTop: 20 * 36 });
    await flush(30);
    const newStart = Number(body.querySelector('[data-row-index]')!.getAttribute('data-row-index'));
    expect(newStart).toBeGreaterThan(0);

    // The SAME nodes stay connected; absolute identity + contents update in
    // place (no remount). This is the mechanism behind the 60 FPS sweep.
    slotNodes.forEach((node, slot) => {
      expect(node.isConnected, `slot ${slot} node must be reused, not remounted`).toBe(true);
      expect(node.getAttribute('data-row-index')).toBe(String(newStart + slot));
      expect(node.getAttribute('aria-rowindex')).toBe(String(newStart + slot + 2));
      expect(node.textContent).toContain(`https://example.com/page/${newStart + slot}`);
    });
    expect(body.querySelectorAll('[data-row-index]').length).toBeLessThanOrEqual(MAX_DOM_ROWS);

    // Selection stays bound to ABSOLUTE row identity across slot reuse.
    setScrollGeometry(body, { clientHeight: 600, scrollTop: 0 });
    await flush(30);
    click(byTestId(mounted.container, 'cf-grid-select-0') as HTMLInputElement);
    await flush(10);
    expect(handle.engine!.state.selectedRowKeys).toEqual(['url-0']);

    setScrollGeometry(body, { clientHeight: 600, scrollTop: 20 * 36 });
    await flush(30);
    // The reused slot now shows a different row — it must NOT inherit the
    // selection of the row that previously occupied it.
    const reusedSlot = body.querySelector(`[data-row-index="${newStart}"]`) as HTMLElement;
    expect(reusedSlot.getAttribute('aria-selected')).toBe('false');
    expect((byTestId(mounted.container, `cf-grid-select-${newStart}`) as HTMLInputElement).checked).toBe(false);

    // Scroll back: row 0's selection survives in the same reused node.
    setScrollGeometry(body, { clientHeight: 600, scrollTop: 0 });
    await flush(30);
    expect(slotNodes[0].isConnected).toBe(true);
    expect(slotNodes[0].getAttribute('data-row-index')).toBe('0');
    expect(slotNodes[0].getAttribute('aria-selected')).toBe('true');
    expect(handle.engine!.state.selectedRowKeys).toEqual(['url-0']);
    mounted.unmount();
  });
});

describe('ARIA grid semantics', () => {
  it('exposes true rowcount/rowindex/colcount and sort state', async () => {
    const { source } = syntheticSource(1_000);
    const handle: HarnessHandle = { engine: null };
    const mounted = await renderHarness(<Harness source={source} handle={handle} />);
    await flush(20);

    const grid = mounted.container.querySelector('[role="grid"]')!;
    expect(grid.getAttribute('aria-rowcount')).toBe('1001');
    expect(grid.getAttribute('aria-label')).toBe('URL Explorer');
    const firstRow = mounted.container.querySelector('[data-row-index="0"]')!;
    expect(firstRow.getAttribute('aria-rowindex')).toBe('2');
    const headerCells = mounted.container.querySelectorAll('[role="columnheader"]');
    expect(headerCells.length).toBeGreaterThanOrEqual(5);
    expect(headerCells[1].getAttribute('aria-sort')).toBe('none');

    click(byTestId(mounted.container, 'cf-grid-sort-statusCode')!);
    await flush(30);
    const statusHeader = byTestId(mounted.container, 'cf-grid-header-statusCode')!;
    expect(statusHeader.getAttribute('aria-sort')).toBe('ascending');
    mounted.unmount();
  });

  it('marks selected rows with aria-selected', async () => {
    const source = recordingSource(seedUrls(100));
    const handle: HarnessHandle = { engine: null };
    const mounted = await renderHarness(<Harness source={source} handle={handle} />);
    await flush(20);
    const checkbox = byTestId(mounted.container, 'cf-grid-select-0') as HTMLInputElement;
    click(checkbox);
    await flush(10);
    const row = mounted.container.querySelector('[data-row-index="0"]')!;
    expect(row.getAttribute('aria-selected')).toBe('true');
    expect(byTestId(mounted.container, 'cf-grid-selection')!.textContent).toContain('1 selected');
    mounted.unmount();
  });
});

describe('server-side multi-column sort', () => {
  it('shift-click builds a sort chain sent as repeated sort/order fields', async () => {
    const { source, queries } = syntheticSource(1_000);
    const handle: HarnessHandle = { engine: null };
    const mounted = await renderHarness(<Harness source={source} handle={handle} />);
    await flush(20);

    click(byTestId(mounted.container, 'cf-grid-sort-statusCode')!);
    await flush(30);
    fire(byTestId(mounted.container, 'cf-grid-sort-depth')!, 'click', { shiftKey: true });
    await flush(30);

    expect(handle.engine!.state.sortChain).toEqual([
      { field: 'statusCode', order: 'asc' },
      { field: 'depth', order: 'asc' }
    ]);
    const last = queries[queries.length - 1];
    expect(last.sort).toEqual(['statusCode', 'depth']);
    expect(last.order).toEqual(['asc', 'asc']);
    // Chain positions render on both headers.
    expect(byTestId(mounted.container, 'cf-grid-sort-indicator-statusCode')!.textContent).toContain('1');
    expect(byTestId(mounted.container, 'cf-grid-sort-indicator-depth')!.textContent).toContain('2');
    mounted.unmount();
  });

  it('cycles asc → desc → unsorted on repeated clicks', async () => {
    const { source } = syntheticSource(100);
    const handle: HarnessHandle = { engine: null };
    const mounted = await renderHarness(<Harness source={source} handle={handle} />);
    await flush(20);
    const button = byTestId(mounted.container, 'cf-grid-sort-depth')!;
    click(button);
    await flush(20);
    expect(handle.engine!.state.sortChain).toEqual([{ field: 'depth', order: 'asc' }]);
    click(button);
    await flush(20);
    expect(handle.engine!.state.sortChain).toEqual([{ field: 'depth', order: 'desc' }]);
    click(button);
    await flush(20);
    expect(handle.engine!.state.sortChain).toEqual([]);
    mounted.unmount();
  });
});

describe('instant filter debounce', () => {
  it('waits the debounce band before issuing the search window', async () => {
    const { source, queries } = syntheticSource(1_000);
    const handle: HarnessHandle = { engine: null };
    const mounted = await renderHarness(<Harness source={source} handle={handle} />);
    await flush(20);
    const baseline = queries.length;

    type(byTestId(mounted.container, 'cf-grid-filter') as HTMLInputElement, 'hello');
    await flush(50); // inside the 200ms debounce band
    expect(queries.length).toBe(baseline);
    expect(handle.engine!.state.filterText).toBe('hello');

    await flush(300); // past the band
    const last = queries[queries.length - 1];
    expect(last.search).toBe('hello');
    mounted.unmount();
  });
});

describe('query builder integration', () => {
  it('folds applied rule-tree leaves into route filters', async () => {
    const { source, queries } = syntheticSource(1_000);
    const handle: HarnessHandle = { engine: null };
    const mounted = await renderHarness(<Harness source={source} handle={handle} />);
    await flush(20);

    const { group, condition } = await import('../../../src/frontend/src/grid/queryBuilder');
    handle.engine!.applyRuleTree(group('and', [condition('statusClass', 'eq', '4xx')]));
    await flush(40);
    const last = queries[queries.length - 1];
    expect(last.statusClass).toBe('4xx');
    mounted.unmount();
  });
});

describe('column pinning', () => {
  it('pins columns and enforces the ≤4 budget', async () => {
    const { source } = syntheticSource(100);
    const handle: HarnessHandle = { engine: null };
    const mounted = await renderHarness(<Harness source={source} handle={handle} />);
    await flush(20);

    click(byTestId(mounted.container, 'cf-grid-pin-canonicalUrl')!);
    await flush(10);
    expect(handle.engine!.state.pinned.canonicalUrl).toBe('left');
    // Pinned header cell carries the sticky class.
    expect(byTestId(mounted.container, 'cf-grid-header-canonicalUrl')!.className).toContain('cf-grid-pinned-left');

    click(byTestId(mounted.container, 'cf-grid-pin-statusCode')!);
    click(byTestId(mounted.container, 'cf-grid-pin-depth')!);
    click(byTestId(mounted.container, 'cf-grid-pin-indexable')!);
    await flush(10);
    expect(Object.keys(handle.engine!.state.pinned).length).toBe(MAX_PINNED_COLUMNS);
    mounted.unmount();
  });
});

describe('keyboard navigation', () => {
  it('moves focus with arrows and retains it across page loads', async () => {
    const { source } = syntheticSource();
    const handle: HarnessHandle = { engine: null };
    const mounted = await renderHarness(<Harness source={source} handle={handle} />);
    await flush(20);
    const body = byTestId(mounted.container, 'cf-grid-body')!;

    keyDown(body, 'ArrowDown');
    await flush(10);
    expect(handle.engine!.focusedCell).toEqual({ rowIndex: 1, colIndex: 0 });
    keyDown(body, 'ArrowRight');
    await flush(10);
    expect(handle.engine!.focusedCell).toEqual({ rowIndex: 1, colIndex: 1 });

    // PageDown jumps a viewport; focus stays coherent.
    keyDown(body, 'PageDown');
    await flush(30);
    expect(handle.engine!.focusedCell!.rowIndex).toBeGreaterThan(1);

    // Ctrl+End lands on the last global row; the window fetches that page.
    keyDown(body, 'End', { ctrlKey: true });
    await flush(40);
    expect(handle.engine!.focusedCell!.rowIndex).toBe(TOTAL - 1);
    const dataRows = body.querySelectorAll('[data-row-index]');
    expect(dataRows.length).toBeLessThanOrEqual(MAX_DOM_ROWS);
    mounted.unmount();
  });

  it('activates the focused row with Enter', async () => {
    const source = recordingSource(seedUrls(100));
    const handle: HarnessHandle = { engine: null };
    const activations: { row: UrlRecordSummary; index: number }[] = [];
    const mounted = await renderHarness(
      <Harness source={source} handle={handle} onActivate={(row, index) => activations.push({ row, index })} />
    );
    await flush(20);
    const body = byTestId(mounted.container, 'cf-grid-body')!;
    keyDown(body, 'ArrowDown');
    await flush(10);
    keyDown(body, 'Enter');
    await flush(10);
    expect(activations).toHaveLength(1);
    expect(activations[0].row.id).toBe('url-1');
    expect(activations[0].index).toBe(1);
    mounted.unmount();
  });
});

describe('density modes', () => {
  it('switches row geometry across the three densities', async () => {
    const { source } = syntheticSource(1_000);
    const handle: HarnessHandle = { engine: null };
    const mounted = await renderHarness(<Harness source={source} handle={handle} />);
    await flush(20);
    const select = byTestId(mounted.container, 'cf-grid-density') as HTMLSelectElement;
    expect(handle.engine!.rowHeight).toBe(36);

    const setter = Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value')?.set;
    setter?.call(select, 'compact');
    select.dispatchEvent(new Event('change', { bubbles: true }));
    await flush(20);
    expect(handle.engine!.density).toBe('compact');
    expect(handle.engine!.rowHeight).toBe(28);
    expect(byTestId(mounted.container, 'cf-grid')!.getAttribute('data-density')).toBe('compact');
    mounted.unmount();
  });
});

describe('selection + export handoff', () => {
  it('carries selection, sort, filter, and rule tree into the handoff', async () => {
    const source = recordingSource(seedUrls(500));
    const handle: HarnessHandle = { engine: null };
    let payload: ReturnType<GridEngine<UrlRecordSummary>['exportHandoff']> | null = null;
    const mounted = await renderHarness(<Harness source={source} handle={handle} onExport={(next) => { payload = next; }} />);
    await flush(20);

    click(byTestId(mounted.container, 'cf-grid-select-0') as HTMLInputElement);
    click(byTestId(mounted.container, 'cf-grid-select-2') as HTMLInputElement);
    click(byTestId(mounted.container, 'cf-grid-sort-depth')!);
    await flush(30);
    click(byTestId(mounted.container, 'cf-grid-export')!);
    await flush(10);

    expect(payload).not.toBeNull();
    expect(payload!.selectedRowKeys).toEqual(['url-0', 'url-2']);
    expect(payload!.sortChain).toEqual([{ field: 'depth', order: 'asc' }]);
    expect(payload!.totalRows).toBe(500);
    expect(payload!.serverQuery.sort).toBe('depth');
    mounted.unmount();
  });

  it('clears selection from the toolbar', async () => {
    const source = recordingSource(seedUrls(100));
    const handle: HarnessHandle = { engine: null };
    const mounted = await renderHarness(<Harness source={source} handle={handle} />);
    await flush(20);
    click(byTestId(mounted.container, 'cf-grid-select-0') as HTMLInputElement);
    await flush(10);
    const clear = mounted.container.querySelector('.cf-grid-link-button')!;
    click(clear);
    await flush(10);
    expect(handle.engine!.state.selectedRowKeys).toEqual([]);
    mounted.unmount();
  });
});

describe('error and offline states', () => {
  it('surfaces fetch errors as an alert, never a crash', async () => {
    const source = recordingSource(seedUrls(100), { failNext: 5, failCode: 'internal' });
    const handle: HarnessHandle = { engine: null };
    const mounted = await renderHarness(<Harness source={source} handle={handle} />);
    await flush(30);
    const alert = byTestId(mounted.container, 'cf-grid-error');
    expect(alert).not.toBeNull();
    expect(alert!.getAttribute('role')).toBe('alert');
    expect(handle.engine!.totalRows).toBe(0);
    mounted.unmount();
  });

  it('refresh retries after a failure', async () => {
    // Mount dedups its two effects into ONE fetch, so a single injected
    // failure is consumed at mount and the refresh recovers.
    const source = recordingSource(seedUrls(100), { failNext: 1 });
    const handle: HarnessHandle = { engine: null };
    const mounted = await renderHarness(<Harness source={source} handle={handle} />);
    await flush(30);
    expect(handle.engine!.error).not.toBeNull();
    click(byTestId(mounted.container, 'cf-grid-refresh')!);
    await flush(30);
    expect(handle.engine!.error).toBeNull();
    expect(handle.engine!.totalRows).toBe(100);
    mounted.unmount();
  });
});
