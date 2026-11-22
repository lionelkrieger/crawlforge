/**
 * Network waterfall viewer (WP-10-03, UI-13 rendered tab / UI-19 render
 * tester; FS-CRAWL-001 §5.6.2, CAP-C-086).
 *
 * Presentational table over the LOCKED `RenderedNetworkEntry` wire shape
 * (`contracts/api/tools`). `transferredBytes` is the CR-031 real-transferred
 * measurement. A null/empty waterfall renders an honest "no waterfall" state
 * (baseline render captured no network traffic) — never a fabricated row.
 *
 * @module surfaces/explorers/rendered/NetworkWaterfallViewer
 */

import { Badge, Group, Paper, Table, Text } from '@mantine/core';
import type { RenderedNetworkEntry } from '../../../contracts/api/tools';
import { totalTransferredBytes } from './renderedArtifacts';

/** Props for the waterfall viewer. */
export interface NetworkWaterfallViewerProps {
  readonly entries: readonly RenderedNetworkEntry[] | null | undefined;
  /** Test id root (default `rendered-waterfall`). */
  readonly testId?: string;
}

/** Formats a byte count for display (B / KB / MB). */
export function formatBytes(bytes: number | null | undefined): string {
  if (bytes === null || bytes === undefined || !Number.isFinite(bytes)) return '—';
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(2)} MB`;
}

/** Status → badge color (0 = no HTTP response, e.g. cache/data URI). */
function statusColor(status: number): string {
  if (status === 0) return 'steel';
  if (status >= 500) return 'red';
  if (status >= 400) return 'yellow';
  if (status >= 300) return 'signal';
  return 'green';
}

/** The network waterfall table. */
export function NetworkWaterfallViewer({ entries, testId = 'rendered-waterfall' }: NetworkWaterfallViewerProps) {
  const rows = entries ?? [];
  if (rows.length === 0) {
    return (
      <Text size="sm" c="dimmed" data-testid={`${testId}-empty`}>
        No network waterfall captured for this render (baseline capture records no rendered traffic).
      </Text>
    );
  }
  const total = totalTransferredBytes(rows);
  return (
    <Paper withBorder p="sm" data-testid={testId}>
      <Group justify="space-between" mb={6}>
        <Text size="xs" fw={700} tt="uppercase" lts={0.6} c="dimmed">
          Network waterfall · {rows.length} request{rows.length === 1 ? '' : 's'}
        </Text>
        <Text size="xs" ff="monospace" c="dimmed" data-testid={`${testId}-total`}>
          {formatBytes(total)} transferred
        </Text>
      </Group>
      <Table striped highlightOnHover withTableBorder={false} fz="xs">
        <Table.Thead>
          <Table.Tr>
            <Table.Th>#</Table.Th>
            <Table.Th>URL</Table.Th>
            <Table.Th>Type</Table.Th>
            <Table.Th>Method</Table.Th>
            <Table.Th>Status</Table.Th>
            <Table.Th>MIME</Table.Th>
            <Table.Th>Transferred</Table.Th>
            <Table.Th>Source</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {rows.map((entry, index) => (
            <Table.Tr key={`${entry.url}-${index}`} data-testid={`${testId}-row-${index}`} data-status={entry.statusCode}>
              <Table.Td ff="monospace" c="dimmed">{index + 1}</Table.Td>
              <Table.Td style={{ maxWidth: 360 }}>
                <Text size="xs" ff="monospace" truncate title={entry.url} data-testid={`${testId}-url-${index}`}>
                  {entry.url}
                </Text>
              </Table.Td>
              <Table.Td>{entry.resourceType || '—'}</Table.Td>
              <Table.Td ff="monospace">{entry.method || '—'}</Table.Td>
              <Table.Td>
                {entry.statusCode === 0 ? (
                  <Text span c="dimmed" data-testid={`${testId}-status-${index}`}>—</Text>
                ) : (
                  <Badge size="xs" variant="light" color={statusColor(entry.statusCode)} data-testid={`${testId}-status-${index}`}>
                    {entry.statusCode}
                  </Badge>
                )}
              </Table.Td>
              <Table.Td c="dimmed">{entry.mimeType ?? '—'}</Table.Td>
              <Table.Td ff="monospace" data-testid={`${testId}-bytes-${index}`}>{formatBytes(entry.transferredBytes)}</Table.Td>
              <Table.Td>
                <Group gap={4} wrap="nowrap">
                  {entry.fromCache ? <Badge size="xs" variant="outline" color="steel">cache</Badge> : null}
                  {entry.fromServiceWorker ? <Badge size="xs" variant="outline" color="grape">sw</Badge> : null}
                  {!entry.fromCache && !entry.fromServiceWorker ? <Text span c="dimmed" size="xs">network</Text> : null}
                </Group>
              </Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </Paper>
  );
}
