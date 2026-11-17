/**
 * Query-builder entry (FS-CRAWL-001 §8.7): recursive AND/OR rule trees over
 * URL-record fields with the eleven comparison operators, saved-query
 * CRUD against /api/queries, and a live JSON preview. Nesting is capped at
 * depth 16 to match the backend whitelist; the field list is curated from
 * the URL explorer surface.
 *
 * @module tools/QueryBuilderTool
 */

import { useCallback, useEffect, useMemo, useState } from 'react';
import {
  ActionIcon,
  Badge,
  Button,
  Group,
  Paper,
  SegmentedControl,
  Select,
  Stack,
  Text,
  TextInput
} from '@mantine/core';
import { IconCommand, IconPlus, IconRefresh, IconX } from '../theme/icons';
import { ToolFrame } from './shared/ToolFrame';
import { ToolErrorBanner } from './shared/ToolErrorBanner';
import { CodeBlock } from './shared/CodeBlock';
import { useToolCall } from './shared/useToolCall';
import type {
  QueryComparisonOperator,
  QueryRuleCondition,
  QueryRuleGroup,
  QueryRuleNode,
  SavedQuery
} from '../contracts/api/queries';
import type { JsonPrimitive } from '../contracts/common/primitives';

/** Maximum rule-tree nesting (backend whitelist). */
export const MAX_TREE_DEPTH = 16;

/** Curated URL-record fields offered by the builder. */
export const QUERY_FIELDS = [
  'canonicalUrl',
  'statusCode',
  'statusClass',
  'contentType',
  'depth',
  'indexable',
  'title',
  'blockReason',
  'wafProvider'
] as const;

const OPERATORS: readonly { value: QueryComparisonOperator; label: string }[] = [
  { value: 'eq', label: '= equals' },
  { value: 'neq', label: '≠ not equals' },
  { value: 'gt', label: '> greater' },
  { value: 'gte', label: '≥ greater or equal' },
  { value: 'lt', label: '< less' },
  { value: 'lte', label: '≤ less or equal' },
  { value: 'contains', label: 'contains' },
  { value: 'startsWith', label: 'starts with' },
  { value: 'endsWith', label: 'ends with' },
  { value: 'in', label: 'in list' },
  { value: 'matches', label: 'matches regex' }
];

/** Parses a raw value input into a JSON primitive (or list for `in`). */
export function parseConditionValue(raw: string, operator: QueryComparisonOperator): JsonPrimitive | readonly JsonPrimitive[] {
  const toPrimitive = (text: string): JsonPrimitive => {
    const trimmed = text.trim();
    if (trimmed === 'true') return true;
    if (trimmed === 'false') return false;
    if (trimmed !== '' && !Number.isNaN(Number(trimmed))) return Number(trimmed);
    return trimmed;
  };
  if (operator === 'in') {
    return raw
      .split(',')
      .map((part) => part.trim())
      .filter((part) => part.length > 0)
      .map(toPrimitive);
  }
  return toPrimitive(raw);
}

/** Serializes a condition value back to input text. */
export function valueToInput(value: JsonPrimitive | readonly JsonPrimitive[]): string {
  if (Array.isArray(value)) return value.join(', ');
  return String(value);
}

/** Replaces the node at `path` (child indices) with `next`, immutably. */
export function replaceNode(tree: QueryRuleNode, path: readonly number[], next: QueryRuleNode): QueryRuleNode {
  if (path.length === 0) return next;
  if (tree.kind !== 'group') return tree;
  const [head, ...rest] = path;
  return {
    ...tree,
    children: tree.children.map((child, index) => (index === head ? replaceNode(child, rest, next) : child))
  };
}

/** Removes the node at `path`, immutably. */
export function removeNode(tree: QueryRuleNode, path: readonly number[]): QueryRuleNode {
  if (path.length === 0 || tree.kind !== 'group') return tree;
  const [head, ...rest] = path;
  if (rest.length === 0) {
    return { ...tree, children: tree.children.filter((_, index) => index !== head) };
  }
  return {
    ...tree,
    children: tree.children.map((child, index) => (index === head ? removeNode(child, rest) : child))
  };
}

const EMPTY_CONDITION: QueryRuleCondition = { kind: 'condition', field: 'statusCode', operator: 'eq', value: 200 };

