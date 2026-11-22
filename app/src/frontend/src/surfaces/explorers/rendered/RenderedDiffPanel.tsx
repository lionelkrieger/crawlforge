/**
 * Raw-vs-rendered diff panel (WP-10-03, UI-13 rendered tab; FS-CRAWL-001
 * §7.18 / CR-009, CAP-A-035 "UI-13 diff").
 *
 * Presentational dual-evidence diff projected by `extractRenderedDiff` from
 * the stored `render.*-delta` findings. Every changed field shows the RAW
 * response value beside the RENDERED value (dual evidence), with its severity
 * and — for the critical rendered-only/raw-only noindex — a critical badge and
 * direction. Title, canonical, noindex and word count get dedicated stable
 * testids the acceptance harness drives; every other delta field renders as a
 * generic dual-evidence row.
 *
 * @module surfaces/explorers/rendered/RenderedDiffPanel
 */

import { Badge, Group, Paper, Stack, Table, Text } from '@mantine/core';
import type { RenderedDiff, RenderedFieldDiff, RenderedNoindexDiff, RenderedWordCountDiff } from './renderedDiff';
import { severityTone } from '../cells';

/** Props for the diff panel. */
export interface RenderedDiffPanelProps {
  readonly diff: RenderedDiff;
  /** Test id root (default `rendered-diff`). */
  readonly testId?: string;
}

/** A monospace value cell (— when null). */
function Value({ value, testId }: { value: string | null; testId: string }) {
  if (value === null || value === '') {
    return <Text span c="dimmed" size="xs" data-testid={testId}>—</Text>;
  }
  return (
    <Text span size="xs" ff="monospace" style={{ whiteSpace: 'pre-wrap', wordBreak: 'break-word' }} data-testid={testId}>
      {value}
    </Text>
  );
}

/** Severity + optional critical badge cluster. */
function SeverityBadges({ severity, critical, testId }: { severity: string; critical: boolean; testId: string }) {
  return (
    <Group gap={4} wrap="nowrap">
      <Badge size="xs" variant="light" color={severityTone(severity) === 'danger' ? 'red' : severityTone(severity) === 'warning' ? 'yellow' : 'signal'}>
        {severity}
      </Badge>
      {critical ? (
        <Badge size="xs" variant="filled" color="red" data-testid={testId}>
          critical
        </Badge>
      ) : null}
    </Group>
  );
}

/** One generic dual-evidence field row. */
function FieldRow({ diff, root }: { diff: RenderedFieldDiff; root: string }) {
  const fieldId = `${root}-field-${diff.field}`;
  return (
    <Table.Tr data-testid={fieldId} data-rule-key={diff.ruleKey} data-field={diff.field}>
      <Table.Td>
        <Stack gap={2}>
          <Text size="xs" fw={600}>{diff.label}</Text>
          <SeverityBadges severity={diff.severity} critical={diff.critical} testId={`${fieldId}-critical`} />
        </Stack>
      </Table.Td>
      <Table.Td style={{ maxWidth: 320 }}>
        <Value value={diff.responseValue} testId={`${fieldId}-raw`} />
      </Table.Td>
      <Table.Td style={{ maxWidth: 320 }}>
        <Value value={diff.renderedValue} testId={`${fieldId}-rendered`} />
      </Table.Td>
    </Table.Tr>
  );
}

/** The headline title row (dedicated harness testids). */
function TitleRow({ diff, root }: { diff: RenderedFieldDiff; root: string }) {
  return (
    <Table.Tr data-testid={`${root}-field-title`} data-rule-key={diff.ruleKey}>
      <Table.Td>
        <Stack gap={2}>
          <Text size="xs" fw={600}>Title</Text>
          <SeverityBadges severity={diff.severity} critical={diff.critical} testId={`${root}-title-critical`} />
        </Stack>
      </Table.Td>
      <Table.Td style={{ maxWidth: 320 }}>
        <Value value={diff.responseValue} testId={`${root}-title-raw`} />
      </Table.Td>
      <Table.Td style={{ maxWidth: 320 }}>
        <Value value={diff.renderedValue} testId={`${root}-title-rendered`} />
      </Table.Td>
    </Table.Tr>
  );
}

/** The headline canonical row (dedicated harness testids). */
function CanonicalRow({ diff, root }: { diff: RenderedFieldDiff; root: string }) {
  return (
    <Table.Tr data-testid={`${root}-field-canonical`} data-rule-key={diff.ruleKey}>
      <Table.Td>
        <Stack gap={2}>
          <Text size="xs" fw={600}>Canonical</Text>
          <SeverityBadges severity={diff.severity} critical={diff.critical} testId={`${root}-canonical-critical`} />
        </Stack>
      </Table.Td>
      <Table.Td style={{ maxWidth: 320 }}>
        <Value value={diff.responseValue} testId={`${root}-canonical-raw`} />
      </Table.Td>
      <Table.Td style={{ maxWidth: 320 }}>
        <Value value={diff.renderedValue} testId={`${root}-canonical-rendered`} />
      </Table.Td>
    </Table.Tr>
  );
}

