/**
 * Export destinations settings section (WP-09-10, UI-21; FS-CRAWL-001 §8.5).
 *
 * CRUD + enable/disable + test-connection over the frozen destinations wire
 * (subset-merge PUT; empty secret clears; secrets are write-only and only a
 * mask is ever displayed). Destinations are OFF by default — a disabled
 * destination is never offered for delivery and never contacted. The heading
 * is exactly "Export destinations" (UI-21 binding).
 *
 * @module surfaces/export/ExportDestinationsSection
 */

import { useCallback, useEffect, useState } from 'react';
import { Alert, Badge, Button, Checkbox, Group, Paper, Select, Stack, Switch, Table, Text, TextInput, Title } from '@mantine/core';
import type { Destination, DestinationUpdateRequest } from '../../contracts/api/destinations';
import { useShell } from '../../shell/ShellContext';
import { useToolCall } from '../../tools/shared/useToolCall';
import { IconCheck, IconPlus, IconRefresh, IconSettings, IconX } from '../../theme/icons';

/** Destination kinds offered (§8.5 classes; webhook kept for wire parity). */
const KIND_OPTIONS = [
  { value: 'local-folder', label: 'Local folder' },
  { value: 'ftp', label: 'FTP' },
  { value: 'sftp', label: 'SFTP' },
  { value: 'webhook', label: 'Webhook' },
  { value: 'other', label: 'Other' }
];

/** Form state for create/edit. */
interface FormState {
  readonly id: string | null;
  readonly name: string;
  readonly kind: string;
  readonly target: string;
  readonly username: string;
  readonly secret: string;
  readonly clearSecret: boolean;
}

const EMPTY_FORM: FormState = { id: null, name: '', kind: 'local-folder', target: '', username: '', secret: '', clearSecret: false };

/** Per-destination test verdict. */
interface Verdict {
  readonly reachable: boolean;
  readonly credentialsValid: boolean;
  readonly message: string | null;
}

