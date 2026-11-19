/**
 * Settings surface (FS-CRAWL-001 §12 settings surfaces; local-only, zero
 * identity/account/entitlement payloads). Theme, status-bar advisories,
 * database provider + connection test, proxy display, MCP automation
 * interface (toggle/transport/token display), embedding provider + test,
 * diagnostics artifacts (inspect/purge), UI language. Every control issues
 * a real local API call with visible feedback.
 *
 * @module shell/SettingsSurface
 */

import { useCallback, useEffect, useState } from 'react';
import {
  Badge,
  Button,
  Group,
  NumberInput,
  Paper,
  Select,
  Stack,
  Switch,
  Table,
  Text,
  TextInput,
  Title
} from '@mantine/core';
import { useShell, useStoreSelector } from './ShellContext';
import { selectSettings } from '../store/selectors';
import type { AppSettings, DiagnosticArtifact } from '../contracts/api/system';
import { IconDatabase, IconPlug, IconShield, IconSettings, IconRefresh, IconX } from '../theme/icons';
import { formatBytes, formatDateTime } from './format';
import { ExportDestinationsSection } from '../surfaces/export/ExportDestinationsSection';

/** Section wrapper. */
function Section({ title, icon, children }: { title: string; icon: React.ReactNode; children: React.ReactNode }) {
  return (
    <Paper withBorder p="md">
      <Group gap={8} mb="sm">
        {icon}
        <Title order={4}>{title}</Title>
      </Group>
      {children}
    </Paper>
  );
}

