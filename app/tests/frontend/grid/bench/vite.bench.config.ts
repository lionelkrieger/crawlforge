/**
 * WP-07-03 benchmark-only Vite build config (NEW file in the owned bench
 * directory — the orchestrator-managed src/frontend/vite.config.ts is never
 * touched). Builds the bench entry against the real renderer sources via the
 * @app alias; the production worker bundling path is preserved.
 */
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

const here = path.dirname(fileURLToPath(import.meta.url));

export default defineConfig({
  plugins: [react()],
  root: path.join(here, 'entry'),
  base: './',
  worker: {
    format: 'es'
  },
  build: {
    outDir: path.join(here, 'dist'),
    emptyOutDir: true,
    target: 'es2022',
    sourcemap: false
  }
});
