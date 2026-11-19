/**
 * Window-state persistence tests: sanitization, clamping, roundtrip, and
 * storage-denied tolerance.
 */

import { describe, expect, it } from 'vitest';
import {
  DEFAULT_WINDOW_STATE,
  loadWindowState,
  sanitizeWindowState,
  saveWindowState
} from '../../../src/frontend/src/shell/windowState';

function memoryStorage(): Storage {
  const map = new Map<string, string>();
  return {
    get length() {
      return map.size;
    },
    clear: () => map.clear(),
    getItem: (key: string) => map.get(key) ?? null,
    key: (index: number) => [...map.keys()][index] ?? null,
    removeItem: (key: string) => void map.delete(key),
    setItem: (key: string, value: string) => void map.set(key, value)
  };
}

describe('window state', () => {
  it('defaults on absent or corrupt values', () => {
    expect(sanitizeWindowState(null)).toEqual(DEFAULT_WINDOW_STATE);
    expect(sanitizeWindowState('garbage')).toEqual(DEFAULT_WINDOW_STATE);
    expect(sanitizeWindowState({ view: 'evil', layout: { split: 99 } })).toEqual({
      view: 'hub',
      layout: { split: 0.85, collapsed: false, direction: 'horizontal' },
      selectedProjectId: null
    });
  });

  it('clamps split fractions and validates orientation', () => {
    const state = sanitizeWindowState({ view: 'settings', layout: { split: 0.01, collapsed: true, direction: 'vertical' } });
    expect(state.view).toBe('settings');
    expect(state.layout.split).toBe(0.15);
    expect(state.layout.collapsed).toBe(true);
    expect(state.layout.direction).toBe('vertical');
  });

  it('roundtrips through storage', () => {
    const storage = memoryStorage();
    saveWindowState({ view: 'tools', layout: { split: 0.4, collapsed: false, direction: 'vertical' }, selectedProjectId: null }, storage);
    const loaded = loadWindowState(storage);
    expect(loaded.view).toBe('tools');
    expect(loaded.layout.split).toBe(0.4);
    expect(loaded.layout.direction).toBe('vertical');
  });

  it('carries the project/configuration views and selected project (WP-09-11)', () => {
    const state = sanitizeWindowState({ view: 'configurations', selectedProjectId: '7', layout: { split: 0.5 } });
    expect(state.view).toBe('configurations');
    expect(state.selectedProjectId).toBe('7');
    expect(sanitizeWindowState({ view: 'project', selectedProjectId: 42 }).selectedProjectId).toBeNull();
    expect(sanitizeWindowState({ view: 'project', selectedProjectId: '' }).selectedProjectId).toBeNull();
  });

  it('tolerates denied storage', () => {
    expect(loadWindowState(null)).toEqual(DEFAULT_WINDOW_STATE);
    expect(() => saveWindowState(DEFAULT_WINDOW_STATE, null)).not.toThrow();
    const throwing = {
      ...memoryStorage(),
      getItem: () => {
        throw new Error('denied');
      },
      setItem: () => {
        throw new Error('denied');
      }
    } as Storage;
    expect(loadWindowState(throwing)).toEqual(DEFAULT_WINDOW_STATE);
    expect(() => saveWindowState(DEFAULT_WINDOW_STATE, throwing)).not.toThrow();
  });
});
