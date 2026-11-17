import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

// CrawlForge renderer build/test configuration (skeleton).
// Vitest discovers suites both next to renderer code and under the shared
// tests/frontend workspace (later workpieces put contracts/shell/grid/... suites there).
export default defineConfig({
  // Relative asset URLs: Electron loads dist/index.html over file://, where
  // absolute /assets/... paths resolve to the drive root and the window stays blank.
  base: './',
  plugins: [react()],
  server: {
    host: '127.0.0.1',
    port: 5173,
    strictPort: false
  },
  test: {
    environment: 'node',
    passWithNoTests: true,
    include: [
      'src/**/*.{test,spec}.{ts,tsx}',
      '../../tests/frontend/**/*.{test,spec}.{ts,tsx}'
    ]
  }
});
