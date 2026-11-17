/**
 * Manual Compact / Trim action (criterion #41 UI facet; FS-CRAWL-002 §7.2).
 *
 * POST /api/system/compact returns the locked shape {before, after,
 * actionsPerformed} (WP-06-A1 verified); the client keeps dual-accept
 * tolerance for an optional completedAt. The modal presents before/after
 * snapshots with reclaimed deltas and the honest action labels. Guard
 * refusals (rate limit / audit in flight → conflict envelope) render inline;
 * nothing destructive ever happens automatically.
 *
 * @module shell/CompactAction
 */

import { useCallback, useState } from 'react';
import { Badge, Button, Group, Modal, Stack, Table, Text } from '@mantine/core';
import type { CompactAction, SystemHealthSnapshot } from '../contracts/api/system';
import { useShell } from './ShellContext';
import { IconCompact, IconCheck, IconWarning } from '../theme/icons';
import { formatByteDelta, formatBytes, formatTime } from './format';

/** Locked compact wire shape (+ optional completedAt tolerance). */
export interface CompactWireResponse {
  readonly before: SystemHealthSnapshot;
  readonly after: SystemHealthSnapshot;
  readonly actionsPerformed?: readonly CompactAction[];
  readonly completedAt?: string;
}

/** Human labels for the wire action values. */
export const ACTION_LABELS: Record<CompactAction, string> = {
  'managed-gc-compaction': 'Managed GC compaction',
  'wal-checkpoint-truncate': 'WAL checkpoint (truncate)',
  'working-set-trim': 'Working-set trim'
};

/** One before/after/delta row. */
function SnapshotRow({
  label,
  before,
  after
}: {
  label: string;
  before: number | null;
  after: number | null;
}) {
  const delta = before !== null && after !== null ? before - after : null;
  return (
    <Table.Tr>
      <Table.Td>{label}</Table.Td>
      <Table.Td className="cf-compact-delta">{formatBytes(before)}</Table.Td>
      <Table.Td className="cf-compact-delta">{formatBytes(after)}</Table.Td>
      <Table.Td className={`cf-compact-delta${delta !== null && delta > 0 ? ' cf-compact-reclaimed' : ''}`}>
        {delta === null ? '—' : formatByteDelta(delta)}
      </Table.Td>
    </Table.Tr>
  );
}

/** The compact trigger button + result modal. */
export function CompactActionButton() {
  const { services } = useShell();
  const [opened, setOpened] = useState(false);
  const [pending, setPending] = useState(false);
  const [result, setResult] = useState<CompactWireResponse | null>(null);
  const [error, setError] = useState<string | null>(null);

  const runCompact = useCallback(async () => {
    if (!services.api) {
      setError('Backend disconnected — compact requires the local API.');
      setOpened(true);
      return;
    }
    setPending(true);
    setError(null);
    setResult(null);
    setOpened(true);
    try {
      const envelope = await services.api.request('system.compact');
      if (envelope.error || envelope.data === null) {
        setError(envelope.error?.message ?? 'Compact refused.');
      } else {
        setResult(envelope.data as CompactWireResponse);
      }
    } catch {
      setError('Transport failure — the backend may be restarting.');
    } finally {
      setPending(false);
    }
  }, [services.api]);

  return (
    <>
      <Button
        size="compact-xs"
        variant="subtle"
        color="ember"
        leftSection={<IconCompact size={13} />}
        onClick={() => void runCompact()}
        loading={pending}
        data-testid="compact-button"
        aria-label="Compact and trim working set"
      >
        Compact
      </Button>
      <Modal
        opened={opened}
        onClose={() => setOpened(false)}
        title={
          <Group gap={8}>
            <IconCompact size={16} />
            <Text fw={600}>Compact / Trim Working Set</Text>
          </Group>
        }
        size="lg"
        data-testid="compact-modal"
      >
        {pending ? (
          <Text c="dimmed" size="sm" data-testid="compact-pending">
            Compacting managed heap, checkpointing WAL, trimming working set…
          </Text>
        ) : error ? (
          <Group gap={8} data-testid="compact-error">
            <IconWarning size={16} color="var(--cf-warning)" />
            <Text size="sm" c="var(--cf-warning)">{error}</Text>
          </Group>
        ) : result ? (
          <Stack gap="sm">
            <Table withTableBorder={false} verticalSpacing={6} data-testid="compact-table">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>Metric</Table.Th>
                  <Table.Th>Before</Table.Th>
                  <Table.Th>After</Table.Th>
                  <Table.Th>Reclaimed</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                <SnapshotRow
                  label="Working set"
                  before={result.before.backendWorkingSetBytes}
                  after={result.after.backendWorkingSetBytes}
                />
                <SnapshotRow
                  label="Managed heap"
                  before={result.before.managedHeapBytes}
                  after={result.after.managedHeapBytes}
                />
                <SnapshotRow
                  label="SQLite WAL"
                  before={result.before.sqliteWalBytes}
                  after={result.after.sqliteWalBytes}
                />
              </Table.Tbody>
            </Table>
            <Group gap={6} data-testid="compact-actions">
              {(result.actionsPerformed ?? []).map((action) => (
                <Badge key={action} variant="light" color="signal" leftSection={<IconCheck size={11} />}>
                  {ACTION_LABELS[action] ?? action}
                </Badge>
              ))}
              {(result.actionsPerformed ?? []).length === 0 ? (
                <Text size="xs" c="dimmed">No actions performed (degraded path).</Text>
              ) : null}
            </Group>
            <Text size="xs" c="dimmed">
              Completed {formatTime(result.completedAt ?? result.after.timestamp)} · logged locally ·
              rate-limited to once per 10 s · never interrupts an in-flight audit.
            </Text>
          </Stack>
        ) : null}
      </Modal>
    </>
  );
}
