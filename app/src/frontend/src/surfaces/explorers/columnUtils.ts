/**
 * Column/query-field construction helpers shared by all explorer
 * definitions. Keeps the eleven surfaces declarative and consistent.
 *
 * @module surfaces/explorers/columnUtils
 */

import type { RowData } from '@tanstack/react-table';
import type { ReactNode } from 'react';
import type { GridColumnDef, GridColumnMeta } from '../../grid/types';
import type { QueryFieldDef } from '../../grid/queryBuilder';

/** Builds a grid column with an accessor and optional custom cell. */
export function makeColumn<TRow extends RowData, TValue = unknown>(
  id: string,
  header: string,
  accessor: (row: TRow) => TValue,
  options: {
    readonly size?: number;
    readonly cell?: (value: TValue, row: TRow) => ReactNode;
    readonly meta?: GridColumnMeta;
  } = {}
): GridColumnDef<TRow> {
  return {
    id,
    header,
    accessorFn: accessor,
    size: options.size ?? 150,
    ...(options.cell
      ? { cell: (info) => options.cell!(info.getValue() as TValue, info.row.original) }
      : {}),
    ...(options.meta ? { meta: options.meta } : {})
  } as GridColumnDef<TRow>;
}

/** Builds a query-builder field definition. */
export function makeField(
  field: string,
  label: string,
  valueKind: QueryFieldDef['valueKind'],
  options: {
    readonly serverFilter?: string;
    readonly enumValues?: readonly string[];
    readonly dynamicOrigin?: string;
  } = {}
): QueryFieldDef {
  return {
    field,
    label,
    valueKind,
    ...(options.serverFilter ? { serverFilter: options.serverFilter } : {}),
    ...(options.enumValues ? { enumValues: options.enumValues } : {}),
    ...(options.dynamicOrigin ? { dynamicOrigin: options.dynamicOrigin } : {})
  };
}
