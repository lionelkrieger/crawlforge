/**
 * Standalone tools workspace frame (FS-CRAWL-001 §8.7). The full instrument
 * bench mounts from the tools workpiece (WP-06-05): Single-Page Analyzer
 * (criterion #10), SERP & Social Card preview (#48), Render Tester with the
 * visual selector picker (#42), robots/sitemap/scope/rewrite/llms/URL/
 * structured-data tools, the query builder, and user-owned export
 * destinations. Every tool runs locally without a full audit.
 *
 * @module shell/ToolsView
 */

import { Stack, Text, Title } from '@mantine/core';
import { ToolsWorkspace } from '../tools/ToolsWorkspace';

/** The tools view: shell header + the mountable tools workspace. */
export function ToolsView() {
  return (
    <Stack gap="md" data-testid="tools-view">
      <div>
        <Title order={3}>Standalone tools</Title>
        <Text size="sm" c="dimmed" mt={2}>
          One-page analysis, previews, and generators — no project, no crawl required. Everything runs locally.
        </Text>
      </div>
      <ToolsWorkspace />
    </Stack>
  );
}