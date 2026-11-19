/**
 * Configurations list (WP-09-07, UI-03) — create/open/duplicate/delete via
 * REST CRUD (configurations.* routes), with the ConfigurationEditor embedded
 * for new/open configuration (UI-03 → UI-04 product path).
 *
 * Data access goes through the typed loopback client passed as a prop (the
 * shell owns the client instance); no shell imports. The "Configurations"
 * heading and the exact-text "New configuration" / "Edit" affordances match
 * the surface map and the walker's text-click contract.
 *
 * @module surfaces/project/ConfigurationsList
 */

import { useCallback, useEffect, useState } from 'react';
import { Alert, Button, Group, Stack, Table, Text, TextInput, Title } from '@mantine/core';
import type { LoopbackApiClient } from '../../contracts/api/client';
import type { ConfigurationSummary } from '../../contracts/api/projects';
import { ConfigurationEditor } from './ConfigurationEditor';
import type { ConfigurationFormValue } from './configurationModel';
import {
  configurationFormErrors,
  configurationFromWire,
  configurationToCreateRequest,
  configurationToUpdateRequest,
  defaultConfigurationForm
} from './configurationModel';

/** Configurations list props. */
export interface ConfigurationsListProps {
  /** Owning project identifier. */
  readonly projectId: string;
  /** Typed loopback API client (null = backend offline). */
  readonly api: LoopbackApiClient | null;
}

type EditorState =
  | { readonly kind: 'list' }
  | { readonly kind: 'new'; readonly form: ConfigurationFormValue }
  | { readonly kind: 'edit'; readonly id: string; readonly form: ConfigurationFormValue };

