/**
 * Runtime type guards for the normalized store shape contracts.
 * @module contracts/store/guards
 */

import { isArrayOf, isNonNegativeInteger, isNumber, isRecord, isString } from '../common/guards';
import { isHubServerEventName } from '../hub/guards';
import type { EntityTable, HubActivityEntry, RecentActivityBuffer, WindowedPage } from './state';

/**
 * Narrows an unknown value to an {@link EntityTable} whose items satisfy
 * `isItem`. Validates the byId/allIds consistency invariant.
 */
export function isEntityTable<T>(
  value: unknown,
  isItem: (item: unknown) => item is T
): value is EntityTable<T> {
  if (!isRecord(value) || !isRecord(value.byId) || !isArrayOf(value.allIds, isString)) {
    return false;
  }
  const byId = value.byId;
  return (
    value.allIds.every((id) => isItem(byId[id])) &&
    Object.keys(byId).length === value.allIds.length
  );
}

/**
 * Narrows an unknown value to a {@link WindowedPage}, enforcing the
 * windowing invariants: offset >= 0, limit >= 1, total >= 0, and
 * items.length <= limit (no slice holds more than one window).
 */
export function isWindowedPage<T>(
  value: unknown,
  isItem: (item: unknown) => item is T
): value is WindowedPage<T> {
  return (
    isRecord(value) &&
    isString(value.key) &&
    isNonNegativeInteger(value.offset) &&
    isNonNegativeInteger(value.limit) &&
    value.limit >= 1 &&
    isNonNegativeInteger(value.total) &&
    isArrayOf(value.items, isItem) &&
    value.items.length <= value.limit &&
    isString(value.fetchedAt)
  );
}

/**
 * Narrows an unknown value to a {@link RecentActivityBuffer}, enforcing the
 * bounded ring-buffer invariant entries.length <= capacity (§7.4).
 */
export function isRecentActivityBuffer(value: unknown): value is RecentActivityBuffer {
  if (
    !isRecord(value) ||
    !isNonNegativeInteger(value.capacity) ||
    value.capacity < 1 ||
    !isNonNegativeInteger(value.droppedCount) ||
    !isArrayOf(value.entries, (entry): entry is HubActivityEntry => {
      if (!isRecord(entry)) return false;
      return (
        isHubServerEventName(entry.event) &&
        isString(entry.receivedAt) &&
        (entry.auditId === null || isString(entry.auditId))
      );
    })
  ) {
    return false;
  }
  return value.entries.length <= value.capacity;
}

/** Guard helper: any finite number (re-exported convenience for store tests). */
export function isHeapBytes(value: unknown): value is number {
  return isNumber(value) && value >= 0;
}
