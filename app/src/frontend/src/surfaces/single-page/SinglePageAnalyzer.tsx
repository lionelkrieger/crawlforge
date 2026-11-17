/**
 * Single-Page Analyzer (FS-CRAWL-001 §8.7, criterion #10 UI facet): full
 * audit-grade findings for one page WITHOUT creating a crawl project —
 * element inventory, WAF verdict, scope diagnostics, and rendered-artifact
 * inspection with the visual selector picker. Captured selectors stage
 * into extraction rule drafts and local scope lists.
 *
 * Mountable surface: the shell tools frame and the window catalog both
 * consume this root. Offline-first — every backend call degrades to a
 * message, never a throw.
 *
 * @module surfaces/single-page/SinglePageAnalyzer
 */

import { useMemo, useState } from 'react';
import {
  Badge,
  Box,
  Button,
  Group,
  Paper,
  Stack,
  Switch,
  Table,
  Tabs,
  Text,
  TextInput,
  Title
} from '@mantine/core';
import { IconBug, IconChart, IconPlay, IconSearch, IconShield, IconTerminal } from '../../theme/icons';
import { ToolFrame } from '../../tools/shared/ToolFrame';
import { ToolErrorBanner } from '../../tools/shared/ToolErrorBanner';
import { VerdictBadge } from '../../tools/shared/VerdictBadge';
import { CodeBlock } from '../../tools/shared/CodeBlock';
import { useToolCall } from '../../tools/shared/useToolCall';
import { SelectorPicker } from '../../tools/picker/SelectorPicker';
import { ExtractionDraftsPanel, type ExtractionRuleDraft } from '../../tools/picker/ExtractionDrafts';
import type { Finding } from '../../contracts/api/audits';
import type { SinglePageAnalysisResponse } from '../../contracts/api/content';
import type { RenderTestResponse } from '../../contracts/api/tools';
import '../../tools/shared/tools.css';

/** Severity display order + color (local rule catalogue keys). */
const SEVERITY_ORDER = ['critical', 'high', 'medium', 'low', 'info'] as const;
const SEVERITY_COLORS: Record<string, string> = {
  critical: 'red',
  high: 'orange',
  medium: 'yellow',
  low: 'signal',
  info: 'steel'
};

/** One finding row with evidence. */
function FindingRow({ finding }: { finding: Finding }) {
  return (
    <Paper withBorder p="sm" className="cf-finding-row" data-testid={`sp-finding-${finding.id}`} data-severity={finding.severity}>
      <Group justify="space-between" mb={6} wrap="nowrap">
        <Group gap={6} wrap="nowrap">
          <Badge size="xs" color={SEVERITY_COLORS[finding.severity] ?? 'steel'} variant="light" data-testid={`sp-finding-severity-${finding.id}`}>
            {finding.severity}
          </Badge>
          <Badge size="xs" variant="outline" color="steel">
            {finding.category}
          </Badge>
          <Text size="xs" ff="monospace" c="dimmed">
            {finding.ruleKey}
          </Text>
        </Group>
      </Group>
      <Text size="sm">{finding.message}</Text>
      {finding.evidence?.selector ? (
        <Text size="xs" ff="monospace" c="var(--mantine-color-signal-4)" mt={4} data-testid={`sp-finding-selector-${finding.id}`}>
          {finding.evidence.selector}
        </Text>
      ) : null}
      {finding.evidence?.snippet ? (
        <Box mt={6}>
          <CodeBlock code={finding.evidence.snippet} testId={`sp-finding-snippet-${finding.id}`} maxHeight={110} noCopy />
        </Box>
      ) : null}
    </Paper>
  );
}

