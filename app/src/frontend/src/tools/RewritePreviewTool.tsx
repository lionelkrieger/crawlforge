/**
 * URL rewrite & host-mapping preview (FS-CRAWL-002 §10.7, FS-CRAWL-001
 * §8.7): dry-run candidate regex rules and host mappings against sample
 * URLs before they touch a crawl. Before/after table with the applied
 * rule patterns per URL.
 *
 * @module tools/RewritePreviewTool
 */

import { useState } from 'react';
import { ActionIcon, Badge, Button, Group, Stack, Switch, Table, Text, TextInput, Textarea } from '@mantine/core';
import { IconLink, IconPlay, IconPlus, IconX } from '../theme/icons';
import { ToolFrame } from './shared/ToolFrame';
import { ToolErrorBanner } from './shared/ToolErrorBanner';
import { useToolCall } from './shared/useToolCall';
import type { RewritePreviewResult } from '../contracts/api/tools';

interface RuleDraft {
  pattern: string;
  replacement: string;
  enabled: boolean;
}

interface MappingDraft {
  fromHost: string;
  toHost: string;
}

const DEFAULT_URLS = `https://old.acme.example.com/docs/intro?utm_source=newsletter
https://old.acme.example.com/blog/post-1
https://cdn.acme.example.com/assets/logo.png`;

