/**
 * Export surface barrel (WP-09-10). Mount points (lead handoff, WP-09-11):
 * ExportDialog from every report surface; ExportDestinationsSection in
 * Settings (UI-21).
 *
 * @module surfaces/export
 */

export { ExportDialog, type ExportDialogProps } from './ExportDialog';
export { ExportDestinationsSection } from './ExportDestinationsSection';
export {
  DEFAULT_DATASETS,
  EXPORT_DATASETS,
  EXPORT_FORMATS,
  PAGE_CONTENT_FORMATS,
  STATIC_DATASET_COLUMNS,
  extensionFor,
  type AdditiveExportFormat,
  type ExportColumnOption,
  type ExportDatasetOption,
  type ExportFormatKey,
  type ExportFormatOption,
  type ExportJobWire
} from './exportCatalog';