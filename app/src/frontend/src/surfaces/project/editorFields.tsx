/**
 * Shared field controls for the configuration editor (WP-09-07).
 *
 * Every control is a real Mantine input bound to the form model — later-phase
 * controls render inside a disabled fieldset with an explicit note (CR-022:
 * never hidden, never functional stubs). Limit overrides use the null
 * unlimited sentinel and are never clamped (03/01 §4.3).
 *
 * @module surfaces/project/editorFields
 */

import { useState } from 'react';
import { ActionIcon, Button, Group, NumberInput, Stack, Switch, Text, Textarea, TextInput, Title } from '@mantine/core';
import { validateLimit } from './configurationModel';

/** A titled editor section. */
export function FieldSection({ title, note, children, testid }: {
  readonly title: string;
  readonly note?: string;
  readonly children: React.ReactNode;
  readonly testid?: string;
}) {
  return (
    <Stack gap="xs" data-testid={testid}>
      <Title order={5}>{title}</Title>
      {note ? <Text size="xs" c="dimmed">{note}</Text> : null}
      {children}
    </Stack>
  );
}

/**
 * A disabled-with-note section (CR-022): the fieldset disables every native
 * control inside; the note explains why. Never a fake placeholder.
 */
export function DisabledSection({ title, note, children, testid }: {
  readonly title: string;
  readonly note: string;
  readonly children?: React.ReactNode;
  readonly testid?: string;
}) {
  return (
    <Stack gap="xs" data-testid={testid}>
      <Title order={5}>{title}</Title>
      <Text size="xs" c="dimmed" data-testid={testid ? `${testid}-note` : undefined}>
        {note}
      </Text>
      <fieldset disabled style={{ border: 0, padding: 0, margin: 0, minWidth: 0 }}>
        <Stack gap="xs">{children}</Stack>
      </fieldset>
    </Stack>
  );
}

/**
 * A disabled-with-note section for controls whose engine lands in a later
 * phase (CR-022). The note names the feature and the phase.
 */
export function LaterPhaseSection({ title, phase, feature, children, testid }: {
  readonly title: string;
  readonly phase: number;
  readonly feature: string;
  readonly children?: React.ReactNode;
  readonly testid?: string;
}) {
  return (
    <DisabledSection
      title={title}
      note={`${feature} — engine lands in phase ${phase}. Available in a later build.`}
      testid={testid}
    >
      {children}
    </DisabledSection>
  );
}

/** A labelled switch row bound to a boolean model field. */
export function ToggleRow({ label, description, checked, onChange, testid, disabled }: {
  readonly label: string;
  readonly description?: string;
  readonly checked: boolean;
  readonly onChange: (next: boolean) => void;
  readonly testid: string;
  readonly disabled?: boolean;
}) {
  return (
    <Switch
      label={label}
      description={description}
      checked={checked}
      disabled={disabled}
      onChange={(event) => onChange(event.currentTarget.checked)}
      data-testid={testid}
    />
  );
}

/**
 * Unlimited-by-default ceiling (03/01 §4.2/§4.3): the Unlimited switch carries
 * the null sentinel; turning it off reveals an explicit override input. The
 * override is never clamped — invalid values surface a validation message.
 */
export function LimitField({ label, description, value, onChange, testid, overrideDefault = 1000 }: {
  readonly label: string;
  readonly description?: string;
  readonly value: number | null | undefined;
  readonly onChange: (next: number | null) => void;
  readonly testid: string;
  readonly overrideDefault?: number;
}) {
  const unlimited = value === null || value === undefined;
  const error = validateLimit(value);
  return (
    <Stack gap={4} data-testid={testid}>
      <Group justify="space-between" align="flex-end" wrap="nowrap">
        <div>
          <Text size="sm" fw={500}>{label}</Text>
          {description ? <Text size="xs" c="dimmed">{description}</Text> : null}
        </div>
        <Switch
          label="Unlimited"
          checked={unlimited}
          onChange={(event) => onChange(event.currentTarget.checked ? null : overrideDefault)}
          data-testid={`${testid}-unlimited`}
        />
      </Group>
      <NumberInput
        value={unlimited ? '' : value}
        disabled={unlimited}
        allowDecimal={false}
        placeholder="Unlimited"
        aria-label={`${label} override`}
        onChange={(next) => onChange(typeof next === 'number' ? next : null)}
        error={error ?? undefined}
        data-testid={`${testid}-override`}
      />
    </Stack>
  );
}

