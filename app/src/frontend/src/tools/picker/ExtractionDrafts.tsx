/**
 * Custom Extraction rule-draft panel (criterion #42 send-to target).
 * Captured selectors stage here as editable rule drafts; the panel emits
 * copyable rule JSON for the configuration form — the frozen wire exposes
 * no rule-write route, so drafts are local until pasted.
 *
 * @module tools/picker/ExtractionDrafts
 */

import { Button, Group, Paper, Stack, Text, TextInput } from '@mantine/core';
import { IconPlus, IconX } from '../../theme/icons';
import { CodeBlock } from '../shared/CodeBlock';

/** One staged Custom Extraction rule draft. */
export interface ExtractionRuleDraft {
  readonly ruleKey: string;
  readonly selector: string;
}

/** Serializes drafts to the configuration-form rule JSON. */
export function draftsToJson(drafts: readonly ExtractionRuleDraft[]): string {
  return JSON.stringify(
    drafts.map((draft) => ({ ruleKey: draft.ruleKey, selector: draft.selector, valueType: 'text' })),
    null,
    2
  );
}

/** ExtractionDraftsPanel props. */
export interface ExtractionDraftsPanelProps {
  readonly drafts: readonly ExtractionRuleDraft[];
  readonly onChange: (next: readonly ExtractionRuleDraft[]) => void;
  readonly testId?: string;
}

/** The staged-drafts panel with JSON export. */
export function ExtractionDraftsPanel({ drafts, onChange, testId = 'extraction-drafts' }: ExtractionDraftsPanelProps) {
  if (drafts.length === 0) return null;
  return (
    <Paper withBorder p="sm" data-testid={testId}>
      <Group justify="space-between" mb={8}>
        <Text size="xs" fw={600} tt="uppercase" lts={0.5} c="dimmed">
          Custom extraction rule drafts ({drafts.length})
        </Text>
        <Button
          size="compact-xs"
          variant="subtle"
          color="red"
          leftSection={<IconX size={12} />}
          onClick={() => onChange([])}
          data-testid={`${testId}-clear`}
        >
          Clear
        </Button>
      </Group>
      <Stack gap={6} mb={8}>
        {drafts.map((draft, index) => (
          <Group key={index} gap={6} wrap="nowrap" data-testid={`${testId}-${index}`}>
            <TextInput
              size="xs"
              ff="monospace"
              w={180}
              value={draft.ruleKey}
              onChange={(event) => onChange(drafts.map((d, i) => (i === index ? { ...d, ruleKey: event.currentTarget.value } : d)))}
              data-testid={`${testId}-key-${index}`}
              aria-label={`Rule key ${index + 1}`}
            />
            <Text size="xs" ff="monospace" c="dimmed" style={{ wordBreak: 'break-all' }}>
              {draft.selector}
            </Text>
          </Group>
        ))}
      </Stack>
      <CodeBlock label="Rule JSON — paste into configuration → Custom extraction" code={draftsToJson(drafts)} testId={`${testId}-json`} />
      <Group gap={6} mt={6}>
        <IconPlus size={12} color="var(--mantine-color-night-2)" />
        <Text size="xs" c="dimmed">
          Drafts are local until pasted into a configuration — the wire exposes no rule-write route.
        </Text>
      </Group>
    </Paper>
  );
}