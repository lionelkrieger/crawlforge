/**
 * Interactive Query Builder panel (FS-CRAWL-001 §8.7, criterion #33 facet).
 *
 * Nested AND/OR groups over static URL columns AND dynamic extraction /
 * snippet columns, composing the LOCKED QueryRuleNode shape. Depth is
 * guarded at 16 (server whitelist) on every edit. Presets come from the
 * plugin contribution registry; saved queries persist through the frozen
 * /api/queries routes (PUT/DELETE carry the id in the body/query).
 *
 * @module grid/QueryBuilderPanel
 */

import { useCallback, useEffect, useMemo, useState } from 'react';
import type { QueryRuleNode, SavedQuery } from '../contracts/api/queries';
import type { LoopbackApiClient } from '../contracts/api/client';
import type { JsonPrimitive } from '../contracts/common/primitives';
import {
  appendChildGuarded,
  condition,
  emptyTree,
  group,
  nodeAt,
  operatorsForKind,
  removeAt,
  replaceAt,
  summarizeTree,
  toggleGroupOperator,
  treeDepth,
  countConditions,
  foldTreeIntoFilters,
  validateTree,
  type QueryFieldDef,
  type TreePath
} from './queryBuilder';
import { MAX_QUERY_DEPTH } from './types';
import type { GridContributionRegistry } from './contributions';

/** Panel props. */
export interface QueryBuilderPanelProps {
  readonly fields: readonly QueryFieldDef[];
  readonly explorerId: string;
  readonly initialTree: QueryRuleNode | null;
  readonly api: LoopbackApiClient | null;
  readonly registry?: GridContributionRegistry;
  readonly onApply: (tree: QueryRuleNode | null) => void;
  readonly onClose: () => void;
}

/** Value editor per field kind. */
function ValueEditor({ field, value, onChange }: {
  field: QueryFieldDef | undefined;
  value: JsonPrimitive | readonly JsonPrimitive[];
  onChange: (value: JsonPrimitive | readonly JsonPrimitive[]) => void;
}) {
  const kind = field?.valueKind ?? 'text';
  if (kind === 'boolean') {
    return (
      <select
        className="cf-qb-value"
        aria-label="Condition value"
        value={String(value)}
        onChange={(event) => onChange(event.currentTarget.value === 'true')}
        data-testid="cf-qb-value"
      >
        <option value="true">true</option>
        <option value="false">false</option>
      </select>
    );
  }
  if (kind === 'enum' && field?.enumValues) {
    return (
      <select
        className="cf-qb-value"
        aria-label="Condition value"
        value={String(value ?? '')}
        onChange={(event) => onChange(event.currentTarget.value)}
        data-testid="cf-qb-value"
      >
        {field.enumValues.map((choice) => <option key={choice} value={choice}>{choice}</option>)}
      </select>
    );
  }
  if (Array.isArray(value)) {
    return (
      <input
        className="cf-qb-value"
        aria-label="Condition value list (comma separated)"
        value={value.join(', ')}
        onChange={(event) => onChange(event.currentTarget.value.split(',').map((part) => part.trim()).filter((part) => part.length > 0))}
        data-testid="cf-qb-value"
      />
    );
  }
  return (
    <input
      className="cf-qb-value"
      type={kind === 'number' ? 'number' : 'text'}
      aria-label="Condition value"
      value={value === null ? '' : String(value)}
      onChange={(event) => {
        const raw = event.currentTarget.value;
        onChange(kind === 'number' ? (raw === '' ? null : Number(raw)) : raw);
      }}
      data-testid="cf-qb-value"
    />
  );
}

