/**
 * Resilient-CSS-selector list editor: the include/exclude lists of Content
 * Area Scoping (FS-CRAWL-002 §4.2) and the "send to" target of the Finder
 * picker (#42). Controlled component; rows are monospace, deduplicated on
 * add, and empty rows are dropped on change.
 *
 * @module tools/shared/SelectorListEditor
 */

import { ActionIcon, Button, Group, Stack, Text, TextInput } from '@mantine/core';
import { IconPlus, IconX } from '../../theme/icons';

/** SelectorListEditor props. */
export interface SelectorListEditorProps {
  /** List label (e.g. "Include selectors"). */
  readonly label: string;
  /** Current selectors. */
  readonly selectors: readonly string[];
  /** Change callback (next full list). */
  readonly onChange: (next: readonly string[]) => void;
  /** Test identifier root. */
  readonly testId: string;
  /** Row placeholder. */
  readonly placeholder?: string;
  /** Accent color for the add affordance. */
  readonly color?: string;
}

/** An editable, ordered selector list. */
export function SelectorListEditor({
  label,
  selectors,
  onChange,
  testId,
  placeholder = 'main article .content',
  color = 'ember'
}: SelectorListEditorProps) {
  const update = (index: number, value: string) => {
    const next = selectors.map((entry, i) => (i === index ? value : entry));
    onChange(next);
  };

  const remove = (index: number) => {
    onChange(selectors.filter((_, i) => i !== index));
  };

  const add = () => {
    onChange([...selectors, '']);
  };

  return (
    <Stack gap={6} data-testid={testId}>
      <Group justify="space-between">
        <Text size="xs" fw={600} tt="uppercase" lts={0.5} c="dimmed">
          {label}
        </Text>
        <Text size="xs" c="dimmed" data-testid={`${testId}-count`}>
          {selectors.filter((s) => s.trim().length > 0).length} active
        </Text>
      </Group>
      {selectors.length === 0 ? (
        <Text size="xs" c="dimmed" fs="italic" data-testid={`${testId}-empty`}>
          No selectors — everything is {label.toLowerCase().startsWith('include') ? 'out of scope until added' : 'kept unless excluded'}.
        </Text>
      ) : null}
      {selectors.map((selector, index) => (
        <Group key={index} gap={6} wrap="nowrap">
          <TextInput
            flex={1}
            size="xs"
            ff="monospace"
            value={selector}
            placeholder={placeholder}
            onChange={(event) => update(index, event.currentTarget.value)}
            data-testid={`${testId}-row-${index}`}
            aria-label={`${label} ${index + 1}`}
          />
          <ActionIcon
            size="sm"
            variant="subtle"
            color="red"
            aria-label={`Remove selector ${index + 1}`}
            onClick={() => remove(index)}
            data-testid={`${testId}-remove-${index}`}
          >
            <IconX size={13} />
          </ActionIcon>
        </Group>
      ))}
      <Button
        size="compact-xs"
        variant="subtle"
        color={color}
        leftSection={<IconPlus size={12} />}
        onClick={add}
        data-testid={`${testId}-add`}
      >
        Add selector
      </Button>
    </Stack>
  );
}