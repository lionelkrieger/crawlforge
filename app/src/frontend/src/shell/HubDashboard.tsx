/**
 * Main hub view (FS-CRAWL-001 §12: projects, dashboards, schedules).
 * Opens on local aggregates: stat cards, the bounded project window, and
 * the audit window with live counters from the hub. Project creation is a
 * real end-to-end flow (POST /api/projects → reload). First-run proceeds
 * directly to project creation — no entitlement screens (§12).
 * Each project row can start an audit from its default (first) configuration.
 *
 * @module shell/HubDashboard
 */

import { useCallback, useState } from 'react';
import {
  Badge,
  Button,
  Group,
  Modal,
  Paper,
  SimpleGrid,
  Stack,
  Table,
  Text,
  TextInput,
  Title
} from '@mantine/core';
import { useShell, useStoreSelector } from './ShellContext';
import { useLiveProgress } from '../store/liveProgress';
import { selectAudits, selectProjects } from '../store/selectors';
import { IconFolder, IconSpider, IconPlus, IconShield, IconChevronRight } from '../theme/icons';
import { formatCount, formatDateTime } from './format';
import type { AuditSummary } from '../contracts/api/audits';
import { ConfigurationEditor } from '../surfaces/project/ConfigurationEditor';
import {
  configurationToCreateRequest,
  defaultConfigurationForm,
  type ConfigurationFormValue
} from '../surfaces/project/configurationModel';

/** Audit state → badge color. */
export const STATE_COLORS: Record<string, string> = {
  queued: 'steel',
  running: 'signal',
  paused: 'yellow',
  completed: 'green',
  failed: 'red',
  cancelled: 'gray'
};

/** One stat card. */
function StatCard({ label, value, accent }: { label: string; value: string; accent?: string }) {
  return (
    <Paper className="cf-stat-card" withBorder p="md" pl="lg">
      <div className="cf-stat-label">{label}</div>
      <div className="cf-stat-value" style={accent ? { color: accent } : undefined}>
        {value}
      </div>
    </Paper>
  );
}

/**
 * Project creation modal — the locked TWO-call flow: POST /api/projects
 * {name, description?}, then POST /api/projects/{id}/configurations with
 * startUrls: [targetUrl] (crawl seeds live on the configuration, never on
 * the project). Real end-to-end creation with field-level error feedback.
 */