/** Recursive rule-node editor. */
function NodeEditor({ root, path, fields, onChange, depth }: {
  root: QueryRuleNode;
  path: TreePath;
  fields: readonly QueryFieldDef[];
  onChange: (next: QueryRuleNode) => void;
  depth: number;
}) {
  const node = nodeAt(root, path);
  const [error, setError] = useState<string | null>(null);
  if (!node) return null;

  if (node.kind === 'condition') {
    const field = fields.find((candidate) => candidate.field === node.field);
    const operators = operatorsForKind(field?.valueKind ?? 'text');
    const operator = operators.some((def) => def.operator === node.operator) ? node.operator : operators[0]?.operator ?? 'eq';
    const isList = operators.find((def) => def.operator === operator)?.list === true;
    return (
      <div className="cf-qb-condition" data-testid="cf-qb-condition">
        <select
          className="cf-qb-field"
          aria-label="Condition field"
          value={node.field}
          onChange={(event) => {
            const nextField = event.currentTarget.value;
            const nextKind = fields.find((candidate) => candidate.field === nextField)?.valueKind ?? 'text';
            const nextOperator = operatorsForKind(nextKind)[0]?.operator ?? 'eq';
            onChange(replaceAt(root, path, condition(nextField, nextOperator, nextKind === 'number' ? null : '')));
          }}
          data-testid="cf-qb-field"
        >
          {fields.map((candidate) => (
            <option key={candidate.field} value={candidate.field}>{candidate.label}</option>
          ))}
        </select>
        <select
          className="cf-qb-operator"
          aria-label="Condition operator"
          value={operator}
          onChange={(event) => {
            const nextOperator = event.currentTarget.value as typeof operator;
            const nextIsList = operators.find((def) => def.operator === nextOperator)?.list === true;
            const nextValue = nextIsList ? [] : (field?.valueKind === 'number' ? null : '');
            onChange(replaceAt(root, path, condition(node.field, nextOperator, nextValue)));
          }}
          data-testid="cf-qb-operator"
        >
          {operators.map((def) => <option key={def.operator} value={def.operator}>{def.label}</option>)}
        </select>
        <ValueEditor
          field={field}
          value={isList && !Array.isArray(node.value) ? [] : node.value}
          onChange={(value) => onChange(replaceAt(root, path, condition(node.field, operator, value)))}
        />
        <button
          type="button"
          className="cf-qb-remove"
          aria-label="Remove condition"
          onClick={() => onChange(removeAt(root, path))}
          data-testid="cf-qb-remove"
        >
          ✕
        </button>
      </div>
    );
  }

  return (
    <div className={`cf-qb-group cf-qb-depth-${Math.min(depth, 4)}`} data-testid="cf-qb-group">
      <div className="cf-qb-group-header">
        <button
          type="button"
          className={`cf-qb-operator-toggle cf-qb-${node.operator}`}
          onClick={() => onChange(toggleGroupOperator(root, path))}
          aria-label={`Toggle group operator (currently ${node.operator.toUpperCase()})`}
          data-testid="cf-qb-group-operator"
        >
          {node.operator.toUpperCase()}
        </button>
        <span className="cf-qb-group-hint">{node.children.length} rule{node.children.length === 1 ? '' : 's'}</span>
        <span className="cf-qb-group-actions">
          <button
            type="button"
            className="cf-qb-add"
            onClick={() => {
              const first = fields[0];
              const result = appendChildGuarded(root, path, condition(first?.field ?? 'url', 'contains', ''));
              if (result.ok) { setError(null); onChange(result.tree); }
              else setError(result.reason);
            }}
            data-testid="cf-qb-add-condition"
          >
            + Condition
          </button>
          <button
            type="button"
            className="cf-qb-add"
            onClick={() => {
              const result = appendChildGuarded(root, path, group('or', []));
              if (result.ok) { setError(null); onChange(result.tree); }
              else setError(result.reason);
            }}
            data-testid="cf-qb-add-group"
          >
            + Group
          </button>
          {path.length > 0 ? (
            <button
              type="button"
              className="cf-qb-remove"
              aria-label="Remove group"
              onClick={() => onChange(removeAt(root, path))}
              data-testid="cf-qb-remove-group"
            >
              ✕
            </button>
          ) : null}
        </span>
      </div>
      {error ? <div className="cf-qb-error" role="alert" data-testid="cf-qb-depth-error">{error}</div> : null}
      <div className="cf-qb-children">
        {node.children.length === 0 ? (
          <div className="cf-qb-empty">Empty group — add a condition or nest a group.</div>
        ) : (
          node.children.map((_, index) => (
            <NodeEditor
              key={index}
              root={root}
              path={[...path, index]}
              fields={fields}
              onChange={onChange}
              depth={depth + 1}
            />
          ))
        )}
      </div>
    </div>
  );
}