/** The settings surface. */
export function SettingsSurface() {
  const { services } = useShell();
  const settings = useStoreSelector(selectSettings);
  const [feedback, setFeedback] = useState<string | null>(null);
  const [artifacts, setArtifacts] = useState<readonly DiagnosticArtifact[]>([]);
  const [workingSetAdvisory, setWorkingSetAdvisory] = useState<number>(2 * 1024 ** 3);
  const [walAdvisory, setWalAdvisory] = useState<number>(256 * 1024 ** 2);

  useEffect(() => {
    if (settings?.statusBar) {
      setWorkingSetAdvisory(settings.statusBar.workingSetAdvisoryBytes);
      setWalAdvisory(settings.statusBar.walAdvisoryBytes);
    }
  }, [settings?.statusBar]);

  const update = useCallback(
    async (patch: Record<string, unknown>, label: string) => {
      if (!services.api) {
        setFeedback('Backend disconnected.');
        return;
      }
      const envelope = await services.api.request('settings.update', { body: patch as never });
      if (envelope.error) {
        setFeedback(`${label}: ${envelope.error.message}`);
      } else {
        await services.actions.loadSettings();
        setFeedback(`${label} saved.`);
      }
    },
    [services]
  );

  const testDatabase = useCallback(async () => {
    if (!services.api || !settings) return;
    const envelope = await services.api.request('settings.databaseTest', {
      body: {
        provider: settings.database.provider,
        host: settings.database.host ?? undefined,
        port: settings.database.port ?? undefined,
        database: settings.database.database ?? undefined,
        username: settings.database.username ?? undefined
      }
    });
    if (envelope.error || !envelope.data) {
      setFeedback(`Database test: ${envelope.error?.message ?? 'failed'}`);
    } else {
      const result = envelope.data;
      setFeedback(
        `Database test: reachable=${result.reachable}, authenticated=${result.authenticated}, migration-compatible=${result.migrationCompatible}${result.message ? ` — ${result.message}` : ''}`
      );
    }
  }, [services, settings]);

  const testEmbedding = useCallback(async () => {
    if (!services.api || !settings) return;
    const envelope = await services.api.request('settings.embeddingTest', {
      body: {
        provider: settings.embeddings.provider,
        endpoint: settings.embeddings.endpoint ?? undefined,
        model: settings.embeddings.model ?? undefined
      } as never
    });
    setFeedback(envelope.error ? `Embedding test: ${envelope.error.message}` : 'Embedding provider reachable.');
  }, [services, settings]);

  const loadArtifacts = useCallback(async () => {
    if (!services.api) return;
    const envelope = await services.api.request('diagnostics.listArtifacts', { query: {} });
    if (!envelope.error && envelope.data) {
      setArtifacts(envelope.data as readonly DiagnosticArtifact[]);
    }
  }, [services]);

  const purgeArtifacts = useCallback(async () => {
    if (!services.api) return;
    const envelope = await services.api.request('diagnostics.purgeArtifacts', { query: {} });
    setFeedback(envelope.error ? `Purge: ${envelope.error.message}` : 'Local diagnostic artifacts purged.');
    await loadArtifacts();
  }, [services, loadArtifacts]);

  useEffect(() => {
    void loadArtifacts();
  }, [loadArtifacts]);

  if (!settings) {
    return (
      <Paper withBorder p="xl" data-testid="settings-empty">
        <Text size="sm" c="dimmed" ta="center">
          Settings load from the local backend. Connect the backend to configure the database,
          proxy, automation interface, and diagnostics.
        </Text>
      </Paper>
    );
  }

  return (
    <Stack gap="md" data-testid="settings-surface">
      <div>
        <Title order={3}>Settings</Title>
        <Text size="sm" c="dimmed">Local configuration only — no accounts, licenses, or remote services.</Text>
      </div>
      {feedback ? (
        <Paper withBorder p="xs" data-testid="settings-feedback">
          <Text size="sm" c="var(--mantine-color-signal-3)">{feedback}</Text>
        </Paper>
      ) : null}

      <Section title="Appearance" icon={<IconSettings size={16} color="var(--mantine-color-ember-5)" />}>
        <Stack gap="sm">
          <Group justify="space-between">
            <Text size="sm">Dark theme (dark-first)</Text>
            <Switch
              checked={settings.theme === 'dark'}
              onChange={(event) => void update({ theme: event.currentTarget.checked ? 'dark' : 'light' }, 'Theme')}
              data-testid="theme-switch"
              label={settings.theme}
            />
          </Group>
          <Select
            label="UI language"
            value={settings.uiLanguage}
            data={['en', 'de', 'fr', 'es', 'ja', 'zh']}
            onChange={(value) => value && void update({ uiLanguage: value }, 'Language')}
            data-testid="language-select"
            allowDeselect={false}
            w={220}
          />
        </Stack>
      </Section>

      <Section title="Status-bar advisories" icon={<IconShield size={16} color="var(--mantine-color-yellow-4)" />}>
        <Stack gap="sm">
          <Text size="xs" c="dimmed">
            Advisory thresholds color the status bar; they never trigger automatic destructive actions.
          </Text>
          <Group grow>
            <NumberInput
              label="Working-set advisory (bytes)"
              value={workingSetAdvisory}
              onChange={(value) => setWorkingSetAdvisory(typeof value === 'number' ? value : 0)}
              min={1}
              data-testid="ws-advisory-input"
            />
            <NumberInput
              label="WAL advisory (bytes)"
              value={walAdvisory}
              onChange={(value) => setWalAdvisory(typeof value === 'number' ? value : 0)}
              min={1}
              data-testid="wal-advisory-input"
            />
          </Group>
          <Group>
            <Button
              size="xs"
              color="ember"
              variant="light"
              data-testid="save-advisories"
              onClick={() =>
                void update(
                  { statusBar: { workingSetAdvisoryBytes: workingSetAdvisory, walAdvisoryBytes: walAdvisory } },
                  'Advisories'
                )
              }
            >
              Save advisories ({formatBytes(workingSetAdvisory)} / {formatBytes(walAdvisory)})
            </Button>
          </Group>
        </Stack>
      </Section>

      <Section title="Database" icon={<IconDatabase size={16} color="var(--mantine-color-signal-4)" />}>
        <Stack gap="sm">
          <Group gap="sm">
            <Badge variant="light" color="signal" size="lg">{settings.database.provider}</Badge>
            {settings.database.provider === 'mysql' ? (
              <Text size="sm" c="dimmed" ff="monospace">
                {settings.database.username ?? '—'}@{settings.database.host ?? '—'}:{settings.database.port ?? '—'}/{settings.database.database ?? '—'}
              </Text>
            ) : (
              <Text size="sm" c="dimmed">Embedded SQLite · WAL mode · local file</Text>
            )}
          </Group>
          <Button size="xs" variant="light" color="signal" onClick={() => void testDatabase()} data-testid="db-test-button">
            Test connection
          </Button>
        </Stack>
      </Section>

      <Section title="Automation interface (MCP)" icon={<IconPlug size={16} color="var(--mantine-color-grape-4)" />}>
        <Stack gap="sm">
          <Group justify="space-between">
            <Text size="sm">Enable local MCP server</Text>
            <Switch
              checked={settings.mcp.enabled}
              onChange={(event) => void update({ mcp: { ...settings.mcp, enabled: event.currentTarget.checked } }, 'MCP')}
              data-testid="mcp-switch"
            />
          </Group>
          <Select
            label="Transport"
            value={settings.mcp.transport}
            data={['sse', 'stdio']}
            onChange={(value) => value && void update({ mcp: { ...settings.mcp, transport: value } }, 'MCP transport')}
            data-testid="mcp-transport"
            allowDeselect={false}
            w={220}
          />
          <TextInput
            label="Automation token (local credential)"
            value={settings.mcp.token ?? ''}
            readOnly
            ff="monospace"
            data-testid="mcp-token"
            description="Displayed locally; rotate by updating the token field."
          />
        </Stack>
      </Section>

      <Section title="Embeddings" icon={<IconDatabase size={16} color="var(--mantine-color-pink-4)" />}>
        <Stack gap="sm">
          <Group gap="sm">
            <Badge variant="light" color="pink" size="lg">{settings.embeddings.provider}</Badge>
            <Text size="sm" c="dimmed">{settings.embeddings.model ?? 'default model'}</Text>
          </Group>
          <Button size="xs" variant="light" color="pink" onClick={() => void testEmbedding()} data-testid="embedding-test-button">
            Test provider
          </Button>
        </Stack>
      </Section>

      <Section title="Proxy" icon={<IconShield size={16} color="var(--mantine-color-night-2)" />}>
        <Text size="sm" c="dimmed">
          {settings.proxy.enabled
            ? `Enabled · ${settings.proxy.host ?? '—'}:${settings.proxy.port ?? '—'} (credentials masked)`
            : 'Disabled — direct connections.'}
        </Text>
      </Section>

      {/* WP-09-11 (UI-21): user-owned export delivery targets, off by default. */}
      <ExportDestinationsSection />

      <Section title="Diagnostics" icon={<IconRefresh size={16} color="var(--mantine-color-lime-4)" />}>
        <Stack gap="sm">
          <Text size="xs" c="dimmed">
            Local structured logs and native minidumps only. No remote diagnostics ever leave this machine.
          </Text>
          {artifacts.length === 0 ? (
            <Text size="sm" c="dimmed">No diagnostic artifacts.</Text>
          ) : (
            <Table verticalSpacing={4} data-testid="artifacts-table">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>Kind</Table.Th>
                  <Table.Th>File</Table.Th>
                  <Table.Th>Created</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {artifacts.map((artifact) => (
                  <Table.Tr key={artifact.id}>
                    <Table.Td><Badge size="xs" variant="light" color={artifact.kind === 'minidump' ? 'red' : 'steel'}>{artifact.kind}</Badge></Table.Td>
                    <Table.Td><Text size="xs" ff="monospace">{artifact.fileName}</Text></Table.Td>
                    <Table.Td><Text size="xs" c="dimmed">{formatDateTime(artifact.createdAt)}</Text></Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          )}
          <Group>
            <Button size="xs" variant="light" color="steel" onClick={() => void loadArtifacts()} data-testid="artifacts-refresh">
              Refresh
            </Button>
            <Button size="xs" variant="light" color="red" leftSection={<IconX size={12} />} onClick={() => void purgeArtifacts()} data-testid="artifacts-purge">
              Purge artifacts
            </Button>
          </Group>
        </Stack>
      </Section>
    </Stack>
  );
}

/** Settings type re-export for consumers. */
export type { AppSettings };
