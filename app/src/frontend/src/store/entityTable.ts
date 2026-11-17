/**
 * Immutable normalized entity-table operations (contracts/store/state
 * EntityTable). Tables hold only loaded entities — never full datasets
 * (FS-CRAWL-002 §7.4/§8.8).
 *
 * @module store/entityTable
 */

import type { EntityTable } from '../contracts/store/state';

/** The empty table singleton. */
export function emptyTable<T>(): EntityTable<T> {
  return { byId: {}, allIds: [] };
}

/** Inserts or replaces one entity, preserving id order (append on insert). */
export function upsertEntity<T extends { readonly id: string }>(
  table: EntityTable<T>,
  entity: T
): EntityTable<T> {
  const exists = table.byId[entity.id] !== undefined;
  return {
    byId: { ...table.byId, [entity.id]: entity },
    allIds: exists ? table.allIds : [...table.allIds, entity.id]
  };
}

/** Upserts many entities in one immutable pass. */
export function upsertEntities<T extends { readonly id: string }>(
  table: EntityTable<T>,
  entities: readonly T[]
): EntityTable<T> {
  if (entities.length === 0) return table;
  const byId = { ...table.byId };
  const allIds = [...table.allIds];
  for (const entity of entities) {
    if (byId[entity.id] === undefined) allIds.push(entity.id);
    byId[entity.id] = entity;
  }
  return { byId, allIds };
}

/** Removes one entity; returns the same table when absent. */
export function removeEntity<T>(table: EntityTable<T>, id: string): EntityTable<T> {
  if (table.byId[id] === undefined) return table;
  const byId = { ...table.byId };
  delete byId[id];
  return { byId, allIds: table.allIds.filter((existing) => existing !== id) };
}

/** Replaces the table contents wholesale (bounded window refresh). */
export function tableFromEntities<T extends { readonly id: string }>(
  entities: readonly T[]
): EntityTable<T> {
  return upsertEntities(emptyTable<T>(), entities);
}

/** Projects the table to its ordered entity list. */
export function tableToList<T>(table: EntityTable<T>): readonly T[] {
  return table.allIds.map((id) => table.byId[id]);
}
