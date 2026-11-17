import { describe, expect, it } from 'vitest';
import { createBridge, parseDescriptor, IPC_CHANNELS } from '../src/preload/bridge';
import { HandshakeDescriptor } from '../src/types';

const descriptor: HandshakeDescriptor = {
  version: 1,
  port: 52000,
  sessionToken: 'per-launch-token',
  createdAtUtc: '2026-11-14T00:00:00Z'
};

describe('preload bridge contract (window.crawlForge)', () => {
  it('exposes exactly backendBaseUrl and getSessionToken with ratified semantics', () => {
    const bridge = createBridge(descriptor);
    expect(Object.keys(bridge).sort()).toEqual(['backendBaseUrl', 'getSessionToken']);
    expect(bridge.backendBaseUrl).toBe('http://127.0.0.1:52000');
    expect(bridge.getSessionToken()).toBe('per-launch-token');
  });

  it('returns a frozen surface (no renderer mutation)', () => {
    const bridge = createBridge(descriptor);
    expect(Object.isFrozen(bridge)).toBe(true);
  });

  it('rejects invalid descriptors at the boundary', () => {
    expect(() => createBridge({ ...descriptor, version: 2 as never })).toThrow();
    expect(() => createBridge({ ...descriptor, port: 0 })).toThrow();
    expect(() => createBridge({ ...descriptor, port: 70000 })).toThrow();
    expect(() => createBridge({ ...descriptor, sessionToken: '' })).toThrow();
  });

  it('parseDescriptor validates IPC payloads defensively', () => {
    expect(parseDescriptor(descriptor)).toEqual(descriptor);
    expect(parseDescriptor(null)).toBeNull();
    expect(parseDescriptor({ version: 1, port: '80' })).toBeNull();
    expect(parseDescriptor({ ...descriptor, sessionToken: '' })).toBeNull();
    expect(parseDescriptor({ ...descriptor, createdAtUtc: 42 })).toBeNull();
  });

  it('freezes the IPC channel names', () => {
    expect(Object.isFrozen(IPC_CHANNELS)).toBe(true);
    expect(IPC_CHANNELS.fileDialog).toBe('crawlforge:file-dialog');
  });
});
