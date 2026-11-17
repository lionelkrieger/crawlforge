/**
 * Plugin contribution registry proofs (WP-06-06 consumes): formatter
 * priority, preset scoping, per-plugin detach, and read-only isolation.
 */

import { describe, expect, it } from 'vitest';
import { createGridContributionRegistry } from '../../../src/frontend/src/grid/contributions';
import { group, condition } from '../../../src/frontend/src/grid/queryBuilder';

describe('GridContributionRegistry', () => {
  it('resolves the highest-priority formatter for a column', () => {
    const registry = createGridContributionRegistry();
    registry.registerFormatter({ pluginId: 'p-low', columnKey: 'price', priority: 1, format: (value) => `low:${value}` });
    registry.registerFormatter({ pluginId: 'p-high', columnKey: 'price', priority: 9, format: (value) => `high:${value}` });
    const formatter = registry.formatterFor('price');
    expect(formatter?.pluginId).toBe('p-high');
    expect(formatter?.format('9.99', {})).toBe('high:9.99');
  });

  it('returns null for unregistered columns', () => {
    const registry = createGridContributionRegistry();
    expect(registry.formatterFor('missing')).toBeNull();
  });

  it('unregister detaches a single formatter', () => {
    const registry = createGridContributionRegistry();
    const detach = registry.registerFormatter({ pluginId: 'p', columnKey: 'url', format: (value) => String(value) });
    expect(registry.formatterFor('url')).not.toBeNull();
    detach();
    expect(registry.formatterFor('url')).toBeNull();
  });

  it('scopes presets by explorer id with wildcard support', () => {
    const registry = createGridContributionRegistry();
    registry.registerPreset({ pluginId: 'p', id: 'errors-only', label: 'Errors only', explorerId: 'urls', ruleTree: group('and', [condition('statusClass', 'eq', '5xx')]) });
    registry.registerPreset({ pluginId: 'p', id: 'global', label: 'Indexable', explorerId: '*', ruleTree: group('and', [condition('indexable', 'eq', true)]) });
    expect(registry.presetsFor('urls').map((preset) => preset.id)).toEqual(['errors-only', 'global']);
    expect(registry.presetsFor('issues').map((preset) => preset.id)).toEqual(['global']);
    expect(registry.allPresets()).toHaveLength(2);
  });

  it('unregisterPlugin detaches every contribution of one plugin', () => {
    const registry = createGridContributionRegistry();
    registry.registerFormatter({ pluginId: 'p-a', columnKey: 'x', format: (value) => String(value) });
    registry.registerFormatter({ pluginId: 'p-b', columnKey: 'x', format: (value) => String(value) });
    registry.registerPreset({ pluginId: 'p-a', id: 'a1', label: 'A', explorerId: '*', ruleTree: group('and', []) });
    registry.registerPreset({ pluginId: 'p-b', id: 'b1', label: 'B', explorerId: '*', ruleTree: group('and', []) });
    registry.unregisterPlugin('p-a');
    expect(registry.formatterFor('x')?.pluginId).toBe('p-b');
    expect(registry.allPresets().map((preset) => preset.id)).toEqual(['b1']);
  });

  it('presets carry locked rule trees untouched (read-only isolation)', () => {
    const registry = createGridContributionRegistry();
    const tree = group('and', [condition('depth', 'gte', 2)]);
    registry.registerPreset({ pluginId: 'p', id: 'deep', label: 'Deep pages', explorerId: 'urls', ruleTree: tree });
    const resolved = registry.presetsFor('urls')[0];
    expect(resolved.ruleTree).toEqual(tree);
    expect(resolved.ruleTree).toBe(tree);
  });
});
