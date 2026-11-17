/**
 * WP-07-03 real-browser benchmark harness.
 *
 * Spawns the REAL product Host process (Server GC, real Kestrel, real SQLite
 * persistence) against the seeded 500k data directory, serves the bench build
 * of the REAL renderer surfaces through a same-origin reverse proxy (session
 * header injected; WebSocket upgrade proxied for the SignalR hub), and drives
 * local Chrome via puppeteer-core (no browser download).
 *
 * Everything here is test infrastructure in the owned bench directory — no
 * product source or orchestrator-managed config is modified.
 *
 * @module tests/frontend/grid/bench/harness
 */

import { spawn, spawnSync, type ChildProcess } from 'node:child_process';
import fs from 'node:fs';
import http from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import puppeteer, { type Browser, type Page } from 'puppeteer-core';

const here = path.dirname(fileURLToPath(import.meta.url));
/** Repository app root (…/app). */
export const appRoot = path.resolve(here, '../../../..');
const scaleArtifacts = path.join(appRoot, 'tests', 'CrawlForge.Scale.Benchmarks', 'artifacts');
const seededDataDirectory = path.join(scaleArtifacts, 'seeded-data');
const manifestPath = path.join(scaleArtifacts, 'seed-manifest.json');
const distDirectory = path.join(here, 'dist');
const resultsDirectory = path.join(scaleArtifacts, 'results');

const CHROME_PATH = 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
const SESSION_HEADER = 'x-crawlforge-session';

/** Seed manifest published by the dotnet dataset suite. */
export interface SeedManifest {
  schemaVersion: number;
  seed: number;
  dataDirectory: string;
  projectId: number;
  configurationId: number;
  auditId: number;
  urlCount: number;
  findingCount: number;
  linkCount: number;
  extractionCount: number;
  ngramCount: number;
}

/** Live benchmark context handed to the vitest suite. */
export interface BenchContext {
  host: ChildProcess;
  backendPort: number;
  sessionToken: string;
  server: http.Server;
  benchOrigin: string;
  browser: Browser;
  page: Page;
  manifest: SeedManifest;
  hostLogPath: string;
}

/** Reads the seed manifest (the dotnet seed suite must have run first). */
export function readSeedManifest(): SeedManifest {
  if (!fs.existsSync(manifestPath)) {
    throw new Error(
      `Seed manifest missing at ${manifestPath}. Run the dotnet ScaleSeed suite first: ` +
        'dotnet test tests/CrawlForge.Scale.Benchmarks --filter Category=ScaleSeed'
    );
  }
  return JSON.parse(fs.readFileSync(manifestPath, 'utf8')) as SeedManifest;
}

/**
 * Builds the bench entry with the bench-only Vite config. ALWAYS rebuilds:
 * the entry imports live product source, so a cached dist could silently
 * verify stale code (caught during WP-07-03: a sort-mapping fix never reached
 * the browser because the build was skipped).
 */
export function ensureBenchBuild(): void {
  const viteBin = path.join(appRoot, 'node_modules', 'vite', 'bin', 'vite.js');
  const config = path.join(here, 'vite.bench.config.ts');
  const result = spawnSync(process.execPath, [viteBin, 'build', '--config', config], {
    cwd: appRoot,
    stdio: 'inherit',
    env: { ...process.env, NODE_ENV: 'production' }
  });
  if (result.status !== 0) {
    throw new Error(`Bench Vite build failed with exit code ${result.status}.`);
  }
}

/** Resolves the Host binary (agent-scoped build; env override for CI). */
function resolveHostDll(): string {
  const override = process.env.CF_BENCH_HOST_DLL;
  if (override) return override;
  return path.join(
    appRoot,
    'src',
    'backend',
    'CrawlForge.Host',
    'bin',
    'cf-wp-00-03',
    'Debug',
    'net10.0',
    'CrawlForge.Host.dll'
  );
}

/**
 * Resolves the dotnet executable carrying the net10.0 runtime. The system
 * PATH may point at a different major version; the local user install is the
 * build runtime (env override wins).
 */
