/**
 * llms.txt Generator & Validator (FS-CRAWL-002 §13.5, §8.7). Generation
 * writes local files only (E9 — no registries, no publishing, no AI-vendor
 * submission); validation checks a pasted body or a URL on the crawl
 * target and lists every issue.
 *
 * @module tools/LlmsTool
 */

import { useEffect, useMemo, useState } from 'react';
import { Badge, Button, Group, List, Select, Stack, Tabs, Text, TextInput, Textarea } from '@mantine/core';
import { IconCheck, IconExport, IconGlobe, IconPlay, IconWarning } from '../theme/icons';
import { ToolFrame } from './shared/ToolFrame';
import { ToolErrorBanner } from './shared/ToolErrorBanner';
import { VerdictBadge } from './shared/VerdictBadge';
import { useToolCall } from './shared/useToolCall';
import { useShell } from '../shell/ShellContext';
import type { LlmsGenerateResponse, LlmsValidateResponse, LlmsVariant } from '../contracts/api/tools';

const SAMPLE_LLMS = `# Acme Example

> A local-first knowledge base for testing crawlers.

## Docs

- [Introduction](https://acme.example.com/docs/intro): Getting started guide
- [API reference](https://acme.example.com/docs/api): Endpoint catalogue

## Optional

- [Changelog](https://acme.example.com/changelog)
`;