/** Recursive rule-node editor. */
function RuleNodeEditor({
  node,
  path,
  depth,
  onChange,
  onRemove
}: {
  node: QueryRuleNode;
  path: readonly number[];
  depth: number;
  onChange: (path: readonly number[], next: QueryRuleNode) => void;
  onRemove: (path: readonly number[]) => void;
}) {
  if (node.kind === 'condition') {
    return (
      <Group gap={6} wrap="nowrap" className="cf-query-condition" data-testid={`query-condition-${path.join('-') || 'root'}`}>
        <Select
          size="xs"
          w={150}
          data={[...QUERY_FIELDS]}
          value={QUERY_FIELDS.includes(node.field as (typeof QUERY_FIELDS)[number]) ? node.field : null}
          onChange={(value) => value && onChange(path, { ...node, field: value })}
          placeholder="field"
          searchable
          data-testid={`query-field-${path.join('-') || 'root'}`}
        />
        <Select
          size="xs"
          w={160}
          data={OPERATORS.map((op) => ({ value: op.value, label: op.label }))}
          value={node.operator}
          onChange={(value) =>
            value && onChange(path, { ...node, operator: value as QueryComparisonOperator, value: parseConditionValue(valueToInput(node.value), value as QueryComparisonOperator) })
          }
          data-testid={`query-operator-${path.join('-') || 'root'}`}
        />
        <TextInput
          flex={1}
          size="xs"
          ff="monospace"
          value={valueToInput(node.value)}
          onChange={(event) => onChange(path, { ...node, value: parseConditionValue(event.currentTarget.value, node.operator) })}
          placeholder={node.operator === 'in' ? 'value, value, …' : 'value'}
          data-testid={`query-value-${path.join('-') || 'root'}`}
        />
        <ActionIcon size="sm" variant="subtle" color="red" onClick={() => onRemove(path)} aria-label="Remove condition" data-testid={`query-remove-${path.join('-') || 'root'}`}>
          <IconX size={13} />
        </ActionIcon>
      </Group>
    );
  }

  const group = node as QueryRuleGroup;
  return (
    <Paper withBorder p="sm" className="cf-query-group" data-testid={`query-group-${path.join('-') || 'root'}`}>
      <Group justify="space-between" mb={8}>
        <Group gap={8}>
          <SegmentedControl
            size="xs"
            value={group.operator}
            onChange={(value) => onChange(path, { ...group, operator: value as 'and' | 'or' })}
            data={[
              { label: 'AND', value: 'and' },
              { label: 'OR', value: 'or' }
            ]}
            data-testid={`query-group-op-${path.join('-') || 'root'}`}
          />
          <Badge size="xs" variant="outline" color="steel">
            depth {depth}
          </Badge>
        </Group>
        <Group gap={4}>
          <Button
            size="compact-xs"
            variant="subtle"
            color="ember"
            leftSection={<IconPlus size={11} />}
            onClick={() => onChange(path, { ...group, children: [...group.children, EMPTY_CONDITION] })}
            data-testid={`query-add-condition-${path.join('-') || 'root'}`}
          >
            Condition
          </Button>
          <Button
            size="compact-xs"
            variant="subtle"
            color="signal"
            leftSection={<IconPlus size={11} />}
            disabled={depth + 1 >= MAX_TREE_DEPTH}
            onClick={() =>
              onChange(path, { ...group, children: [...group.children, { kind: 'group', operator: 'and', children: [EMPTY_CONDITION] }] })
            }
            data-testid={`query-add-group-${path.join('-') || 'root'}`}
          >
            Group
          </Button>
          {path.length > 0 ? (
            <ActionIcon size="sm" variant="subtle" color="red" onClick={() => onRemove(path)} aria-label="Remove group" data-testid={`query-remove-${path.join('-') || 'root'}`}>
              <IconX size={13} />
            </ActionIcon>
          ) : null}
        </Group>
      </Group>
      <Stack gap={6}>
        {group.children.length === 0 ? (
          <Text size="xs" c="dimmed" fs="italic">
            Empty group — matches everything. Add a condition.
          </Text>
        ) : null}
        {group.children.map((child, index) => (
          <RuleNodeEditor
            key={index}
            node={child}
            path={[...path, index]}
            depth={depth + 1}
            onChange={onChange}
            onRemove={onRemove}
          />
        ))}
      </Stack>
    </Paper>
  );
}

