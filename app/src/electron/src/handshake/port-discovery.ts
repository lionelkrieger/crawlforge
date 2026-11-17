/**
 * Loopback port discovery (WP-06-01): reads the Security handshake descriptor
 * published by the backend (loopback-handshake.json, schema v1). Discovery is
 * strictly local-file based — no DNS, no network, no machine identity
 * (clean-room guardrails #1/#4). Freshness is checked against the descriptor
 * timestamp so a stale file from a dead launch is never trusted blindly; the
 * supervisor still health-probes before declaring readiness.
 */
import {
  FileSystemPort,
  HandshakeDescriptor,
  HANDSHAKE_FILE_NAME,
  loopbackOrigin
} from '../types';

/** Maximum age of a handshake descriptor before it is considered stale. */
export const HANDSHAKE_MAX_AGE_MS = 24 * 60 * 60 * 1000;

/** Result of one discovery attempt. */
export type DiscoveryResult =
  | { readonly ok: true; readonly descriptor: HandshakeDescriptor; readonly baseUrl: string }
  | { readonly ok: false; readonly reason: DiscoveryFailureReason };

/** Why discovery failed (local diagnostics only; no identity dimensions). */
export type DiscoveryFailureReason =
  | 'missing'
  | 'unreadable'
  | 'malformed'
  | 'unsupported-version'
  | 'invalid-port'
  | 'missing-token'
  | 'stale';

/** Reads and validates the handshake descriptor from the per-user data dir. */
export class PortDiscovery {
  private readonly fs: FileSystemPort;
  private readonly now: () => number;

  constructor(fs: FileSystemPort, now: () => number = () => Date.now()) {
    this.fs = fs;
    this.now = now;
  }

  /** Absolute path of the handshake file inside a data directory. */
  static handshakePath(dataDirectory: string): string {
    return joinPath(dataDirectory, HANDSHAKE_FILE_NAME);
  }

  /** Attempts one discovery read against the given data directory. */
  async discover(dataDirectory: string): Promise<DiscoveryResult> {
    const path = PortDiscovery.handshakePath(dataDirectory);
    let text: string | null;
    try {
      text = await this.fs.readTextFile(path);
    } catch {
      return { ok: false, reason: 'unreadable' };
    }

    if (text === null) {
      return { ok: false, reason: 'missing' };
    }

    let parsed: unknown;
    try {
      parsed = JSON.parse(text);
    } catch {
      return { ok: false, reason: 'malformed' };
    }

    if (typeof parsed !== 'object' || parsed === null) {
      return { ok: false, reason: 'malformed' };
    }

    const record = parsed as Record<string, unknown>;
    if (record.version !== 1) {
      return { ok: false, reason: 'unsupported-version' };
    }

    const port = record.port;
    if (typeof port !== 'number' || !Number.isInteger(port) || port < 1 || port > 65535) {
      return { ok: false, reason: 'invalid-port' };
    }

    const sessionToken = record.sessionToken;
    if (typeof sessionToken !== 'string' || sessionToken.length === 0) {
      return { ok: false, reason: 'missing-token' };
    }

    const createdAtUtc = record.createdAtUtc;
    if (typeof createdAtUtc !== 'string') {
      return { ok: false, reason: 'malformed' };
    }

    const createdAt = Date.parse(createdAtUtc);
    if (Number.isNaN(createdAt)) {
      return { ok: false, reason: 'malformed' };
    }

    if (this.now() - createdAt > HANDSHAKE_MAX_AGE_MS) {
      return { ok: false, reason: 'stale' };
    }

    const descriptor: HandshakeDescriptor = {
      version: 1,
      port,
      sessionToken,
      createdAtUtc
    };
    return { ok: true, descriptor, baseUrl: loopbackOrigin(port) };
  }
}

/** Joins path segments with the platform-neutral separator used by the shell. */
export function joinPath(...segments: readonly string[]): string {
  const cleaned = segments
    .filter((segment) => segment.length > 0)
    .map((segment, index) =>
      index === 0 ? segment.replace(/[\\/]+$/, '') : segment.replace(/^[\\/]+|[\\/]+$/g, '')
    );
  return cleaned.join('/');
}
