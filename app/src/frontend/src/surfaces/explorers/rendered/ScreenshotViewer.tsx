/**
 * Screenshot viewer (WP-10-03, UI-13 rendered tab / UI-18 / UI-19;
 * FS-CRAWL-001 §8.2 "optional rendered screenshot", §8.7).
 *
 * Renders the base64 PNG screenshot (ContentArtifactKind.Screenshot) captured
 * by the CDP rendering layer. A null/empty value renders an honest empty
 * state. The image is local-only (a data URL) — no remote load (E9).
 *
 * @module surfaces/explorers/rendered/ScreenshotViewer
 */

import { Paper, Text } from '@mantine/core';

/** Props for the screenshot viewer. */
export interface ScreenshotViewerProps {
  readonly screenshotBase64: string | null | undefined;
  /** Test id root (default `rendered-screenshot`). */
  readonly testId?: string;
  readonly maxHeight?: number;
}

/** The rendered screenshot viewer. */
export function ScreenshotViewer({ screenshotBase64, testId = 'rendered-screenshot', maxHeight = 340 }: ScreenshotViewerProps) {
  if (!screenshotBase64 || screenshotBase64.trim() === '') {
    return (
      <Text size="sm" c="dimmed" data-testid={`${testId}-empty`}>
        No screenshot captured for this render.
      </Text>
    );
  }
  return (
    <Paper withBorder p="xs" data-testid={testId}>
      <img
        src={`data:image/png;base64,${screenshotBase64}`}
        alt="Rendered screenshot"
        data-testid={`${testId}-img`}
        style={{ maxWidth: '100%', maxHeight, borderRadius: 6, display: 'block' }}
      />
    </Paper>
  );
}
