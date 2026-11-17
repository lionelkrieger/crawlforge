/**
 * User dictionary DTOs (local spell-check dictionaries).
 *
 * Routes: GET/POST/DELETE /api/dictionary — language filter; word on add.
 * Dictionaries are local user data; bundled stop-word/dictionary assets are
 * never fetched remotely (E2).
 *
 * @module contracts/api/dictionary
 */

import type { IsoTimestamp } from '../common/primitives';
import type { ListQuery } from '../common/pagination';

/** One user dictionary entry. */
export interface DictionaryEntry {
  readonly word: string;
  /** Language tag the word belongs to. */
  readonly language: string;
  readonly addedAt: IsoTimestamp;
}

/** GET /api/dictionary — language filter, pagination. */
export interface DictionaryListQuery extends ListQuery {
  readonly language?: string;
}

/** POST /api/dictionary — word on add. */
export interface DictionaryAddRequest {
  readonly word: string;
  readonly language: string;
}

/** DELETE /api/dictionary — word (and optional language) to remove. */
export interface DictionaryRemoveRequest {
  readonly word: string;
  readonly language?: string;
}

/** Add/remove confirmation. */
export interface DictionaryMutationResponse {
  readonly word: string;
  readonly language: string;
  readonly added?: boolean;
  readonly removed?: boolean;
}