function CreateProjectModal({ opened, onClose }: { opened: boolean; onClose: () => void }) {
  const { services } = useShell();
  const [name, setName] = useState('');
  const [targetUrl, setTargetUrl] = useState('');
  const [description, setDescription] = useState('');
  // WP-09-11 (UI-04): the full 13-tab configuration editor is mounted in the
  // creation flow; the target URL input stays the seed authority and syncs
  // into the form's startUrls.
  const [form, setForm] = useState<ConfigurationFormValue>(() => defaultConfigurationForm());
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const updateTargetUrl = useCallback((value: string) => {
    setTargetUrl(value);
    setForm((current) => ({ ...current, startUrls: value.trim() ? [value.trim()] : [] }));
  }, []);

  const submit = useCallback(async () => {
    if (!services.api) {
      setError('Backend disconnected.');
      return;
    }
    if (!name.trim() || !targetUrl.trim()) {
      setError('Project name and target URL are required.');
      return;
    }
    setPending(true);
    setError(null);
    try {
      const created = await services.api.request('projects.create', {
        body: {
          name: name.trim(),
          ...(description.trim() ? { description: description.trim() } : {})
        }
      });
      if (created.error || !created.data) {
        const fieldReasons = created.error?.fieldErrors
          ? Object.entries(created.error.fieldErrors).map(([field, reasons]) => `${field}: ${reasons.join('; ')}`).join(' · ')
          : null;
        setError(fieldReasons ?? created.error?.message ?? 'Project creation failed.');
        return;
      }
      const projectId = created.data.id;
      const configured = await services.api.request('configurations.create', {
        params: { id: projectId },
        body: configurationToCreateRequest({
          ...form,
          name: 'Default configuration',
          startUrls: [targetUrl.trim()]
        })
      });
      if (configured.error) {
        const fieldReasons = configured.error.fieldErrors
          ? Object.entries(configured.error.fieldErrors).map(([field, reasons]) => `${field}: ${reasons.join('; ')}`).join(' · ')
          : null;
        setError(`Project created, configuration failed: ${fieldReasons ?? configured.error.message}`);
        return;
      }
      await services.actions.loadProjects();
      setName('');
      setTargetUrl('');
      setDescription('');
      setForm(defaultConfigurationForm());
      onClose();
    } catch {
      setError('Transport failure — is the backend running?');
    } finally {
      setPending(false);
    }
  }, [services, name, targetUrl, description, form, onClose]);

  return (
    <Modal opened={opened} onClose={onClose} title="New project" size="xl" data-testid="create-project-modal">
      <Stack gap="sm">
        <TextInput
          label="Project name"
          placeholder="Example Corp — production site"
          value={name}
          onChange={(event) => setName(event.currentTarget.value)}
          data-testid="project-name-input"
          required
        />
        <TextInput
          label="Target URL (crawl seed)"
          placeholder="https://example.com"
          value={targetUrl}
          onChange={(event) => updateTargetUrl(event.currentTarget.value)}
          data-testid="project-url-input"
          description="Stored as the default configuration's start URL."
          required
        />
        <TextInput
          label="Description (optional)"
          placeholder="What this project covers"
          value={description}
          onChange={(event) => setDescription(event.currentTarget.value)}
          data-testid="project-description-input"
        />
        <ConfigurationEditor value={form} onChange={setForm} />
        {error ? (
          <Text size="sm" c="var(--cf-danger)" data-testid="create-project-error">{error}</Text>
        ) : null}
        <Group justify="flex-end">
          <Button variant="subtle" color="steel" onClick={onClose}>Cancel</Button>
          <Button color="ember" onClick={() => void submit()} loading={pending} data-testid="create-project-submit">
            Create project
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

/** The audit row (live counters replace via the demultiplexer). */
function AuditRow({ audit, onOpen }: { audit: AuditSummary; onOpen: (id: string) => void }) {
  return (
    <Table.Tr data-testid={`audit-row-${audit.id}`}>
      <Table.Td>
        <Text size="sm" fw={600} ff="monospace">{audit.id}</Text>
      </Table.Td>
      <Table.Td>
        <Badge size="sm" variant="light" color={STATE_COLORS[audit.state] ?? 'steel'} data-testid={`audit-state-${audit.id}`}>
          {audit.state}
        </Badge>
        {audit.state === 'failed' && audit.errorMessage ? (
          <Text size="xs" c="var(--cf-danger)" data-testid={`audit-error-${audit.id}`} lineClamp={2}>
            {audit.errorMessage}
          </Text>
        ) : null}
      </Table.Td>
      <Table.Td><Text size="sm" c="dimmed">{audit.phase}</Text></Table.Td>
      <Table.Td className="cf-compact-delta">{formatCount(audit.counters.fetched)}</Table.Td>
      <Table.Td className="cf-compact-delta">{formatCount(audit.counters.queued)}</Table.Td>
      <Table.Td className="cf-compact-delta">{formatCount(audit.counters.error)}</Table.Td>
      <Table.Td className="cf-compact-delta">
        {formatCount(audit.counters.blocked)}
        {audit.counters.wafBlocked ? (
          <Text span size="xs" c="var(--cf-danger)"> ({audit.counters.wafBlocked} WAF)</Text>
        ) : null}
      </Table.Td>
      <Table.Td><Text size="xs" c="dimmed">{formatDateTime(audit.startedAt)}</Text></Table.Td>
      <Table.Td>
        <Button size="compact-xs" variant="subtle" color="signal" rightSection={<IconChevronRight size={12} />} onClick={() => onOpen(audit.id)} data-testid={`open-audit-${audit.id}`}>
          Open
        </Button>
      </Table.Td>
    </Table.Tr>
  );
}

/** The main hub dashboard. */
export function HubDashboard() {
  const { services, navigation } = useShell();
  const projects = useStoreSelector(selectProjects);
  const audits = useStoreSelector(selectAudits);
  const connectionState = useStoreSelector((state) => state.hub.connectionState);
  const wafTotals = useLiveProgress(services.live, (state) => state.wafCountsByAudit);
  const [createOpen, setCreateOpen] = useState(false);
  const [startingProjectId, setStartingProjectId] = useState<string | null>(null);
  const [startError, setStartError] = useState<string | null>(null);

  // Start an audit from the project's first configuration (created with the
  // project), then refresh the audit list and open the new audit.
  const startAudit = useCallback(async (projectId: string) => {
    setStartError(null);
    if (!services.api) {
      setStartError('Backend disconnected.');
      return;
    }
    setStartingProjectId(projectId);
    try {
      const configs = await services.api.request('configurations.list', { params: { id: projectId } });
      if (configs.error || !configs.data) {
        setStartError(configs.error?.message ?? 'Could not load the project configuration.');
        return;
      }
      const config = configs.data[0];
      if (!config) {
        setStartError('This project has no configuration to audit.');
        return;
      }
      const started = await services.api.request('audits.start', { body: { configurationId: config.id } });
      if (started.error || !started.data) {
        const fieldReasons = started.error?.fieldErrors
          ? Object.entries(started.error.fieldErrors).map(([field, reasons]) => `${field}: ${reasons.join('; ')}`).join(' · ')
          : null;
        setStartError(fieldReasons ?? started.error?.message ?? 'Audit start failed.');
        return;
      }
      await services.actions.loadAudits();
      navigation.openAudit(started.data.auditId);
    } catch {
      setStartError('Transport failure — is the backend running?');
    } finally {
      setStartingProjectId(null);
    }
  }, [services, navigation]);

  const running = audits.filter((audit) => audit.state === 'running').length;
  const totalFetched = audits.reduce((sum, audit) => sum + audit.counters.fetched, 0);
  const totalWafBlocked = Object.values(wafTotals).reduce(
    (sum, providers) => sum + Object.values(providers).reduce((inner, count) => inner + count, 0),
    0
  );

  return (
    <Stack gap="md" data-testid="hub-dashboard">
      <Group justify="space-between">
        <div>
          <Title order={3}>Main hub</Title>
          <Text size="sm" c="dimmed">
            Local aggregates only · {connectionState === 'Connected' ? 'hub connected' : `hub ${connectionState.toLowerCase()}`}
          </Text>
        </div>
        <Button color="ember" leftSection={<IconPlus size={15} />} onClick={() => setCreateOpen(true)} data-testid="new-project-button">
          New project
        </Button>
      </Group>

      <SimpleGrid cols={{ base: 2, md: 4 }} spacing="sm">
        <StatCard label="Projects" value={formatCount(projects.length)} />
        <StatCard label="Running audits" value={formatCount(running)} accent={running > 0 ? 'var(--mantine-color-signal-4)' : undefined} />
        <StatCard label="URLs fetched" value={formatCount(totalFetched)} />
        <StatCard label="WAF blocks (live)" value={formatCount(totalWafBlocked)} accent={totalWafBlocked > 0 ? 'var(--cf-danger)' : undefined} />
      </SimpleGrid>

      <Paper withBorder p="md">
        <Group gap={8} mb="sm">
          <IconFolder size={16} color="var(--mantine-color-ember-5)" />
          <Title order={4}>Projects</Title>
        </Group>
        {projects.length === 0 ? (
          <Text size="sm" c="dimmed" data-testid="projects-empty">
            No projects yet. Create your first project to start crawling — the first-run experience
            proceeds directly to project creation.
          </Text>
        ) : (
          <Table.ScrollContainer minWidth={520}>
            <Table verticalSpacing={6} data-testid="projects-table">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>Name</Table.Th>
                  <Table.Th>Description</Table.Th>
                  <Table.Th>Created</Table.Th>
                  <Table.Th />
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {projects.map((project) => (
                  <Table.Tr
                    key={project.id}
                    data-testid={`project-row-${project.id}`}
                    style={{ cursor: 'pointer' }}
                    onClick={() => navigation.openProject(project.id)}
                  >
                    <Table.Td>
                      <Text size="sm" fw={600}>{project.name}</Text>
                      {project.tags.length > 0 ? (
                        <Group gap={4} mt={2}>
                          {project.tags.map((tag) => (
                            <Badge key={tag} size="xs" variant="light" color="steel">{tag}</Badge>
                          ))}
                        </Group>
                      ) : null}
                    </Table.Td>
                    <Table.Td><Text size="sm" c="dimmed">{project.description ?? project.scopeSummary ?? '—'}</Text></Table.Td>
                    <Table.Td><Text size="xs" c="dimmed">{formatDateTime(project.createdAt)}</Text></Table.Td>
                    <Table.Td>
                      <Group gap={6} wrap="nowrap">
                        <Button
                          size="compact-xs"
                          variant="subtle"
                          color="steel"
                          onClick={(event) => {
                            event.stopPropagation();
                            navigation.openConfigurations(project.id);
                          }}
                          data-testid={`project-configurations-${project.id}`}
                        >
                          Configurations
                        </Button>
                        <Button
                          size="compact-xs"
                          color="ember"
                          leftSection={<IconSpider size={12} />}
                          loading={startingProjectId === project.id}
                          disabled={startingProjectId !== null}
                          onClick={(event) => {
                            event.stopPropagation();
                            void startAudit(project.id);
                          }}
                          data-testid={`start-audit-${project.id}`}
                        >
                          Start audit
                        </Button>
                      </Group>
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        )}
        {startError ? (
          <Text size="sm" c="red" mt="sm" data-testid="start-audit-error">{startError}</Text>
        ) : null}
      </Paper>

      <Paper withBorder p="md">
        <Group gap={8} mb="sm">
          <IconSpider size={16} color="var(--mantine-color-signal-4)" />
          <Title order={4}>Audits</Title>
          {totalWafBlocked > 0 ? (
            <Badge color="red" variant="light" size="sm" leftSection={<IconShield size={11} />}>
              {totalWafBlocked} WAF blocks
            </Badge>
          ) : null}
        </Group>
        {audits.length === 0 ? (
          <Text size="sm" c="dimmed" data-testid="audits-empty">
            No audits loaded. Audits appear here once started from a project configuration.
          </Text>
        ) : (
          <Table.ScrollContainer minWidth={760}>
            <Table verticalSpacing={6} data-testid="audits-table">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>ID</Table.Th>
                  <Table.Th>State</Table.Th>
                  <Table.Th>Phase</Table.Th>
                  <Table.Th>Fetched</Table.Th>
                  <Table.Th>Queued</Table.Th>
                  <Table.Th>Errors</Table.Th>
                  <Table.Th>Blocked</Table.Th>
                  <Table.Th>Started</Table.Th>
                  <Table.Th />
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {audits.map((audit) => (
                  <AuditRow key={audit.id} audit={audit} onOpen={navigation.openAudit} />
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        )}
      </Paper>

      <CreateProjectModal opened={createOpen} onClose={() => setCreateOpen(false)} />
    </Stack>
  );
}
