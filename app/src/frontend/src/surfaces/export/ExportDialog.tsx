/**
 * Export dialog (WP-09-10, UI-20; FS-CRAWL-001 §8.4/§11.8).
 *
 * Mountable from every report surface: format selection (baseline + additive
 * agent/page-content formats), dataset scope, per-dataset column selection
 * (static catalog + dynamic extraction columns fetched live), local download,
 * and optional delivery to a user-configured ENABLED destination (§8.5 —
 * destinations are off by default and never required for a local export).
 *
 * Additive wire notes (lead handoff): the shared ExportCreateRequest union
 * carries only baseline formats; this dialog sends additive format keys and
 * an optional `columns` map that the server ignores until the Api amendment
 * lands. Job completion is observed by bounded polling of the non-enveloped
 * file route until the additive `exports.get` route exists.
 *
 * @module surfaces/export/ExportDialog
 */

import { useCallback, useEffect, useRef, useState } from 'react';
import { Alert, Badge, Button, Checkbox, Group, Loader, Modal, Paper, Select, Stack, Text } from '@mantine/core';
import type { EntityId } from '../../contracts/common/primitives';
import type { ExportCreateRequest } from '../../contracts/api/exports';
import type { Destination } from '../../contracts/api/destinations';
import { useShell } from '../../shell/ShellContext';
import { useToolCall } from '../../tools/shared/useToolCall';
import { IconExport, IconRefresh } from '../../theme/icons';
import {
  DEFAULT_DATASETS,
  EXPORT_DATASETS,
  EXPORT_FORMATS,
  PAGE_CONTENT_FORMATS,
  STATIC_DATASET_COLUMNS,
  extensionFor,
  type ExportColumnOption,
  type ExportFormatKey
} from './exportCatalog';

/** Dialog props: the audit to export plus open/close control. */
export interface ExportDialogProps {
  readonly auditId: EntityId;
  readonly open: boolean;
  readonly onClose: () => void;
}

/** Dialog lifecycle phase. */
type Phase = 'configure' | 'creating' | 'polling' | 'delivery' | 'ready';

const POLL_ATTEMPTS = 40;
const POLL_DELAY_MS = 500;

