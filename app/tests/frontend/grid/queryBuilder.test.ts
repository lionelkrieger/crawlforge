/**
 * Query builder proofs: the EXACT locked QueryRuleNode shape, depth-16
 * enforcement, between→AND(gte,lte) expansion, dynamic columns, immutable
 * edits, fold-to-route-filters, and saved-query validation.
 */

import { describe, expect, it } from 'vitest';
import {
  condition,
  group,
  emptyTree,
  treeDepth,
  countConditions,
  nodeAt,
  replaceAt,
  removeAt,
  appendChild,
  appendChildGuarded,
  between,
  toggleGroupOperator,
  validateTree,
  foldTreeIntoFilters,
  summarizeTree,
  extractionField,
  snippetField,
  isDynamicField,
  operatorsForKind,
  OPERATOR_DEFS,
  type QueryFieldDef
} from '../../../src/frontend/src/grid/queryBuilder';
import { MAX_QUERY_DEPTH } from '../../../src/frontend/src/grid/types';
import type { QueryRuleGroup } from '../../../src/frontend/src/contracts/api/queries';

const fields: QueryFieldDef[] = [
  { field: 'statusClass', label: 'Status class', valueKind: 'enum', serverFilter: 'statusClass', enumValues: ['2xx', '4xx'] },
  { field: 'depth', label: 'Depth', valueKind: 'number', serverFilter: 'depth' },
  { field: 'indexable', label: 'Indexable', valueKind: 'boolean', serverFilter: 'indexable' },
  { field: 'canonicalUrl', label: 'URL', valueKind: 'text' },
  { field: 'extract:*:price', label: 'Extracted: price', valueKind: 'text', dynamicOrigin: 'extraction' }
];

describe('locked shape composition', () => {
  it('composes conditions with the exact contract literals', () => {
    const node = condition('depth', 'gte', 3);
    expect(node).toEqual({ kind: 'condition', field: 'depth', operator: 'gte', value: 3 });
  });

  it('composes nested AND/OR groups with the exact contract literals', () => {
    const tree = group('and', [
      condition('statusClass', 'eq', '2xx'),
      group('or', [condition('depth', 'lt', 3), condition('indexable', 'eq', true)])
    ]);
    expect(tree.kind).toBe('group');
    expect(tree.operator).toBe('and');
    expect(tree.children[1]).toMatchObject({ kind: 'group', operator: 'or' });
    expect(validateTree(tree).valid).toBe(true);
  });

  it('offers only contract operators (no invented wire operators)', () => {
    const locked = ['eq', 'neq', 'gt', 'gte', 'lt', 'lte', 'contains', 'startsWith', 'endsWith', 'in', 'matches'];
    expect(OPERATOR_DEFS.map((def) => def.operator).sort()).toEqual([...locked].sort());
    expect(OPERATOR_DEFS.some((def) => (def.operator as string) === 'between')).toBe(false);
  });

  it('filters operators by field kind', () => {
    expect(operatorsForKind('number').map((def) => def.operator)).toContain('gte');
    expect(operatorsForKind('number').map((def) => def.operator)).not.toContain('contains');
    expect(operatorsForKind('text').map((def) => def.operator)).toContain('matches');
    expect(operatorsForKind('boolean').map((def) => def.operator)).toEqual(['eq', 'neq']);
  });
});

describe('depth-16 whitelist enforcement', () => {
  it('measures depth from the root group', () => {
    expect(treeDepth(emptyTree())).toBe(1);
    expect(treeDepth(group('and', [condition('a', 'eq', 1)]))).toBe(2);
    expect(treeDepth(group('and', [group('or', [condition('a', 'eq', 1)])]))).toBe(3);
  });

  it('accepts edits up to depth 16 and rejects depth 17', () => {
    // Build a chain of nested groups at the limit.
    let tree: QueryRuleGroup = group('and', [condition('depth', 'eq', 1)]);
    for (let i = 0; i < MAX_QUERY_DEPTH - 2; i += 1) {
      tree = group('and', [tree]);
    }
    expect(treeDepth(tree)).toBe(MAX_QUERY_DEPTH);
    expect(validateTree(tree).valid).toBe(true);

    // One more level would exceed the server whitelist.
    const path: number[] = [];
    let cursor = tree;
    while (cursor.children[0]?.kind === 'group') {
      path.push(0);
      cursor = cursor.children[0] as QueryRuleGroup;
    }
    const rejected = appendChildGuarded(tree, path, group('or', [condition('depth', 'eq', 2)]));
    expect(rejected.ok).toBe(false);
    if (!rejected.ok) expect(rejected.reason).toContain('16');
  });

  it('guarded append succeeds when depth stays within the limit', () => {
    const result = appendChildGuarded(emptyTree(), [], condition('depth', 'eq', 1));
    expect(result.ok).toBe(true);
    if (result.ok) expect(countConditions(result.tree)).toBe(1);
  });

  it('validateTree rejects over-depth saved trees', () => {
    let node: unknown = condition('a', 'eq', 1);
    for (let i = 0; i < MAX_QUERY_DEPTH + 2; i += 1) node = group('and', [node as never]);
    const result = validateTree(node);
    expect(result.valid).toBe(false);
    expect(result.errors.join(' ')).toContain('depth');
  });

  it('validateTree rejects malformed nodes', () => {
    expect(validateTree(null).valid).toBe(false);
    expect(validateTree({ kind: 'group', operator: 'xor', children: [] }).valid).toBe(false);
    expect(validateTree({ kind: 'condition', field: '', operator: 'eq', value: 1 }).valid).toBe(false);
    expect(validateTree({ kind: 'condition', field: 'a', operator: 'bogus', value: 1 }).valid).toBe(false);
    expect(validateTree({ kind: 'other' }).valid).toBe(false);
  });
});

