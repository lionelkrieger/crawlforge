/**
 * Semantic verdict badge: allow/disallow, valid/invalid, pass/fail — the
 * tools layer speaks in verdicts. Color is never the only carrier: every
 * badge pairs an icon with a text label (FS-CRAWL-002 §8.1).
 *
 * @module tools/shared/VerdictBadge
 */

import { Badge } from '@mantine/core';
import { IconCheck, IconX } from '../../theme/icons';

/** VerdictBadge props. */
export interface VerdictBadgeProps {
  /** The verdict polarity. */
  readonly ok: boolean;
  /** Label when ok (default "Allowed"). */
  readonly okLabel?: string;
  /** Label when not ok (default "Disallowed"). */
  readonly failLabel?: string;
  /** Mantine badge size. */
  readonly size?: 'xs' | 'sm' | 'md';
  /** Optional test identifier. */
  readonly testId?: string;
}

/** A polarity badge with icon + label. */
export function VerdictBadge({ ok, okLabel = 'Allowed', failLabel = 'Disallowed', size = 'sm', testId }: VerdictBadgeProps) {
  const label = ok ? okLabel : failLabel;
  return (
    <Badge
      size={size}
      variant="light"
      color={ok ? 'green' : 'red'}
      leftSection={ok ? <IconCheck size={11} /> : <IconX size={11} />}
      data-testid={testId}
      data-verdict={ok ? 'ok' : 'fail'}
    >
      {label}
    </Badge>
  );
}