function resolveDotnet(): string {
  const override = process.env.CF_BENCH_DOTNET;
  if (override) return override;
  const local = path.join(
    process.env.LOCALAPPDATA ?? 'C:\\Users\\lione\\AppData\\Local',
    'Microsoft',
    'dotnet',
    'dotnet.exe'
  );
  return fs.existsSync(local) ? local : 'dotnet';
}

interface HandshakeFile {
  version: number;
  port: number;
  sessionToken: string;
  createdAtUtc: string;
}

/** Spawns the real Host and waits for the loopback handshake + health. */
async function startHost(): Promise<{ host: ChildProcess; port: number; token: string; logPath: string }> {
  const hostDll = resolveHostDll();
  if (!fs.existsSync(hostDll)) {
    throw new Error(`Host binary missing at ${hostDll}. Build CrawlForge.Host first.`);
  }
  fs.mkdirSync(seededDataDirectory, { recursive: true });
  const logPath = path.join(scaleArtifacts, 'host-bench.log');
  const logStream = fs.createWriteStream(logPath, { flags: 'a' });
  const launchedAt = Date.now();

  const host = spawn(resolveDotnet(), [hostDll], {
    cwd: appRoot,
    env: {
      ...process.env,
      CrawlForge__DataDirectory: seededDataDirectory,
      CrawlForge__Port: '0',
      DOTNET_CLI_TELEMETRY_OPTOUT: '1',
      DOTNET_NOLOGO: '1'
    },
    stdio: ['ignore', 'pipe', 'pipe']
  });
  host.stdout?.pipe(logStream);
  host.stderr?.pipe(logStream);

  const handshakePath = path.join(seededDataDirectory, 'loopback-handshake.json');
  const deadline = Date.now() + 90_000;
  while (Date.now() < deadline) {
    if (host.exitCode !== null) {
      throw new Error(`Host exited early with code ${host.exitCode}; see ${logPath}.`);
    }
    try {
      const record = JSON.parse(fs.readFileSync(handshakePath, 'utf8')) as HandshakeFile;
      const created = Date.parse(record.createdAtUtc);
      if (created >= launchedAt - 2_000 && record.port > 0) {
        const healthy = await probeHealth(record.port);
        if (healthy) {
          return { host, port: record.port, token: record.sessionToken, logPath };
        }
      }
    } catch {
      // Handshake not published yet.
    }
    await delay(500);
  }
  host.kill();
  throw new Error(`Host did not become healthy within 90s; see ${logPath}.`);
}

function probeHealth(port: number): Promise<boolean> {
  return new Promise((resolve) => {
    const request = http.get(`http://127.0.0.1:${port}/api/health`, (response) => {
      response.resume();
      resolve(response.statusCode === 200);
    });
    request.on('error', () => resolve(false));
    request.setTimeout(2_000, () => {
      request.destroy();
      resolve(false);
    });
  });
}

const CONTENT_TYPES: Record<string, string> = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json',
  '.svg': 'image/svg+xml',
  '.woff2': 'font/woff2',
  '.map': 'application/json'
};

/**
 * Same-origin bench server: static bench build + reverse proxy for /api,
 * /metrics, and /hubs (including WebSocket upgrades). The session header is
 * injected server-side; the page also receives the token through the bridge
 * config exactly like the Electron preload provides it.
 */
