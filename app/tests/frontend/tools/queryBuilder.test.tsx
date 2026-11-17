// @vitest-environment jsdom
/**
 * Query-builder tests: pure rule-tree helpers (value parsing, immutable
 * replace/remove) plus the UI — recursive editing, the depth-16 cap, and
 * saved-query CRUD round-trips against /api/queries.
 */

import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import {
  MAX_TREE_DEPTH,
  QueryBuilderTool,
  parseConditionValue,
  removeNode,
  replaceNode,
  valueToInput
} from '../../../src/frontend/src/tools/QueryBuilderTool';
import type { QueryRuleGroup } from '../../../src/frontend/src/contracts/api/queries';
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

describe('pure rule-tree helpers', () => {
  it('parses primitives by operator', () => {
    expect(parseConditionValue('200', 'eq')).toBe(200);
    expect(parseConditionValue('true', 'eq')).toBe(true);
    expect(parseConditionValue('false', 'neq')).toBe(false);
    expect(parseConditionValue('hello', 'contains')).toBe('hello');
    expect(parseConditionValue('a, b, 3', 'in')).toEqual(['a', 'b', 3]);
  });

  it('round-trips values to input text', () => {
    expect(valueToInput(200)).toBe('200');
    expect(valueToInput(['a', 'b'])).toBe('a, b');
  });

  it('replaces and removes nodes immutably', () => {
    const tree: QueryRuleGroup = {
      kind: 'group',
      operator: 'and',
      children: [
        { kind: 'condition', field: 'statusCode', operator: 'eq', value: 200 },
        { kind: 'group', operator: 'or', children: [{ kind: 'condition', field: 'depth', operator: 'lt', value: 3 }] }
      ]
    };
    const replaced = replaceNode(tree, [1, 0], { kind: 'condition', field: 'depth', operator: 'gte', value: 5 });
    expect((replaced as QueryRuleGroup).children[1]).toEqual({
      kind: 'group',
      operator: 'or',
      children: [{ kind: 'condition', field: 'depth', operator: 'gte', value: 5 }]
    });
    expect(tree.children[1]).toEqual({ kind: 'group', operator: 'or', children: [{ kind: 'condition', field: 'depth', operator: 'lt', value: 3 }] });

    const removed = removeNode(tree, [0]);
    expect((removed as QueryRuleGroup).children.length).toBe(1);
    expect(tree.children.length).toBe(2);
  });

  it('caps nesting at depth 16', () => {
    expect(MAX_TREE_DEPTH).toBe(16);
  });
});

describe('QueryBuilderTool UI', () => {
  async function renderBuilder() {
    const { services, calls } = toolServices();
    mounted = await renderTool(<QueryBuilderTool />, services);
    await waitFor(() => byTestId(mounted!.container, 'query-saved-5') !== null);
    return { container: mounted.container, calls };
  }

  function treeOf(container: HTMLElement): { operator: string; children: unknown[] } {
    return JSON.parse(textOf(byTestId(container, 'query-json-pre')));
  }

  it('loads saved queries and previews the seeded tree as JSON', async () => {
    const { container } = await renderBuilder();
    expect(byTestId(container, 'query-saved-5')).not.toBeNull();
    const tree = treeOf(container);
    expect(tree.operator).toBe('and');
    expect(tree.children.length).toBe(2);
  });

  it('adds a condition and toggles the group operator live', async () => {
    const { container } = await renderBuilder();
    click(byTestId(container, 'query-add-condition-root')!);
    await flush();
    expect(treeOf(container).children.length).toBe(3);

    const or = byTestId(container, 'query-group-op-root')!.querySelector('input[value="or"]')!;
    click(or);
    await flush();
    expect(treeOf(container).operator).toBe('or');
  });

  it('creates, updates, loads, and deletes saved queries', async () => {
    const { container, calls } = await renderBuilder();
    typeInto(byTestId(container, 'query-name') as HTMLInputElement, 'My query');
    click(byTestId(container, 'query-save')!);
    await waitFor(() => byTestId(mounted!.container, 'query-saved-11') !== null);
    const post = calls.find((call) => call.url.includes('/api/queries') && call.method === 'POST')!;
    expect(JSON.parse(post.body!).name).toBe('My query');
    expect(textOf(byTestId(container, 'query-save'))).toBe('Update saved');

    click(byTestId(container, 'query-save')!);
    await flush();
    const put = calls.find((call) => call.url.includes('/api/queries') && call.method === 'PUT')!;
    expect(JSON.parse(put.body!).id).toBe('11');

    click(byTestId(container, 'query-load-5')!);
    await flush();
    expect((byTestId(container, 'query-name') as HTMLInputElement).value).toBe('Indexable success pages');

    click(byTestId(container, 'query-delete-5')!);
    await waitFor(() => byTestId(mounted!.container, 'query-saved-5') === null);
    const del = calls.find((call) => call.url.includes('/api/queries') && call.method === 'DELETE')!;
    expect(new URL(del.url).searchParams.get('id')).toBe('5');
  });

  it('disables deeper grouping once the depth cap is reached', async () => {
    const { container } = await renderBuilder();
    const path: number[] = [];
    click(byTestId(container, 'query-add-group-root')!);
    path.push(2);
    await flush();
    for (let i = 0; i < 13; i += 1) {
      click(byTestId(container, `query-add-group-${path.join('-')}`)!);
      path.push(1);
      await flush();
    }
    const deepest = byTestId(container, `query-add-group-${path.join('-')}`) as HTMLButtonElement;
    expect(deepest.disabled).toBe(true);
    // Conditions remain addable at the cap (only nesting is bounded).
    expect((byTestId(container, `query-add-condition-${path.join('-')}`) as HTMLButtonElement).disabled).toBe(false);
  });
});