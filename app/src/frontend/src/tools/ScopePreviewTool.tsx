/**
 * Content Area Scoping preview (FS-CRAWL-002 §4.6/§4.2, §8.7): pick a
 * sample URL, load its rendered artifact into the selector picker, and
 * build include/exclude lists visually — captured selectors land directly
 * in the lists. The preview verdict shows inclusion, matched selectors,
 * and the scoped visible-text preview the crawler will analyze.
 *
 * @module tools/ScopePreviewTool
 */

import { useState } from 'react';
import { Badge, Button, Group, Paper, SegmentedControl, Stack, Text, TextInput } from '@mantine/core';
import { IconPlay, IconSearch, IconShield } from '../theme/icons';
import { ToolFrame } from './shared/ToolFrame';
import { ToolErrorBanner } from './shared/ToolErrorBanner';
import { VerdictBadge } from './shared/VerdictBadge';
import { SelectorListEditor } from './shared/SelectorListEditor';
import { CodeBlock } from './shared/CodeBlock';
import { useToolCall } from './shared/useToolCall';
import { SelectorPicker } from './picker/SelectorPicker';
import type { ScopePreviewResponse } from '../contracts/api/tools';

/** The content-scope preview tool. */
export function ScopePreviewTool() {
  const { pending, error, fieldErrors, run } = useToolCall();
  const [source, setSource] = useState<'url' | 'artifact'>('url');
  const [url, setUrl] = useState('https://acme.example.com/blog/post-1');
  const [artifactId, setArtifactId] = useState('');
  const [includeSelectors, setIncludeSelectors] = useState<readonly string[]>(['main article']);
  const [excludeSelectors, setExcludeSelectors] = useState<readonly string[]>(['nav', 'footer', '.sidebar']);
  const [artifactHtml, setArtifactHtml] = useState<string | null>(null);
  const [artifactPending, setArtifactPending] = useState(false);
  const [result, setResult] = useState<ScopePreviewResponse | null>(null);

  const addToScope = (selector: string, list: 'include' | 'exclude') => {
    const setter = list === 'include' ? setIncludeSelectors : setExcludeSelectors;
    const current = list === 'include' ? includeSelectors : excludeSelectors;
    if (!current.includes(selector)) setter([...current, selector]);
  };

  const loadArtifact = async () => {
    setArtifactPending(true);
    const data = await run('tools.renderTest', { body: { url, picker: true } });
    setArtifactPending(false);
    if (data) setArtifactHtml(data.renderedHtml);
  };

  const preview = async () => {
    const contentScope = {
      includeSelectors: includeSelectors.filter((s) => s.trim().length > 0),
      excludeSelectors: excludeSelectors.filter((s) => s.trim().length > 0)
    };
    const data = await run('tools.scopePreview', {
      body: {
        ...(source === 'url' ? { url } : { artifactId }),
        contentScope
      }
    });
    if (data) setResult(data);
  };

  return (
    <ToolFrame
      testId="tool-scope"
      icon={<IconShield size={17} />}
      title="Content Scope Preview"
      spec="§4.6"
      subtitle="Build include/exclude lists visually — the picker sends captured selectors straight into them."
    >
      <Stack gap="sm">
        <Group justify="space-between">
          <SegmentedControl
            size="xs"
            value={source}
            onChange={(value) => setSource(value as 'url' | 'artifact')}
            data={[
              { label: 'Sample URL', value: 'url' },
              { label: 'Stored artifact', value: 'artifact' }
            ]}
            data-testid="scope-source"
          />
          {source === 'url' ? (
            <Button
              size="xs"
              variant="light"
              color="signal"
              leftSection={<IconSearch size={13} />}
              loading={artifactPending}
              onClick={() => void loadArtifact()}
              data-testid="scope-load-artifact"
            >
              Load rendered artifact
            </Button>
          ) : null}
        </Group>
        {source === 'url' ? (
          <TextInput
            label="Sample URL"
            value={url}
            onChange={(event) => setUrl(event.currentTarget.value)}
            ff="monospace"
            size="xs"
            data-testid="scope-url"
          />
        ) : (
          <TextInput
            label="Stored artifact identifier"
            value={artifactId}
            onChange={(event) => setArtifactId(event.currentTarget.value)}
            ff="monospace"
            size="xs"
            data-testid="scope-artifact-id"
          />
        )}

        <Group align="stretch" grow>
          <SelectorListEditor
            label="Include selectors"
            selectors={includeSelectors}
            onChange={setIncludeSelectors}
            testId="scope-include"
            color="green"
          />
          <SelectorListEditor
            label="Exclude selectors"
            selectors={excludeSelectors}
            onChange={setExcludeSelectors}
            testId="scope-exclude"
            color="red"
          />
        </Group>

        {artifactHtml !== null && source === 'url' ? (
          <Paper withBorder p="sm">
            <SelectorPicker
              html={artifactHtml}
              onSendToScope={addToScope}
              testId="scope-picker"
            />
          </Paper>
        ) : null}

        <Button
          size="xs"
          color="ember"
          leftSection={<IconPlay size={13} />}
          loading={pending}
          disabled={source === 'artifact' && artifactId.trim().length === 0}
          onClick={() => void preview()}
          data-testid="scope-run"
        >
          Preview scope
        </Button>
        <ToolErrorBanner error={error} fieldErrors={fieldErrors} testId="scope-error" />

        {result ? (
          <Stack gap="sm" className="cf-picker-capture" data-testid="scope-result">
            <Group gap="md">
              <VerdictBadge ok={result.included} okLabel="Included" failLabel="Excluded" testId="scope-verdict" />
              <Text size="xs" c="dimmed">
                The scoped visible-text tree is what the crawler analyzes.
              </Text>
            </Group>
            <Group gap={6}>
              {result.matchedIncludeSelectors.map((selector) => (
                <Badge key={`i-${selector}`} size="xs" color="green" variant="light" ff="monospace">
                  + {selector}
                </Badge>
              ))}
              {result.matchedExcludeSelectors.map((selector) => (
                <Badge key={`e-${selector}`} size="xs" color="red" variant="light" ff="monospace">
                  − {selector}
                </Badge>
              ))}
            </Group>
            {result.scopedTextPreview !== null ? (
              <CodeBlock label="Scoped text preview" code={result.scopedTextPreview} testId="scope-text" maxHeight={180} />
            ) : (
              <Text size="xs" c="dimmed" fs="italic" data-testid="scope-text-empty">
                No scoped text — the include/exclude combination removed everything.
              </Text>
            )}
          </Stack>
        ) : null}
      </Stack>
    </ToolFrame>
  );
}