/** Generator panel: audit + variant + hosts → local file. */
function LlmsGenerator() {
  const { services } = useShell();
  const { pending, error, fieldErrors, run } = useToolCall();
  const auditEntities = services.hook((state) => state.audits.entities);
  const [auditId, setAuditId] = useState<string | null>(null);
  const [variant, setVariant] = useState<LlmsVariant>('llms-txt');
  const [hosts, setHosts] = useState('');
  const [result, setResult] = useState<LlmsGenerateResponse | null>(null);

  const auditOptions = useMemo(
    () =>
      auditEntities.allIds.map((id) => {
        const audit = auditEntities.byId[id];
        return { value: id, label: `Audit ${id} · ${audit.state} · ${audit.counters.fetched} fetched` };
      }),
    [auditEntities]
  );

  useEffect(() => {
    if (auditEntities.allIds.length === 0) {
      void services.actions.loadAudits();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const generate = async () => {
    if (!auditId) return;
    const hostList = hosts
      .split(',')
      .map((host) => host.trim())
      .filter(Boolean);
    const data = await run('tools.llmsGenerate', {
      body: { auditId, variant, ...(hostList.length > 0 ? { hosts: hostList } : {}) }
    });
    if (data) setResult(data);
  };

  return (
    <Stack gap="sm" data-testid="llms-generator">
      <Select
        label="Source audit"
        placeholder={auditOptions.length > 0 ? 'Pick an audit' : 'No audits loaded — connect the backend'}
        data={auditOptions}
        value={auditId}
        onChange={setAuditId}
        searchable
        size="xs"
        data-testid="llms-audit"
      />
      <Group grow>
        <Select
          label="Variant"
          data={[
            { value: 'llms-txt', label: 'llms.txt (summary)' },
            { value: 'llms-full-txt', label: 'llms-full.txt (full content)' }
          ]}
          value={variant}
          onChange={(value) => setVariant((value ?? 'llms-txt') as LlmsVariant)}
          size="xs"
          data-testid="llms-variant"
        />
        <TextInput
          label="Restrict to hosts (comma-separated, optional)"
          placeholder="acme.example.com, blog.acme.example.com"
          value={hosts}
          onChange={(event) => setHosts(event.currentTarget.value)}
          size="xs"
          data-testid="llms-hosts"
        />
      </Group>
      <Group>
        <Button
          size="xs"
          color="ember"
          leftSection={<IconExport size={13} />}
          loading={pending}
          disabled={!auditId}
          onClick={() => void generate()}
          data-testid="llms-generate"
        >
          Generate locally
        </Button>
        <Text size="xs" c="dimmed">
          Writes the local exports directory only — never published, never submitted.
        </Text>
      </Group>
      <ToolErrorBanner error={error} fieldErrors={fieldErrors} testId="llms-generate-error" />
      {result ? (
        <Group className="cf-picker-capture" gap="md" data-testid="llms-generate-result">
          <IconGlobe size={20} color="var(--mantine-color-signal-5)" />
          <Stack gap={2} flex={1}>
            <Text size="sm" fw={600} ff="monospace" data-testid="llms-filename">
              {result.fileName}
            </Text>
            <Text size="xs" c="dimmed" ff="monospace" data-testid="llms-filepath">
              {result.filePath}
            </Text>
          </Stack>
          <Badge color="signal" variant="light" data-testid="llms-variant-badge">
            {result.variant}
          </Badge>
        </Group>
      ) : null}
    </Stack>
  );
}

/** Validator panel: body or URL → verdict + issues. */
function LlmsValidator() {
  const { pending, error, fieldErrors, run } = useToolCall();
  const [source, setSource] = useState<'body' | 'url'>('body');
  const [body, setBody] = useState(SAMPLE_LLMS);
  const [url, setUrl] = useState('https://acme.example.com/llms.txt');
  const [result, setResult] = useState<LlmsValidateResponse | null>(null);

  const validate = async () => {
    const data = await run('tools.llmsValidate', {
      body: source === 'body' ? { body } : { url }
    });
    if (data) setResult(data);
  };

  return (
    <Stack gap="sm" data-testid="llms-validator">
      <Tabs value={source} onChange={(value) => setSource((value ?? 'body') as 'body' | 'url')}>
        <Tabs.List>
          <Tabs.Tab value="body" size="xs">
            Paste body
          </Tabs.Tab>
          <Tabs.Tab value="url" size="xs">
            Fetch URL
          </Tabs.Tab>
        </Tabs.List>
      </Tabs>
      {source === 'body' ? (
        <Textarea
          value={body}
          onChange={(event) => setBody(event.currentTarget.value)}
          autosize
          minRows={6}
          maxRows={14}
          ff="monospace"
          size="xs"
          data-testid="llms-body"
        />
      ) : (
        <TextInput
          value={url}
          onChange={(event) => setUrl(event.currentTarget.value)}
          ff="monospace"
          size="xs"
          data-testid="llms-validate-url"
        />
      )}
      <Button
        size="xs"
        color="ember"
        leftSection={<IconPlay size={13} />}
        loading={pending}
        onClick={() => void validate()}
        data-testid="llms-validate"
      >
        Validate
      </Button>
      <ToolErrorBanner error={error} fieldErrors={fieldErrors} testId="llms-validate-error" />
      {result ? (
        <Stack gap="sm" className="cf-picker-capture" data-testid="llms-validate-result">
          <VerdictBadge ok={result.valid} okLabel="Valid llms.txt" failLabel="Invalid llms.txt" testId="llms-verdict" />
          {result.issues.length > 0 ? (
            <List size="xs" spacing={4} icon={<IconWarning size={13} color="var(--cf-danger, #f03e3e)" />} data-testid="llms-issues">
              {result.issues.map((issue, index) => (
                <List.Item key={index}>{issue}</List.Item>
              ))}
            </List>
          ) : (
            <Group gap={6}>
              <IconCheck size={14} color="var(--mantine-color-green-6)" />
              <Text size="xs" c="dimmed">
                Structure, links, and formatting all conform.
              </Text>
            </Group>
          )}
        </Stack>
      ) : null}
    </Stack>
  );
}

/** The combined llms.txt tool. */
export function LlmsTool() {
  const [tab, setTab] = useState<string | null>('generate');
  return (
    <ToolFrame
      testId="tool-llms"
      icon={<IconGlobe size={17} />}
      title="llms.txt Generator & Validator"
      spec="§13.5"
      subtitle="GEO readiness, local-only: generate to disk or validate a body/URL."
    >
      <Tabs value={tab} onChange={setTab}>
        <Tabs.List mb="sm">
          <Tabs.Tab value="generate" size="xs" leftSection={<IconExport size={13} />}>
            Generator
          </Tabs.Tab>
          <Tabs.Tab value="validate" size="xs" leftSection={<IconCheck size={13} />}>
            Validator
          </Tabs.Tab>
        </Tabs.List>
        <Tabs.Panel value="generate">
          <LlmsGenerator />
        </Tabs.Panel>
        <Tabs.Panel value="validate">
          <LlmsValidator />
        </Tabs.Panel>
      </Tabs>
    </ToolFrame>
  );
}