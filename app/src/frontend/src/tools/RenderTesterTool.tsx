/**
 * Render Tester (FS-CRAWL-001 §8.7, criterion #42): headless-render a
 * target URL through the local backend, inspect the screenshot, console
 * output, and post-render WAF verdict, then pick selectors visually on the
 * rendered artifact. Captured selectors stage into Custom Extraction rule
 * drafts (copyable JSON — the frozen wire exposes no rule-write route, so
 * drafts paste into the configuration form).
 *
 * The preview sandbox loads ONLY the crawled target's rendered artifact.
 *
 * @module tools/RenderTesterTool
 */

import { useState } from 'react';
import {
  Badge,
  Box,
  Button,
  Group,
  Paper,
  Select,
  Stack,
  Switch,
  Table,
  Text,
  TextInput
} from '@mantine/core';
import { IconBug, IconPlay, IconTerminal } from '../theme/icons';
import { ToolFrame } from './shared/ToolFrame';
import { ToolErrorBanner } from './shared/ToolErrorBanner';
import { VerdictBadge } from './shared/VerdictBadge';
import { useToolCall } from './shared/useToolCall';
import { SelectorPicker } from './picker/SelectorPicker';
import { ExtractionDraftsPanel, type ExtractionRuleDraft } from './picker/ExtractionDrafts';
import type { ConsoleMessage, RenderTestResponse } from '../contracts/api/tools';

export type { ExtractionRuleDraft } from './picker/ExtractionDrafts';

/** Console level → badge color. */
const LEVEL_COLORS: Record<ConsoleMessage['level'], string> = {
  log: 'steel',
  info: 'signal',
  warn: 'yellow',
  error: 'red',
  debug: 'grape'
};

/** The render tester tool. */
export function RenderTesterTool() {
  const { pending, error, fieldErrors, run } = useToolCall();
  const [url, setUrl] = useState('https://acme.example.com/');
  const [deviceProfile, setDeviceProfile] = useState<string | null>('desktop');
  const [pickerEnabled, setPickerEnabled] = useState(true);
  const [result, setResult] = useState<RenderTestResponse | null>(null);
  const [drafts, setDrafts] = useState<readonly ExtractionRuleDraft[]>([]);

  const execute = async () => {
    const data = await run('tools.renderTest', {
      body: { url, deviceProfile: deviceProfile ?? undefined, picker: pickerEnabled }
    });
    if (data) setResult(data);
  };

  const stageSelector = (selector: string) => {
    setDrafts((current) => [
      ...current,
      { ruleKey: `custom.rule-${current.length + 1}`, selector }
    ]);
  };

  return (
    <ToolFrame
      testId="tool-render"
      icon={<IconTerminal size={17} />}
      title="Render Tester"
      spec="§8.7 · #42"
      subtitle="Headless render via the local backend — screenshot, console, WAF verdict, and the visual selector picker."
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
            data-testid="render-url"
          />
          <Select
            label="Device"
            data={['desktop', 'mobile']}
            value={deviceProfile}
            onChange={setDeviceProfile}
            size="xs"
            w={130}
            data-testid="render-device"
          />
          <Switch
            size="xs"
            label="Picker"
            checked={pickerEnabled}
            onChange={(event) => setPickerEnabled(event.currentTarget.checked)}
            data-testid="render-picker-toggle"
            mb={6}
          />
          <Button
            size="xs"
            color="ember"
            leftSection={<IconPlay size={13} />}
            loading={pending}
            onClick={() => void execute()}
            data-testid="render-run"
            mb={1}
          >
            Render
          </Button>
        </Group>
        <ToolErrorBanner error={error} fieldErrors={fieldErrors} testId="render-error" />

        {result ? (
          <Stack gap="sm" data-testid="render-result">
            {result.wafVerdict ? (
              <Paper withBorder p="sm" className="cf-picker-capture" data-testid="render-waf">
                <Group gap="md">
                  <IconBug size={18} color="var(--cf-danger, #f03e3e)" />
                  <Stack gap={0} flex={1}>
                    <Text size="sm" fw={600}>
                      WAF / challenge detected after rendering
                    </Text>
                    <Text size="xs" c="dimmed">
                      {result.wafVerdict.provider} · {result.wafVerdict.challengeClass}
                    </Text>
                  </Stack>
                  <VerdictBadge ok={false} okLabel="Clear" failLabel="Challenged" size="xs" />
                </Group>
              </Paper>
            ) : null}

            <Group align="flex-start" gap="md" wrap="nowrap">
              {result.screenshotBase64 ? (
                <Box className="cf-render-shot" flex="0 0 320px">
                  <img
                    src={`data:image/png;base64,${result.screenshotBase64}`}
                    alt="Rendered screenshot"
                    style={{ width: '100%', display: 'block', borderRadius: 6 }}
                    data-testid="render-screenshot"
                  />
                </Box>
              ) : (
                <Text size="xs" c="dimmed" fs="italic" data-testid="render-no-screenshot">
                  No screenshot captured.
                </Text>
              )}
              <Stack gap={4} flex={1} maw={420}>
                <Text size="xs" fw={600} tt="uppercase" lts={0.5} c="dimmed">
                  Console ({result.consoleMessages.length})
                </Text>
                {result.consoleMessages.length === 0 ? (
                  <Text size="xs" c="dimmed" fs="italic">
                    Console silent.
                  </Text>
                ) : (
                  <Table verticalSpacing={4} className="cf-tool-table" data-testid="render-console">
                    <Table.Tbody>
                      {result.consoleMessages.map((message, index) => (
                        <Table.Tr key={index}>
                          <Table.Td w={70}>
                            <Badge size="xs" color={LEVEL_COLORS[message.level]} variant="light">
                              {message.level}
                            </Badge>
                          </Table.Td>
                          <Table.Td>
                            <Text size="xs" ff="monospace" style={{ wordBreak: 'break-word' }}>
                              {message.text}
                            </Text>
                          </Table.Td>
                        </Table.Tr>
                      ))}
                    </Table.Tbody>
                  </Table>
                )}
              </Stack>
            </Group>

            {pickerEnabled && result.pickerActive !== false ? (
              <Paper withBorder p="sm">
                <SelectorPicker
                  html={result.renderedHtml}
                  onSendToExtraction={stageSelector}
                  testId="render-picker"
                />
              </Paper>
            ) : null}

            <ExtractionDraftsPanel drafts={drafts} onChange={setDrafts} testId="render-drafts" />
          </Stack>
        ) : null}
      </Stack>
    </ToolFrame>
  );
}