import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App';

// Renderer entry point (skeleton). The packaged bundle is loaded locally by the
// Electron shell; it never loads remote content (process-model spec §2.1).
const container = document.getElementById('root');
if (!container) {
  throw new Error('CrawlForge root element is missing.');
}

createRoot(container).render(
  <StrictMode>
    <App />
  </StrictMode>
);
