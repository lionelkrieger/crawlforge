/**
 * Interactive Query Builder core (FS-CRAWL-001 §8.7, criterion #33 facet).
 *
 * Composes the LOCKED query shape (contracts/api/queries QueryRuleNode):
 * nested AND/OR groups of leaf conditions with the contract operator set
 * (eq/neq/gt/gte/lt/lte/contains/startsWith/endsWith/in/matches). The server
 * whitelist accepts depth ≤ 16 — enforced here at edit time so an invalid
 * tree can never be composed. "between" is a UI convenience that expands to
 * AND(gte, lte) — no wire operator is invented.
 *
 * Dynamic custom-extraction/snippet columns participate as fields named
 * `extract:<ruleKey>:<column>` / `snippet:<snippetKey>:<column>`; conditions
 * on them compose into the same locked tree (saved queries + export handoff).
 * For live explorer filtering, translatable leaves fold into the route's
 * whitelisted domain filters; the full tree always travels with saved
 * queries and the export handoff.
 *
 * @module grid/queryBuilder
 */

import type {
  QueryComparisonOperator,
  QueryRuleCondition,
  QueryRuleGroup,
  QueryRuleNode
} from '../contracts/api/queries';
import type { JsonPrimitive } from '../contracts/common/primitives';
import { MAX_QUERY_DEPTH } from './types';

/** One field the builder can target (static column or dynamic column). */
export interface QueryFieldDef {
  readonly field: string;
  readonly label: string;
  readonly valueKind: 'text' | 'number' | 'boolean' | 'enum';
  readonly enumValues?: readonly string[];
  /** Route filter key this field folds into for live filtering, if any. */
  readonly serverFilter?: string;
  /** Dynamic origin marker (extraction rule / snippet key). */
  readonly dynamicOrigin?: string;
}

/** Operator metadata for the editor UI. */
export interface OperatorDef {
  readonly operator: QueryComparisonOperator;
  readonly label: string;
  readonly kinds: ReadonlyArray<QueryFieldDef['valueKind']>;
  /** Value arity: single scalar vs list (in). */
  readonly list?: boolean;
}

export const OPERATOR_DEFS: readonly OperatorDef[] = [
  { operator: 'eq', label: 'equals', kinds: ['text', 'number', 'boolean', 'enum'] },
  { operator: 'neq', label: 'not equals', kinds: ['text', 'number', 'boolean', 'enum'] },
  { operator: 'contains', label: 'contains', kinds: ['text'] },
  { operator: 'startsWith', label: 'starts with', kinds: ['text'] },
  { operator: 'endsWith', label: 'ends with', kinds: ['text'] },
  { operator: 'gt', label: 'greater than', kinds: ['number'] },
  { operator: 'gte', label: 'at least', kinds: ['number'] },
  { operator: 'lt', label: 'less than', kinds: ['number'] },
  { operator: 'lte', label: 'at most', kinds: ['number'] },
  { operator: 'in', label: 'in set', kinds: ['text', 'number', 'enum'], list: true },
  { operator: 'matches', label: 'matches regex', kinds: ['text'] }
];

/** Operators valid for a field kind. */
export function operatorsForKind(kind: QueryFieldDef['valueKind']): OperatorDef[] {
  return OPERATOR_DEFS.filter((def) => def.kinds.includes(kind));
}

/** Creates a leaf condition (locked shape). */
export function condition(
  field: string,
  operator: QueryComparisonOperator,
  value: JsonPrimitive | readonly JsonPrimitive[]
): QueryRuleCondition {
  return { kind: 'condition', field, operator, value };
}

/** Creates an AND/OR group (locked shape). */
export function group(operator: 'and' | 'or', children: readonly QueryRuleNode[] = []): QueryRuleGroup {
  return { kind: 'group', operator, children };
}

/** The empty builder tree: a root AND group. */
export function emptyTree(): QueryRuleGroup {
  return group('and', []);
}

