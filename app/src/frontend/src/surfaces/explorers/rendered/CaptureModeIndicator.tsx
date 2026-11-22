/**
 * Capture-mode indicator (WP-10-03, UI-10 audit run view; FS-CRAWL-001
 * §5.6.1, CAP-A-X09).
 *
 * Compact badge showing the audit's capture mode (non-js / js / dual /
 * adaptive) and, optionally, a per-URL capture source (raw / rendered / both).
 * Placement in the run view is coordinator wiring — this component is the
 * exact export the shell mounts (props + testids documented in the handoff).
 *
 * Reads the LOCKED wire tokens (`contracts/api/vocabulary`); an unknown or
 * absent mode renders an honest "baseline" state, never a fabricated mode.
 *
 * @module surfaces/explorers/rendered/CaptureModeIndicator
 */

import { Badge, Group, Tooltip } from '@mantine/core';
import {
  isCaptureModeWire,
  isCaptureSourceWire,
  type CaptureModeWire,
  type CaptureSourceWire
} from '../../../contracts/api/vocabulary';
import { CAPTURE_MODE_LABELS, CAPTURE_SOURCE_LABELS } from './renderedKeys';

/** Props for the capture-mode indicator. */
export interface CaptureModeIndicatorProps {
  /** The configuration/audit capture mode (null/absent = baseline non-JS). */
  readonly mode?: CaptureModeWire | null;
  /** Optional per-URL capture source (raw/rendered/both). */
  readonly captureSource?: CaptureSourceWire | null;
  /** Test id root (default `capture-mode-indicator`). */
  readonly testId?: string;
}

/** Mode → badge color (dual/adaptive highlight the rendered capture path). */
function modeColor(mode: CaptureModeWire | null): string {
  switch (mode) {
    case 'js': return 'signal';
    case 'dual': return 'ember';
    case 'adaptive': return 'grape';
    case 'non-js': return 'steel';
    default: return 'steel';
  }
}

/** The capture-mode indicator badge cluster. */
export function CaptureModeIndicator({ mode, captureSource, testId = 'capture-mode-indicator' }: CaptureModeIndicatorProps) {
  const knownMode = isCaptureModeWire(mode) ? mode : null;
  const modeLabel = knownMode ? CAPTURE_MODE_LABELS[knownMode] : 'Baseline (non-JS)';
  const knownSource = isCaptureSourceWire(captureSource) ? captureSource : null;
  return (
    <Group gap={6} wrap="nowrap" data-testid={testId} data-mode={knownMode ?? 'baseline'}>
      <Tooltip label="Capture mode for this audit (FS-CRAWL-001 §5.6.1)" openDelay={300}>
        <Badge size="sm" variant="light" color={modeColor(knownMode)} data-testid={`${testId}-value`}>
          {modeLabel}
        </Badge>
      </Tooltip>
      {knownSource ? (
        <Badge size="sm" variant="outline" color="steel" data-testid={`${testId}-source`} data-source={knownSource}>
          {CAPTURE_SOURCE_LABELS[knownSource]}
        </Badge>
      ) : null}
    </Group>
  );
}