/** The query-builder tool. */
export function QueryBuilderTool() {
  const { pending, error, fieldErrors, run } = useToolCall();
  const [tree, setTree] = useState<QueryRuleNode>({
    kind: 'group',
    operator: 'and',
    children: [
      { kind: 'condition', field: 'statusClass', operator: 'eq', value: '2xx' },
      { kind: 'condition', field: 'indexable', operator: 'eq', value: true }
    ]
  });
  const [name, setName] = useState('Indexable success pages');
  const [saved, setSaved] = useState<readonly SavedQuery[]>([]);
  const [selectedId, setSelectedId] = useState<string | null>(null);

  const loadSaved = useCallback(async () => {
    const data = await run('queries.list', { query: { limit: 100 } });
    if (data) setSaved(data);
  }, [run]);

  useEffect(() => {
    void loadSaved();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const changeNode = useCallback((path: readonly number[], next: QueryRuleNode) => {
    setTree((current) => replaceNode(current, path, next));
  }, []);

  const deleteNode = useCallback((path: readonly number[]) => {
    setTree((current) => removeNode(current, path));
  }, []);

  const save = async () => {
    if (selectedId) {
      const data = await run('queries.update', { body: { id: selectedId, name, ruleTree: tree } });
      if (data) await loadSaved();
    } else {
      const data = await run('queries.create', { body: { name, ruleTree: tree } });
      if (data) {
        setSelectedId(data.id);
        await loadSaved();
      }
    }
  };

  const remove = async (id: string) => {
    const data = await run('queries.delete', { query: { id } });
    if (data) {
      if (selectedId === id) setSelectedId(null);
      await loadSaved();
    }
  };

  const treeJson = useMemo(() => JSON.stringify(tree, null, 2), [tree]);

  return (
    <ToolFrame
      testId="tool-query"
      icon={<IconCommand size={17} />}
      title="Query Builder"
      spec="§8.7"
      subtitle="Saved AND/OR rule trees over URL fields — depth capped at 16, operators whitelisted."
      actions={
        <Button size="compact-xs" variant="subtle" color="steel" leftSection={<IconRefresh size={12} />} onClick={() => void loadSaved()} data-testid="query-refresh">
          Reload
        </Button>
      }
    >
      <Stack gap="sm">
        <Group gap={6} wrap="nowrap" align="flex-end">
          <TextInput
            flex={1}
            label="Query name"
            size="xs"
            value={name}
            onChange={(event) => setName(event.currentTarget.value)}
            data-testid="query-name"
          />
          <Button size="xs" color="ember" loading={pending} onClick={() => void save()} data-testid="query-save" mb={1}>
            {selectedId ? 'Update saved' : 'Save query'}
          </Button>
          {selectedId ? (
            <Button
              size="xs"
              variant="subtle"
              color="steel"
              mb={1}
              onClick={() => {
                setSelectedId(null);
                setName('New query');
              }}
              data-testid="query-new"
            >
              New
            </Button>
          ) : null}
        </Group>

        {saved.length > 0 ? (
          <Group gap={6} data-testid="query-saved-list">
            {saved.map((query) => (
              <Group key={query.id} gap={2} className="cf-query-chip" data-testid={`query-saved-${query.id}`}>
                <Button
                  size="compact-xs"
                  variant={selectedId === query.id ? 'light' : 'subtle'}
                  color={selectedId === query.id ? 'ember' : 'steel'}
                  onClick={() => {
                    setSelectedId(query.id);
                    setName(query.name);
                    setTree(query.ruleTree);
                  }}
                  data-testid={`query-load-${query.id}`}
                >
                  {query.name}
                </Button>
                <ActionIcon
                  size="sm"
                  variant="subtle"
                  color="red"
                  onClick={() => void remove(query.id)}
                  aria-label={`Delete ${query.name}`}
                  data-testid={`query-delete-${query.id}`}
                >
                  <IconX size={12} />
                </ActionIcon>
              </Group>
            ))}
          </Group>
        ) : (
          <Text size="xs" c="dimmed" fs="italic" data-testid="query-saved-empty">
            No saved queries yet — build a tree and save it.
          </Text>
        )}

        <RuleNodeEditor node={tree} path={[]} depth={1} onChange={changeNode} onRemove={deleteNode} />
        <ToolErrorBanner error={error} fieldErrors={fieldErrors} testId="query-error" />
        <CodeBlock label="Rule tree JSON" code={treeJson} testId="query-json" maxHeight={200} />
      </Stack>
    </ToolFrame>
  );
}