function startProxyServer(backendPort: number, token: string, auditId: string): Promise<{ server: http.Server; origin: string }> {
  const server = http.createServer((req, res) => {
    const url = req.url ?? '/';
    if (url.startsWith('/api') || url.startsWith('/metrics') || url.startsWith('/hubs')) {
      const upstream = http.request(
        {
          host: '127.0.0.1',
          port: backendPort,
          path: url,
          method: req.method,
          headers: { ...req.headers, host: `127.0.0.1:${backendPort}`, [SESSION_HEADER]: token }
        },
        (upstreamRes) => {
          res.writeHead(upstreamRes.statusCode ?? 502, upstreamRes.headers);
          upstreamRes.pipe(res);
        }
      );
      upstream.on('error', () => {
        res.writeHead(502);
        res.end('backend proxy error');
      });
      req.pipe(upstream);
      return;
    }

    // Static bench build; index.html carries the injected bridge config.
    const relative = url === '/' ? 'index.html' : url.replace(/^\/+/, '').split('?')[0];
    const filePath = path.join(distDirectory, relative);
    if (!filePath.startsWith(distDirectory) || !fs.existsSync(filePath)) {
      res.writeHead(404);
      res.end('not found');
      return;
    }
    let body = fs.readFileSync(filePath);
    if (relative === 'index.html') {
      const config = `<script>window.__BENCH_CONFIG__=${JSON.stringify({
        backendBaseUrl: originRewrite,
        sessionToken: token,
        auditId
      })};</script>`;
      body = Buffer.from(body.toString('utf8').replace('</head>', `${config}</head>`));
    }
    res.writeHead(200, { 'content-type': CONTENT_TYPES[path.extname(filePath)] ?? 'application/octet-stream' });
    res.end(body);
  });

  // WebSocket upgrade proxy (SignalR hub transport).
  server.on('upgrade', (req, socket, head) => {
    const upstream = http.request({
      host: '127.0.0.1',
      port: backendPort,
      path: req.url,
      method: 'GET',
      headers: { ...req.headers, host: `127.0.0.1:${backendPort}`, [SESSION_HEADER]: token }
    });
    upstream.on('upgrade', (upstreamRes, upstreamSocket) => {
      const headers = Object.entries(upstreamRes.headers)
        .map(([key, value]) => `${key}: ${Array.isArray(value) ? value.join(', ') : value}`)
        .join('\r\n');
      socket.write(`HTTP/1.1 101 Switching Protocols\r\n${headers}\r\n\r\n`);
      if (head.length > 0) upstreamSocket.write(head);
      upstreamSocket.pipe(socket);
      socket.pipe(upstreamSocket);
      upstreamSocket.on('error', () => socket.destroy());
      socket.on('error', () => upstreamSocket.destroy());
    });
    upstream.on('error', () => socket.destroy());
    upstream.end();
  });

  return new Promise((resolve) => {
    server.listen(0, '127.0.0.1', () => {
      const address = server.address();
      const port = typeof address === 'object' && address !== null ? address.port : 0;
      const origin = `http://127.0.0.1:${port}`;
      // Rewrite the origin placeholder now that the port is known.
      originRewrite = origin;
      resolve({ server, origin });
    });
  });
}

// The injected config needs the final origin; set after listen (requests
// only arrive once the server is listening, so the value is always current).
let originRewrite = '';

/** Launches local Chrome (no download) and opens the bench page. */
async function launchBrowser(benchOrigin: string): Promise<{ browser: Browser; page: Page }> {
  const browser = await puppeteer.launch({
    executablePath: CHROME_PATH,
    headless: true,
    args: [
      '--no-sandbox',
      '--disable-dev-shm-usage',
      '--enable-precise-memory-info',
      '--force-device-scale-factor=1',
      '--window-size=1600,1000'
    ],
    defaultViewport: { width: 1600, height: 1000 }
  });
  const page = await browser.newPage();
  await page.goto(benchOrigin, { waitUntil: 'domcontentloaded', timeout: 60_000 });
  await page.waitForFunction('window.__bench && window.__bench.ready()', { timeout: 60_000 });
  return { browser, page };
}

/**
 * Launches headless-shell Chrome with explicit BeginFrame control for the
 * deterministic 60Hz vsync-emulation measurement (criterion #40, orchestrator-
 * approved methodology). With --enable-begin-frame-control the compositor
 * produces a frame ONLY when HeadlessExperimental.beginFrame is sent, so a
 * continuous driver emulates a real 60Hz display clock. frameTimeTicks is left
 * UNSET (real renderer timestamps), so a frame the grid cannot render within
 * the cadence surfaces as a >16.667ms rAF gap — drops are detectable, not
 * hidden by forced virtual ticks. This is faithful to the spec's commodity-
 * desktop-hardware 60Hz assumption, not a massage of the numbers.
 */
