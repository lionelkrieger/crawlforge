/**
 * Project overview (WP-09-07, UI-02) — the surface opened by a hub project
 * row click. Sections: Configurations, Integrations, Segments, Audits (with
 * start-audit action), Logs, plus the storage/retention summary (UI-07
 * marker). Props-driven: the lead supplies project/audit/configuration data
 * and navigation callbacks; no shell imports, no data fetching.
 *
 * Walker contract (ui_walker.py, read-only): root testid `project-overview`;
 * "Configurations" AND "Integrations" present as text/buttons/testids. The
 * section nav buttons carry the EXACT section texts so text-click navigation
 * resolves to a real affordance.
 *
 * @module surfaces/project/ProjectOverview
 */

import { useState } from 'react';
import { Alert, Badge, Button, Group, Select, SimpleGrid, Stack, Table, Text, Title } from '@mantine/core';
import type { AuditSummary } from '../../contracts/api/audits';
import type { ConfigurationSummary, ProjectSummary } from '../../contracts/api/projects';

/** Overview navigation sections (surface map UI-03/05/06/07/17). */
export type OverviewSection = 'configurations' | 'integrations' | 'segments' | 'audits' | 'logs';

/** Storage summary (app settings storage options, read-only display). */
export interface OverviewStorage {
  readonly databaseLocation?: string | null;
  readonly contentStoreLocation?: string | null;
  readonly compressionEnabled?: boolean;
}

/** Retention policy display (pruning engine lands in phase 12, WP-12-10). */
export interface OverviewRetention {
  readonly keepLastAudits?: number | null;
  readonly keepDays?: number | null;
}

/** Project overview props. */
export interface ProjectOverviewProps {
  readonly project: ProjectSummary;
  readonly audits: readonly AuditSummary[];
  readonly configurations: readonly ConfigurationSummary[];
  readonly storage?: OverviewStorage | null;
  readonly retention?: OverviewRetention | null;
  readonly onNavigate?: (section: OverviewSection) => void;
  readonly onStartAudit?: (configurationId: string) => void;
  readonly onOpenAudit?: (auditId: string) => void;
  readonly startingAudit?: boolean;
  readonly startError?: string | null;
}

const STATE_COLORS: Record<string, string> = {
  queued: 'steel',
  running: 'blue',
  paused: 'yellow',
  completed: 'green',
  failed: 'red',
  cancelled: 'gray'
};

/** A section heading that is itself the navigation affordance (exact text). */
function SectionNav({ section, label, count, onNavigate }: {
  readonly section: OverviewSection;
  readonly label: string;
  readonly count?: number;
  readonly onNavigate?: (section: OverviewSection) => void;
}) {
  return (
    <Group gap="xs" align="center">
      <Button
        variant="subtle"
        size="compact-md"
        onClick={() => onNavigate?.(section)}
        data-testid={`overview-nav-${section}`}
        styles={{ root: { fontSize: 'var(--mantine-font-size-lg)', fontWeight: 600, paddingInline: 0 } }}
      >
        {label}
      </Button>
      {count !== undefined ? <Badge variant="light" color="steel" size="sm">{count}</Badge> : null}
    </Group>
  );
}

