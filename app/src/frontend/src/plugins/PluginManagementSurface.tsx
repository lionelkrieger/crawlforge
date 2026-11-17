/**
 * Plugin management surface (FS-CRAWL-002 §6.7).
 *
 * Registry table with load state + integrity status, enable/disable, rescan,
 * remove, workspace trust consent, collision warnings, and the grant review
 * panel (read-only data / UI contribution permissions via the locked REST
 * routes; network destination grants as local consent state). The
 * mutation-oriented permission is shown as locked and never offered (E5;
 * backend 400). Plugin errors are surfaced here — never thrown into the shell.
 *
 * Dark-first: rendered under the shell's MantineProvider + crawlForgeTheme.
 *
 * @module plugins/PluginManagementSurface
 */

import { useState, useSyncExternalStore } from 'react';
import {
  Alert,
  Badge,
  Button,
  Checkbox,
  Divider,
  Group,
  Stack,
  Switch,
  Table,
  Text,
  Title
} from '@mantine/core';
import type { PluginPermission } from '../contracts/api/plugins';
import type { PluginManager } from './pluginManager';
import type { PluginRegistry, UiPluginEntry } from './pluginRegistry';

/** Surface props: the registry plus an optional lifecycle manager. */
export interface PluginManagementSurfaceProps {
  readonly registry: PluginRegistry;
  readonly manager?: PluginManager;
}

const GRANTABLE_PERMISSIONS: readonly PluginPermission[] = ['read-data', 'ui-contributions'];

function stateColor(state: UiPluginEntry['state']): string {
  switch (state) {
    case 'loaded': return 'green';
    case 'failed': return 'red';
    case 'disabled': return 'gray';
    default: return 'yellow';
  }
}

function integrityColor(status: UiPluginEntry['integrity']['status']): string {
  switch (status) {
    case 'verified': return 'green';
    case 'changed': return 'red';
    default: return 'gray';
  }
}