/** The query builder panel. */
export function QueryBuilderPanel(props: QueryBuilderPanelProps) {
  const { fields, explorerId, initialTree, api, registry, onApply, onClose } = props;
  const [tree, setTree] = useState<QueryRuleNode>(() => initialTree ?? emptyTree());
  const [saveName, setSaveName] = useState('');
  const [saved, setSaved] = useState<readonly SavedQuery[]>([]);
  const [feedback, setFeedback] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const depth = treeDepth(tree);
  const conditions = countConditions(tree);
  const validation = useMemo(() => validateTree(tree), [tree]);
  const fold = useMemo(() => foldTreeIntoFilters(tree, fields), [tree, fields]);
  const presets = useMemo(() => registry?.presetsFor(explorerId) ?? [], [registry, explorerId]);

  const loadSaved = useCallback(async () => {
    if (!api) return;
    const envelope = await api.request('queries.list', { query: { limit: 50 } });
    if (envelope.data) setSaved(envelope.data);
  }, [api]);

  useEffect(() => {
    void loadSaved();
  }, [loadSaved]);

  const save = useCallback(async () => {
    if (!api || !saveName.trim() || !validation.valid) return;
    setBusy(true);
    setFeedback(null);
    const envelope = await api.request('queries.create', { body: { name: saveName.trim(), ruleTree: tree } });
    setBusy(false);
    if (envelope.error) setFeedback(envelope.error.message);
    else {
      setFeedback(`Saved “${saveName.trim()}”.`);
      setSaveName('');
      await loadSaved();
    }
  }, [api, saveName, tree, validation.valid, loadSaved]);

  const removeSaved = useCallback(async (id: string) => {
    if (!api) return;
    await api.request('queries.delete', { query: { id } });
    await loadSaved();
  }, [api, loadSaved]);

  return (
    <div className="cf-qb" data-testid="cf-qb-panel">
      <div className="cf-qb-header">
        <h3 className="cf-qb-title">Query builder</h3>
        <span className="cf-qb-depth" data-testid="cf-qb-depth">depth {depth}/{MAX_QUERY_DEPTH}</span>
        <span className="cf-qb-count">{conditions} condition{conditions === 1 ? '' : 's'}</span>
        <button type="button" className="cf-qb-close" onClick={onClose} aria-label="Close query builder" data-testid="cf-qb-close">✕</button>
      </div>

      <div className="cf-qb-summary" data-testid="cf-qb-summary">{summarizeTree(tree, fields)}</div>

      <NodeEditor root={tree} path={[]} fields={fields} onChange={setTree} depth={1} />

      {!validation.valid ? (
        <div className="cf-qb-error" role="alert" data-testid="cf-qb-invalid">{validation.errors.join('; ')}</div>
      ) : null}

      <div className="cf-qb-fold-note" data-testid="cf-qb-fold-note">
        {fold.appliedFields.length} condition{fold.appliedFields.length === 1 ? '' : 's'} apply live to this explorer
        {fold.deferredFields.length > 0
          ? ` · ${fold.deferredFields.length} travel with saved queries & export (dynamic/OR conditions)`
          : ''}
      </div>

      {presets.length > 0 ? (
        <div className="cf-qb-presets" data-testid="cf-qb-presets">
          <span className="cf-qb-section-label">Plugin presets</span>
          {presets.map((preset) => (
            <button
              key={`${preset.pluginId}:${preset.id}`}
              type="button"
              className="cf-qb-preset"
              onClick={() => setTree(preset.ruleTree)}
              data-testid={`cf-qb-preset-${preset.id}`}
            >
              {preset.label}
            </button>
          ))}
        </div>
      ) : null}

      <div className="cf-qb-actions">
        <button
          type="button"
          className="cf-qb-apply"
          disabled={!validation.valid}
          onClick={() => { onApply(countConditions(tree) === 0 ? null : tree); onClose(); }}
          data-testid="cf-qb-apply"
        >
          Apply query
        </button>
        <button type="button" className="cf-qb-reset" onClick={() => setTree(emptyTree())} data-testid="cf-qb-reset">
          Reset
        </button>
      </div>

      <div className="cf-qb-save-row">
        <input
          className="cf-qb-save-name"
          placeholder="Save as named query…"
          aria-label="Saved query name"
          value={saveName}
          onChange={(event) => setSaveName(event.currentTarget.value)}
          data-testid="cf-qb-save-name"
        />
        <button type="button" className="cf-qb-save" disabled={!api || busy || !saveName.trim()} onClick={() => void save()} data-testid="cf-qb-save">
          Save
        </button>
      </div>
      {feedback ? <div className="cf-qb-feedback" data-testid="cf-qb-feedback">{feedback}</div> : null}

      {saved.length > 0 ? (
        <div className="cf-qb-saved" data-testid="cf-qb-saved">
          <span className="cf-qb-section-label">Saved queries</span>
          {saved.map((query) => (
            <div key={query.id} className="cf-qb-saved-row">
              <button
                type="button"
                className="cf-qb-saved-load"
                onClick={() => setTree(query.ruleTree)}
                data-testid={`cf-qb-saved-load-${query.id}`}
              >
                {query.name}
              </button>
              <button
                type="button"
                className="cf-qb-remove"
                aria-label={`Delete saved query ${query.name}`}
                onClick={() => void removeSaved(query.id)}
                data-testid={`cf-qb-saved-delete-${query.id}`}
              >
                ✕
              </button>
            </div>
          ))}
        </div>
      ) : null}
    </div>
  );
}
