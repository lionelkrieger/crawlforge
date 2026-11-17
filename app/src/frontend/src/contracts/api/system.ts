/**
 * System / settings / diagnostics DTOs.
 *
 * Routes: GET /api/health, GET /api/version, GET /metrics, GET/PUT /api/settings,
 * POST /api/settings/database/test, GET/DELETE /api/diagnostics/artifacts,
 * GET /api/system/health, POST /api/system/compact (additive, FS-CRAWL-002 §7.2).
 *
 * Clean-room: settings carry only local configuration. The sole credential
 * concepts are the local automation-interface token and user-owned outbound
 * secrets (database/proxy/destination), which are never returned in clear
 * (FS-CRAWL-001 §10.1, §10.2). No accounts, licenses, or entitlements.
 *
 * @module contracts/api/system
 */

import type { IsoTimestamp } from '../common/primitives';
import type { DeepPartial } from '../common/primitives';
import type { ListQuery } from '../common/pagination';
import type { EmbeddingProvider } from './semantics';

/** GET /api/health — readiness flag and process uptime. */
export interface HealthResponse {
  /** True once the backend is ready to serve the shell. */
  readonly ready: boolean;
  /** Backend process uptime in seconds. */
  readonly uptimeSeconds: number;
}

/** GET /api/version — product version, schema version, active runtime profile. */
export interface VersionResponse {
  readonly productVersion: string;
  /** Versioned migration schema level of the local database. */
  readonly schemaVersion: number;
  /** Active runtime profile identifier (local configuration only). */
  readonly runtimeProfile: string;
}

/**
 * GET /metrics — standardized Prometheus-class text exposition of local
 * technical metric families. NOT JSON-enveloped (route catalog marks it
 * `enveloped: false`); the payload is the raw exposition text.
 */
export type MetricsResponse = string;

/** Local database provider selection (embedded SQLite default, optional user-owned MySQL). */
export type DatabaseProvider = 'sqlite' | 'mysql';

/**
 * Database connection settings. MySQL credentials are user-owned secrets
 * used solely for the external-database connection; `password` is masked
 * (never returned in clear) on reads.
 */
export interface DatabaseSettings {
  readonly provider: DatabaseProvider;
  readonly host?: string | null;
  readonly port?: number | null;
  readonly database?: string | null;
  readonly username?: string | null;
  /** Masked on read; accepted in clear only on update/test. */
  readonly password?: string | null;
}

/**
 * Outbound proxy configuration (user-owned). Credentials are used solely
 * for the configured proxy connection and masked on read.
 */
export interface ProxySettings {
  readonly enabled: boolean;
  readonly host?: string | null;
  readonly port?: number | null;
  readonly username?: string | null;
  /** Masked on read. */
  readonly password?: string | null;
}

/** MCP automation-interface transport (local loopback server only). */
export type McpTransport = 'sse' | 'stdio';

/**
 * Local MCP automation-interface settings. The token is a locally generated
 * automation credential (FS-CRAWL-001 §11) — displayed and rotatable in the
 * settings surface; it is not an identity/account concept.
 */
export interface McpSettings {
  readonly enabled: boolean;
  readonly transport: McpTransport;
  readonly token: string | null;
}

/**
 * Embedding provider settings (E7). `endpoint` is only meaningful for the
 * loopback daemon and must be loopback-only; non-loopback values are
 * rejected at configuration time by the backend.
 */
export interface EmbeddingSettings {
  readonly provider: EmbeddingProvider;
  readonly endpoint?: string | null;
  readonly model?: string | null;
}

/**
 * Locally configurable status-bar advisory thresholds (FS-CRAWL-002 §7.2).
 * Defaults: working-set advisory 2 GiB, WAL advisory 256 MiB. Advisories
 * never trigger automatic destructive actions.
 */
export interface StatusBarSettings {
  readonly workingSetAdvisoryBytes: number;
  readonly walAdvisoryBytes: number;
}