/** The headline noindex row (dual evidence + critical direction). */
function NoindexRow({ diff, root }: { diff: RenderedNoindexDiff; root: string }) {
  const rawLabel = diff.rawNoindex ? 'noindex' : 'index';
  const renderedLabel = diff.renderedNoindex ? 'noindex' : 'index';
  return (
    <Table.Tr data-testid={`${root}-field-noindex`} data-rule-key="render.noindex-delta" data-direction={diff.direction}>
      <Table.Td>
        <Stack gap={2}>
          <Text size="xs" fw={600}>Noindex</Text>
          <SeverityBadges severity={diff.severity} critical={diff.critical} testId={`${root}-noindex-critical`} />
          <Text size="xs" c="dimmed" data-testid={`${root}-noindex-direction`}>{diff.direction}</Text>
        </Stack>
      </Table.Td>
      <Table.Td>
        <Stack gap={2}>
          <Value value={rawLabel} testId={`${root}-noindex-raw`} />
          {diff.rawDirectives ? <Text size="xs" c="dimmed" ff="monospace" data-testid={`${root}-noindex-raw-directives`}>{diff.rawDirectives}</Text> : null}
        </Stack>
      </Table.Td>
      <Table.Td>
        <Stack gap={2}>
          <Value value={renderedLabel} testId={`${root}-noindex-rendered`} />
          {diff.renderedDirectives ? <Text size="xs" c="dimmed" ff="monospace" data-testid={`${root}-noindex-rendered-directives`}>{diff.renderedDirectives}</Text> : null}
        </Stack>
      </Table.Td>
    </Table.Tr>
  );
}

/** The headline word-count row (raw vs rendered visible-word counts). */
function WordCountRow({ diff, root }: { diff: RenderedWordCountDiff; root: string }) {
  const raw = diff.rawWordCount === null ? '—' : String(diff.rawWordCount);
  const rendered = diff.renderedWordCount === null ? '—' : String(diff.renderedWordCount);
  return (
    <Table.Tr data-testid={`${root}-field-text`} data-rule-key="render.text-delta">
      <Table.Td>
        <Stack gap={2}>
          <Text size="xs" fw={600}>Word count</Text>
          <SeverityBadges severity={diff.severity} critical={false} testId={`${root}-wordcount-critical`} />
          {diff.wordCountDelta !== null ? (
            <Text size="xs" c="dimmed" data-testid={`${root}-wordcount-delta`}>
              {diff.wordCountDelta >= 0 ? '+' : ''}{diff.wordCountDelta} words
            </Text>
          ) : null}
        </Stack>
      </Table.Td>
      <Table.Td>
        <Text span size="sm" ff="monospace" data-testid={`${root}-wordcount-raw`}>{raw}</Text>
      </Table.Td>
      <Table.Td>
        <Text span size="sm" ff="monospace" data-testid={`${root}-wordcount-rendered`}>{rendered}</Text>
      </Table.Td>
    </Table.Tr>
  );
}

/** Rule keys rendered as dedicated headline rows (excluded from generic loop). */
const HEADLINE_KEYS = new Set<string>([
  'render.title-delta',
  'render.canonical-delta',
  'render.noindex-delta',
  'render.text-delta'
]);

/** The raw-vs-rendered dual-evidence diff panel. */
export function RenderedDiffPanel({ diff, testId = 'rendered-diff' }: RenderedDiffPanelProps) {
  if (!diff.hasDifferences) {
    return (
      <Text size="sm" c="dimmed" data-testid={`${testId}-empty`}>
        No response-vs-rendered differences — the raw response and the rendered DOM agree on every compared field.
      </Text>
    );
  }
  const otherFields = diff.fields.filter((field) => !HEADLINE_KEYS.has(field.ruleKey));
  return (
    <Paper withBorder p="sm" data-testid={testId}>
      <Group justify="space-between" mb={6}>
        <Text size="xs" fw={700} tt="uppercase" lts={0.6} c="dimmed">
          Raw response vs rendered DOM · {diff.fields.length} difference{diff.fields.length === 1 ? '' : 's'}
        </Text>
        {diff.criticalCount > 0 ? (
          <Badge size="sm" variant="filled" color="red" data-testid={`${testId}-critical`}>
            {diff.criticalCount} critical
          </Badge>
        ) : null}
      </Group>
      <Table striped withTableBorder={false} fz="xs" data-testid={`${testId}-table`}>
        <Table.Thead>
          <Table.Tr>
            <Table.Th style={{ width: 150 }}>Field</Table.Th>
            <Table.Th>Response (raw)</Table.Th>
            <Table.Th>Rendered</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {diff.title ? <TitleRow diff={diff.title} root={testId} /> : null}
          {diff.canonical ? <CanonicalRow diff={diff.canonical} root={testId} /> : null}
          {diff.noindex ? <NoindexRow diff={diff.noindex} root={testId} /> : null}
          {diff.wordCount ? <WordCountRow diff={diff.wordCount} root={testId} /> : null}
          {otherFields.map((field) => (
            <FieldRow key={field.findingId} diff={field} root={testId} />
          ))}
        </Table.Tbody>
      </Table>
    </Paper>
  );
}