/** Depth of a tree (root group = 1). The server whitelist accepts ≤16. */
export function treeDepth(node: QueryRuleNode): number {
  if (node.kind === 'condition') return 1;
  if (node.children.length === 0) return 1;
  return 1 + Math.max(...node.children.map(treeDepth));
}

/** Number of leaf conditions in the tree. */
export function countConditions(node: QueryRuleNode): number {
  if (node.kind === 'condition') return 1;
  return node.children.reduce((sum, child) => sum + countConditions(child), 0);
}

/** A path addresses a node by child indices from the root. */
export type TreePath = readonly number[];

/** Reads the node at a path (null when the path is invalid). */
export function nodeAt(root: QueryRuleNode, path: TreePath): QueryRuleNode | null {
  let current: QueryRuleNode = root;
  for (const index of path) {
    if (current.kind !== 'group') return null;
    const child = current.children[index];
    if (!child) return null;
    current = child;
  }
  return current;
}

/** Immutably replaces the node at a path. */
export function replaceAt(root: QueryRuleNode, path: TreePath, replacement: QueryRuleNode): QueryRuleNode {
  if (path.length === 0) return replacement;
  if (root.kind !== 'group') return root;
  const [head, ...rest] = path;
  const children = root.children.map((child, index) =>
    index === head ? replaceAt(child, rest, replacement) : child
  );
  return { ...root, children };
}

/** Immutably removes the node at a path (no-op for the root path). */
export function removeAt(root: QueryRuleNode, path: TreePath): QueryRuleNode {
  if (path.length === 0 || root.kind !== 'group') return root;
  const [head, ...rest] = path;
  if (rest.length === 0) {
    return { ...root, children: root.children.filter((_, index) => index !== head) };
  }
  const children = root.children.map((child, index) => (index === head ? removeAt(child, rest) : child));
  return { ...root, children };
}

/** Immutably appends a child to the group at a path. */
export function appendChild(root: QueryRuleNode, path: TreePath, child: QueryRuleNode): QueryRuleNode {
  const target = nodeAt(root, path);
  if (!target || target.kind !== 'group') return root;
  return replaceAt(root, path, { ...target, children: [...target.children, child] });
}

/** Result of a depth-guarded edit. */
export type TreeEditResult =
  | { readonly ok: true; readonly tree: QueryRuleNode }
  | { readonly ok: false; readonly reason: string };

/**
 * Appends a child only when the resulting depth stays within the server
 * whitelist (MAX_QUERY_DEPTH = 16). The single gate every UI edit goes through.
 */
export function appendChildGuarded(root: QueryRuleNode, path: TreePath, child: QueryRuleNode): TreeEditResult {
  const next = appendChild(root, path, child);
  const depth = treeDepth(next);
  if (depth > MAX_QUERY_DEPTH) {
    return { ok: false, reason: `Query depth ${depth} exceeds the server limit of ${MAX_QUERY_DEPTH}.` };
  }
  return { ok: true, tree: next };
}

/**
 * "between" convenience: expands to AND(gte lower, lte upper) on the same
 * field — composed entirely from locked operators.
 */
export function between(field: string, lower: number, upper: number): QueryRuleGroup {
  return group('and', [condition(field, 'gte', lower), condition(field, 'lte', upper)]);
}

/** Flips a group's boolean operator (AND ↔ OR), immutably. */
export function toggleGroupOperator(root: QueryRuleNode, path: TreePath): QueryRuleNode {
  const target = nodeAt(root, path);
  if (!target || target.kind !== 'group') return root;
  return replaceAt(root, path, { ...target, operator: target.operator === 'and' ? 'or' : 'and' });
}