describe('between convenience', () => {
  it('expands to AND(gte, lte) using only locked operators', () => {
    const node = between('depth', 2, 5);
    expect(node).toEqual({
      kind: 'group',
      operator: 'and',
      children: [
        { kind: 'condition', field: 'depth', operator: 'gte', value: 2 },
        { kind: 'condition', field: 'depth', operator: 'lte', value: 5 }
      ]
    });
    expect(validateTree(node).valid).toBe(true);
  });
});

describe('immutable tree edits', () => {
  const base = group('and', [condition('depth', 'eq', 1), group('or', [condition('indexable', 'eq', true)])]);

  it('replaceAt does not mutate the original', () => {
    const next = replaceAt(base, [0], condition('depth', 'eq', 9));
    expect(nodeAt(next, [0])).toMatchObject({ value: 9 });
    expect(nodeAt(base, [0])).toMatchObject({ value: 1 });
  });

  it('removeAt removes nested children immutably', () => {
    const next = removeAt(base, [1, 0]);
    expect((nodeAt(next, [1]) as QueryRuleGroup).children).toHaveLength(0);
    expect((nodeAt(base, [1]) as QueryRuleGroup).children).toHaveLength(1);
  });

  it('appendChild adds to the addressed group only', () => {
    const next = appendChild(base, [1], condition('depth', 'gt', 2));
    expect((nodeAt(next, [1]) as QueryRuleGroup).children).toHaveLength(2);
    expect((nodeAt(next, []) as QueryRuleGroup).children).toHaveLength(2);
  });

  it('toggleGroupOperator flips AND ↔ OR in place', () => {
    const next = toggleGroupOperator(base, [1]);
    expect((nodeAt(next, [1]) as QueryRuleGroup).operator).toBe('and');
    expect((nodeAt(base, [1]) as QueryRuleGroup).operator).toBe('or');
  });

  it('nodeAt returns null for invalid paths', () => {
    expect(nodeAt(base, [5])).toBeNull();
    expect(nodeAt(base, [0, 0])).toBeNull();
  });
});

describe('dynamic columns', () => {
  it('names extraction and snippet fields distinctly', () => {
    expect(extractionField('product-schema', 'price')).toBe('extract:product-schema:price');
    expect(snippetField('serp-title', 'title')).toBe('snippet:serp-title:title');
    expect(isDynamicField('extract:r:c')).toBe(true);
    expect(isDynamicField('snippet:s:c')).toBe(true);
    expect(isDynamicField('depth')).toBe(false);
  });

  it('dynamic conditions compose into the same locked tree', () => {
    const tree = group('and', [condition(extractionField('*', 'price'), 'contains', '9.99')]);
    expect(validateTree(tree).valid).toBe(true);
    expect(countConditions(tree)).toBe(1);
  });
});

describe('foldTreeIntoFilters — live route filtering', () => {
  it('folds translatable AND leaves into whitelisted route filters', () => {
    const tree = group('and', [
      condition('statusClass', 'eq', '2xx'),
      condition('indexable', 'eq', true)
    ]);
    const fold = foldTreeIntoFilters(tree, fields);
    expect(fold.filters).toEqual({ statusClass: '2xx', indexable: true });
    expect(fold.appliedFields).toEqual(['statusClass', 'indexable']);
    expect(fold.deferredFields).toHaveLength(0);
  });

  it('defers OR-group conditions (routes express AND filters)', () => {
    const tree = group('or', [condition('statusClass', 'eq', '2xx'), condition('statusClass', 'eq', '4xx')]);
    const fold = foldTreeIntoFilters(tree, fields);
    expect(fold.filters).toEqual({});
    expect(fold.deferredFields).toHaveLength(2);
  });

  it('defers dynamic-column and non-equality conditions honestly', () => {
    const tree = group('and', [
      condition('extract:*:price', 'contains', '9'),
      condition('depth', 'gte', 2),
      condition('canonicalUrl', 'neq', 'x')
    ]);
    const fold = foldTreeIntoFilters(tree, fields);
    expect(fold.filters).toEqual({});
    expect(fold.deferredFields).toEqual(['extract:*:price', 'depth', 'canonicalUrl']);
  });

  it('handles a null tree', () => {
    expect(foldTreeIntoFilters(null, fields).filters).toEqual({});
  });
});

describe('summary', () => {
  it('renders a readable tree summary with field labels', () => {
    const tree = group('and', [
      condition('statusClass', 'eq', '2xx'),
      group('or', [condition('depth', 'lt', 3), condition('depth', 'gt', 5)])
    ]);
    const summary = summarizeTree(tree, fields);
    expect(summary).toContain('Status class eq 2xx');
    expect(summary).toContain('OR');
  });
});