/** The Single-Page Analyzer surface. */
export function SinglePageAnalyzer() {
  const { pending, error, fieldErrors, run } = useToolCall();
  const [url, setUrl] = useState('https://acme.example.com/blog/post-1');
  const [render, setRender] = useState(false);
  const [result, setResult] = useState<SinglePageAnalysisResponse | null>(null);
  const [severityFilter, setSeverityFilter] = useState<string | null>(null);
  const [tab, setTab] = useState<string | null>('findings');
  const [artifact, setArtifact] = useState<RenderTestResponse | null>(null);
  const [artifactPending, setArtifactPending] = useState(false);
  const [drafts, setDrafts] = useState<readonly ExtractionRuleDraft[]>([]);
  const [scopeInclude, setScopeInclude] = useState<readonly string[]>([]);
  const [scopeExclude, setScopeExclude] = useState<readonly string[]>([]);

  const analyze = async () => {
    const data = await run('analysis.singlePage', { body: { url, render } });
    if (data) {
      setResult(data);
      setSeverityFilter(null);
      setTab('findings');
    }
  };

  const inspectRendered = async () => {
    setArtifactPending(true);
    const data = await run('tools.renderTest', { body: { url, picker: true } });
    setArtifactPending(false);
    if (data) {
      setArtifact(data);
      setTab('artifact');
    }
  };

  const severities = useMemo(() => {
    if (!result) return [];
    const present = new Set(result.findings.map((finding) => finding.severity));
    return SEVERITY_ORDER.filter((severity) => present.has(severity));
  }, [result]);

  const visibleFindings = useMemo(() => {
    if (!result) return [];
    const filtered = severityFilter ? result.findings.filter((f) => f.severity === severityFilter) : result.findings;
    const rank = (severity: string) => {
      const index = SEVERITY_ORDER.indexOf(severity as (typeof SEVERITY_ORDER)[number]);
      return index === -1 ? SEVERITY_ORDER.length : index;
    };
    return [...filtered].sort((a, b) => rank(a.severity) - rank(b.severity));
  }, [result, severityFilter]);

  const elementRows = useMemo(() => {
    if (!result) return [];
    return Object.entries(result.elementInventory.counts).sort((a, b) => b[1] - a[1]);
  }, [result]);

  const sendToScope = (selector: string, list: 'include' | 'exclude') => {
    const setter = list === 'include' ? setScopeInclude : setScopeExclude;
    const current = list === 'include' ? scopeInclude : scopeExclude;
    if (!current.includes(selector)) setter([...current, selector]);
  };

  return (
    <ToolFrame
      testId="single-page-analyzer"
      icon={<IconSearch size={17} />}
      title="Single-Page Analyzer"
      spec="criterion #10"
      subtitle="Audit-grade findings for one page — no project, no crawl, no setup."
      actions={
        <Button
          size="xs"
          variant="light"
          color="signal"
          leftSection={<IconTerminal size={13} />}
          loading={artifactPending}
          onClick={() => void inspectRendered()}
          data-testid="sp-inspect-rendered"
        >
          Inspect rendered artifact
        </Button>
      }
    >
      <Stack gap="sm">
        <Group gap={6} wrap="nowrap" align="flex-end">
          <TextInput
            flex={1}
            label="Page URL"
            value={url}
            onChange={(event) => setUrl(event.currentTarget.value)}
            ff="monospace"
            size="xs"
            data-testid="sp-url"
          />
          <Switch
            size="xs"
            label="Render"
            checked={render}
            onChange={(event) => setRender(event.currentTarget.checked)}
            data-testid="sp-render-toggle"
            mb={6}
          />
          <Button
            size="xs"
            color="ember"
            leftSection={<IconPlay size={13} />}
            loading={pending}
            onClick={() => void analyze()}
            data-testid="sp-analyze"
            mb={1}
          >
            Analyze page
          </Button>
        </Group>
        <ToolErrorBanner error={error} fieldErrors={fieldErrors} testId="sp-error" />

        {result ? (
          <Group gap="md" className="cf-sp-summary" data-testid="sp-summary">
              <Stack gap={0} align="center">
                <Title order={3} className="cf-sp-bignum" data-testid="sp-finding-count">
                  {result.findings.length}
                </Title>
                <Text size="xs" c="dimmed">
                  findings
                </Text>
              </Stack>
              <Stack gap={0} align="center">
                <Title order={3} className="cf-sp-bignum" data-testid="sp-element-total">
                  {result.elementInventory.totalElements}
                </Title>
                <Text size="xs" c="dimmed">
                  elements
                </Text>
              </Stack>
              {result.wafVerdict ? (
                <Group gap={6} className="cf-sp-waf" data-testid="sp-waf">
                  <IconBug size={16} color="var(--cf-danger, #f03e3e)" />
                  <Stack gap={0}>
                    <Text size="xs" fw={600}>
                      WAF / challenge
                    </Text>
                    <Text size="xs" c="dimmed">
                      {result.wafVerdict.provider} · {result.wafVerdict.challengeClass}
                    </Text>
                  </Stack>
                </Group>
              ) : null}
              {result.scopeDiagnostics ? (
                <Group gap={6} data-testid="sp-scope">
                  <IconShield size={16} color="var(--mantine-color-signal-5)" />
                  <Stack gap={0}>
                    <VerdictBadge ok={result.scopeDiagnostics.included} okLabel="In scope" failLabel="Out of scope" size="xs" />
                    <Text size="xs" c="dimmed">
                      {result.scopeDiagnostics.scopedWordCount}/{result.scopeDiagnostics.totalWordCount} words
                    </Text>
                  </Stack>
                </Group>
              ) : null}
          </Group>
        ) : null}

        <Tabs value={tab} onChange={setTab}>
              <Tabs.List mb="sm">
                <Tabs.Tab value="findings" size="xs" leftSection={<IconChart size={13} />}>
                  Findings{result ? ` (${result.findings.length})` : ''}
                </Tabs.Tab>
                <Tabs.Tab value="elements" size="xs" leftSection={<IconChart size={13} />}>
                  Element inventory
                </Tabs.Tab>
                <Tabs.Tab value="artifact" size="xs" leftSection={<IconTerminal size={13} />}>
                  Rendered artifact
                </Tabs.Tab>
              </Tabs.List>

              <Tabs.Panel value="findings">
                {result ? (
                <Stack gap="sm">
                  <Group gap={6} data-testid="sp-severity-filters">
                    <Button
                      size="compact-xs"
                      variant={severityFilter === null ? 'light' : 'subtle'}
                      color="steel"
                      onClick={() => setSeverityFilter(null)}
                      data-testid="sp-filter-all"
                    >
                      All
                    </Button>
                    {severities.map((severity) => (
                      <Button
                        key={severity}
                        size="compact-xs"
                        variant={severityFilter === severity ? 'light' : 'subtle'}
                        color={SEVERITY_COLORS[severity]}
                        onClick={() => setSeverityFilter(severity)}
                        data-testid={`sp-filter-${severity}`}
                      >
                        {severity} ({result.findings.filter((f) => f.severity === severity).length})
                      </Button>
                    ))}
                  </Group>
                  {visibleFindings.length === 0 ? (
                    <Text size="sm" c="dimmed" fs="italic" data-testid="sp-findings-empty">
                      No findings{severityFilter ? ` at ${severityFilter} severity` : ''} — the page is clean.
                    </Text>
                  ) : (
                    <Stack gap={8} data-testid="sp-findings-list">
                      {visibleFindings.map((finding) => (
                        <FindingRow key={finding.id} finding={finding} />
                      ))}
                    </Stack>
                  )}
                </Stack>
                ) : (
                  <Text size="sm" c="dimmed" fs="italic" data-testid="sp-empty">
                    Enter a URL and analyze — findings, element inventory, and scope diagnostics arrive without a project.
                  </Text>
                )}
              </Tabs.Panel>

              <Tabs.Panel value="elements">
                {result ? (
                <Table verticalSpacing={6} className="cf-tool-table" data-testid="sp-elements-table">
                  <Table.Thead>
                    <Table.Tr>
                      <Table.Th>Element group</Table.Th>
                      <Table.Th ta="right">Count</Table.Th>
                    </Table.Tr>
                  </Table.Thead>
                  <Table.Tbody>
                    {elementRows.map(([group, count]) => (
                      <Table.Tr key={group} data-testid={`sp-element-row-${group}`}>
                        <Table.Td>
                          <Text size="xs" ff="monospace">
                            {group}
                          </Text>
                        </Table.Td>
                        <Table.Td ta="right">
                          <Badge size="xs" variant="light" color="signal">
                            {count}
                          </Badge>
                        </Table.Td>
                      </Table.Tr>
                    ))}
                  </Table.Tbody>
                </Table>
                ) : (
                  <Text size="sm" c="dimmed" fs="italic" data-testid="sp-elements-empty">
                    No element inventory yet — analyze a page to count its elements.
                  </Text>
                )}
              </Tabs.Panel>

              <Tabs.Panel value="artifact">
                {artifact ? (
                  <Stack gap="sm" data-testid="sp-artifact">
                    {artifact.screenshotBase64 ? (
                      <Box className="cf-render-shot" maw={420}>
                        <img
                          src={`data:image/png;base64,${artifact.screenshotBase64}`}
                          alt="Rendered screenshot"
                          style={{ width: '100%', display: 'block' }}
                          data-testid="sp-screenshot"
                        />
                      </Box>
                    ) : null}
                    <Paper withBorder p="sm">
                      <SelectorPicker
                        html={artifact.renderedHtml}
                        onSendToExtraction={(selector) =>
                          setDrafts((current) => [...current, { ruleKey: `custom.rule-${current.length + 1}`, selector }])
                        }
                        onSendToScope={sendToScope}
                        testId="sp-picker"
                      />
                    </Paper>
                    {scopeInclude.length + scopeExclude.length > 0 ? (
                      <Group gap={6} data-testid="sp-scope-captures">
                        <Text size="xs" c="dimmed">
                          Captured scope:
                        </Text>
                        {scopeInclude.map((selector) => (
                          <Badge key={`i-${selector}`} size="xs" color="green" variant="light" ff="monospace">
                            + {selector}
                          </Badge>
                        ))}
                        {scopeExclude.map((selector) => (
                          <Badge key={`e-${selector}`} size="xs" color="red" variant="light" ff="monospace">
                            − {selector}
                          </Badge>
                        ))}
                      </Group>
                    ) : null}
                    <ExtractionDraftsPanel drafts={drafts} onChange={setDrafts} testId="sp-drafts" />
                  </Stack>
                ) : (
                  <Stack gap="sm" align="flex-start">
                    <Text size="sm" c="dimmed" data-testid="sp-artifact-empty">
                      No rendered artifact yet — render the page to inspect it offline and pick selectors visually.
                    </Text>
                    <Button
                      size="xs"
                      color="ember"
                      leftSection={<IconTerminal size={13} />}
                      loading={artifactPending}
                      onClick={() => void inspectRendered()}
                      data-testid="sp-render-now"
                    >
                      Render & inspect
                    </Button>
                  </Stack>
                )}
              </Tabs.Panel>
        </Tabs>
      </Stack>
    </ToolFrame>
  );
}