/**
 * Export destinations management (FS-CRAWL-001 §8.5/§10.2, §12 settings):
 * user-owned destinations only — never vendor services. Credentials are
 * entered by the user, protected at rest, and never echoed back (the wire
 * carries a mask indicator). Subset-merge updates: absent fields keep
 * stored values, an empty secret clears it. The `enabled` switch (WP-06-A3)
 * toggles delivery — the engine skips disabled destinations.
 *
 * @module tools/DestinationsManager
 */

import { useCallback, useEffect, useState } from 'react';
import {
  ActionIcon,
  Badge,
  Button,
  Group,
  Paper,
  PasswordInput,
  Select,
  Stack,
  Switch,
  Table,
  Text,
  TextInput,
  Tooltip
} from '@mantine/core';
import { IconCheck, IconExport, IconPlay, IconPlus, IconRefresh, IconSettings, IconX } from '../theme/icons';
import { ToolFrame } from './shared/ToolFrame';
import { ToolErrorBanner } from './shared/ToolErrorBanner';
import { VerdictBadge } from './shared/VerdictBadge';
import { useToolCall } from './shared/useToolCall';
import type { Destination, DestinationTestResponse } from '../contracts/api/destinations';

const KIND_OPTIONS = [
  { value: 'local-folder', label: 'Local folder' },
  { value: 'ftp', label: 'FTP' },
  { value: 'sftp', label: 'SFTP' },
  { value: 'webhook', label: 'Webhook' },
  { value: 'other', label: 'Other' }
];

const KIND_COLORS: Record<string, string> = {
  'local-folder': 'signal',
  ftp: 'violet',
  sftp: 'violet',
  webhook: 'ember',
  other: 'steel'
};

interface FormState {
  readonly id: string | null;
  name: string;
  kind: string;
  target: string;
  username: string;
  secret: string;
  clearSecret: boolean;
}

const EMPTY_FORM: FormState = { id: null, name: '', kind: 'local-folder', target: '', username: '', secret: '', clearSecret: false };