/** A line-list editor (one entry per line) bound to a string[] field. */
export function PatternListField({ label, description, lines, onChange, testid, placeholder, disabled }: {
  readonly label: string;
  readonly description?: string;
  readonly lines: readonly string[];
  readonly onChange: (next: string[]) => void;
  readonly testid: string;
  readonly placeholder?: string;
  readonly disabled?: boolean;
}) {
  return (
    <Textarea
      label={label}
      description={description}
      value={lines.join('\n')}
      placeholder={placeholder}
      disabled={disabled}
      autosize
      minRows={2}
      onChange={(event) => onChange(event.currentTarget.value.split(/\r?\n/).map((line) => line.trim()).filter((line) => line.length > 0))}
      data-testid={testid}
    />
  );
}

/**
 * A name/value pair editor (custom headers, user cookies). Rows are held
 * locally and emitted as a record on every edit; empty names are dropped.
 * Remount (React key) when switching configurations.
 */
export function KeyValueEditor({ label, description, value, onChange, testid, namePlaceholder = 'Name', valuePlaceholder = 'Value' }: {
  readonly label: string;
  readonly description?: string;
  readonly value: Readonly<Record<string, string>>;
  readonly onChange: (next: Record<string, string>) => void;
  readonly testid: string;
  readonly namePlaceholder?: string;
  readonly valuePlaceholder?: string;
}) {
  const [rows, setRows] = useState<{ name: string; value: string }[]>(() =>
    Object.entries(value).map(([name, val]) => ({ name, value: val }))
  );

  const emit = (next: { name: string; value: string }[]) => {
    setRows(next);
    const record: Record<string, string> = {};
    for (const row of next) {
      if (row.name.trim().length > 0) record[row.name.trim()] = row.value;
    }
    onChange(record);
  };

  return (
    <Stack gap="xs" data-testid={testid}>
      <div>
        <Text size="sm" fw={500}>{label}</Text>
        {description ? <Text size="xs" c="dimmed">{description}</Text> : null}
      </div>
      {rows.map((row, index) => (
        <Group key={index} gap="xs" wrap="nowrap" align="flex-start">
          <TextInput
            value={row.name}
            placeholder={namePlaceholder}
            aria-label={`${label} name ${index + 1}`}
            onChange={(event) => emit(rows.map((r, i) => (i === index ? { ...r, name: event.currentTarget.value } : r)))}
            data-testid={`${testid}-name-${index}`}
            style={{ flex: 1 }}
          />
          <TextInput
            value={row.value}
            placeholder={valuePlaceholder}
            aria-label={`${label} value ${index + 1}`}
            onChange={(event) => emit(rows.map((r, i) => (i === index ? { ...r, value: event.currentTarget.value } : r)))}
            data-testid={`${testid}-value-${index}`}
            style={{ flex: 2 }}
          />
          <ActionIcon
            variant="subtle"
            color="red"
            aria-label={`Remove ${label} row ${index + 1}`}
            onClick={() => emit(rows.filter((_, i) => i !== index))}
            data-testid={`${testid}-remove-${index}`}
          >
            ×
          </ActionIcon>
        </Group>
      ))}
      <div>
        <Button
          variant="light"
          size="compact-xs"
          onClick={() => emit([...rows, { name: '', value: '' }])}
          data-testid={`${testid}-add`}
        >
          Add entry
        </Button>
      </div>
    </Stack>
  );
}
