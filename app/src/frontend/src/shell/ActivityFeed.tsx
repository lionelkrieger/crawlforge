/**
 * Bounded recent-activity feed (FS-CRAWL-002 §7.4). Renders the ring buffer
 * newest-first with a drop counter; raw payloads are never shown or kept.
 *
 * @module shell/ActivityFeed
 */

import { Badge, Group, Stack, Text } from '@mantine/core';
import { useShell } from './ShellContext';
import type { HubActivityEntry } from '../contracts/store/state';
import { formatTime } from './format';
import { IconActivity } from '../theme/icons';

/** Event → accent color mapping (semantic, separable). */
const EVENT_COLORS: Record<string, string> = {
  'crawl-progress': 'signal',
  'url-batch': 'steel',
  'stage-changed': 'ember',
  'findings-updated': 'orange',
  'audit-completed': 'green',
  'audit-failed': 'red',
  'backend-lifecycle': 'grape',
  'schedule-fired': 'cyan',
  'schedule-run-completed': 'teal',
  'export-delivery': 'lime',
  'waf-blocks': 'red',
  'host-throttle': 'yellow',
  'matrix-row-validated': 'violet',
  'log-import-progress': 'blue',
  'log-import-completed': 'indigo',
  'embedding-progress': 'pink',
  'embedding-completed': 'grape'
};

/** The activity feed pane body. */
export function ActivityFeed() {
  const { services } = useShell();
  const activity = services.hook((state) => state.hub.recentActivity);
  const entries = [...activity.entries].reverse();

  return (
    <Stack gap={2} data-testid="activity-feed">
      <Group justify="space-between" px={4}>
        <Group gap={6}>
          <IconActivity size={14} />
          <Text size="xs" fw={600} tt="uppercase" lts={0.6} c="dimmed">
            Live activity
          </Text>
        </Group>
        <Text size="xs" c="dimmed" data-testid="activity-count">
          {activity.entries.length}/{activity.capacity}
          {activity.droppedCount > 0 ? ` · ${activity.droppedCount} dropped` : ''}
        </Text>
      </Group>
      {entries.length === 0 ? (
        <Text size="sm" c="dimmed" p="sm" data-testid="activity-empty">
          No hub activity yet. Start an audit or connect the backend to see live events here.
        </Text>
      ) : (
        entries.slice(0, 200).map((entry: HubActivityEntry, index) => (
          <div className="cf-activity-row" key={`${entry.receivedAt}-${index}`}>
            <Badge size="xs" variant="dot" color={EVENT_COLORS[entry.event] ?? 'steel'}>
              <span className="cf-activity-event">{entry.event}</span>
            </Badge>
            {entry.auditId ? (
              <Text size="xs" c="dimmed">audit {entry.auditId}</Text>
            ) : null}
            <span className="cf-activity-time">{formatTime(entry.receivedAt)}</span>
          </div>
        ))
      )}
    </Stack>
  );
}