/** The destinations management tool. */
export function DestinationsManager() {
  const { pending, error, fieldErrors, run } = useToolCall();
  const [items, setItems] = useState<readonly Destination[]>([]);
  const [form, setForm] = useState<FormState | null>(null);
  const [verdicts, setVerdicts] = useState<Readonly<Record<string, DestinationTestResponse>>>({});
  const [confirmDelete, setConfirmDelete] = useState<string | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);

  const load = useCallback(async () => {
    const data = await run('destinations.list', { query: { limit: 100 } });
    if (data) setItems(data);
  }, [run]);

  useEffect(() => {
    void load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const submit = async () => {
    if (!form) return;
    if (form.id === null) {
      const data = await run('destinations.create', {
        body: {
          name: form.name,
          kind: form.kind,
          target: form.target,
          ...(form.username ? { username: form.username } : {}),
          ...(form.secret ? { secret: form.secret } : {})
        }
      });
      if (data) {
        setForm(null);
        await load();
      }
    } else {
      const data = await run('destinations.update', {
        params: { id: form.id },
        body: {
          name: form.name,
          kind: form.kind,
          target: form.target,
          username: form.username || null,
          ...(form.clearSecret ? { secret: '' } : form.secret ? { secret: form.secret } : {})
        }
      });
      if (data) {
        setForm(null);
        await load();
      }
    }
  };

  const toggleEnabled = async (destination: Destination, enabled: boolean) => {
    setBusyId(destination.id);
    const data = await run('destinations.update', { params: { id: destination.id }, body: { enabled } });
    setBusyId(null);
    if (data) {
      setItems((current) => current.map((item) => (item.id === data.id ? data : item)));
    }
  };

  const test = async (destination: Destination) => {
    setBusyId(destination.id);
    const data = await run('destinations.test', { params: { id: destination.id } });
    setBusyId(null);
    if (data) setVerdicts((current) => ({ ...current, [destination.id]: data }));
  };

  const remove = async (id: string) => {
    const data = await run('destinations.delete', { params: { id } });
    setConfirmDelete(null);
    if (data) await load();
  };

  return (
    <ToolFrame
      testId="tool-destinations"
      icon={<IconExport size={17} />}
      title="Export Destinations"
      spec="§8.5 · §10.2"
      subtitle="User-owned targets only. Credentials stay local and masked; disabled destinations are skipped by delivery."
      actions={
        <Group gap={6}>
          <Button size="compact-xs" variant="subtle" color="steel" leftSection={<IconRefresh size={12} />} onClick={() => void load()} data-testid="destinations-refresh">
            Reload
          </Button>
          <Button size="compact-xs" color="ember" leftSection={<IconPlus size={12} />} onClick={() => setForm({ ...EMPTY_FORM })} data-testid="destinations-new">
            New destination
          </Button>
        </Group>
      }
    >
      <Stack gap="sm">
        <ToolErrorBanner error={error} fieldErrors={fieldErrors} testId="destinations-error" />

        {form ? (
          <Paper withBorder p="sm" className="cf-picker-capture" data-testid="destinations-form">
            <Text size="xs" fw={600} tt="uppercase" lts={0.5} c="dimmed" mb={8}>
              {form.id === null ? 'New destination' : `Edit destination ${form.id}`}
            </Text>
            <Stack gap={8}>
              <Group grow>
                <TextInput
                  label="Name"
                  size="xs"
                  value={form.name}
                  onChange={(event) => setForm({ ...form, name: event.currentTarget.value })}
                  data-testid="destination-name"
                />
                <Select
                  label="Kind"
                  size="xs"
                  data={KIND_OPTIONS}
                  value={form.kind}
                  onChange={(value) => setForm({ ...form, kind: value ?? 'local-folder' })}
                  data-testid="destination-kind"
                />
              </Group>
              <TextInput
                label="Target (path, host:port/path, or URL)"
                size="xs"
                ff="monospace"
                value={form.target}
                onChange={(event) => setForm({ ...form, target: event.currentTarget.value })}
                data-testid="destination-target"
              />
              <Group grow align="flex-end">
                <TextInput
                  label={form.kind === 'webhook' ? 'Username (not used for webhooks)' : 'Username'}
                  size="xs"
                  value={form.username}
                  onChange={(event) => setForm({ ...form, username: event.currentTarget.value })}
                  data-testid="destination-username"
                />
                <PasswordInput
                  label={form.kind === 'webhook' ? 'Token' : 'Password / secret'}
                  size="xs"
                  placeholder={form.id === null ? 'optional' : 'leave blank to keep stored'}
                  value={form.secret}
                  onChange={(event) => setForm({ ...form, secret: event.currentTarget.value })}
                  data-testid="destination-secret"
                />
              </Group>
              {form.id !== null ? (
                <Switch
                  size="xs"
                  label="Clear the stored secret"
                  checked={form.clearSecret}
                  onChange={(event) => setForm({ ...form, clearSecret: event.currentTarget.checked })}
                  data-testid="destination-clear-secret"
                />
              ) : null}
              <Group gap={6}>
                <Button size="xs" color="ember" loading={pending} onClick={() => void submit()} data-testid="destination-save">
                  {form.id === null ? 'Create' : 'Save changes'}
                </Button>
                <Button size="xs" variant="subtle" color="steel" onClick={() => setForm(null)} data-testid="destination-cancel">
                  Cancel
                </Button>
              </Group>
            </Stack>
          </Paper>
        ) : null}

        {items.length === 0 && !form ? (
          <Text size="xs" c="dimmed" fs="italic" data-testid="destinations-empty">
            No destinations configured — exports stay local until you add one.
          </Text>
        ) : (
          <Table verticalSpacing={8} className="cf-tool-table" data-testid="destinations-table">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Destination</Table.Th>
                <Table.Th>Target</Table.Th>
                <Table.Th>Credentials</Table.Th>
                <Table.Th>Enabled</Table.Th>
                <Table.Th>Connection</Table.Th>
                <Table.Th />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {items.map((destination) => {
                const verdict = verdicts[destination.id];
                const busy = busyId === destination.id;
                return (
                  <Table.Tr key={destination.id} data-testid={`destination-row-${destination.id}`} data-enabled={destination.enabled}>
                    <Table.Td>
                      <Stack gap={2}>
                        <Text size="sm" fw={600}>
                          {destination.name}
                        </Text>
                        <Badge size="xs" color={KIND_COLORS[destination.kind] ?? 'steel'} variant="light">
                          {destination.kind}
                        </Badge>
                      </Stack>
                    </Table.Td>
                    <Table.Td>
                      <Text size="xs" ff="monospace" c="dimmed" style={{ wordBreak: 'break-all' }}>
                        {destination.target}
                      </Text>
                    </Table.Td>
                    <Table.Td>
                      <Stack gap={2}>
                        <Text size="xs" c="dimmed">
                          {destination.username ?? '—'}
                        </Text>
                        {destination.secretMask ? (
                          <Tooltip label="A credential is stored, protected at rest — never shown in clear." openDelay={200}>
                            <Badge size="xs" variant="outline" color="steel" ff="monospace" data-testid={`destination-mask-${destination.id}`}>
                              {destination.secretMask}
                            </Badge>
                          </Tooltip>
                        ) : (
                          <Text size="xs" c="dimmed" fs="italic">
                            none
                          </Text>
                        )}
                      </Stack>
                    </Table.Td>
                    <Table.Td>
                      <Switch
                        size="xs"
                        checked={destination.enabled}
                        disabled={busy}
                        onChange={(event) => void toggleEnabled(destination, event.currentTarget.checked)}
                        data-testid={`destination-enabled-${destination.id}`}
                        aria-label={`Enable delivery to ${destination.name}`}
                      />
                    </Table.Td>
                    <Table.Td>
                      {verdict ? (
                        <Group gap={4}>
                          <VerdictBadge ok={verdict.reachable} okLabel="Reachable" failLabel="Unreachable" size="xs" testId={`destination-reachable-${destination.id}`} />
                          <VerdictBadge ok={verdict.credentialsValid} okLabel="Creds OK" failLabel="Creds bad" size="xs" testId={`destination-creds-${destination.id}`} />
                        </Group>
                      ) : (
                        <Button
                          size="compact-xs"
                          variant="light"
                          color="signal"
                          leftSection={<IconPlay size={11} />}
                          loading={busy}
                          onClick={() => void test(destination)}
                          data-testid={`destination-test-${destination.id}`}
                        >
                          Test
                        </Button>
                      )}
                    </Table.Td>
                    <Table.Td>
                      <Group gap={4} wrap="nowrap">
                        <ActionIcon
                          size="sm"
                          variant="subtle"
                          color="steel"
                          onClick={() =>
                            setForm({
                              id: destination.id,
                              name: destination.name,
                              kind: destination.kind,
                              target: destination.target,
                              username: destination.username ?? '',
                              secret: '',
                              clearSecret: false
                            })
                          }
                          aria-label={`Edit ${destination.name}`}
                          data-testid={`destination-edit-${destination.id}`}
                        >
                          <IconSettings size={13} />
                        </ActionIcon>
                        {confirmDelete === destination.id ? (
                          <>
                            <ActionIcon size="sm" variant="filled" color="red" onClick={() => void remove(destination.id)} aria-label="Confirm delete" data-testid={`destination-confirm-${destination.id}`}>
                              <IconCheck size={13} />
                            </ActionIcon>
                            <ActionIcon size="sm" variant="subtle" color="steel" onClick={() => setConfirmDelete(null)} aria-label="Cancel delete" data-testid={`destination-cancel-delete-${destination.id}`}>
                              <IconX size={13} />
                            </ActionIcon>
                          </>
                        ) : (
                          <ActionIcon
                            size="sm"
                            variant="subtle"
                            color="red"
                            onClick={() => setConfirmDelete(destination.id)}
                            aria-label={`Delete ${destination.name}`}
                            data-testid={`destination-delete-${destination.id}`}
                          >
                            <IconX size={13} />
                          </ActionIcon>
                        )}
                      </Group>
                    </Table.Td>
                  </Table.Tr>
                );
              })}
            </Table.Tbody>
          </Table>
        )}
      </Stack>
    </ToolFrame>
  );
}