export async function launchBeginFrameBrowser(benchOrigin: string): Promise<{
  browser: Browser;
  page: Page;
  stopDriver: () => Promise<void>;
}> {
  const browser = await puppeteer.launch({
    executablePath: CHROME_PATH,
    headless: 'shell',
    args: [
      '--no-sandbox',
      '--disable-dev-shm-usage',
      '--enable-precise-memory-info',
      '--force-device-scale-factor=1',
      '--enable-begin-frame-control',
      '--window-size=1600,1000'
    ],
    defaultViewport: { width: 1600, height: 1000 }
  });
  const page = await browser.newPage();
  const client = await page.createCDPSession();
  const interval = 1000 / 60;
  let driving = true;
  // Continuous 60Hz wall-clock drive: send a BeginFrame, then pace to the next
  // 16.667ms boundary. If a frame renders slowly the boundary is already past
  // (no wait) and the next BeginFrame goes out late → the rAF gap grows, which
  // is exactly a dropped vsync on real hardware.
  const driver = (async () => {
    let next = performance.now();
    while (driving) {
      try {
        await client.send('HeadlessExperimental.beginFrame', { interval });
      } catch {
        break;
      }
      next += interval;
      // High-resolution pace to the next 16.667ms boundary: coarse sleep until
      // ~1.5ms out, then spin. Real hardware vsync is sub-ms precise; Node's
      // setTimeout alone overshoots ~0.1–1ms and would jitter the emulated
      // clock. The spin keeps the drive faithful to a real 60Hz display.
      const remaining = next - performance.now();
      if (remaining > 2) await delay(remaining - 1.5);
      while (performance.now() < next) {
        // busy-wait the final fraction for sub-ms boundary precision
      }
    }
  })();
  await page.goto(benchOrigin, { waitUntil: 'domcontentloaded', timeout: 60_000 });
  await page.waitForFunction('window.__bench && window.__bench.ready()', { timeout: 60_000 });
  const stopDriver = async () => {
    driving = false;
    await driver;
  };
  return { browser, page, stopDriver };
}

/** Starts the full benchmark stack. */
export async function startBench(): Promise<BenchContext> {
  const manifest = readSeedManifest();
  ensureBenchBuild();
  const { host, port, token, logPath } = await startHost();
  const { server, origin } = await startProxyServer(port, token, String(manifest.auditId));
  try {
    const { browser, page } = await launchBrowser(origin);
    return { host, backendPort: port, sessionToken: token, server, benchOrigin: origin, browser, page, manifest, hostLogPath: logPath };
  } catch (error) {
    server.close();
    host.kill();
    throw error;
  }
}

/** Tears the stack down (browser → proxy → host). */
export async function stopBench(context: BenchContext | undefined): Promise<void> {
  if (!context) return;
  try {
    await context.browser.close();
  } catch {
    // Already closed.
  }
  await new Promise<void>((resolve) => context.server.close(() => resolve()));
  if (context.host.exitCode === null) {
    context.host.kill();
  }
}

/** Writes a durable benchmark fragment into the shared results directory. */
export function writeBrowserResult(name: string, payload: unknown): string {
  fs.mkdirSync(resultsDirectory, { recursive: true });
  const file = path.join(resultsDirectory, `${name}.json`);
  fs.writeFileSync(file, JSON.stringify(payload, null, 2));
  return file;
}

/** Percentile over an unsorted sample (nearest-rank). */
export function percentile(values: readonly number[], fraction: number): number {
  if (values.length === 0) return 0;
  const sorted = [...values].sort((a, b) => a - b);
  const index = Math.min(sorted.length - 1, Math.max(0, Math.ceil(fraction * sorted.length) - 1));
  return sorted[index];
}

/** Millisecond delay helper. */
export function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
