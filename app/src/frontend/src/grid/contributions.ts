/**
 * Plugin contribution points for the grid (FS-CRAWL-002 §6, WP-06-06
 * consumes this registry).
 *
 * Plugins contribute TWO additive surfaces, both read-only (E5):
 *   - column formatters: decorate cell rendering for a column/key without
 *     touching data or layout;
 *   - filter presets: named query rule trees offered in the builder's
 *     preset menu (composed from the locked QueryRuleNode shape).
 *
 * Contributions never mutate grid state, the store, or the API surface.
 * Registration is keyed by plugin id so unregister(pluginId) detaches
 * everything a plugin added (tab-deactivation cleanup, §7.4).
 *
 * @module grid/contributions
 */

import type { QueryRuleNode } from '../contracts/api/queries';
import type { RowData } from '@tanstack/react-table';

/** A plugin-supplied cell formatter. Returns display text; never mutates. */
export interface GridColumnFormatter<TValue = unknown, TRow extends RowData = RowData> {
  readonly pluginId: string;
  /** Column id or meta.formatterKey this formatter decorates. */
  readonly columnKey: string;
  /** Higher priority wins when several plugins target one column. */
  readonly priority?: number;
  format(value: TValue, row: TRow): string;
}

/** A plugin-supplied named filter preset (locked rule tree). */
export interface GridFilterPreset {
  readonly pluginId: string;
  readonly id: string;
  readonly label: string;
  /** Explorer ids this preset applies to ('*' = all). */
  readonly explorerId: string;
  readonly ruleTree: QueryRuleNode;
}

/** Registry surface consumed by the grid engine and the plugin host. */
export interface GridContributionRegistry {
  registerFormatter<TValue, TRow extends RowData>(formatter: GridColumnFormatter<TValue, TRow>): () => void;
  registerPreset(preset: GridFilterPreset): () => void;
  /** Detaches every contribution of one plugin (cleanup on deactivate). */
  unregisterPlugin(pluginId: string): void;
  /** Resolves the winning formatter for a column (highest priority). */
  formatterFor(columnKey: string): GridColumnFormatter | null;
  /** Presets applicable to an explorer, registration order preserved. */
  presetsFor(explorerId: string): GridFilterPreset[];
  /** All registered presets (builder menu). */
  allPresets(): GridFilterPreset[];
}

/** Creates an empty contribution registry. */
export function createGridContributionRegistry(): GridContributionRegistry {
  const formatters = new Map<string, GridColumnFormatter[]>();
  const presets: GridFilterPreset[] = [];

  const registerFormatter = <TValue, TRow extends RowData>(formatter: GridColumnFormatter<TValue, TRow>): (() => void) => {
    const list = formatters.get(formatter.columnKey) ?? [];
    const entry = formatter as GridColumnFormatter;
    formatters.set(formatter.columnKey, [...list, entry]);
    return () => {
      const current = formatters.get(formatter.columnKey) ?? [];
      const next = current.filter((candidate) => candidate !== entry);
      if (next.length === 0) formatters.delete(formatter.columnKey);
      else formatters.set(formatter.columnKey, next);
    };
  };

  const registerPreset = (preset: GridFilterPreset): (() => void) => {
    presets.push(preset);
    return () => {
      const index = presets.indexOf(preset);
      if (index >= 0) presets.splice(index, 1);
    };
  };

  return {
    registerFormatter,
    registerPreset,
    unregisterPlugin(pluginId: string): void {
      for (const [key, list] of [...formatters.entries()]) {
        const next = list.filter((formatter) => formatter.pluginId !== pluginId);
        if (next.length === 0) formatters.delete(key);
        else formatters.set(key, next);
      }
      for (let i = presets.length - 1; i >= 0; i -= 1) {
        if (presets[i].pluginId === pluginId) presets.splice(i, 1);
      }
    },
    formatterFor(columnKey: string): GridColumnFormatter | null {
      const list = formatters.get(columnKey);
      if (!list || list.length === 0) return null;
      return [...list].sort((a, b) => (b.priority ?? 0) - (a.priority ?? 0))[0];
    },
    presetsFor(explorerId: string): GridFilterPreset[] {
      return presets.filter((preset) => preset.explorerId === '*' || preset.explorerId === explorerId);
    },
    allPresets(): GridFilterPreset[] {
      return [...presets];
    }
  };
}
