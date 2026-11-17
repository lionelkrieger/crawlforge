/**
 * CrawlForge Electron main entry (WP-06-01). Thin binding layer: all behavior
 * lives in the pure modules (supervisor, windows, recovery, plugins, startup)
 * which are vitest-provable offline. This file only binds Electron/Node
 * implementations to the injected ports and wires app lifecycle.
 *
 * Security posture (§8.1): contextIsolation ON, nodeIntegration OFF, sandbox
 * ON for renderers; the preload exposes only the typed bridge. All backend
 * traffic is loopback-only with the per-launch session token.
 */
import { app, BrowserWindow, dialog, ipcMain, Notification, shell } from 'electron';
import { spawn } from 'node:child_process';
import * as fs from 'node:fs/promises';
import * as path from 'node:path';
import { PortDiscovery, joinPath } from './handshake/port-discovery';
import { IPC_CHANNELS } from './preload/bridge';
import { PluginScanner, PluginCandidate } from './plugins/plugin-scanner';
import { RecoveryService } from './recovery/recovery-service';
import { BackendSupervisor } from './supervisor/backend-supervisor';
import { StartupOrchestrator, HydrationSignal } from './startup/startup-orchestrator';
import {
  ChildProcessLike,
  ClockPort,
  FileSystemPort,
  HttpProbePort,
  HttpResponseLike,
  ProcessSpawner,
  ShellLogger,
  SpawnOptions,
  TaskbarProgressMode,
  WindowKind,
  WindowState
} from './types';
import { WindowManager, WindowFactory, WindowInstanceLike } from './windows/window-manager';
import { WINDOW_CATALOG } from './windows/window-catalog';

// ---------------------------------------------------------------------------
// Port bindings
// ---------------------------------------------------------------------------

const logger: ShellLogger = {
  info: (message) => console.log(`[crawlforge] ${message}`),
  warn: (message) => console.warn(`[crawlforge] ${message}`),
  error: (message, error) => console.error(`[crawlforge] ${message}`, error ?? '')
};

const clock: ClockPort = {
  now: () => Date.now(),
  delay: (milliseconds) => new Promise((resolve) => setTimeout(resolve, milliseconds))
};

const fileSystem: FileSystemPort = {
  async readTextFile(filePath) {
    try {
      return await fs.readFile(filePath, 'utf8');
    } catch {
      return null;
    }
  },
  async writeTextFile(filePath, content) {
    await fs.writeFile(filePath, content, 'utf8');
  },
  async deleteFile(filePath) {
    await fs.rm(filePath, { force: true });
  },
  async listDirectory(directory) {
    return await fs.readdir(directory);
  },
  async exists(filePath) {
    try {
      await fs.access(filePath);
      return true;
    } catch {
      return false;
    }
  },
  async ensureDirectory(directory) {
    await fs.mkdir(directory, { recursive: true });
  }
};

const spawner: ProcessSpawner = {
  spawn(command, args, options: SpawnOptions): ChildProcessLike {
    const child = spawn(command, [...args], {
      cwd: options.cwd,
      env: { ...process.env, ...options.env },
      detached: options.detached ?? false,
      stdio: ['ignore', 'pipe', 'pipe'],
      windowsHide: true
    });
    const exitListeners: ((code: number | null, signal: string | null) => void)[] = [];
    const stderrListeners: ((chunk: string) => void)[] = [];
    child.on('exit', (code, signal) => {
      for (const listener of exitListeners) listener(code, signal);
    });
    child.stderr?.on('data', (chunk: Buffer) => {
      const text = chunk.toString('utf8');
      console.log(`[backend:stderr] ${text.trim()}`);
      for (const listener of stderrListeners) listener(text);
    });
    child.stdout?.on('data', (chunk: Buffer) => {
      console.log(`[backend:stdout] ${chunk.toString('utf8').trim()}`);
    });
    return {
      pid: child.pid,
      get exitCode() {
        return child.exitCode;
      },
      get killed() {
        return child.killed;
      },
      onExit(listener) {
        exitListeners.push(listener);
      },
      onStderr(listener) {
        stderrListeners.push(listener);
      },
      kill() {
        child.kill();
      }
    };
  }
};

async function probe(
  method: 'GET' | 'POST' | 'DELETE',
  url: string,
  body: string | null,
  headers?: Readonly<Record<string, string>>
): Promise<HttpResponseLike | null> {
  // Loopback boundary: the shell only ever probes its own local backend. The
  // fetch target is re-pinned to a literal 127.0.0.1 origin regardless of the
  // input, so no dynamic host can ever be dialed.
  let target: URL;
  try {
    target = new URL(url);
  } catch {
    return null;
  }
  if (target.hostname !== '127.0.0.1' && target.hostname !== 'localhost') {
    return null;
  }
  const port = target.port;
  const path = target.pathname + target.search;
  try {
    const response = await fetch(`http://127.0.0.1:${port}${path}`, {
      method,
      headers: { ...(headers ?? {}), ...(body !== null ? { 'content-type': 'application/json' } : {}) },
      body: body ?? undefined,
      signal: AbortSignal.timeout(10000)
    });
    return { status: response.status, body: await response.text() };
  } catch {
    return null;
  }
}

