/**
 * Standalone tools workspace (FS-CRAWL-001 §8.7): the full instrument
 * bench — every tool runs without a full audit. A catalog rail selects
 * the active instrument; the Single-Page Analyzer leads (criterion #10),
 * followed by the preview, picker-bearing, and generator tools, ending
 * with saved queries and user-owned export destinations.
 *
 * Mountable root: the shell tools frame consumes this component. All
 * state is local to the active tool; nothing here creates a project.
 *
 * @module tools/ToolsWorkspace
 */

import { useState, type ReactNode } from 'react';
import { Box, Stack, Text } from '@mantine/core';
import {
  IconChart,
  IconCommand,
  IconExport,
  IconGlobe,
  IconLink,
  IconSearch,
  IconShield,
  IconTerminal
} from '../theme/icons';
import { SinglePageAnalyzer } from '../surfaces/single-page/SinglePageAnalyzer';
import { SerpPreview } from './serp/SerpPreview';
import { RenderTesterTool } from './RenderTesterTool';
import { RobotsTesterTool } from './RobotsTesterTool';
import { SitemapGeneratorTool } from './SitemapGeneratorTool';
import { ScopePreviewTool } from './ScopePreviewTool';
import { RewritePreviewTool } from './RewritePreviewTool';
import { LlmsTool } from './LlmsTool';
import { UrlCheckTool } from './UrlCheckTool';
import { StructuredDataTool } from './StructuredDataTool';
import { QueryBuilderTool } from './QueryBuilderTool';
import { DestinationsManager } from './DestinationsManager';
import './shared/tools.css';

/** One catalog entry. */
interface ToolEntry {
  readonly key: string;
  readonly label: string;
  readonly spec: string;
  readonly icon: ReactNode;
  readonly element: ReactNode;
}

const CATALOG: readonly ToolEntry[] = [
  { key: 'single-page', label: 'Single-Page Analyzer', spec: '#10', icon: <IconSearch size={15} />, element: <SinglePageAnalyzer /> },
  { key: 'serp', label: 'SERP & Social Preview', spec: '#48', icon: <IconChart size={15} />, element: <SerpPreview /> },
  { key: 'render', label: 'Render Tester + Picker', spec: '#42', icon: <IconTerminal size={15} />, element: <RenderTesterTool /> },
  { key: 'robots', label: 'Robots.txt Tester', spec: '§8.7', icon: <IconShield size={15} />, element: <RobotsTesterTool /> },
  { key: 'sitemap', label: 'Sitemap Generator', spec: '§8.7', icon: <IconGlobe size={15} />, element: <SitemapGeneratorTool /> },
  { key: 'scope', label: 'Content Scope Preview', spec: '§4.6', icon: <IconShield size={15} />, element: <ScopePreviewTool /> },
  { key: 'rewrite', label: 'Rewrite & Host Mapping', spec: '§10.7', icon: <IconLink size={15} />, element: <RewritePreviewTool /> },
  { key: 'llms', label: 'llms.txt Generate / Validate', spec: '§13.5', icon: <IconGlobe size={15} />, element: <LlmsTool /> },
  { key: 'urlcheck', label: 'URL Check', spec: '§8.7', icon: <IconGlobe size={15} />, element: <UrlCheckTool /> },
  { key: 'structured', label: 'Structured Data Checker', spec: '§7.13', icon: <IconTerminal size={15} />, element: <StructuredDataTool /> },
  { key: 'query', label: 'Query Builder', spec: '§8.7', icon: <IconCommand size={15} />, element: <QueryBuilderTool /> },
  { key: 'destinations', label: 'Export Destinations', spec: '§8.5', icon: <IconExport size={15} />, element: <DestinationsManager /> }
];

/** ToolsWorkspace props. */
export interface ToolsWorkspaceProps {
  /** Initially active tool key (default: single-page). */
  readonly initialTool?: string;
}

/** The full standalone-tools workspace. */
export function ToolsWorkspace({ initialTool = 'single-page' }: ToolsWorkspaceProps) {
  const [active, setActive] = useState(initialTool);
  const entry = CATALOG.find((tool) => tool.key === active) ?? CATALOG[0];

  return (
    <Box className="cf-tools-workspace" data-testid="tools-workspace">
      <nav className="cf-tools-rail" aria-label="Tool catalog" data-testid="tools-rail">
        <Text size="xs" fw={600} tt="uppercase" lts={0.6} c="dimmed" px={10} py={4}>
          Instrument bench
        </Text>
        {CATALOG.map((tool) => (
          <button
            key={tool.key}
            type="button"
            className="cf-tools-rail-item"
            data-active={tool.key === entry.key}
            onClick={() => setActive(tool.key)}
            data-testid={`tools-rail-${tool.key}`}
          >
            <span className="cf-tools-rail-icon" aria-hidden>
              {tool.icon}
            </span>
            <span className="cf-tools-rail-label">{tool.label}</span>
            <span className="cf-tools-rail-spec">{tool.spec}</span>
          </button>
        ))}
      </nav>
      <Stack gap="sm" data-testid={`tools-panel-${entry.key}`}>
        {entry.element}
      </Stack>
    </Box>
  );
}

/** The catalog keys (mounting surfaces / tests). */
export const TOOL_CATALOG_KEYS = CATALOG.map((tool) => tool.key);