/** UI theme (dark-first; light optional). */
export type UiTheme = 'dark' | 'light';

/**
 * Full application settings object (GET /api/settings). Defaults are
 * guaranteed on first read; secrets are never returned in clear.
 */
export interface AppSettings {
  readonly theme: UiTheme;
  /** UI language tag (localization framework). */
  readonly uiLanguage: string;
  readonly database: DatabaseSettings;
  readonly proxy: ProxySettings;
  readonly mcp: McpSettings;
  readonly embeddings: EmbeddingSettings;
  readonly statusBar: StatusBarSettings;
}

/** PUT /api/settings — any subset of settings fields; returns the updated object. */
export type SettingsUpdateRequest = DeepPartial<AppSettings>;

/** POST /api/settings/database/test — provider selection and connection fields. */
export interface DatabaseTestRequest {
  readonly provider: DatabaseProvider;
  readonly host?: string;
  readonly port?: number;
  readonly database?: string;
  readonly username?: string;
  readonly password?: string;
}

/** Reachability, authentication, and migration-compatibility verdict. */
export interface DatabaseTestResponse {
  readonly reachable: boolean;
  readonly authenticated: boolean;
  readonly migrationCompatible: boolean;
  readonly message?: string | null;
}

/** Diagnostic artifact kinds: local structured logs and local native minidumps. */
export type DiagnosticArtifactKind = 'log' | 'minidump';

/**
 * Local diagnostic artifact metadata (no remote diagnostics, exclusion #6).
 * Artifacts are user-viewable and purgeable.
 */
export interface DiagnosticArtifact {
  readonly id: string;
  readonly kind: DiagnosticArtifactKind;
  readonly fileName: string;
  readonly createdAt: IsoTimestamp;
  readonly sizeBytes: number;
}

/** GET /api/diagnostics/artifacts — kind filter on list. */
export interface DiagnosticArtifactListQuery extends ListQuery {
  readonly kind?: DiagnosticArtifactKind;
}

/** DELETE /api/diagnostics/artifacts — purge confirmation. */
export interface DiagnosticPurgeRequest {
  /** When omitted, all artifact kinds are purged. */
  readonly kind?: DiagnosticArtifactKind;
}

/** Purge confirmation with the number of artifacts removed. */
export interface DiagnosticPurgeResponse {
  readonly purgedCount: number;
}

/**
 * Memory/WAL snapshot shared by the `system-health` hub heartbeat and
 * GET /api/system/health (FS-CRAWL-002 §7.2 — identical payload).
 * Field names mirror the spec payload table verbatim (camelCase).
 */
export interface SystemHealthSnapshot {
  /** UTC emission time. */
  readonly timestamp: IsoTimestamp;
  /** Backend process working-set RAM. */
  readonly backendWorkingSetBytes: number;
  /** Managed heap size under the server-GC profile. */
  readonly managedHeapBytes: number;
  /** GC generation collection counts, aggregated across server-GC heaps. */
  readonly gcGen0Count: number;
  readonly gcGen1Count: number;
  readonly gcGen2Count: number;
  /** Write-ahead log file size; null when the external provider is active. */
  readonly sqliteWalBytes: number | null;
  /** Present while an audit is running. */
  readonly auditId?: string | null;
}

/** Actions performed by POST /api/system/compact (FS-CRAWL-002 §7.2). */
export type CompactAction =
  | 'managed-gc-compaction'
  | 'wal-checkpoint-truncate'
  | 'working-set-trim';

/**
 * POST /api/system/compact — before/after snapshots and actions performed.
 * Rate-limited (>= 10 s between invocations); never interrupts an in-flight
 * audit or transaction; logged locally.
 */
export interface SystemCompactResponse {
  readonly before: SystemHealthSnapshot;
  readonly after: SystemHealthSnapshot;
  readonly actionsPerformed: readonly CompactAction[];
}