/** The rewrite/host-mapping preview tool. */
export function RewritePreviewTool() {
  const { pending, error, fieldErrors, run } = useToolCall();
  const [urls, setUrls] = useState(DEFAULT_URLS);
  const [rules, setRules] = useState<readonly RuleDraft[]>([
    { pattern: '^https://old\\.acme\\.example\\.com', replacement: 'https://acme.example.com', enabled: true },
    { pattern: '\\?utm_[^=]+=[^&]*&?', replacement: '', enabled: true }
  ]);
  const [mappings, setMappings] = useState<readonly MappingDraft[]>([
    { fromHost: 'cdn.acme.example.com', toHost: 'acme.example.com' }
  ]);
  const [results, setResults] = useState<readonly RewritePreviewResult[] | null>(null);

  const updateRule = (index: number, patch: Partial<RuleDraft>) => {
    setRules(rules.map((rule, i) => (i === index ? { ...rule, ...patch } : rule)));
  };

  const preview = async () => {
    const data = await run('tools.rewritePreview', {
      body: {
        urls: urls
          .split('\n')
          .map((line) => line.trim())
          .filter(Boolean),
        rules: rules.filter((rule) => rule.pattern.length > 0),
        hostMappings: mappings.filter((mapping) => mapping.fromHost.length > 0 && mapping.toHost.length > 0)
      }
    });
    if (data) setResults(data.results);
  };

  return (
    <ToolFrame
      testId="tool-rewrite"
      icon={<IconLink size={17} />}
      title="Rewrite & Host-Mapping Preview"
      spec="§10.7"
      subtitle="Dry-run regex rules and host mappings against sample URLs — no crawl side effects."
    >
      <Stack gap="sm">
        <Textarea
          label="Sample URLs (one per line)"
          value={urls}
          onChange={(event) => setUrls(event.currentTarget.value)}
          autosize
          minRows={3}
          maxRows={8}
          ff="monospace"
          size="xs"
          data-testid="rewrite-urls"
        />

        <Stack gap={6}>
          <Group justify="space-between">
            <Text size="xs" fw={600} tt="uppercase" lts={0.5} c="dimmed">
              Rewrite rules
            </Text>
            <Button
              size="compact-xs"
              variant="subtle"
              color="ember"
              leftSection={<IconPlus size={12} />}
              onClick={() => setRules([...rules, { pattern: '', replacement: '', enabled: true }])}
              data-testid="rewrite-add-rule"
            >
              Add rule
            </Button>
          </Group>
          {rules.map((rule, index) => (
            <Group key={index} gap={6} wrap="nowrap" data-testid={`rewrite-rule-${index}`}>
              <TextInput
                flex={1}
                size="xs"
                ff="monospace"
                placeholder="pattern (regex)"
                value={rule.pattern}
                onChange={(event) => updateRule(index, { pattern: event.currentTarget.value })}
                data-testid={`rewrite-rule-pattern-${index}`}
              />
              <TextInput
                flex={1}
                size="xs"
                ff="monospace"
                placeholder="replacement"
                value={rule.replacement}
                onChange={(event) => updateRule(index, { replacement: event.currentTarget.value })}
                data-testid={`rewrite-rule-replacement-${index}`}
              />
              <Switch
                size="xs"
                checked={rule.enabled}
                onChange={(event) => updateRule(index, { enabled: event.currentTarget.checked })}
                data-testid={`rewrite-rule-enabled-${index}`}
                aria-label={`Rule ${index + 1} enabled`}
              />
              <ActionIcon
                size="sm"
                variant="subtle"
                color="red"
                onClick={() => setRules(rules.filter((_, i) => i !== index))}
                data-testid={`rewrite-rule-remove-${index}`}
                aria-label={`Remove rule ${index + 1}`}
              >
                <IconX size={13} />
              </ActionIcon>
            </Group>
          ))}
        </Stack>

        <Stack gap={6}>
          <Group justify="space-between">
            <Text size="xs" fw={600} tt="uppercase" lts={0.5} c="dimmed">
              Host mappings
            </Text>
            <Button
              size="compact-xs"
              variant="subtle"
              color="signal"
              leftSection={<IconPlus size={12} />}
              onClick={() => setMappings([...mappings, { fromHost: '', toHost: '' }])}
              data-testid="rewrite-add-mapping"
            >
              Add mapping
            </Button>
          </Group>
          {mappings.map((mapping, index) => (
            <Group key={index} gap={6} wrap="nowrap" data-testid={`rewrite-mapping-${index}`}>
              <TextInput
                flex={1}
                size="xs"
                ff="monospace"
                placeholder="from host"
                value={mapping.fromHost}
                onChange={(event) =>
                  setMappings(mappings.map((m, i) => (i === index ? { ...m, fromHost: event.currentTarget.value } : m)))
                }
                data-testid={`rewrite-mapping-from-${index}`}
              />
              <Text size="xs" c="dimmed">
                →
              </Text>
              <TextInput
                flex={1}
                size="xs"
                ff="monospace"
                placeholder="to host"
                value={mapping.toHost}
                onChange={(event) =>
                  setMappings(mappings.map((m, i) => (i === index ? { ...m, toHost: event.currentTarget.value } : m)))
                }
                data-testid={`rewrite-mapping-to-${index}`}
              />
              <ActionIcon
                size="sm"
                variant="subtle"
                color="red"
                onClick={() => setMappings(mappings.filter((_, i) => i !== index))}
                data-testid={`rewrite-mapping-remove-${index}`}
                aria-label={`Remove mapping ${index + 1}`}
              >
                <IconX size={13} />
              </ActionIcon>
            </Group>
          ))}
        </Stack>

        <Button
          size="xs"
          color="ember"
          leftSection={<IconPlay size={13} />}
          loading={pending}
          onClick={() => void preview()}
          data-testid="rewrite-run"
        >
          Preview rewrite
        </Button>
        <ToolErrorBanner error={error} fieldErrors={fieldErrors} testId="rewrite-error" />
        {results ? (
          <Table verticalSpacing={6} className="cf-tool-table" data-testid="rewrite-results">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Original</Table.Th>
                <Table.Th>Rewritten</Table.Th>
                <Table.Th>Applied rules</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {results.map((result) => {
                const changed = result.originalUrl !== result.rewrittenUrl;
                return (
                  <Table.Tr key={result.originalUrl} data-testid={`rewrite-row-${result.originalUrl}`}>
                    <Table.Td>
                      <Text size="xs" ff="monospace" c="dimmed">
                        {result.originalUrl}
                      </Text>
                    </Table.Td>
                    <Table.Td>
                      <Text size="xs" ff="monospace" c={changed ? 'var(--mantine-color-signal-4)' : undefined}>
                        {result.rewrittenUrl}
                      </Text>
                    </Table.Td>
                    <Table.Td>
                      <Group gap={4}>
                        {result.appliedRulePatterns.length === 0 ? (
                          <Text size="xs" c="dimmed">
                            none
                          </Text>
                        ) : (
                          result.appliedRulePatterns.map((pattern) => (
                            <Badge key={pattern} size="xs" variant="outline" color="steel" ff="monospace">
                              {pattern}
                            </Badge>
                          ))
                        )}
                      </Group>
                    </Table.Td>
                  </Table.Tr>
                );
              })}
            </Table.Tbody>
          </Table>
        ) : null}
      </Stack>
    </ToolFrame>
  );
}