/** The plugin management UI (registry + grant review + integrity status). */
export function PluginManagementSurface({ registry, manager }: PluginManagementSurfaceProps): React.JSX.Element {
  const snapshot = useSyncExternalStore(registry.subscribe, registry.getSnapshot);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [operationError, setOperationError] = useState<string | null>(null);
  const [rescanSummary, setRescanSummary] = useState<string | null>(null);
  const [destinationSelection, setDestinationSelection] = useState<readonly string[]>([]);

  const selected = snapshot.entries.find((entry) => entry.id === selectedId) ?? null;

  const run = async (operation: () => Promise<{ ok: boolean; error?: string }>): Promise<void> => {
    setOperationError(null);
    const result = await operation();
    if (!result.ok) setOperationError(result.error ?? 'operation failed');
  };

  const toggleEnabled = (entry: UiPluginEntry, enabled: boolean): void => {
    void run(async () => {
      const result = await registry.setEnabled(entry.id, enabled);
      if (result.ok && manager) {
        if (enabled) await manager.loadPlugin(entry.id);
        else manager.unload(entry.id);
      }
      return result;
    });
  };

  const rescan = (): void => {
    setRescanSummary(null);
    void run(async () => {
      const result = await registry.rescan();
      if (result.ok) setRescanSummary(`Rescan complete: ${result.added} added, ${result.removed} removed.`);
      return result;
    });
  };

  const remove = (entry: UiPluginEntry): void => {
    void run(async () => {
      const result = await registry.remove(entry.id);
      if (result.ok) {
        manager?.unload(entry.id);
        if (selectedId === entry.id) setSelectedId(null);
      }
      return result;
    });
  };

  const review = (entry: UiPluginEntry): void => {
    setSelectedId(entry.id);
    setDestinationSelection(entry.networkGrant.destinationRefs);
    setOperationError(null);
  };

  return (
    <Stack gap="md" data-testid="plugin-management-surface">
      <Group justify="space-between">
        <Title order={4}>Plugins</Title>
        <Button size="xs" variant="default" data-testid="plugin-rescan" onClick={rescan}>
          Rescan local directories
        </Button>
      </Group>

      <Text size="sm" c="dimmed">
        Local plugins only — no marketplace, no remote code, no accounts. Grants give read-only data
        access and UI contribution rights; network access requires explicit destination consent.
      </Text>

      {snapshot.warnings.map((warning, index) => (
        <Alert key={index} color="yellow" variant="light" data-testid={`plugin-warning-${index}`}>
          {warning}
        </Alert>
      ))}

      {operationError !== null && (
        <Alert color="red" variant="light" data-testid="operation-error">
          {operationError}
        </Alert>
      )}

      {rescanSummary !== null && (
        <Alert color="blue" variant="light" data-testid="rescan-summary">
          {rescanSummary}
        </Alert>
      )}

      <Table.ScrollContainer minWidth={640}>
        <Table striped highlightOnHover data-testid="plugin-table">
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Plugin</Table.Th>
              <Table.Th>Source</Table.Th>
              <Table.Th>State</Table.Th>
              <Table.Th>Integrity</Table.Th>
              <Table.Th>Enabled</Table.Th>
              <Table.Th>Actions</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {snapshot.entries.map((entry) => (
              <Table.Tr key={entry.id} data-testid={`plugin-row-${entry.id}`}>
                <Table.Td>
                  <Stack gap={2}>
                    <Text size="sm" fw={600}>{entry.manifest.name}</Text>
                    <Text size="xs" c="dimmed">{entry.id} · v{entry.manifest.version}</Text>
                    {entry.loadError !== null && (
                      <Text size="xs" c="red" data-testid={`plugin-error-${entry.id}`}>{entry.loadError}</Text>
                    )}
                  </Stack>
                </Table.Td>
                <Table.Td>
                  <Badge variant="light" color={entry.source === 'workspace' ? 'grape' : 'blue'}>
                    {entry.source}
                  </Badge>
                  {entry.collisionOverride && (
                    <Badge variant="light" color="yellow" ml={4} data-testid={`plugin-collision-${entry.id}`}>
                      overrides user
                    </Badge>
                  )}
                </Table.Td>
                <Table.Td>
                  <Badge variant="light" color={stateColor(entry.state)} data-testid={`plugin-state-${entry.id}`}>
                    {entry.state}
                  </Badge>
                </Table.Td>
                <Table.Td>
                  <Badge variant="light" color={integrityColor(entry.integrity.status)} data-testid={`plugin-integrity-${entry.id}`}>
                    {entry.integrity.status}
                  </Badge>
                </Table.Td>
                <Table.Td>
                  <Switch
                    size="sm"
                    checked={entry.enabled}
                    disabled={entry.state === 'disabled' && entry.loadError !== null}
                    data-testid={`plugin-toggle-${entry.id}`}
                    onChange={(event) => toggleEnabled(entry, event.currentTarget.checked)}
                  />
                </Table.Td>
                <Table.Td>
                  <Group gap={6}>
                    {!entry.trusted && entry.source === 'workspace' && (
                      <Button
                        size="compact-xs"
                        color="grape"
                        data-testid={`plugin-trust-${entry.id}`}
                        onClick={() => registry.trustWorkspacePlugin(entry.id)}
                      >
                        Trust workspace plugin
                      </Button>
                    )}
                    <Button size="compact-xs" variant="default" data-testid={`plugin-review-${entry.id}`} onClick={() => review(entry)}>
                      Grants
                    </Button>
                    <Button size="compact-xs" variant="light" color="red" data-testid={`plugin-remove-${entry.id}`} onClick={() => remove(entry)}>
                      Remove
                    </Button>
                  </Group>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </Table.ScrollContainer>

      {selected !== null && (
        <Stack gap="sm" data-testid="grant-panel">
          <Divider />
          <Title order={5}>Grant review — {selected.manifest.name}</Title>

          <Group gap="md">
            {GRANTABLE_PERMISSIONS.map((permission) => {
              const granted = selected.permissions.includes(permission);
              return (
                <Group key={permission} gap={6}>
                  <Text size="sm">{permission}</Text>
                  <Button
                    size="compact-xs"
                    variant="light"
                    color={granted ? 'red' : 'green'}
                    data-testid={`grant-toggle-${permission}`}
                    onClick={() => {
                      void run(() =>
                        granted
                          ? registry.revokePermission(selected.id, permission)
                          : registry.grantPermission(selected.id, permission)
                      );
                    }}
                  >
                    {granted ? 'Revoke' : 'Grant'}
                  </Button>
                </Group>
              );
            })}
            <Group gap={6}>
              <Text size="sm" c="dimmed">commands</Text>
              <Badge variant="light" color="gray" data-testid="grant-commands-locked">locked (400)</Badge>
            </Group>
          </Group>

          <Divider />
          <Text size="sm" fw={600}>Network access</Text>
          {selected.manifest.permissions?.network?.mode === 'destinations' ? (
            <Stack gap={6}>
              <Text size="xs" c="dimmed">
                Declared destination references: {selected.manifest.permissions.network.destinationRefs.join(', ')}
              </Text>
              {registry.destinationRules.map((rule) => (
                <Checkbox
                  key={rule.ref}
                  size="xs"
                  label={`${rule.ref} → ${rule.host}`}
                  checked={destinationSelection.includes(rule.ref)}
                  data-testid={`destination-checkbox-${rule.ref}`}
                  onChange={(event) => {
                    const checked = event.currentTarget.checked;
                    setDestinationSelection((current) =>
                      checked ? [...current, rule.ref] : current.filter((ref) => ref !== rule.ref)
                    );
                  }}
                />
              ))}
              <Group gap={6}>
                <Button
                  size="compact-xs"
                  color="green"
                  variant="light"
                  data-testid="network-grant-button"
                  onClick={() => {
                    const result = registry.grantNetwork(selected.id, destinationSelection);
                    setOperationError(result.ok ? null : result.error);
                  }}
                >
                  Grant network to selected destinations
                </Button>
                <Button
                  size="compact-xs"
                  color="red"
                  variant="light"
                  data-testid="network-revoke-button"
                  onClick={() => {
                    const result = registry.revokeNetwork(selected.id);
                    setOperationError(result.ok ? null : result.error);
                  }}
                >
                  Revoke network
                </Button>
                <Badge variant="light" color={selected.networkGrant.granted ? 'green' : 'gray'} data-testid="network-grant-state">
                  {selected.networkGrant.granted ? 'granted' : 'not granted'}
                </Badge>
              </Group>
            </Stack>
          ) : (
            <Text size="xs" c="dimmed" data-testid="network-none">No network access declared (mode: none).</Text>
          )}
        </Stack>
      )}
    </Stack>
  );
}