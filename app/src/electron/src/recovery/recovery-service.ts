/**
 * Recovery screen backend (WP-06-01, §12): local diagnostics collection,
 * purge, and retry orchestration for the case where the backend cannot start.
 * Diagnostics are strictly local artifacts (logs, crash dumps, descriptor
 * state) — no identity dimensions, no telemetry (clean-room guardrail #4).
 */
import { joinPath } from '../handshake/port-discovery';
import {
  BackendSupervisor
} from '../supervisor/backend-supervisor';
import { FileSystemPort, HttpProbePort, SESSION_TOKEN_HEADER, ShellLogger } from '../types';

/** One local diagnostic artifact listed on the recovery screen. */
export interface DiagnosticArtifact {
  readonly name: string;
  readonly path: string;
  readonly kind: 'log' | 'crash-dump' | 'descriptor' | 'other';
  readonly sizeBytes: number | null;
}

/** Aggregated recovery view model. */
export interface RecoveryReport {
  readonly backendState: string;
  readonly lastError: string | null;
  readonly consecutiveFailures: number;
  readonly artifacts: readonly DiagnosticArtifact[];
}

/** Directories inspected for local diagnostics (relative to the data dir). */
export const DIAGNOSTIC_SUBDIRECTORIES = ['logs', 'diagnostics', 'crash-dumps'] as const;

/** Collects/purges local diagnostics and drives retry through the supervisor. */
export class RecoveryService {
  private readonly fs: FileSystemPort;
  private readonly http: HttpProbePort;
  private readonly logger: ShellLogger;
  private readonly supervisor: BackendSupervisor;
  private readonly dataDirectory: string;

  constructor(
    fs: FileSystemPort,
    http: HttpProbePort,
    logger: ShellLogger,
    supervisor: BackendSupervisor,
    dataDirectory: string
  ) {
    this.fs = fs;
    this.http = http;
    this.logger = logger;
    this.supervisor = supervisor;
    this.dataDirectory = dataDirectory;
  }

  /** Builds the recovery view model from supervisor state + local artifacts. */
  async buildReport(): Promise<RecoveryReport> {
    const status = this.supervisor.status;
    const artifacts = await this.listArtifacts();
    return {
      backendState: status.state,
      lastError: status.lastError,
      consecutiveFailures: status.consecutiveFailures,
      artifacts
    };
  }

  /** Lists local diagnostic artifacts (best effort per directory). */
  async listArtifacts(): Promise<readonly DiagnosticArtifact[]> {
    const artifacts: DiagnosticArtifact[] = [];
    for (const subdirectory of DIAGNOSTIC_SUBDIRECTORIES) {
      const directory = joinPath(this.dataDirectory, subdirectory);
      let entries: readonly string[];
      try {
        entries = await this.fs.listDirectory(directory);
      } catch {
        continue;
      }

      for (const name of entries) {
        artifacts.push({
          name,
          path: joinPath(directory, name),
          kind: classifyArtifact(name),
          sizeBytes: null
        });
      }
    }

    return artifacts;
  }

  /**
   * Purges local diagnostics. When the backend is ready, the purge goes
   * through its diagnostics API (registry-tracked); otherwise files are
   * removed directly from the local data directory. Returns the count
   * removed. Never touches the database or content store.
   */
  async purgeDiagnostics(): Promise<number> {
    const status = this.supervisor.status;
    if (status.state === 'ready' && status.baseUrl !== null && status.sessionToken !== null) {
      const response = await this.http.delete(
        `${status.baseUrl}/api/diagnostics/artifacts`,
        { [SESSION_TOKEN_HEADER]: status.sessionToken }
      );
      if (response !== null && response.status === 200) {
        try {
          const parsed: unknown = JSON.parse(response.body);
          if (typeof parsed === 'object' && parsed !== null) {
            const record = parsed as Record<string, unknown>;
            const data = record.data as Record<string, unknown> | undefined;
            const purged = data?.purged ?? record.purged;
            if (typeof purged === 'number') {
              return purged;
            }
          }
        } catch {
          // Fall through to the local count when the envelope is unexpected.
        }
      }
    }

    let removed = 0;
    const artifacts = await this.listArtifacts();
    for (const artifact of artifacts) {
      try {
        await this.fs.deleteFile(artifact.path);
        removed++;
      } catch (error) {
        this.logger.warn(`Could not delete ${artifact.path}.`);
      }
    }

    return removed;
  }

  /** Retry action: resets the failure budget and restarts the backend. */
  async retry(): Promise<boolean> {
    const status = await this.supervisor.restart();
    return status.state === 'ready';
  }
}

/** Classifies an artifact by file name (local diagnostics taxonomy). */
export function classifyArtifact(name: string): DiagnosticArtifact['kind'] {
  const lower = name.toLowerCase();
  if (lower.endsWith('.dmp') || lower.includes('minidump')) {
    return 'crash-dump';
  }
  if (lower.endsWith('.log') || lower.startsWith('log')) {
    return 'log';
  }
  if (lower.endsWith('.json') && (lower.includes('handshake') || lower.includes('port') || lower.includes('endpoint'))) {
    return 'descriptor';
  }
  return 'other';
}
