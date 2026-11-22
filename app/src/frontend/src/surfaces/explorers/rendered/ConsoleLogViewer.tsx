/**
 * Console log viewer (WP-10-03, UI-13 rendered tab / UI-18 / UI-19;
 * FS-CRAWL-001 §8.7, CAP-A-X10 "UI-13 network waterfall and console").
 *
 * Presentational list over the LOCKED `ConsoleMessage` wire shape
 * (`contracts/api/tools`). An empty/null log renders an honest empty state.
 *
 * @module surfaces/explorers/rendered/ConsoleLogViewer
 */

import { Badge, Group, Paper, Stack, Text } from '@mantine/core';
import type { ConsoleMessage } from '../../../contracts/api/tools';

/** Props for the console log viewer. */
export interface ConsoleLogViewerProps {
  readonly messages: readonly ConsoleMessage[] | null | undefined;
  /** Test id root (default `rendered-console`). */
  readonly testId?: string;
}

/** Console level → badge color. */
const LEVEL_COLORS: Record<ConsoleMessage['level'], string> = {
  log: 'steel',
  info: 'signal',
  warn: 'yellow',
  error: 'red',
  debug: 'grape'
};

/** The console log list. */
export function ConsoleLogViewer({ messages, testId = 'rendered-console' }: ConsoleLogViewerProps) {
  const rows = messages ?? [];
  if (rows.length === 0) {
    return (
      <Text size="sm" c="dimmed" data-testid={`${testId}-empty`}>
        No console messages were captured during this render.
      </Text>
    );
  }
  return (
    <Paper withBorder p="sm" data-testid={testId}>
      <Text size="xs" fw={700} tt="uppercase" lts={0.6} c="dimmed" mb={6}>
        Console · {rows.length} message{rows.length === 1 ? '' : 's'}
      </Text>
      <Stack gap={4}>
        {rows.map((message, index) => (
          <Group
            key={index}
            gap={6}
            wrap="nowrap"
            align="flex-start"
            data-testid={`${testId}-row-${index}`}
            data-level={message.level}
          >
            <Badge size="xs" variant="light" color={LEVEL_COLORS[message.level] ?? 'steel'}>
              {message.level}
            </Badge>
            <Text size="xs" ff="monospace" style={{ whiteSpace: 'pre-wrap', wordBreak: 'break-word' }} data-testid={`${testId}-text-${index}`}>
              {message.text}
            </Text>
          </Group>
        ))}
      </Stack>
    </Paper>
  );
}
