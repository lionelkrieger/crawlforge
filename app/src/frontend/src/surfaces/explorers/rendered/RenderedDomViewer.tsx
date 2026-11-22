/**
 * Rendered DOM viewer (WP-10-03, UI-13 rendered tab; FS-CRAWL-001 §5.6.2 /
 * §8.7 "the rendered document").
 *
 * Shows the post-render serialized DOM (ContentArtifactKind.RenderedDom) in a
 * scrollable monospace block with a copy affordance. A null/empty DOM renders
 * an honest empty state (baseline non-JS capture stores no rendered DOM).
 *
 * @module surfaces/explorers/rendered/RenderedDomViewer
 */

import { Text } from '@mantine/core';
import { CodeBlock } from '../../../tools/shared/CodeBlock';

/** Props for the rendered DOM viewer. */
export interface RenderedDomViewerProps {
  readonly html: string | null | undefined;
  /** Test id root (default `rendered-dom`). */
  readonly testId?: string;
  readonly maxHeight?: number;
}

/** The rendered DOM source viewer. */
export function RenderedDomViewer({ html, testId = 'rendered-dom', maxHeight = 320 }: RenderedDomViewerProps) {
  if (!html || html.trim() === '') {
    return (
      <Text size="sm" c="dimmed" data-testid={`${testId}-empty`}>
        No rendered DOM captured for this URL (baseline non-JS capture stores only the raw response).
      </Text>
    );
  }
  return <CodeBlock code={html} label="Rendered DOM" testId={testId} maxHeight={maxHeight} />;
}
