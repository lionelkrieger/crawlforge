/**
 * Audit workspace views: the audit list and the active-audit report frame.
 * The report frame renders LIVE hub telemetry (counters, throughput, ETA,
 * host-throttle AIMD state, WAF aggregates, matrix/embedding/log-import
 * progress) and real lifecycle controls (pause/resume/cancel). Detailed
 * grids/charts arrive with the sibling report workpieces and mount into
 * this frame's secondary pane.
 *
 * @module shell/AuditViews
 */

import { useCallback, useState } from 'react';
import {
  Badge,
  Button,
  Group,
  Paper,
  Progress,
  SimpleGrid,
  Stack,
  Table,
  Text,
  Title
} from '@mantine/core';
import { useShell, useStoreSelector } from './ShellContext';
import { useLiveProgress } from '../store/liveProgress';
import { selectActiveAudit, selectAudits, selectHostStates } from '../store/selectors';
import { STATE_COLORS } from './HubDashboard';
import { formatMs } from './format';
import {
  IconSpider,
  IconPlay,
  IconPause,
  IconStop,
  IconGauge,
  IconShield,
  IconLink,
  IconClock,
  IconDatabase
} from '../theme/icons';
import { formatBytes, formatCount, formatDuration, formatRate, formatDateTime } from './format';

/** Lifecycle control for the active audit (real API calls). */
function LifecycleControls({ auditId, state }: { auditId: string; state: string }) {
  const { services } = useShell();
  const [pending, setPending] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const act = useCallback(
    async (route: 'audits.pause' | 'audits.resume' | 'audits.cancel', label: string) => {
      if (!services.api) return;
      setPending(label);
      setError(null);
      try {
        const envelope = await services.api.request(route, { params: { id: auditId } });
        if (envelope.error) {
          setError(envelope.error.message);
        } else {
          await services.actions.loadAudits();
        }
      } catch {
        setError('Transport failure.');
      } finally {
        setPending(null);
      }
    },
    [services, auditId]
  );

  return (
    <Group gap={6}>
      {state === 'running' ? (
        <Button size="compact-xs" variant="light" color="yellow" leftSection={<IconPause size={12} />} loading={pending === 'pause'} onClick={() => void act('audits.pause', 'pause')} data-testid="audit-pause">
          Pause
        </Button>
      ) : null}
      {state === 'paused' ? (
        <Button size="compact-xs" variant="light" color="signal" leftSection={<IconPlay size={12} />} loading={pending === 'resume'} onClick={() => void act('audits.resume', 'resume')} data-testid="audit-resume">
          Resume
        </Button>
      ) : null}
      {state === 'running' || state === 'paused' || state === 'queued' ? (
        <Button size="compact-xs" variant="light" color="red" leftSection={<IconStop size={12} />} loading={pending === 'cancel'} onClick={() => void act('audits.cancel', 'cancel')} data-testid="audit-cancel">
          Cancel
        </Button>
      ) : null}
      {error ? <Text size="xs" c="var(--cf-danger)">{error}</Text> : null}
    </Group>
  );
}

