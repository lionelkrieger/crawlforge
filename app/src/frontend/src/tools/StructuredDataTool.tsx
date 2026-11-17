/**
 * Standalone structured-data checker (FS-CRAWL-001 §8.7, §7.13): parse
 * JSON-LD / Microdata from pasted markup or a target URL, with validation
 * and rich-result eligibility verdicts per block.
 *
 * @module tools/StructuredDataTool
 */

import { useState } from 'react';
import { Badge, Button, Group, Paper, SegmentedControl, Stack, Text, TextInput, Textarea } from '@mantine/core';
import { IconCheck, IconPlay, IconTerminal, IconWarning } from '../theme/icons';
import { ToolFrame } from './shared/ToolFrame';
import { ToolErrorBanner } from './shared/ToolErrorBanner';
import { VerdictBadge } from './shared/VerdictBadge';
import { CodeBlock } from './shared/CodeBlock';
import { useToolCall } from './shared/useToolCall';
import type { StructuredDataCheckResponse } from '../contracts/api/tools';

const SAMPLE_MARKUP = `<script type="application/ld+json">
{
  "@context": "https://schema.org",
  "@type": "Article",
  "headline": "Local-first crawling",
  "author": { "@type": "Person", "name": "Ada Forge" },
  "datePublished": "2026-11-01"
}
</script>`;

/** The structured-data checker tool. */
export function StructuredDataTool() {
  const { pending, error, fieldErrors, run } = useToolCall();
  const [source, setSource] = useState<'markup' | 'url'>('markup');
  const [markup, setMarkup] = useState(SAMPLE_MARKUP);
  const [url, setUrl] = useState('https://acme.example.com/blog/post-1');
  const [result, setResult] = useState<StructuredDataCheckResponse | null>(null);

  const check = async () => {
    const data = await run('tools.structuredDataCheck', {
      body: source === 'markup' ? { markup } : { url }
    });
    if (data) setResult(data);
  };

  return (
    <ToolFrame
      testId="tool-structured"
      icon={<IconTerminal size={17} />}
      title="Structured Data Checker"
      spec="§7.13"
      subtitle="JSON-LD & Microdata blocks with validation and rich-result verdicts."
    >
      <Stack gap="sm">
        <SegmentedControl
          size="xs"
          value={source}
          onChange={(value) => setSource(value as 'markup' | 'url')}
          data={[
            { label: 'Paste markup', value: 'markup' },
            { label: 'Fetch URL', value: 'url' }
          ]}
          data-testid="structured-source"
        />
        {source === 'markup' ? (
          <Textarea
            value={markup}
            onChange={(event) => setMarkup(event.currentTarget.value)}
            autosize
            minRows={6}
            maxRows={14}
            ff="monospace"
            size="xs"
            data-testid="structured-markup"
          />
        ) : (
          <TextInput
            value={url}
            onChange={(event) => setUrl(event.currentTarget.value)}
            ff="monospace"
            size="xs"
            data-testid="structured-url"
          />
        )}
        <Button
          size="xs"
          color="ember"
          leftSection={<IconPlay size={13} />}
          loading={pending}
          onClick={() => void check()}
          data-testid="structured-run"
        >
          Check blocks
        </Button>
        <ToolErrorBanner error={error} fieldErrors={fieldErrors} testId="structured-error" />
        {result ? (
          <Stack gap="sm" data-testid="structured-result">
            {result.blocks.length === 0 ? (
              <Text size="xs" c="dimmed" fs="italic" data-testid="structured-empty">
                No structured-data blocks found.
              </Text>
            ) : null}
            {result.blocks.map((block, index) => (
              <Paper key={index} withBorder p="sm" className="cf-picker-capture" data-testid={`structured-block-${index}`}>
                <Group justify="space-between" mb={8}>
                  <Group gap={8}>
                    <Badge size="sm" color="violet" variant="light" ff="monospace">
                      {block.format}
                    </Badge>
                    <Text size="sm" fw={600}>
                      {block.type}
                    </Text>
                  </Group>
                  <Group gap={6}>
                    <VerdictBadge ok={block.valid} okLabel="Valid" failLabel="Invalid" size="xs" />
                    <Badge
                      size="xs"
                      color={block.richResultEligible ? 'green' : 'steel'}
                      variant="light"
                      leftSection={block.richResultEligible ? <IconCheck size={10} /> : undefined}
                      data-testid={`structured-rich-${index}`}
                    >
                      {block.richResultEligible ? 'Rich result eligible' : 'No rich result'}
                    </Badge>
                  </Group>
                </Group>
                {block.issues.length > 0 ? (
                  <Stack gap={2} mb={8} data-testid={`structured-issues-${index}`}>
                    {block.issues.map((issue, issueIndex) => (
                      <Group key={issueIndex} gap={6} wrap="nowrap">
                        <IconWarning size={12} color="var(--cf-danger, #f03e3e)" />
                        <Text size="xs" c="dimmed">
                          {issue}
                        </Text>
                      </Group>
                    ))}
                  </Stack>
                ) : null}
                <CodeBlock label="Parsed properties" code={JSON.stringify(block.properties, null, 2)} testId={`structured-props-${index}`} maxHeight={180} />
              </Paper>
            ))}
          </Stack>
        ) : null}
      </Stack>
    </ToolFrame>
  );
}