/** The project overview surface. */
export function ProjectOverview({
  project,
  audits,
  configurations,
  storage,
  retention,
  onNavigate,
  onStartAudit,
  onOpenAudit,
  startingAudit,
  startError
}: ProjectOverviewProps) {
  const [selectedConfiguration, setSelectedConfiguration] = useState<string | null>(null);
  const effectiveConfiguration = selectedConfiguration ?? configurations[0]?.id ?? null;
  const keepLast = retention?.keepLastAudits ?? null;
  const keepDays = retention?.keepDays ?? null;

  return (
    <Stack gap="lg" data-testid="project-overview" data-project-id={project.id}>
      <Stack gap={2}>
        <Title order={3} data-testid="overview-project-name">{project.name}</Title>
        {project.description ? <Text size="sm" c="dimmed">{project.description}</Text> : null}
        {project.scopeSummary ? <Text size="xs" c="dimmed" data-testid="overview-scope-summary">{project.scopeSummary}</Text> : null}
      </Stack>

      <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="lg">
        <Stack gap="xs" data-testid="overview-section-configurations">
          <SectionNav section="configurations" label="Configurations" count={configurations.length} onNavigate={onNavigate} />
          {configurations.length === 0 ? (
            <Text size="xs" c="dimmed">No configurations yet.</Text>
          ) : (
            <Group gap={6}>
              {configurations.map((configuration) => (
                <Badge key={configuration.id} variant="outline" color="steel" size="sm" data-testid={`overview-configuration-${configuration.id}`}>
                  {configuration.name}
                </Badge>
              ))}
            </Group>
          )}
        </Stack>

        <Stack gap="xs" data-testid="overview-section-integrations">
          <SectionNav section="integrations" label="Integrations" onNavigate={onNavigate} />
          <Text size="xs" c="dimmed" data-testid="overview-integrations-note">
            Google Search Console and GA4 connections — engine lands in phase 14. Available in a later build.
          </Text>
          <Group gap="xs">
            <Button size="compact-xs" variant="light" disabled data-testid="overview-connect-gsc">
              Connect Search Console
            </Button>
            <Button size="compact-xs" variant="light" disabled data-testid="overview-connect-ga4">
              Connect GA4
            </Button>
          </Group>
          <Text size="xs" c="dimmed">Status: not connected.</Text>
        </Stack>

        <Stack gap="xs" data-testid="overview-section-segments">
          <SectionNav section="segments" label="Segments" onNavigate={onNavigate} />
          <Text size="xs" c="dimmed" data-testid="overview-segments-note">
            URL segments for scoped reporting — engine lands in phase 12. Available in a later build.
          </Text>
        </Stack>

        <Stack gap="xs" data-testid="overview-section-logs">
          <SectionNav section="logs" label="Logs" onNavigate={onNavigate} />
          <Text size="xs" c="dimmed" data-testid="overview-logs-note">
            Server-log import and correlation — engine lands in phase 12. Available in a later build.
          </Text>
        </Stack>
      </SimpleGrid>

      <Stack gap="xs" data-testid="overview-section-audits">
        <SectionNav section="audits" label="Audits" count={audits.length} onNavigate={onNavigate} />
        <Group gap="xs" align="flex-end" wrap="wrap">
          <Select
            label="Configuration"
            placeholder={configurations.length === 0 ? 'No configurations' : 'Choose configuration'}
            value={effectiveConfiguration}
            data={configurations.map((configuration) => ({ value: configuration.id, label: configuration.name }))}
            onChange={(next) => setSelectedConfiguration(next)}
            disabled={configurations.length === 0}
            data-testid="overview-start-configuration"
            style={{ minWidth: 220 }}
          />
          <Button
            color="ember"
            onClick={() => effectiveConfiguration && onStartAudit?.(effectiveConfiguration)}
            disabled={!effectiveConfiguration || !onStartAudit}
            loading={startingAudit}
            data-testid="overview-start-audit"
          >
            Start audit
          </Button>
        </Group>
        {startError ? (
          <Alert color="red" title="Start failed" data-testid="overview-start-error">{startError}</Alert>
        ) : null}
        {audits.length === 0 ? (
          <Text size="sm" c="dimmed" data-testid="overview-audits-empty">No audits yet — start one with a configuration.</Text>
        ) : (
          <Table verticalSpacing={6} data-testid="overview-audits-table">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Audit</Table.Th>
                <Table.Th>Status</Table.Th>
                <Table.Th>Phase</Table.Th>
                <Table.Th>Fetched</Table.Th>
                <Table.Th>Started</Table.Th>
                <Table.Th aria-label="Actions" />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {audits.map((audit) => (
                <Table.Tr key={audit.id} data-testid={`overview-audit-row-${audit.id}`}>
                  <Table.Td><Text size="xs" c="dimmed">#{audit.id}</Text></Table.Td>
                  <Table.Td>
                    <Badge size="sm" variant="light" color={STATE_COLORS[audit.state] ?? 'steel'} data-testid={`overview-audit-state-${audit.id}`}>
                      {audit.state}
                    </Badge>
                  </Table.Td>
                  <Table.Td><Text size="xs">{audit.phase}</Text></Table.Td>
                  <Table.Td><Text size="xs">{audit.counters.fetched}</Text></Table.Td>
                  <Table.Td><Text size="xs" c="dimmed">{audit.startedAt}</Text></Table.Td>
                  <Table.Td>
                    <Button size="compact-xs" variant="subtle" onClick={() => onOpenAudit?.(audit.id)} data-testid={`overview-open-audit-${audit.id}`}>
                      Open
                    </Button>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        )}
      </Stack>

      <Stack gap="xs" data-testid="overview-section-storage">
        <Title order={5}>Storage and retention</Title>
        <Text size="xs" c="dimmed" data-testid="overview-storage-summary">
          Database: {storage?.databaseLocation ?? 'local embedded database'} · Content store: {storage?.contentStoreLocation ?? 'beside-database'} · Compression: {storage?.compressionEnabled === false ? 'off' : 'on'}
        </Text>
        <Text size="xs" data-testid="overview-retention-policy">
          Retention policy: {keepLast === null && keepDays === null
            ? 'keep all audits (no pruning configured)'
            : `keep ${keepLast !== null ? `the last ${keepLast} audits` : ''}${keepLast !== null && keepDays !== null ? ' / ' : ''}${keepDays !== null ? `audits from the last ${keepDays} days` : ''}`}
        </Text>
        <Text size="xs" c="dimmed" data-testid="overview-retention-note">
          Automatic retention pruning — engine lands in phase 12. Available in a later build.
        </Text>
      </Stack>
    </Stack>
  );
}