/** The project configurations surface. */
export function ConfigurationsList({ projectId, api }: ConfigurationsListProps) {
  const [configurations, setConfigurations] = useState<readonly ConfigurationSummary[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [search, setSearch] = useState('');
  const [editor, setEditor] = useState<EditorState>({ kind: 'list' });
  const [pending, setPending] = useState(false);
  const [confirmDeleteId, setConfirmDeleteId] = useState<string | null>(null);

  const reload = useCallback(async () => {
    if (!api) return;
    setLoading(true);
    setError(null);
    try {
      const envelope = await api.request('configurations.list', { params: { id: projectId }, query: { offset: 0, limit: 200 } });
      if (envelope.error) setError(envelope.error.message);
      else setConfigurations(envelope.data ?? []);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Failed to load configurations.');
    } finally {
      setLoading(false);
    }
  }, [api, projectId]);

  useEffect(() => {
    void reload();
  }, [reload]);

  const openNew = () => {
    setEditor({ kind: 'new', form: { ...defaultConfigurationForm(), name: 'New configuration' } });
    setError(null);
  };

  const openEdit = async (id: string) => {
    if (!api) return;
    setPending(true);
    setError(null);
    try {
      const envelope = await api.request('configurations.get', { params: { id } });
      if (envelope.error || !envelope.data) {
        setError(envelope.error?.message ?? `Configuration ${id} not found.`);
        return;
      }
      setEditor({ kind: 'edit', id, form: configurationFromWire(envelope.data) });
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Failed to open configuration.');
    } finally {
      setPending(false);
    }
  };

  const duplicate = async (id: string) => {
    if (!api) return;
    setPending(true);
    setError(null);
    try {
      const source = await api.request('configurations.get', { params: { id } });
      if (source.error || !source.data) {
        setError(source.error?.message ?? `Configuration ${id} not found.`);
        return;
      }
      const form = configurationFromWire(source.data);
      const created = await api.request('configurations.create', {
        params: { id: projectId },
        body: configurationToCreateRequest({ ...form, name: `${form.name} (copy)` })
      });
      if (created.error) setError(created.error.message);
      else await reload();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Failed to duplicate configuration.');
    } finally {
      setPending(false);
    }
  };

  const remove = async (id: string) => {
    if (!api) return;
    setPending(true);
    setError(null);
    try {
      const envelope = await api.request('configurations.delete', { params: { id } });
      if (envelope.error) setError(envelope.error.message);
      else {
        setConfirmDeleteId(null);
        await reload();
      }
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Failed to delete configuration.');
    } finally {
      setPending(false);
    }
  };

  const save = async () => {
    if (!api || editor.kind === 'list') return;
    const problems = configurationFormErrors(editor.form);
    if (Object.keys(problems).length > 0) {
      setError(Object.values(problems).join(' '));
      return;
    }
    setPending(true);
    setError(null);
    try {
      const envelope = editor.kind === 'new'
        ? await api.request('configurations.create', { params: { id: projectId }, body: configurationToCreateRequest(editor.form) })
        : await api.request('configurations.update', { params: { id: editor.id }, body: configurationToUpdateRequest(editor.form) });
      if (envelope.error) {
        setError(envelope.error.message);
        return;
      }
      setEditor({ kind: 'list' });
      await reload();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Failed to save configuration.');
    } finally {
      setPending(false);
    }
  };

  const visible = configurations.filter((configuration) =>
    search.trim().length === 0 || configuration.name.toLowerCase().includes(search.trim().toLowerCase())
  );

  return (
    <Stack gap="md" data-testid="configurations-list" data-project-id={projectId}>
      <Group justify="space-between" align="center">
        <Title order={3} data-testid="configurations-heading">Configurations</Title>
        <Button color="ember" onClick={openNew} disabled={!api || pending} data-testid="configurations-new">
          New configuration
        </Button>
      </Group>
      {!api ? (
        <Alert color="steel" title="Offline" data-testid="configurations-offline">
          Backend offline — configurations are unavailable until the local API is reachable.
        </Alert>
      ) : null}
      {error ? (
        <Alert color="red" title="Configuration error" data-testid="configurations-error">
          {error}
        </Alert>
      ) : null}
      {editor.kind !== 'list' ? (
        <Stack gap="sm" data-testid="configurations-editor-pane">
          <ConfigurationEditor
            key={editor.kind === 'edit' ? editor.id : 'new'}
            value={editor.form}
            onChange={(next) => setEditor(editor.kind === 'edit' ? { ...editor, form: next } : { kind: 'new', form: next })}
            projectId={projectId}
          />
          <Group>
            <Button color="ember" onClick={() => void save()} loading={pending} data-testid="configuration-save">
              Save configuration
            </Button>
            <Button variant="default" onClick={() => setEditor({ kind: 'list' })} disabled={pending} data-testid="configuration-cancel">
              Cancel
            </Button>
          </Group>
        </Stack>
      ) : (
        <>
          <TextInput
            placeholder="Search configurations"
            value={search}
            onChange={(event) => setSearch(event.currentTarget.value)}
            data-testid="configurations-search"
          />
          {loading ? (
            <Text size="sm" c="dimmed" data-testid="configurations-loading">Loading configurations…</Text>
          ) : visible.length === 0 ? (
            <Text size="sm" c="dimmed" data-testid="configurations-empty">
              No configurations yet — create one to define crawl scope, limits and analysis.
            </Text>
          ) : (
            <Table verticalSpacing={6} data-testid="configurations-table">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>Name</Table.Th>
                  <Table.Th>Description</Table.Th>
                  <Table.Th>Updated</Table.Th>
                  <Table.Th aria-label="Actions" />
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {visible.map((configuration) => (
                  <Table.Tr key={configuration.id} data-testid={`configuration-row-${configuration.id}`}>
                    <Table.Td>
                      <Text size="sm" fw={500} data-testid={`configuration-name-${configuration.id}`}>{configuration.name}</Text>
                    </Table.Td>
                    <Table.Td>
                      <Text size="xs" c="dimmed" lineClamp={1}>{configuration.description ?? '—'}</Text>
                    </Table.Td>
                    <Table.Td>
                      <Text size="xs" c="dimmed">{configuration.updatedAt}</Text>
                    </Table.Td>
                    <Table.Td>
                      <Group gap={4} wrap="nowrap">
                        <Button size="compact-xs" variant="subtle" onClick={() => void openEdit(configuration.id)} disabled={pending} data-testid={`configuration-edit-${configuration.id}`}>
                          Edit
                        </Button>
                        <Button size="compact-xs" variant="subtle" onClick={() => void duplicate(configuration.id)} disabled={pending} data-testid={`configuration-duplicate-${configuration.id}`}>
                          Duplicate
                        </Button>
                        {confirmDeleteId === configuration.id ? (
                          <Button size="compact-xs" color="red" variant="light" onClick={() => void remove(configuration.id)} disabled={pending} data-testid={`configuration-delete-confirm-${configuration.id}`}>
                            Confirm delete
                          </Button>
                        ) : (
                          <Button size="compact-xs" color="red" variant="subtle" onClick={() => setConfirmDeleteId(configuration.id)} disabled={pending} data-testid={`configuration-delete-${configuration.id}`}>
                            Delete
                          </Button>
                        )}
                      </Group>
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          )}
        </>
      )}
    </Stack>
  );
}
