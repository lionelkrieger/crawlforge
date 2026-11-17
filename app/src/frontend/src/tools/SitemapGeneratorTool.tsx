/**
 * Standalone sitemap generator (FS-CRAWL-001 §8.7): standards-conformant
 * XML sitemaps from a filtered URL set of any completed audit — written
 * locally only (E9: no remote publishing). Filters mirror the URL
 * explorer: status class, indexability, depth, text search.
 *
 * @module tools/SitemapGeneratorTool
 */

import { useEffect, useMemo, useState } from 'react';
import {
  Badge,
  Button,
  Group,
  NumberInput,
  Select,
  Slider,
  Stack,
  Switch,
  Text,
  TextInput
} from '@mantine/core';
import { IconExport, IconGlobe } from '../theme/icons';
import { ToolFrame } from './shared/ToolFrame';
import { ToolErrorBanner } from './shared/ToolErrorBanner';
import { useToolCall } from './shared/useToolCall';
import { useShell } from '../shell/ShellContext';
import type { HttpStatusClass } from '../contracts/api/audits';
import type { SitemapChangeFrequency, SitemapGenerateResponse } from '../contracts/api/tools';

const CHANGE_FREQUENCIES: readonly SitemapChangeFrequency[] = [
  'always',
  'hourly',
  'daily',
  'weekly',
  'monthly',
  'yearly',
  'never'
];

/** The sitemap generator tool. */
export function SitemapGeneratorTool() {
  const { services } = useShell();
  const { pending, error, fieldErrors, run } = useToolCall();
  const auditEntities = services.hook((state) => state.audits.entities);
  const [auditId, setAuditId] = useState<string | null>(null);
  const [statusClass, setStatusClass] = useState<string | null>('2xx');
  const [indexableOnly, setIndexableOnly] = useState(true);
  const [depth, setDepth] = useState<number | ''>('');
  const [search, setSearch] = useState('');
  const [changeFrequency, setChangeFrequency] = useState<string | null>('weekly');
  const [priority, setPriority] = useState(0.5);
  const [includeLastmod, setIncludeLastmod] = useState(true);
  const [result, setResult] = useState<SitemapGenerateResponse | null>(null);

  const auditOptions = useMemo(
    () =>
      auditEntities.allIds.map((id) => {
        const audit = auditEntities.byId[id];
        return { value: id, label: `Audit ${id} · ${audit.state} · ${audit.counters.fetched} fetched` };
      }),
    [auditEntities]
  );

  // Load the audit catalog once when the store is empty (offline-safe).
  useEffect(() => {
    if (auditEntities.allIds.length === 0) {
      void services.actions.loadAudits();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const generate = async () => {
    if (!auditId) return;
    const data = await run('tools.sitemapGenerate', {
      body: {
        auditId,
        filters: {
          ...(statusClass ? { statusClass: statusClass as HttpStatusClass } : {}),
          indexable: indexableOnly,
          ...(depth !== '' ? { depth } : {}),
          ...(search.trim() ? { search: search.trim() } : {})
        },
        ...(changeFrequency ? { changeFrequency: changeFrequency as SitemapChangeFrequency } : {}),
        priority,
        includeLastmod
      }
    });
    if (data) setResult(data);
  };

  return (
    <ToolFrame
      testId="tool-sitemap"
      icon={<IconGlobe size={17} />}
      title="Sitemap Generator"
      spec="§8.7"
      subtitle="Standards-conformant XML from a filtered URL set — written locally, never published."
    >
      <Stack gap="sm">
        <Select
          label="Source audit"
          placeholder={auditOptions.length > 0 ? 'Pick an audit' : 'No audits loaded — connect the backend'}
          data={auditOptions}
          value={auditId}
          onChange={setAuditId}
          searchable
          size="xs"
          data-testid="sitemap-audit"
        />
        <Group grow>
          <Select
            label="Status class"
            data={['1xx', '2xx', '3xx', '4xx', '5xx']}
            value={statusClass}
            onChange={setStatusClass}
            clearable
            size="xs"
            data-testid="sitemap-status"
          />
          <Select
            label="Change frequency"
            data={[...CHANGE_FREQUENCIES]}
            value={changeFrequency}
            onChange={setChangeFrequency}
            clearable
            size="xs"
            data-testid="sitemap-changefreq"
          />
          <NumberInput
            label="Max depth"
            placeholder="any"
            min={0}
            max={64}
            value={depth}
            onChange={(value) => setDepth(value === '' ? '' : Number(value))}
            size="xs"
            data-testid="sitemap-depth"
          />
        </Group>
        <TextInput
          label="URL contains"
          placeholder="/docs/"
          value={search}
          onChange={(event) => setSearch(event.currentTarget.value)}
          size="xs"
          data-testid="sitemap-search"
        />
        <Group gap="lg">
          <Switch
            size="xs"
            label="Indexable URLs only"
            checked={indexableOnly}
            onChange={(event) => setIndexableOnly(event.currentTarget.checked)}
            data-testid="sitemap-indexable"
          />
          <Switch
            size="xs"
            label="Include lastmod"
            checked={includeLastmod}
            onChange={(event) => setIncludeLastmod(event.currentTarget.checked)}
            data-testid="sitemap-lastmod"
          />
        </Group>
        <Stack gap={4}>
          <Text size="xs" c="dimmed">
            Priority: {priority.toFixed(1)}
          </Text>
          <Slider
            min={0}
            max={1}
            step={0.1}
            value={priority}
            onChange={setPriority}
            color="ember"
            size="sm"
            label={(value) => value.toFixed(1)}
            data-testid="sitemap-priority"
          />
        </Stack>
        <Group>
          <Button
            size="xs"
            color="ember"
            leftSection={<IconExport size={13} />}
            loading={pending}
            disabled={!auditId}
            onClick={() => void generate()}
            data-testid="sitemap-generate"
          >
            Generate sitemap
          </Button>
          <Text size="xs" c="dimmed">
            Output writes to the local exports directory only.
          </Text>
        </Group>
        <ToolErrorBanner error={error} fieldErrors={fieldErrors} testId="sitemap-error" />
        {result ? (
          <Group className="cf-picker-capture" gap="md" data-testid="sitemap-result">
            <IconExport size={20} color="var(--mantine-color-ember-5)" />
            <Stack gap={2} flex={1}>
              <Text size="sm" fw={600} ff="monospace" data-testid="sitemap-filename">
                {result.fileName}
              </Text>
              <Text size="xs" c="dimmed" ff="monospace" data-testid="sitemap-filepath">
                {result.filePath}
              </Text>
            </Stack>
            <Badge color="signal" variant="light" data-testid="sitemap-urlcount">
              {result.urlCount} URLs
            </Badge>
            {result.isIndex ? (
              <Badge color="violet" variant="light" data-testid="sitemap-index">
                sitemap index
              </Badge>
            ) : null}
          </Group>
        ) : null}
      </Stack>
    </ToolFrame>
  );
}