/**
 * Standalone URL check (FS-CRAWL-001 §8.7): status + class, redirect chain,
 * response headers, indexability verdict, plus the additive WAF verdict
 * and content-scope diagnostics — one URL, no audit, no project.
 *
 * @module tools/UrlCheckTool
 */

import { useState } from 'react';
import { Badge, Button, Group, Stack, Table, Text, TextInput } from '@mantine/core';
import { IconChevronRight, IconGlobe, IconPlay } from '../theme/icons';
import { ToolFrame } from './shared/ToolFrame';
import { ToolErrorBanner } from './shared/ToolErrorBanner';
import { VerdictBadge } from './shared/VerdictBadge';
import { useToolCall } from './shared/useToolCall';
import type { UrlCheckResponse } from '../contracts/api/tools';

/** Status-class badge color. */
const CLASS_COLORS: Record<string, string> = {
  '1xx': 'signal',
  '2xx': 'green',
  '3xx': 'yellow',
  '4xx': 'orange',
  '5xx': 'red'
};

/** The URL check tool. */
export function UrlCheckTool() {
  const { pending, error, fieldErrors, run } = useToolCall();
  const [url, setUrl] = useState('https://acme.example.com/old-page');
  const [result, setResult] = useState<UrlCheckResponse | null>(null);

  const check = async () => {
    const data = await run('tools.urlCheck', { body: { url } });
    if (data) setResult(data);
  };

  return (
    <ToolFrame
      testId="tool-urlcheck"
      icon={<IconGlobe size={17} />}
      title="URL Check"
      spec="§8.7"
      subtitle="Status, redirects, headers, indexability, WAF verdict — one URL, no audit."
    >
      <Stack gap="sm">
        <Group gap={6} wrap="nowrap" align="flex-end">
          <TextInput
            flex={1}
            label="Target URL"
            value={url}
            onChange={(event) => setUrl(event.currentTarget.value)}
            ff="monospace"
            size="xs"
            data-testid="urlcheck-url"
          />
          <Button
            size="xs"
            color="ember"
            leftSection={<IconPlay size={13} />}
            loading={pending}
            onClick={() => void check()}
            data-testid="urlcheck-run"
            mb={1}
          >
            Check
          </Button>
        </Group>
        <ToolErrorBanner error={error} fieldErrors={fieldErrors} testId="urlcheck-error" />
        {result ? (
          <Stack gap="sm" data-testid="urlcheck-result">
            <Group gap="md">
              <Badge size="lg" color={CLASS_COLORS[result.statusClass] ?? 'steel'} variant="light" data-testid="urlcheck-status">
                {result.statusCode} · {result.statusClass}
              </Badge>
              <VerdictBadge ok={result.indexable} okLabel="Indexable" failLabel="Not indexable" testId="urlcheck-indexable" />
              {result.wafVerdict ? (
                <Badge size="sm" color="red" variant="light" data-testid="urlcheck-waf">
                  WAF: {result.wafVerdict.provider} · {result.wafVerdict.challengeClass}
                </Badge>
              ) : null}
            </Group>

            {result.redirectChain.length > 0 ? (
              <Stack gap={4} data-testid="urlcheck-chain">
                <Text size="xs" fw={600} tt="uppercase" lts={0.5} c="dimmed">
                  Redirect chain ({result.redirectChain.length})
                </Text>
                {result.redirectChain.map((hop, index) => (
                  <Group key={index} gap={6} wrap="nowrap">
                    <Badge size="xs" variant="outline" color="steel">
                      {hop.statusCode}
                    </Badge>
                    <Text size="xs" ff="monospace" c="dimmed">
                      {hop.url}
                    </Text>
                    <IconChevronRight size={12} color="var(--mantine-color-night-2)" />
                    <Text size="xs" ff="monospace" c="var(--mantine-color-signal-4)">
                      {hop.location ?? '(end)'}
                    </Text>
                  </Group>
                ))}
              </Stack>
            ) : null}

            {result.scopeDiagnostics ? (
              <Group gap="md" className="cf-picker-capture" data-testid="urlcheck-scope">
                <VerdictBadge ok={result.scopeDiagnostics.included} okLabel="In scope" failLabel="Out of scope" size="xs" />
                <Text size="xs" c="dimmed">
                  Scoped words: {result.scopeDiagnostics.scopedWordCount} / {result.scopeDiagnostics.totalWordCount}
                </Text>
              </Group>
            ) : null}

            <Table verticalSpacing={4} className="cf-tool-table" data-testid="urlcheck-headers">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>Header</Table.Th>
                  <Table.Th>Value</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {Object.entries(result.headers).map(([name, value]) => (
                  <Table.Tr key={name}>
                    <Table.Td>
                      <Text size="xs" ff="monospace" fw={600}>
                        {name}
                      </Text>
                    </Table.Td>
                    <Table.Td>
                      <Text size="xs" ff="monospace" c="dimmed" style={{ wordBreak: 'break-all' }}>
                        {value}
                      </Text>
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Stack>
        ) : null}
      </Stack>
    </ToolFrame>
  );
}