const httpProbe: HttpProbePort = {
  get: (url, headers) => probe('GET', url, null, headers),
  post: (url, body, headers) => probe('POST', url, body, headers),
  delete: (url, headers) => probe('DELETE', url, null, headers)
};

// ---------------------------------------------------------------------------
// Paths and backend launch configuration
// ---------------------------------------------------------------------------

const dataDirectory =
  process.env.CRAWLFORGE_DATA_DIRECTORY ??
  joinPath(process.env.APPDATA ?? app.getPath('appData'), 'CrawlForge');

/** Resolves the backend launch command (dev tree or packaged resources). */
function resolveBackendLaunch(): { command: string; args: string[] } {
  if (process.env.CF_BACKEND_COMMAND) {
    return {
      command: process.env.CF_BACKEND_COMMAND,
      args: (process.env.CF_BACKEND_ARGS ?? '').split(' ').filter((part) => part.length > 0)
    };
  }

  const packaged = path.join(process.resourcesPath ?? '', 'backend', 'CrawlForge.Host.exe');
  const devDll = path.resolve(
    __dirname,
    '../../backend/CrawlForge.Host/bin/Debug/net10.0/CrawlForge.Host.dll'
  );
  if (process.resourcesPath && require('node:fs').existsSync(packaged)) {
    return { command: packaged, args: [] };
  }
  return { command: 'dotnet', args: [devDll] };
}

// ---------------------------------------------------------------------------
// Window factory binding
// ---------------------------------------------------------------------------

const windowFactory: WindowFactory = {
  create(kind: WindowKind, state: WindowState): WindowInstanceLike {
    const definition = WINDOW_CATALOG[kind];
    const window = new BrowserWindow({
      x: state.x,
      y: state.y,
      width: state.width,
      height: state.height,
      frame: definition.frame,
      resizable: definition.resizable,
      show: false,
      webPreferences: {
        preload: path.join(__dirname, 'preload.js'),
        contextIsolation: true,
        nodeIntegration: false,
        sandbox: true
      }
    });
    if (state.maximized) {
      window.maximize();
    }
    window.once('ready-to-show', () => window.show());

    const destroyedListeners: (() => void)[] = [];
    window.on('closed', () => {
      for (const listener of destroyedListeners) listener();
    });

    return {
      id: String(window.id),
      focus: () => window.focus(),
      close: () => window.close(),
      isVisible: () => window.isVisible(),
      getState: (): WindowState => {
        const bounds = window.getBounds();
        return { ...bounds, maximized: window.isMaximized() };
      },
      onDestroyed(listener) {
        destroyedListeners.push(listener);
      },
      async loadRoute(route, query) {
        // Local UI assets only (CSP default-src 'self'); the renderer shell
        // (WP-06-02) is served from the packaged ui directory.
        const uiRoot = process.env.CF_UI_ROOT ?? path.resolve(__dirname, '../../frontend/dist');
        const url = new URL(`file://${path.join(uiRoot, 'index.html')}`);
        url.searchParams.set('route', route);
        for (const [key, value] of Object.entries(query ?? {})) {
          url.searchParams.set(key, value);
        }
        await window.loadURL(url.toString());
      }
    };
  }
};

// ---------------------------------------------------------------------------
// Composition
// ---------------------------------------------------------------------------

let supervisor: BackendSupervisor;
let windows: WindowManager;
let recovery: RecoveryService;
let orchestrator: StartupOrchestrator;
let hydrationResolve: (() => void) | null = null;

const hydration: HydrationSignal = {
  wait(timeoutMs) {
    return new Promise<void>((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error('hydration timeout')), timeoutMs);
      hydrationResolve = () => {
        clearTimeout(timer);
        resolve();
      };
    });
  }
};

