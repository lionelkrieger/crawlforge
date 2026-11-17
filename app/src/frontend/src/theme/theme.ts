/**
 * CrawlForge dark-first Mantine theme (FS-CRAWL-002 §8.1/§8.9).
 *
 * Visual identity: a crawler's forge — deep steel-night surfaces carrying
 * ember-orange heat, with cool signal-cyan for live telemetry. All assets
 * are local (system font stacks, hand-built SVG icons); no CDN, no remote
 * fonts. Light mode is derived, never the default.
 *
 * @module theme/theme
 */

import { createTheme, type MantineColorsTuple, type MantineThemeOverride } from '@mantine/core';

/** Ember: the forge heat — primary brand family. */
export const ember: MantineColorsTuple = [
  '#fff4e6', '#ffe8cc', '#ffd8a8', '#ffc078', '#ffa94d',
  '#ff922b', '#ff8a3d', '#f76707', '#e8590c', '#d9480f'
];

/** Steel: cool graphite-blue surfaces and borders. */
export const steel: MantineColorsTuple = [
  '#f3f5f9', '#e7ebf2', '#cdd5e1', '#aeb9cc', '#8b9ab3',
  '#6b7c99', '#52627e', '#3d4a63', '#2a3448', '#1b2233'
];

/** Signal: live-telemetry cyan for hub activity and data accents. */
export const signal: MantineColorsTuple = [
  '#e3fafc', '#c5f6fa', '#99e9f2', '#66d9e8', '#3bc9db',
  '#22b8cf', '#15aabf', '#1098ad', '#0b7285', '#095c6b'
];

/** Night: the dark-first surface ramp (index 7 = app background). */
export const night: MantineColorsTuple = [
  '#c9d1e0', '#aab4c8', '#8892a8', '#646e85', '#454e63',
  '#2b3348', '#1d2436', '#141a29', '#0f1420', '#0b0e14'
];

/** Semantic tones (charts borrow these plus ember/signal/violet/lime). */
export const semantic = {
  success: '#37b24d',
  warning: '#f59f00',
  danger: '#f03e3e',
  info: '#22b8cf',
  violet: '#ae3ec9',
  lime: '#a9e34b',
  amber: '#ffa94d'
} as const;

/** Chart palette: six separable hues (luminance + direct labels carry meaning too). */
export const chartPalette = [
  '#ff8a3d', '#22b8cf', '#ae3ec9', '#a9e34b', '#f03e3e', '#ffa94d', '#66d9e8', '#8b9ab3'
] as const;

/** Local font stacks only (§8.9: no remote fonts). */
export const fontStacks = {
  body: '"Segoe UI Variable Text", "Segoe UI", system-ui, -apple-system, sans-serif',
  display: '"Segoe UI Variable Display", "Segoe UI", system-ui, sans-serif',
  mono: '"Cascadia Code", "Cascadia Mono", Consolas, "SF Mono", monospace'
} as const;

/** Default status-bar advisories (FS-CRAWL-002 §7.2; locally configurable). */
export const DEFAULT_WORKING_SET_ADVISORY_BYTES = 2 * 1024 * 1024 * 1024; // 2 GiB
export const DEFAULT_WAL_ADVISORY_BYTES = 256 * 1024 * 1024; // 256 MiB

/**
 * The dark-first theme. `forceColorScheme` is driven by local settings at
 * the provider; dark is the construction default.
 */
export const crawlForgeTheme: MantineThemeOverride = createTheme({
  primaryColor: 'ember',
  primaryShade: { light: 6, dark: 5 },
  colors: { ember, steel, signal, night },
  defaultRadius: 'md',
  fontFamily: fontStacks.body,
  fontFamilyMonospace: fontStacks.mono,
  headings: {
    fontFamily: fontStacks.display,
    fontWeight: '650',
    sizes: {
      h1: { fontSize: '28px', lineHeight: '1.2' },
      h2: { fontSize: '22px', lineHeight: '1.25' },
      h3: { fontSize: '18px', lineHeight: '1.3' },
      h4: { fontSize: '15px', lineHeight: '1.35' }
    }
  },
  luminanceThreshold: 0.25,
  other: {
    semantic,
    chartPalette,
    surfaceRaised: 'var(--mantine-color-night-7)',
    surfaceOverlay: 'var(--mantine-color-night-6)',
    hairline: 'var(--mantine-color-night-5)'
  },
  components: {
    AppShell: {
      styles: {
        root: { background: 'var(--mantine-color-night-9)' },
        header: {
          background: 'var(--mantine-color-night-8)',
          borderBottom: '1px solid var(--mantine-color-night-5)'
        },
        navbar: {
          background: 'var(--mantine-color-night-8)',
          borderRight: '1px solid var(--mantine-color-night-5)'
        },
        footer: {
          background: 'var(--mantine-color-night-8)',
          borderTop: '1px solid var(--mantine-color-night-5)'
        }
      }
    },
    Paper: {
      defaultProps: { radius: 'md' },
      styles: {
        root: {
          background: 'var(--mantine-color-night-7)',
          borderColor: 'var(--mantine-color-night-5)'
        }
      }
    },
    Modal: {
      defaultProps: { radius: 'md', overlayProps: { backgroundOpacity: 0.7, blur: 3 } },
      styles: {
        content: { background: 'var(--mantine-color-night-7)' },
        header: { background: 'var(--mantine-color-night-7)' }
      }
    },
    NavLink: {
      styles: {
        root: { borderRadius: 'var(--mantine-radius-md)' }
      }
    },
    Tooltip: {
      defaultProps: { radius: 'sm', withArrow: true }
    },
    Badge: {
      defaultProps: { radius: 'sm' }
    }
  }
});
