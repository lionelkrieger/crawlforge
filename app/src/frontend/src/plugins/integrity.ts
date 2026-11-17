/**
 * Plugin integrity hashing (FS-CRAWL-002 §6.7 registry: integrity status).
 *
 * Deterministic SHA-256 over canonical JSON (sorted keys) of the manifest and
 * over the entry module text. A content change flips the status to `changed`
 * and revokes network grants until the user re-reviews. No identity material
 * is hashed or recorded — only local artifact content.
 *
 * @module plugins/integrity
 */

/** Recorded hashes for one plugin artifact set. */
export interface IntegrityHashes {
  readonly manifestHash: string;
  readonly entryHash: string | null;
}

/** Integrity status surfaced in the management UI. */
export type IntegrityStatus = 'verified' | 'changed' | 'unknown';

/** Full integrity record stored on a registry entry. */
export interface IntegrityRecord extends IntegrityHashes {
  readonly status: IntegrityStatus;
}

/** The record used when hashing is unavailable (never blocks loading). */
export const UNKNOWN_INTEGRITY: IntegrityRecord = {
  manifestHash: '',
  entryHash: null,
  status: 'unknown'
};

/** Canonical JSON: object keys sorted recursively; deterministic output. */
export function canonicalJson(value: unknown): string {
  if (value === null || typeof value !== 'object') {
    return JSON.stringify(value) ?? 'null';
  }
  if (Array.isArray(value)) {
    return `[${value.map((item) => canonicalJson(item)).join(',')}]`;
  }
  const record = value as Record<string, unknown>;
  const keys = Object.keys(record).sort();
  const parts = keys.map((key) => `${JSON.stringify(key)}:${canonicalJson(record[key])}`);
  return `{${parts.join(',')}}`;
}

/** SHA-256 hex digest via Web Crypto; null when the API is unavailable. */
export async function sha256Hex(text: string): Promise<string | null> {
  const subtle = globalThis.crypto?.subtle;
  if (!subtle) return null;
  const bytes = new TextEncoder().encode(text);
  const digest = await subtle.digest('SHA-256', bytes);
  return [...new Uint8Array(digest)].map((byte) => byte.toString(16).padStart(2, '0')).join('');
}

/** Computes artifact hashes; returns null when hashing is unavailable. */
export async function computeIntegrityHashes(
  manifestRaw: unknown,
  entryContent: string | null
): Promise<IntegrityHashes | null> {
  const manifestHash = await sha256Hex(canonicalJson(manifestRaw));
  if (manifestHash === null) return null;
  const entryHash = entryContent === null ? null : await sha256Hex(entryContent);
  return { manifestHash, entryHash };
}

/**
 * Compares recorded hashes against freshly computed ones. Missing hashes on
 * either side yield `unknown`; any difference yields `changed`.
 */
export function compareIntegrity(
  recorded: IntegrityHashes | null,
  current: IntegrityHashes | null
): IntegrityStatus {
  if (!recorded || !current || recorded.manifestHash.length === 0 || current.manifestHash.length === 0) {
    return 'unknown';
  }
  if (recorded.manifestHash !== current.manifestHash) return 'changed';
  if (recorded.entryHash !== current.entryHash) return 'changed';
  return 'verified';
}