/** The audits list view. */
export function AuditsView() {
  const { navigation } = useShell();
  const audits = useStoreSelector(selectAudits);

  return (
    <Stack gap="md" data-testid="audits-view">
      <div>
        <Title order={3}>Audits</Title>
        <Text size="sm" c="dimmed">Crawl runs · live counters stream over the hub (≤1/s).</Text>
      </div>
      {audits.length === 0 ? (
        <Paper withBorder p="xl">
          <Text size="sm" c="dimmed" ta="center">
            No audits yet. Start one from a project configuration; progress, findings, and host
            throttle state stream here in real time.
          </Text>
        </Paper>
      ) : (
        <Paper withBorder p="md">
          <Table.ScrollContainer minWidth={720}>
            <Table verticalSpacing={8}>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>ID</Table.Th>
                  <Table.Th>State</Table.Th>
                  <Table.Th>Phase</Table.Th>
                  <Table.Th>Fetched</Table.Th>
                  <Table.Th>Errors</Table.Th>
                  <Table.Th>Started</Table.Th>
                  <Table.Th />
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {audits.map((audit) => (
                  <Table.Tr key={audit.id}>
                    <Table.Td><Text ff="monospace" size="sm" fw={600}>{audit.id}</Text></Table.Td>
                    <Table.Td><Badge size="sm" variant="light" color={STATE_COLORS[audit.state] ?? 'steel'}>{audit.state}</Badge></Table.Td>
                    <Table.Td><Text size="sm" c="dimmed">{audit.phase}</Text></Table.Td>
                    <Table.Td className="cf-compact-delta">{formatCount(audit.counters.fetched)}</Table.Td>
                    <Table.Td className="cf-compact-delta">{formatCount(audit.counters.error)}</Table.Td>
                    <Table.Td><Text size="xs" c="dimmed">{formatDateTime(audit.startedAt)}</Text></Table.Td>
                    <Table.Td>
                      <Button size="compact-xs" variant="subtle" color="signal" onClick={() => navigation.openAudit(audit.id)}>
                        Open report
                      </Button>
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        </Paper>
      )}
    </Stack>
  );
}

/** The active-audit report frame with live telemetry. */
export function ReportView() {
  const { services, navigation } = useShell();
  const audit = useStoreSelector(selectActiveAudit);
  const hosts = useStoreSelector(selectHostStates);
  const crawl = useLiveProgress(services.live, (state) =>
    audit ? state.crawlByAudit[audit.id] : undefined
  );
  const wafCounts = useLiveProgress(services.live, (state) =>
    audit ? state.wafCountsByAudit[audit.id] : undefined
  );
  const matrix = useLiveProgress(services.live, (state) =>
    audit ? state.matrixByAudit[audit.id] : undefined
  );
  const embedding = useLiveProgress(services.live, (state) =>
    audit ? state.embeddingByAudit[audit.id] : undefined
  );

  if (!audit) {
    return (
      <Paper withBorder p="xl" data-testid="report-empty">
        <Stack align="center" gap="sm">
          <IconSpider size={32} color="var(--mantine-color-night-3)" />
          <Text size="sm" c="dimmed" ta="center">
            No active audit. Open one from the hub or the audits list — its live report mounts here.
          </Text>
          <Button variant="light" color="ember" size="xs" onClick={() => navigation.setView('audits')}>
            Browse audits
          </Button>
        </Stack>
      </Paper>
    );
  }

  const fetched = crawl?.counters.fetched ?? audit.counters.fetched;
  const queued = crawl?.counters.queued ?? audit.counters.queued;
  const totalKnown = fetched + queued;
  const progress = totalKnown > 0 ? fetched / totalKnown : 0;

  return (
    <Stack gap="md" data-testid="report-view">
      <Group justify="space-between" wrap="wrap">
        <div>
          <Group gap={8}>
            <Title order={3}>Audit {audit.id}</Title>
            <Badge variant="light" color={STATE_COLORS[audit.state] ?? 'steel'} data-testid="report-state">
              {audit.state}
            </Badge>
          </Group>
          <Text size="sm" c="dimmed">
            Phase {crawl?.phase ?? audit.phase} · started {formatDateTime(audit.startedAt)}
          </Text>
        </div>
        <LifecycleControls auditId={audit.id} state={audit.state} />
      </Group>

      <Paper withBorder p="md">
        <Group justify="space-between" mb={6}>
          <Text size="xs" fw={600} tt="uppercase" lts={0.6} c="dimmed">Crawl progress</Text>
          <Text size="xs" c="dimmed" className="cf-compact-delta">
            {formatCount(fetched)} fetched · {formatCount(queued)} queued
            {crawl ? ` · ${formatRate(crawl.throughputPerSecond)} · ETA ${formatDuration(crawl.estimatedRemainderSeconds)}` : ''}
          </Text>
        </Group>
        <Progress value={progress * 100} color="signal" size="lg" radius="xl" striped={audit.state === 'running'} animated={audit.state === 'running'} data-testid="report-progress" />
        <SimpleGrid cols={{ base: 2, md: 4 }} spacing="sm" mt="sm">
          <MiniStat label="Errors" value={formatCount(crawl?.counters.error ?? audit.counters.error)} />
          <MiniStat label="Blocked" value={formatCount(crawl?.counters.blocked ?? audit.counters.blocked)} />
          <MiniStat label="WAF blocked" value={formatCount(crawl?.counters.wafBlocked ?? audit.counters.wafBlocked ?? 0)} danger />
          <MiniStat label="Elapsed" value={formatDuration(crawl?.elapsedSeconds)} />
        </SimpleGrid>
      </Paper>

      {matrix ? (
        <Paper withBorder p="md" data-testid="matrix-progress">
          <Group gap={8} mb={6}>
            <IconLink size={15} color="var(--mantine-color-violet-4)" />
            <Text size="xs" fw={600} tt="uppercase" lts={0.6} c="dimmed">Redirect matrix</Text>
          </Group>
          <Text size="sm" className="cf-compact-delta">
            {formatCount(matrix.validated)} rows validated{matrix.total !== null ? ` of ${formatCount(matrix.total)}` : ''}
          </Text>
          {matrix.total ? <Progress value={(matrix.validated / matrix.total) * 100} color="violet" mt={6} radius="xl" /> : null}
        </Paper>
      ) : null}

      {embedding ? (
        <Paper withBorder p="md" data-testid="embedding-progress">
          <Group gap={8} mb={6}>
            <IconDatabase size={15} color="var(--mantine-color-pink-4)" />
            <Text size="xs" fw={600} tt="uppercase" lts={0.6} c="dimmed">
              Embeddings · {embedding.provider} {embedding.isFinal ? '· final' : `· ${embedding.phase}`}
            </Text>
          </Group>
          <Text size="sm" className="cf-compact-delta">
            {formatCount(embedding.embeddedCount)} / {formatCount(embedding.totalEligible)} embedded
            {embedding.skipped !== undefined ? ` · ${formatCount(embedding.skipped)} skipped` : ''}
          </Text>
          <Progress
            value={embedding.totalEligible > 0 ? (embedding.embeddedCount / embedding.totalEligible) * 100 : 0}
            color="pink"
            mt={6}
            radius="xl"
          />
        </Paper>
      ) : null}

      <Paper withBorder p="md">
        <Group gap={8} mb="sm">
          <IconGauge size={15} color="var(--mantine-color-yellow-4)" />
          <Text size="xs" fw={600} tt="uppercase" lts={0.6} c="dimmed">Host throttle (AIMD)</Text>
        </Group>
        {hosts.length === 0 ? (
          <Text size="sm" c="dimmed">No host state yet — adjustments stream as the crawler adapts per host.</Text>
        ) : (
          <Table.ScrollContainer minWidth={560}>
            <Table verticalSpacing={6} data-testid="hosts-table">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>Host</Table.Th>
                  <Table.Th>Rate</Table.Th>
                  <Table.Th>Concurrency</Table.Th>
                  <Table.Th>TTFB EWMA</Table.Th>
                  <Table.Th>State</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {hosts.map((host) => (
                  <Table.Tr key={host.host}>
                    <Table.Td><Text size="sm" ff="monospace">{host.host}</Text></Table.Td>
                    <Table.Td className="cf-compact-delta">{typeof host.effectiveRate === 'number' && Number.isFinite(host.effectiveRate) ? formatRate(host.effectiveRate) : '—'}</Table.Td>
                    <Table.Td className="cf-compact-delta">{typeof host.concurrency === 'number' && Number.isFinite(host.concurrency) ? host.concurrency : '—'}</Table.Td>
                    <Table.Td className="cf-compact-delta">{formatMs(host.ttfbEwmaMs)}</Table.Td>
                    <Table.Td>
                      <Badge size="xs" variant="light" color={host.throttled ? 'yellow' : 'green'}>
                        {host.throttled ? 'throttled' : 'nominal'}
                      </Badge>
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        )}
      </Paper>

      {wafCounts && Object.keys(wafCounts).length > 0 ? (
        <Paper withBorder p="md" data-testid="waf-summary">
          <Group gap={8} mb="sm">
            <IconShield size={15} color="var(--cf-danger)" />
            <Text size="xs" fw={600} tt="uppercase" lts={0.6} c="dimmed">WAF blocks by provider (live aggregate)</Text>
          </Group>
          <Group gap={6}>
            {Object.entries(wafCounts).map(([provider, count]) => (
              <Badge key={provider} color="red" variant="light" size="lg">
                {provider}: {formatCount(count)}
              </Badge>
            ))}
          </Group>
        </Paper>
      ) : null}

      <Paper withBorder p="md">
        <Group gap={8} mb={4}>
          <IconClock size={15} color="var(--mantine-color-night-2)" />
          <Text size="xs" fw={600} tt="uppercase" lts={0.6} c="dimmed">Report surfaces</Text>
        </Group>
        <Text size="sm" c="dimmed">
          URL / finding / link grids, charts, and the link graph mount into this workspace from the
          report workpieces; all render locally from aggregate endpoints. Live telemetry above is
          already streaming for this audit.
        </Text>
      </Paper>
    </Stack>
  );
}

/** Small telemetry stat. */
function MiniStat({ label, value, danger }: { label: string; value: string; danger?: boolean }) {
  return (
    <div>
      <div className="cf-stat-label">{label}</div>
      <div className="cf-stat-value" style={{ fontSize: 18, color: danger ? 'var(--cf-danger)' : undefined }}>
        {value}
      </div>
    </div>
  );
}

/** Re-exported for tests. */
export { formatBytes };