/** Mountable export dialog (UI-20). */
export function ExportDialog({ auditId, open, onClose }: ExportDialogProps): React.JSX.Element {
  const { services } = useShell();
  const call = useToolCall();
  const [format, setFormat] = useState<ExportFormatKey>('xlsx');
  const [datasets, setDatasets] = useState<readonly string[]>([...DEFAULT_DATASETS]);
  /** null/absent = all columns; an array = explicit selection. */
  const [columnSelection, setColumnSelection] = useState<Readonly<Record<string, readonly string[] | null>>>({});
  const [dynamicColumns, setDynamicColumns] = useState<Readonly<Record<string, readonly ExportColumnOption[]>>>({});
  const [destinations, setDestinations] = useState<readonly Destination[]>([]);
  const [destinationId, setDestinationId] = useState<string | null>(null);
  const [phase, setPhase] = useState<Phase>('configure');
  const [exportId, setExportId] = useState<EntityId | null>(null);
  const [pollError, setPollError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  const active = useRef(true);

  const isPageContent = PAGE_CONTENT_FORMATS.includes(format);

  // Reset + load enabled destinations whenever the dialog opens.
  useEffect(() => {
    if (!open) return;
    active.current = true;
    setPhase('configure');
    setExportId(null);
    setPollError(null);
    setSaved(false);
    call.reset();
    void (async () => {
      const list = await call.run('destinations.list', { query: { limit: 100 } });
      if (active.current && list) setDestinations(list.filter((destination) => destination.enabled));
    })();
    return () => {
      active.current = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, auditId]);

  // Dynamic extraction columns: fetch the effective column order live.
  const wantsExtraction = datasets.includes('custom-extraction');
  useEffect(() => {
    if (!open || !wantsExtraction || dynamicColumns['custom-extraction']) return;
    void (async () => {
      const response = await call.run('audits.listExtractedContent', { params: { id: auditId }, query: { limit: 1 } });
      if (active.current && response) {
        setDynamicColumns((current) => ({
          ...current,
          'custom-extraction': response.columns.map((key) => ({ key, label: key }))
        }));
      }
    })();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, wantsExtraction, auditId]);

  const columnsFor = useCallback(
    (dataset: string): readonly ExportColumnOption[] => STATIC_DATASET_COLUMNS[dataset] ?? dynamicColumns[dataset] ?? [],
    [dynamicColumns]
  );

  const toggleDataset = useCallback((key: string) => {
    setDatasets((current) => (current.includes(key) ? current.filter((entry) => entry !== key) : [...current, key]));
  }, []);

  const toggleColumn = useCallback(
    (dataset: string, key: string) => {
      const all = columnsFor(dataset).map((column) => column.key);
      setColumnSelection((current) => {
        const selected = current[dataset] ?? all;
        const next = selected.includes(key) ? selected.filter((entry) => entry !== key) : [...selected, key];
        return { ...current, [dataset]: next.length === all.length ? null : next };
      });
    },
    [columnsFor]
  );

  const saveBlob = useCallback(
    (blob: Blob, id: EntityId) => {
      if (typeof URL.createObjectURL !== 'function') return;
      const url = URL.createObjectURL(blob);
      const anchor = document.createElement('a');
      anchor.href = url;
      anchor.download = `crawlforge-export-${id}${extensionFor(format)}`;
      document.body.appendChild(anchor);
      anchor.click();
      anchor.remove();
      URL.revokeObjectURL(url);
      setSaved(true);
    },
    [format]
  );

  /** Bounded polling of the file route until the artifact exists (handoff: exports.get). */
  const pollAndDownload = useCallback(
    async (id: EntityId) => {
      if (!services.api) return;
      setPollError(null);
      for (let attempt = 0; attempt < POLL_ATTEMPTS; attempt += 1) {
        if (!active.current) return;
        try {
          const blob = (await services.api.request('exports.download', { params: { id } })) as Blob;
          if (!active.current) return;
          saveBlob(blob, id);
          setPhase('ready');
          return;
        } catch {
          // Not ready yet (job running) — bounded retry.
          await new Promise((resolve) => setTimeout(resolve, POLL_DELAY_MS));
        }
      }
      if (active.current) setPollError('The export is taking longer than expected — use Download to retry.');
    },
    [services, saveBlob]
  );

  const start = useCallback(async () => {
    setPhase('creating');
    const body: Record<string, unknown> = { auditId, format, datasets: isPageContent ? [] : datasets };
    if (destinationId) body.destinationId = destinationId;
    // Additive column-selection map (server ignores until the Api handoff).
    const columns: Record<string, readonly string[]> = {};
    for (const dataset of datasets) {
      const selection = columnSelection[dataset];
      if (selection) columns[dataset] = selection;
    }
    if (Object.keys(columns).length > 0) body.columns = columns;

    const created = await call.run('exports.create', { body: body as unknown as ExportCreateRequest });
    if (!active.current) return;
    if (!created) {
      setPhase('configure');
      return;
    }
    setExportId(created.exportId);
    setPhase(destinationId ? 'delivery' : 'polling');
    void pollAndDownload(created.exportId);
  }, [auditId, format, isPageContent, datasets, destinationId, columnSelection, call, pollAndDownload]);

  const selectedDestination = destinations.find((destination) => destination.id === destinationId) ?? null;
  const offline = !services.api;

  return (
    <Modal opened={open} onClose={onClose} title="Export report" size="lg">
      <Paper data-testid="export-dialog" p="xs">
        <Stack gap="sm">
          {offline ? (
            <Alert color="yellow" data-testid="export-offline" title="Backend disconnected">
              Export needs the local backend. Reconnect and reopen this dialog.
            </Alert>
          ) : null}

          <Select
            data-testid="export-format"
            label="Format"
            data={EXPORT_FORMATS.map((option) => ({ value: option.key, label: option.label }))}
            value={format}
            onChange={(value) => setFormat((value ?? 'xlsx') as ExportFormatKey)}
            allowDeselect={false}
          />
          <Text size="xs" c="dimmed">
            {EXPORT_FORMATS.find((option) => option.key === format)?.description}
          </Text>

          {!isPageContent ? (
            <Stack gap={4} data-testid="export-datasets">
              <Text size="sm" fw={500}>
                Datasets
              </Text>
              {EXPORT_DATASETS.map((dataset) => (
                <Checkbox
                  key={dataset.key}
                  data-testid={`export-dataset-${dataset.key}`}
                  label={dataset.label}
                  checked={datasets.includes(dataset.key)}
                  onChange={() => toggleDataset(dataset.key)}
                />
              ))}
            </Stack>
          ) : (
            <Text size="sm" c="dimmed" data-testid="export-page-content-note">
              Page-content formats export every stored page of this audit; dataset selection does not apply.
            </Text>
          )}

          {!isPageContent
            ? datasets
                .filter((dataset) => columnsFor(dataset).length > 0)
                .map((dataset) => (
                  <Stack key={dataset} gap={4} data-testid={`export-columns-${dataset}`}>
                    <Text size="sm" fw={500}>
                      Columns — {EXPORT_DATASETS.find((option) => option.key === dataset)?.label ?? dataset}
                    </Text>
                    {columnsFor(dataset).map((column) => {
                      const selection = columnSelection[dataset];
                      const checked = !selection || selection.includes(column.key);
                      return (
                        <Checkbox
                          key={column.key}
                          data-testid={`export-column-${dataset}-${column.key}`}
                          label={column.label}
                          checked={checked}
                          onChange={() => toggleColumn(dataset, column.key)}
                        />
                      );
                    })}
                  </Stack>
                ))
            : null}

          <Select
            data-testid="export-destination"
            label="Delivery destination (optional)"
            description="Only enabled, user-configured destinations are offered; local download always works."
            data={[
              { value: '', label: 'Local download only' },
              ...destinations.map((destination) => ({ value: destination.id, label: `${destination.name} (${destination.kind})` }))
            ]}
            value={destinationId ?? ''}
            onChange={(value) => setDestinationId(value && value.length > 0 ? value : null)}
            allowDeselect={false}
          />

          {phase === 'polling' ? (
            <Group gap="xs" data-testid="export-status">
              <Loader size="xs" />
              <Text size="sm">Generating export…</Text>
            </Group>
          ) : null}
          {phase === 'delivery' ? (
            <Text size="sm" data-testid="export-status">
              Delivering to {selectedDestination?.name ?? 'destination'} — delivery progress appears in the activity feed.
            </Text>
          ) : null}
          {phase === 'ready' ? (
            <Group gap="xs" data-testid="export-status">
              <Badge color="green">Ready</Badge>
              <Text size="sm">{saved ? 'Saved to your downloads.' : 'Export file is ready.'}</Text>
            </Group>
          ) : null}

          {call.error ? (
            <Alert color="red" data-testid="export-error" title="Export failed">
              {call.error}
            </Alert>
          ) : null}
          {pollError ? (
            <Text size="sm" c="yellow" data-testid="export-poll-error">
              {pollError}
            </Text>
          ) : null}

          <Group justify="flex-end" gap="xs">
            <Button variant="default" onClick={onClose} data-testid="export-close">
              Close
            </Button>
            {exportId && phase !== 'creating' ? (
              <Button
                variant="light"
                leftSection={<IconRefresh size={16} />}
                onClick={() => void pollAndDownload(exportId)}
                data-testid="export-download"
              >
                Download
              </Button>
            ) : null}
            <Button
              leftSection={<IconExport size={16} />}
              onClick={() => void start()}
              loading={phase === 'creating' || call.pending}
              disabled={offline || (!isPageContent && datasets.length === 0)}
              data-testid="export-start"
            >
              Export
            </Button>
          </Group>
        </Stack>
      </Paper>
    </Modal>
  );
}