/** Settings section: "Export destinations" (UI-21). */
export function ExportDestinationsSection(): React.JSX.Element {
  const { services } = useShell();
  const call = useToolCall();
  const [destinations, setDestinations] = useState<readonly Destination[]>([]);
  const [form, setForm] = useState<FormState | null>(null);
  const [confirmDelete, setConfirmDelete] = useState<string | null>(null);
  const [verdicts, setVerdicts] = useState<Readonly<Record<string, Verdict>>>({});

  const refresh = useCallback(async () => {
    const list = await call.run('destinations.list', { query: { limit: 100 } });
    if (list) setDestinations(list);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    void refresh();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const submit = useCallback(async () => {
    if (!form) return;
    if (!form.id) {
      const created = await call.run('destinations.create', {
        body: {
          name: form.name,
          kind: form.kind,
          target: form.target,
          ...(form.username ? { username: form.username } : {}),
          ...(form.secret ? { secret: form.secret } : {})
        }
      });
      if (created) {
        setForm(null);
        await refresh();
      }
      return;
    }

    // Subset merge: only changed fields travel; absent keeps stored values.
    const stored = destinations.find((destination) => destination.id === form.id);
    const body: Record<string, unknown> = {};
    if (stored && form.name !== stored.name) body.name = form.name;
    if (stored && form.kind !== stored.kind) body.kind = form.kind;
    if (stored && form.target !== stored.target) body.target = form.target;
    if (stored && form.username !== (stored.username ?? '')) body.username = form.username || null;
    if (form.clearSecret) body.secret = '';
    else if (form.secret) body.secret = form.secret;

    const updated = await call.run('destinations.update', {
      params: { id: form.id },
      body: body as DestinationUpdateRequest
    });
    if (updated) {
      setForm(null);
      await refresh();
    }
  }, [form, destinations, call, refresh]);

  const toggleEnabled = useCallback(
    async (destination: Destination, enabled: boolean) => {
      const updated = await call.run('destinations.update', { params: { id: destination.id }, body: { enabled } });
      if (updated) setDestinations((current) => current.map((entry) => (entry.id === updated.id ? updated : entry)));
    },
    [call]
  );

  const remove = useCallback(
    async (id: string) => {
      const confirmation = await call.run('destinations.delete', { params: { id } });
      if (confirmation) {
        setConfirmDelete(null);
        await refresh();
      }
    },
    [call, refresh]
  );

  const test = useCallback(
    async (id: string) => {
      const verdict = await call.run('destinations.test', { params: { id } });
      if (verdict) setVerdicts((current) => ({ ...current, [id]: verdict }));
    },
    [call]
  );

  const startEdit = useCallback((destination: Destination) => {
    setConfirmDelete(null);
    setForm({
      id: destination.id,
      name: destination.name,
      kind: destination.kind,
      target: destination.target,
      username: destination.username ?? '',
      secret: '',
      clearSecret: false
    });
  }, []);

  if (!services.api) {
    return (
      <Paper withBorder p="md" data-testid="export-destinations-section">
        <Title order={3}>Export destinations</Title>
        <Alert color="yellow" mt="sm" data-testid="export-destinations-offline" title="Backend disconnected">
          Destination management needs the local backend.
        </Alert>
      </Paper>
    );
  }

  return (
    <Paper withBorder p="md" data-testid="export-destinations-section">
      <Stack gap="sm">
        <Group justify="space-between">
          <Title order={3}>Export destinations</Title>
          <Group gap="xs">
            <Button variant="default" size="xs" leftSection={<IconRefresh size={14} />} onClick={() => void refresh()} data-testid="export-destinations-refresh">
              Refresh
            </Button>
            <Button
              size="xs"
              leftSection={<IconPlus size={14} />}
              onClick={() => {
                setConfirmDelete(null);
                setForm({ ...EMPTY_FORM });
              }}
              data-testid="export-destinations-new"
            >
              New destination
            </Button>
          </Group>
        </Group>
        <Text size="sm" c="dimmed">
          User-configured delivery targets (§8.5). Destinations are disabled until you enable them; credentials are stored
          encrypted and never shown again.
        </Text>

        {call.error ? (
          <Alert color="red" data-testid="export-destinations-error" title="Destination operation failed">
            {call.error}
          </Alert>
        ) : null}

        {form ? (
          <Paper withBorder p="sm" data-testid="export-destination-form">
            <Stack gap="xs">
              <TextInput
                label="Name"
                value={form.name}
                onChange={(event) => setForm({ ...form, name: event.currentTarget.value })}
                data-testid="export-destination-name"
              />
              <Select
                label="Kind"
                data={KIND_OPTIONS}
                value={form.kind}
                onChange={(value) => setForm({ ...form, kind: value ?? 'local-folder' })}
                allowDeselect={false}
                data-testid="export-destination-kind"
              />
              <TextInput
                label={form.kind === 'webhook' ? 'Webhook URL' : 'Target (path / host)'}
                value={form.target}
                onChange={(event) => setForm({ ...form, target: event.currentTarget.value })}
                data-testid="export-destination-target"
              />
              <TextInput
                label="Username (optional)"
                value={form.username}
                onChange={(event) => setForm({ ...form, username: event.currentTarget.value })}
                data-testid="export-destination-username"
              />
              <TextInput
                label={form.kind === 'webhook' ? 'Token' : 'Password / secret'}
                type="password"
                value={form.secret}
                onChange={(event) => setForm({ ...form, secret: event.currentTarget.value })}
                data-testid="export-destination-secret"
              />
              {form.id ? (
                <Checkbox
                  label="Clear the stored secret"
                  checked={form.clearSecret}
                  onChange={(event) => setForm({ ...form, clearSecret: event.currentTarget.checked })}
                  data-testid="export-destination-clear-secret"
                />
              ) : null}
              <Group justify="flex-end" gap="xs">
                <Button variant="default" onClick={() => setForm(null)} data-testid="export-destination-cancel">
                  Cancel
                </Button>
                <Button onClick={() => void submit()} loading={call.pending} data-testid="export-destination-save">
                  {form.id ? 'Save changes' : 'Create destination'}
                </Button>
              </Group>
            </Stack>
          </Paper>
        ) : null}

        <Table data-testid="export-destinations-table">
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Name</Table.Th>
              <Table.Th>Kind</Table.Th>
              <Table.Th>Target</Table.Th>
              <Table.Th>Credential</Table.Th>
              <Table.Th>Enabled</Table.Th>
              <Table.Th>Actions</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {destinations.map((destination) => {
              const verdict = verdicts[destination.id];
              return (
                <Table.Tr key={destination.id} data-testid={`export-destination-row-${destination.id}`} data-enabled={destination.enabled}>
                  <Table.Td>{destination.name}</Table.Td>
                  <Table.Td>{destination.kind}</Table.Td>
                  <Table.Td>{destination.target}</Table.Td>
                  <Table.Td>
                    {destination.secretMask ? (
                      <Badge variant="light" data-testid={`export-destination-mask-${destination.id}`}>
                        {destination.secretMask}
                      </Badge>
                    ) : (
                      <Text size="sm" c="dimmed">
                        none
                      </Text>
                    )}
                  </Table.Td>
                  <Table.Td>
                    <Switch
                      checked={destination.enabled}
                      onChange={(event) => void toggleEnabled(destination, event.currentTarget.checked)}
                      data-testid={`export-destination-enabled-${destination.id}`}
                    />
                  </Table.Td>
                  <Table.Td>
                    <Group gap={4} wrap="nowrap">
                      <Button size="compact-xs" variant="subtle" onClick={() => void test(destination.id)} data-testid={`export-destination-test-${destination.id}`}>
                        Test
                      </Button>
                      <Button size="compact-xs" variant="subtle" leftSection={<IconSettings size={14} />} onClick={() => startEdit(destination)} data-testid={`export-destination-edit-${destination.id}`}>
                        Edit
                      </Button>
                      {confirmDelete === destination.id ? (
                        <>
                          <Button size="compact-xs" color="red" leftSection={<IconCheck size={14} />} onClick={() => void remove(destination.id)} data-testid={`export-destination-confirm-${destination.id}`}>
                            Confirm
                          </Button>
                          <Button size="compact-xs" variant="default" onClick={() => setConfirmDelete(null)} data-testid={`export-destination-cancel-delete-${destination.id}`}>
                            Keep
                          </Button>
                        </>
                      ) : (
                        <Button size="compact-xs" color="red" variant="subtle" leftSection={<IconX size={14} />} onClick={() => setConfirmDelete(destination.id)} data-testid={`export-destination-delete-${destination.id}`}>
                          Delete
                        </Button>
                      )}
                      {verdict ? (
                        <>
                          <Badge color={verdict.reachable ? 'green' : 'red'} data-testid={`export-destination-reachable-${destination.id}`} data-verdict={verdict.reachable ? 'ok' : 'fail'}>
                            {verdict.reachable ? 'reachable' : 'unreachable'}
                          </Badge>
                          <Badge color={verdict.credentialsValid ? 'green' : 'red'} data-testid={`export-destination-creds-${destination.id}`} data-verdict={verdict.credentialsValid ? 'ok' : 'fail'}>
                            {verdict.credentialsValid ? 'credentials ok' : 'credentials invalid'}
                          </Badge>
                        </>
                      ) : null}
                    </Group>
                  </Table.Td>
                </Table.Tr>
              );
            })}
          </Table.Tbody>
        </Table>
        {destinations.length === 0 ? (
          <Text size="sm" c="dimmed" data-testid="export-destinations-empty">
            No destinations configured — exports download locally.
          </Text>
        ) : null}
      </Stack>
    </Paper>
  );
}