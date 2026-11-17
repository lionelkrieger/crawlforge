/**
 * Persistent bottom status bar (FS-CRAWL-002 §7.2, criterion #41 UI facet).
 *
 * Backend working-set / managed heap / GC generation counts / SQLite WAL
 * stream from the 2-second system-health heartbeat; the renderer JS heap is
 * measured client-side and never sent to the backend. Advisories use the
 * locally configurable thresholds (defaults 2 GiB / 256 MiB) and never
 * trigger automatic destructive actions. Throughput shows while an audit is
 * running. The manual Compact/Trim action lives here.
 *
 * @module shell/StatusBar
 */

import { useMemo } from 'react';
import { Tooltip } from '@mantine/core';
import { useShell, useStoreSelector } from './ShellContext';
import { useLiveProgress } from '../store/liveProgress';
import {
  selectActiveAudit,
  selectFrontendJsHeapBytes,
  selectSettings,
  selectSystemHealth
} from '../store/selectors';
import {
  DEFAULT_WAL_ADVISORY_BYTES,
  DEFAULT_WORKING_SET_ADVISORY_BYTES
} from '../theme/theme';
import { IconDatabase, IconGauge, IconMemory, IconActivity } from '../theme/icons';
import { formatBytes, formatCount, formatRate, formatTime } from './format';
import { CompactActionButton } from './CompactAction';

/** Heartbeats older than this are rendered stale (3 missed 2 s beats). */
const STALE_AFTER_MS = 6000;

/** Advisory level for a measured value against its threshold. */
export function advisoryLevel(
  value: number | null | undefined,
  advisory: number
): 'ok' | 'warning' | 'critical' {
  if (value === null || value === undefined || !Number.isFinite(value)) return 'ok';
  if (value >= advisory * 1.5) return 'critical';
  if (value >= advisory) return 'warning';
  return 'ok';
}

/** One telemetry cell. */
function StatusItem({
  icon,
  label,
  value,
  advisory = 'ok',
  stale = false,
  tooltip
}: {
  icon: React.ReactNode;
  label: string;
  value: string;
  advisory?: 'ok' | 'warning' | 'critical';
  stale?: boolean;
  tooltip: string;
}) {
  return (
    <Tooltip label={tooltip} openDelay={300}>
      <span
        className={`cf-status-item${stale ? ' cf-status-stale' : ''}`}
        data-advisory={advisory}
        data-testid={`status-${label.toLowerCase().replace(/\s+/g, '-')}`}
      >
        {icon}
        <span className="cf-status-label">{label}</span>
        <span>{value}</span>
      </span>
    </Tooltip>
  );
}

/** The persistent status bar. */
export function StatusBar() {
  const { services } = useShell();
  const health = useStoreSelector(selectSystemHealth);
  const frontendHeap = useStoreSelector(selectFrontendJsHeapBytes);
  const settings = useStoreSelector(selectSettings);
  const activeAudit = useStoreSelector(selectActiveAudit);
  const liveCrawl = useLiveProgress(services.live, (state) =>
    activeAudit ? state.crawlByAudit[activeAudit.id] : undefined
  );

  const workingSetAdvisory =
    settings?.statusBar?.workingSetAdvisoryBytes ?? DEFAULT_WORKING_SET_ADVISORY_BYTES;
  const walAdvisory = settings?.statusBar?.walAdvisoryBytes ?? DEFAULT_WAL_ADVISORY_BYTES;

  const stale = useMemo(() => {
    if (!health?.timestamp) return false;
    const age = Date.now() - new Date(health.timestamp).getTime();
    return Number.isFinite(age) && age > STALE_AFTER_MS;
  }, [health?.timestamp]);

  const running = activeAudit?.state === 'running';

  return (
    <div className="cf-statusbar" role="status" aria-label="System telemetry" data-testid="cf-statusbar">
      <StatusItem
        icon={<IconMemory size={14} />}
        label="WS"
        value={formatBytes(health?.backendWorkingSetBytes)}
        advisory={advisoryLevel(health?.backendWorkingSetBytes, workingSetAdvisory)}
        stale={stale}
        tooltip={`Backend working-set RAM · advisory ${formatBytes(workingSetAdvisory)}`}
      />
      <StatusItem
        icon={<IconMemory size={14} />}
        label="Heap"
        value={formatBytes(health?.managedHeapBytes)}
        stale={stale}
        tooltip="Managed heap (server GC)"
      />
      <StatusItem
        icon={<IconGauge size={14} />}
        label="GC"
        value={
          health
            ? `${formatCount(health.gcGen0Count)}/${formatCount(health.gcGen1Count)}/${formatCount(health.gcGen2Count)}`
            : '—'
        }
        stale={stale}
        tooltip="GC collections gen0/gen1/gen2 (aggregated across server-GC heaps)"
      />
      <StatusItem
        icon={<IconDatabase size={14} />}
        label="WAL"
        value={health ? (health.sqliteWalBytes === null ? 'external' : formatBytes(health.sqliteWalBytes)) : '—'}
        advisory={advisoryLevel(health?.sqliteWalBytes, walAdvisory)}
        stale={stale}
        tooltip={`SQLite write-ahead log · advisory ${formatBytes(walAdvisory)} · null = external provider`}
      />
      <StatusItem
        icon={<IconActivity size={14} />}
        label="JS"
        value={formatBytes(frontendHeap)}
        tooltip="Renderer JS heap (measured locally, never sent to the backend)"
      />
      {running && liveCrawl ? (
        <StatusItem
          icon={<IconGauge size={14} />}
          label="Rate"
          value={formatRate(liveCrawl.throughputPerSecond)}
          tooltip={`Audit ${activeAudit?.id} throughput · phase ${liveCrawl.phase}`}
        />
      ) : null}
      <span className="cf-status-spacer" />
      <span className="cf-status-item cf-status-stale" data-testid="status-heartbeat">
        <span className="cf-status-label">beat</span>
        <span>{health ? formatTime(health.timestamp) : '—'}</span>
      </span>
      <CompactActionButton />
    </div>
  );
}
