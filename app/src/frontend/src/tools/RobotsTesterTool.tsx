/**
 * Standalone robots.txt tester (FS-CRAWL-001 §8.7): allow/disallow verdicts
 * with the matched rule for each test URL, against a pasted body or a
 * robots URL fetched from the crawl target. No audit, no project — the
 * tool runs on its own.
 *
 * @module tools/RobotsTesterTool
 */

import { useState } from 'react';
import { Button, Group, SegmentedControl, Stack, Table, Text, TextInput, Textarea } from '@mantine/core';
import { IconPlay, IconShield } from '../theme/icons';
import { ToolFrame } from './shared/ToolFrame';
import { ToolErrorBanner } from './shared/ToolErrorBanner';
import { VerdictBadge } from './shared/VerdictBadge';
import { useToolCall } from './shared/useToolCall';
import type { RobotsTestResult } from '../contracts/api/tools';

const DEFAULT_ROBOTS = `User-agent: *
Disallow: /admin/
Disallow: /checkout
Allow: /admin/public-docs
Sitemap: https://acme.example.com/sitemap.xml`;

const DEFAULT_URLS = `https://acme.example.com/
https://acme.example.com/admin/settings
https://acme.example.com/admin/public-docs
https://acme.example.com/checkout`;

/** The robots.txt tester tool. */
export function RobotsTesterTool() {
  const { pending, error, fieldErrors, run } = useToolCall();
  const [source, setSource] = useState<'body' | 'url'>('body');
  const [robotsBody, setRobotsBody] = useState(DEFAULT_ROBOTS);
  const [robotsUrl, setRobotsUrl] = useState('https://acme.example.com/robots.txt');
  const [testUrls, setTestUrls] = useState(DEFAULT_URLS);
  const [userAgent, setUserAgent] = useState('CrawlForge');
  const [results, setResults] = useState<readonly RobotsTestResult[] | null>(null);

  const execute = async () => {
    const urls = testUrls
      .split('\n')
      .map((line) => line.trim())
      .filter(Boolean);
    const data = await run('tools.robotsTest', {
      body: {
        ...(source === 'body' ? { robotsBody } : { robotsUrl }),
        testUrls: urls,
        userAgent
      }
    });
    if (data) setResults(data.results);
  };

  return (
    <ToolFrame
      testId="tool-robots"
      icon={<IconShield size={17} />}
      title="Robots.txt Tester"
      spec="§8.7"
      subtitle="Allow/disallow verdicts with the matched rule — standalone, no audit required."
    >
      <Stack gap="sm">
        <Group justify="space-between">
          <SegmentedControl
            size="xs"
            value={source}
            onChange={(value) => setSource(value as 'body' | 'url')}
            data={[
              { label: 'Paste body', value: 'body' },
              { label: 'Fetch URL', value: 'url' }
            ]}
            data-testid="robots-source"
          />
          <TextInput
            size="xs"
            label="User-agent"
            value={userAgent}
            onChange={(event) => setUserAgent(event.currentTarget.value)}
            data-testid="robots-useragent"
            w={220}
          />
        </Group>
        {source === 'body' ? (
          <Textarea
            label="robots.txt body"
            value={robotsBody}
            onChange={(event) => setRobotsBody(event.currentTarget.value)}
            autosize
            minRows={5}
            maxRows={12}
            ff="monospace"
            size="xs"
            data-testid="robots-body"
          />
        ) : (
          <TextInput
            label="robots.txt URL (fetched from the crawl target)"
            value={robotsUrl}
            onChange={(event) => setRobotsUrl(event.currentTarget.value)}
            ff="monospace"
            size="xs"
            data-testid="robots-url"
          />
        )}
        <Textarea
          label="Test URLs (one per line)"
          value={testUrls}
          onChange={(event) => setTestUrls(event.currentTarget.value)}
          autosize
          minRows={3}
          maxRows={8}
          ff="monospace"
          size="xs"
          data-testid="robots-test-urls"
        />
        <Group>
          <Button
            size="xs"
            color="ember"
            leftSection={<IconPlay size={13} />}
            loading={pending}
            onClick={() => void execute()}
            data-testid="robots-run"
          >
            Test verdicts
          </Button>
          {results ? (
            <Text size="xs" c="dimmed" data-testid="robots-summary">
              {results.filter((r) => r.allowed).length} allowed · {results.filter((r) => !r.allowed).length} disallowed
            </Text>
          ) : null}
        </Group>
        <ToolErrorBanner error={error} fieldErrors={fieldErrors} testId="robots-error" />
        {results ? (
          <Table verticalSpacing={6} className="cf-tool-table" data-testid="robots-results">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>URL</Table.Th>
                <Table.Th>Verdict</Table.Th>
                <Table.Th>Matched rule</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {results.map((result) => (
                <Table.Tr key={result.url} data-testid={`robots-row-${result.url}`}>
                  <Table.Td>
                    <Text size="xs" ff="monospace">
                      {result.url}
                    </Text>
                  </Table.Td>
                  <Table.Td>
                    <VerdictBadge ok={result.allowed} okLabel="Allowed" failLabel="Disallowed" size="xs" />
                  </Table.Td>
                  <Table.Td>
                    <Text size="xs" ff="monospace" c="dimmed">
                      {result.matchedRule ?? '(default)'}
                    </Text>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        ) : null}
      </Stack>
    </ToolFrame>
  );
}