async function compose(): Promise<void> {
  await fileSystem.ensureDirectory(dataDirectory);
  const launch = resolveBackendLaunch();
  supervisor = new BackendSupervisor(spawner, fileSystem, clock, httpProbe, logger, {
    backendCommand: launch.command,
    backendArgs: [...launch.args, `--CrawlForge:DataDirectory=${dataDirectory}`, '--CrawlForge:Port=0'],
    dataDirectory,
    spawnOptions: { cwd: path.dirname(launch.args[0] ?? '.') }
  });
  windows = new WindowManager(windowFactory, fileSystem, logger, dataDirectory);
  recovery = new RecoveryService(fileSystem, httpProbe, logger, supervisor, dataDirectory);

  const trustPrompt = async (candidate: PluginCandidate): Promise<boolean> => {
    const result = await dialog.showMessageBox({
      type: 'question',
      buttons: ['Trust', 'Skip'],
      defaultId: 1,
      cancelId: 1,
      title: 'Plugin trust',
      message: `Allow plugin "${candidate.manifest.name}" (${candidate.manifest.id}) from ${candidate.source}?`,
      detail: 'Plugins run locally with the permissions declared in their manifest. Only trust plugins you installed yourself.'
    });
    return result.response === 0;
  };

  // WP-06-06 owns the registry implementation; until it is bound the scanner
  // reports consented candidates without loading code (inert insertion point).
  const plugins = new PluginScanner(fileSystem, logger, trustPrompt, null);
  orchestrator = new StartupOrchestrator(
    supervisor,
    windows,
    recovery,
    plugins,
    hydration,
    logger,
    dataDirectory,
    process.env.CF_WORKSPACE_DIRECTORY ?? null
  );

  supervisor.onStatusChange((status) => {
    for (const window of BrowserWindow.getAllWindows()) {
      window.webContents.send(IPC_CHANNELS.backendStatus, status);
    }
  });
}

// ---------------------------------------------------------------------------
// IPC wiring (typed channels only)
// ---------------------------------------------------------------------------

function wireIpc(): void {
  ipcMain.handle(IPC_CHANNELS.fileDialog, async (_event, request) => {
    if (request.mode === 'save-file') {
      const saveResult = await dialog.showSaveDialog({
        title: request.title,
        defaultPath: request.defaultPath,
        filters: request.filters
      });
      return saveResult.canceled || !saveResult.filePath ? [] : [saveResult.filePath];
    }
    const openResult = await dialog.showOpenDialog({
      title: request.title,
      defaultPath: request.defaultPath,
      filters: request.filters,
      properties: request.mode === 'open-directory' ? ['openDirectory'] : ['openFile']
    });
    return openResult.canceled ? [] : openResult.filePaths;
  });

  ipcMain.on(IPC_CHANNELS.notification, (_event, title: string, message: string) => {
    if (Notification.isSupported()) {
      new Notification({ title, body: message }).show();
    }
  });

  ipcMain.on(IPC_CHANNELS.taskbarProgress, (event, mode: TaskbarProgressMode, value?: number) => {
    const window = BrowserWindow.fromWebContents(event.sender);
    window?.setProgressBar(mode === 'none' ? -1 : (value ?? 0.5), { mode });
  });

  ipcMain.on(IPC_CHANNELS.windowMinimize, (event) => {
    BrowserWindow.fromWebContents(event.sender)?.minimize();
  });
  ipcMain.on(IPC_CHANNELS.windowMaximize, (event) => {
    const window = BrowserWindow.fromWebContents(event.sender);
    if (window?.isMaximized()) {
      window.unmaximize();
    } else {
      window?.maximize();
    }
  });
  ipcMain.on(IPC_CHANNELS.windowClose, (event) => {
    BrowserWindow.fromWebContents(event.sender)?.close();
  });

  ipcMain.handle(IPC_CHANNELS.backendRestart, async () => {
    await orchestrator.retryFromRecovery();
  });
  ipcMain.handle(IPC_CHANNELS.diagnosticsPurge, () => recovery.purgeDiagnostics());

  // Renderer handshake: the main process pushes the discovered descriptor;
  // the preload builds window.crawlForge from it (never raw Node access).
  ipcMain.handle('crawlforge:get-bridge', async () => {
    const discovery = new PortDiscovery(fileSystem);
    const result = await discovery.discover(dataDirectory);
    return result.ok ? result.descriptor : null;
  });

  ipcMain.on('crawlforge:hydrated', () => {
    hydrationResolve?.();
    hydrationResolve = null;
  });

  // External links open in the OS browser, never inside the shell (§8.1).
  app.on('web-contents-created', (_event, contents) => {
    contents.setWindowOpenHandler(({ url }) => {
      if (url.startsWith('https://') || url.startsWith('http://')) {
        void shell.openExternal(url);
      }
      return { action: 'deny' };
    });
  });
}

// ---------------------------------------------------------------------------
// App lifecycle
// ---------------------------------------------------------------------------

const gotLock = app.requestSingleInstanceLock();
if (!gotLock) {
  app.quit();
} else {
  app.on('second-instance', () => {
    void windows?.open({ kind: 'main' }, supervisor?.status.state === 'ready');
  });

  app.whenReady().then(async () => {
    await compose();
    wireIpc();
    await orchestrator.run();
  });

  app.on('window-all-closed', () => {
    if (process.platform !== 'darwin') {
      app.quit();
    }
  });

  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) {
      void orchestrator?.run();
    }
  });

  let quitStarted = false;
  app.on('before-quit', (event) => {
    if (quitStarted) {
      return;
    }
    quitStarted = true;
    event.preventDefault();
    void (async () => {
      try {
        await windows?.persistState();
        await supervisor?.stop();
      } finally {
        app.exit(0);
      }
    })();
  });
}