/** Structural validation of a (possibly saved) tree against the lock. */
export function validateTree(node: unknown): { readonly valid: boolean; readonly errors: readonly string[] } {
  const errors: string[] = [];
  const walk = (value: unknown, depth: number, location: string): void => {
    if (depth > MAX_QUERY_DEPTH) {
      errors.push(`${location}: depth exceeds ${MAX_QUERY_DEPTH}`);
      return;
    }
    if (typeof value !== 'object' || value === null) {
      errors.push(`${location}: not a rule node`);
      return;
    }
    const record = value as Record<string, unknown>;
    if (record.kind === 'condition') {
      if (typeof record.field !== 'string' || record.field.length === 0) errors.push(`${location}: missing field`);
      const operator = record.operator as QueryComparisonOperator;
      if (!OPERATOR_DEFS.some((def) => def.operator === operator)) errors.push(`${location}: unknown operator`);
      if (record.value === undefined) errors.push(`${location}: missing value`);
      return;
    }
    if (record.kind === 'group') {
      if (record.operator !== 'and' && record.operator !== 'or') errors.push(`${location}: group operator must be and/or`);
      if (!Array.isArray(record.children)) {
        errors.push(`${location}: group children must be an array`);
        return;
      }
      record.children.forEach((child, index) => walk(child, depth + 1, `${location}.${index}`));
      return;
    }
    errors.push(`${location}: kind must be condition or group`);
  };
  walk(node, 1, 'root');
  return { valid: errors.length === 0, errors };
}

/**
 * Folds translatable AND-group leaves into a route's whitelisted domain
 * filters for live explorer filtering. Only equality-family operators on
 * fields with a serverFilter mapping fold (eq → scalar, in → first value
 * when the route filter is scalar); everything else stays in the tree for
 * saved queries / export. OR groups never fold (routes express AND filters).
 */
export function foldTreeIntoFilters(
  tree: QueryRuleNode | null,
  fields: readonly QueryFieldDef[]
): { readonly filters: Record<string, JsonPrimitive>; readonly appliedFields: readonly string[]; readonly deferredFields: readonly string[] } {
  const filters: Record<string, JsonPrimitive> = {};
  const appliedFields: string[] = [];
  const deferredFields: string[] = [];
  if (!tree) return { filters, appliedFields, deferredFields };

  const byName = new Map(fields.map((field) => [field.field, field]));
  const visit = (node: QueryRuleNode, insideOr: boolean): void => {
    if (node.kind === 'group') {
      const childInsideOr = insideOr || node.operator === 'or';
      for (const child of node.children) visit(child, childInsideOr);
      return;
    }
    const field = byName.get(node.field);
    const foldable =
      !insideOr &&
      field?.serverFilter !== undefined &&
      (node.operator === 'eq' || node.operator === 'neq') &&
      !Array.isArray(node.value);
    if (foldable && field?.serverFilter) {
      // neq has no route-filter expression; defer it honestly.
      if (node.operator === 'eq') {
        filters[field.serverFilter] = node.value as JsonPrimitive;
        appliedFields.push(node.field);
        return;
      }
    }
    deferredFields.push(node.field);
  };
  visit(tree, false);
  return { filters, appliedFields, deferredFields };
}

/** Compact human-readable summary of a tree (palette/tooltip copy). */
export function summarizeTree(node: QueryRuleNode, fields: readonly QueryFieldDef[] = []): string {
  const label = (field: string): string => fields.find((f) => f.field === field)?.label ?? field;
  const render = (current: QueryRuleNode): string => {
    if (current.kind === 'condition') {
      const value = Array.isArray(current.value) ? current.value.join(', ') : String(current.value);
      return `${label(current.field)} ${current.operator} ${value}`;
    }
    if (current.children.length === 0) return '(empty)';
    return `(${current.children.map(render).join(` ${current.operator.toUpperCase()} `)})`;
  };
  return render(node);
}

/** Builds the dynamic field name for an extraction column. */
export function extractionField(ruleKey: string, column: string): string {
  return `extract:${ruleKey}:${column}`;
}

/** Builds the dynamic field name for a snippet column. */
export function snippetField(snippetKey: string, column: string): string {
  return `snippet:${snippetKey}:${column}`;
}

/** True when a field name addresses a dynamic extraction/snippet column. */
export function isDynamicField(field: string): boolean {
  return field.startsWith('extract:') || field.startsWith('snippet:');
}
