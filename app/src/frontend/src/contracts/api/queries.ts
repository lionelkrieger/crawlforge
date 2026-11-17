/**
 * Saved query-builder DTOs (section 8.7).
 *
 * Routes: GET/POST/PUT/DELETE /api/queries — query rule tree; name.
 * PUT/DELETE carry the identifier in the body/query (the spec path has no
 * {id} segment for this route group).
 *
 * @module contracts/api/queries
 */

import type { EntityId, IsoTimestamp, JsonPrimitive } from '../common/primitives';
import type { ListQuery } from '../common/pagination';

/** Comparison operators available in the query builder. */
export type QueryComparisonOperator =
  | 'eq'
  | 'neq'
  | 'gt'
  | 'gte'
  | 'lt'
  | 'lte'
  | 'contains'
  | 'startsWith'
  | 'endsWith'
  | 'in'
  | 'matches';

/** One leaf condition of the rule tree. */
export interface QueryRuleCondition {
  readonly kind: 'condition';
  readonly field: string;
  readonly operator: QueryComparisonOperator;
  readonly value: JsonPrimitive | readonly JsonPrimitive[];
}

/** Boolean group of child rules. */
export interface QueryRuleGroup {
  readonly kind: 'group';
  readonly operator: 'and' | 'or';
  readonly children: readonly QueryRuleNode[];
}

/** Recursive query rule tree. */
export type QueryRuleNode = QueryRuleCondition | QueryRuleGroup;

/** Saved query-builder definition. */
export interface SavedQuery {
  readonly id: EntityId;
  readonly name: string;
  readonly ruleTree: QueryRuleNode;
  readonly createdAt: IsoTimestamp;
  readonly updatedAt: IsoTimestamp;
}

/** GET /api/queries — pagination and name search. */
export interface SavedQueryListQuery extends ListQuery {
  readonly search?: string;
}

/** POST /api/queries — query rule tree; name. */
export interface SavedQueryCreateRequest {
  readonly name: string;
  readonly ruleTree: QueryRuleNode;
}

/** PUT /api/queries — identifier plus any subset of fields. */
export interface SavedQueryUpdateRequest {
  readonly id: EntityId;
  readonly name?: string;
  readonly ruleTree?: QueryRuleNode;
}

/** DELETE /api/queries — identifier in the query string. */
export interface SavedQueryDeleteRequest {
  readonly id: EntityId;
}
