/**
 * Bounded recent-activity ring buffer (contracts/store/state
 * RecentActivityBuffer). FS-CRAWL-002 §7.4: <= 1,000 entries; overflow
 * increments droppedCount; raw event payloads are never accumulated.
 *
 * @module store/ringBuffer
 */

import type { HubActivityEntry, RecentActivityBuffer } from '../contracts/store/state';
import { DEFAULT_RECENT_ACTIVITY_CAPACITY } from '../contracts/store/state';

/** Creates an empty bounded buffer. */
export function emptyActivityBuffer(
  capacity: number = DEFAULT_RECENT_ACTIVITY_CAPACITY
): RecentActivityBuffer {
  return { entries: [], capacity: Math.max(1, capacity), droppedCount: 0 };
}

/**
 * Appends one entry, evicting the oldest under overflow and accounting the
 * drop. Returns a new buffer object (immutable updates, §8.8).
 */
export function pushActivity(
  buffer: RecentActivityBuffer,
  entry: HubActivityEntry
): RecentActivityBuffer {
  const entries = [...buffer.entries, entry];
  let droppedCount = buffer.droppedCount;
  while (entries.length > buffer.capacity) {
    entries.shift();
    droppedCount += 1;
  }
  return { entries, capacity: buffer.capacity, droppedCount };
}
