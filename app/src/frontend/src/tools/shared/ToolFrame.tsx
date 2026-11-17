/**
 * Shared tool chrome (FS-CRAWL-002 §8.1 dark-first forge identity): a raised
 * night-surface panel with an ember icon chip, display-face title, and a
 * spec-reference tag. Every standalone tool mounts inside one frame so the
 * catalog reads as one instrument bench, not a pile of forms.
 *
 * @module tools/shared/ToolFrame
 */

import type { ReactNode } from 'react';
import { Badge, Group, Paper, Stack, Text, Title } from '@mantine/core';

/** ToolFrame props. */
export interface ToolFrameProps {
  /** Stable test identifier root (rendered as data-testid). */
  readonly testId: string;
  /** Local icon node (theme/icons — never remote). */
  readonly icon: ReactNode;
  /** Tool title (display face). */
  readonly title: string;
  /** Spec reference tag, e.g. "§8.7" or "criterion #42". */
  readonly spec?: string;
  /** One-line purpose statement under the title. */
  readonly subtitle?: string;
  /** Optional header-right actions. */
  readonly actions?: ReactNode;
  /** Tool body. */
  readonly children: ReactNode;
}

/** The standard tool panel. */
export function ToolFrame({ testId, icon, title, spec, subtitle, actions, children }: ToolFrameProps) {
  return (
    <Paper withBorder p="md" data-testid={testId} className="cf-tool-frame">
      <Group justify="space-between" align="flex-start" mb={subtitle ? 4 : 12} wrap="nowrap">
        <Group gap={10} wrap="nowrap" align="center">
          <span className="cf-tool-icon" aria-hidden>
            {icon}
          </span>
          <Stack gap={0}>
            <Group gap={8} align="center">
              <Title order={4} className="cf-tool-title">
                {title}
              </Title>
              {spec ? (
                <Badge size="xs" variant="outline" color="steel" className="cf-tool-spec">
                  {spec}
                </Badge>
              ) : null}
            </Group>
            {subtitle ? (
              <Text size="xs" c="dimmed" mt={2}>
                {subtitle}
              </Text>
            ) : null}
          </Stack>
        </Group>
        {actions ? <Group gap={6}>{actions}</Group> : null}
      </Group>
      {children}
    </Paper>
  );
}