import { BrowserWindow, Menu, app, dialog, net, protocol, session } from 'electron';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { DesktopHostBridge } from './host-bridge';
import { registerIpc } from './ipc';
import {
  packagedRendererUrl,
  rendererScheme,
  resolveRendererFilePath,
} from './renderer-protocol';
protocol.registerSchemesAsPrivileged([{
  scheme: rendererScheme,
  privileges: {
    standard: true,
    secure: true,
    supportFetchAPI: true,
    corsEnabled: false,
  },
}]);

const smokeTest = process.argv.includes('--smoke-test');
const smokeReadyToken = 'READY renderer-bootstrap-v1';
const smokeFixtureTitle = 'Electron smoke fixture';
if (smokeTest) {
  // Any implicit or premature shutdown must fail closed. The complete smoke
  // path exits explicitly with zero only after every renderer/Host assertion.
  process.exitCode = 1;
  app.disableHardwareAcceleration();
}

if (process.platform === 'linux') {
  // Electron 38+ may prefer native Wayland. The declared Linux support target is X11/XWayland.
  app.commandLine.appendSwitch('ozone-platform', 'x11');
  app.commandLine.appendSwitch('ozone-platform-hint', 'x11');
}

const supported = process.arch === 'x64' && (process.platform === 'win32' || process.platform === 'linux');
const smokeDataRoot = smokeTest && supported
  ? mkdtempSync(path.join(os.tmpdir(), 'blcm-electron-smoke-'))
  : null;
if (smokeDataRoot) {
  // Chromium can keep userData files open until the browser process has fully
  // exited. Reuse one OS-temp-only location so smoke runs never touch the real
  // profile and do not accumulate a new locked directory on every invocation.
  const electronUserData = path.join(os.tmpdir(), 'blcm-electron-smoke-user-data');
  try { rmSync(electronUserData, { recursive: true, force: true }); } catch { /* A concurrent smoke will fail the lock below. */ }
  mkdirSync(electronUserData, { recursive: true });
  app.setPath('userData', electronUserData);

  const cacheRoot = path.join(smokeDataRoot, 'cache');
  mkdirSync(cacheRoot, { recursive: true });
  const avid = 990001;
  const segmentRoot = path.join(cacheRoot, String(avid), 'c_1');
  const mediaRoot = path.join(segmentRoot, 'lua.flv.bb2api.80');
  mkdirSync(mediaRoot, { recursive: true });
  const timestamp = Date.now();
  writeFileSync(path.join(segmentRoot, 'entry.json'), JSON.stringify({
    is_completed: true,
    total_bytes: 16,
    downloaded_bytes: 16,
    title: smokeFixtureTitle,
    type_tag: 'type',
    cover: 'cover',
    prefered_video_quality: 80,
    guessed_total_bytes: 16,
    total_time_milli: 1_000,
    danmaku_count: 0,
    time_update_stamp: timestamp,
    time_create_stamp: timestamp,
    avid,
    bvid: 'BV1SmokeFixture',
    owner_name: 'Smoke Test',
    spid: 0,
    seasion_id: 0,
    page_data: { cid: 99000101, page: 1, from: 'local', part: 'Smoke page', vid: 'vid', has_alias: false, tid: 0 },
  }), 'utf8');
  writeFileSync(path.join(mediaRoot, '0.mp4'), 'smoke-media', 'utf8');
  writeFileSync(
    path.join(smokeDataRoot, 'settings.json'),
    JSON.stringify({
      SchemaVersion: 2,
      RootPath: cacheRoot,
      RememberRootPath: true,
      ScanOnStartup: true,
      IncludeIncomplete: false,
    }),
    'utf8',
  );
}
const trustedHostEnvironmentOverrides = smokeDataRoot
  ? {
      BILIBILI_LOCAL_CACHE_MANAGER_SETTINGS_PATH: path.join(smokeDataRoot, 'settings.json'),
      BILIBILI_LOCAL_CACHE_MANAGER_TRANSCODE_CACHE_ROOT: path.join(smokeDataRoot, 'transcode'),
    }
  : undefined;
if (!supported) {
  app.whenReady().then(() => {
    const message = `当前版本仅支持 Windows/Linux x64；检测到 ${process.platform}/${process.arch}。`;
    if (smokeTest) {
      console.error(`[smoke] ${message}`);
      app.exit(1);
    } else {
      dialog.showErrorBox('不支持的平台', message);
      app.quit();
    }
  });
}

const singleInstance = supported && app.requestSingleInstanceLock();
if (supported && !singleInstance) {
  if (smokeTest) {
    console.error('[smoke] Another application instance already owns the single-instance lock.');
    app.exit(1);
  } else {
    app.quit();
  }
}

let mainWindow: BrowserWindow | null = null;
let unregisterIpc: (() => void) | null = null;
const bridge = new DesktopHostBridge({
  trustedEnvOverrides: trustedHostEnvironmentOverrides,
});
const dirname = __dirname;
let smokeCompleted = false;

function createWindow(): BrowserWindow {
  const developmentUrl = 'http://127.0.0.1:5173/';
  const configuredDevelopmentUrl = process.env.VITE_DEV_SERVER_URL
    ? new URL(process.env.VITE_DEV_SERVER_URL).href
    : null;
  if (configuredDevelopmentUrl && configuredDevelopmentUrl !== developmentUrl) {
    throw new Error('VITE_DEV_SERVER_URL 只能使用 http://127.0.0.1:5173/。');
  }
  const allowedRendererUrl = configuredDevelopmentUrl && !app.isPackaged
    ? developmentUrl
    : packagedRendererUrl;
  const window = new BrowserWindow({
    width: 1440,
    height: 900,
    minWidth: 1080,
    minHeight: 720,
    show: false,
    backgroundColor: '#0b1020',
    autoHideMenuBar: true,
    title: '哔哩哔哩本地缓存管理器',
    webPreferences: {
      preload: path.join(dirname, 'preload.cjs'),
      contextIsolation: true,
      sandbox: true,
      nodeIntegration: false,
      webSecurity: true,
      allowRunningInsecureContent: false,
      spellcheck: false,
      devTools: !app.isPackaged,
    },
  });

  window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  window.webContents.on('will-navigate', (event, destination) => {
    if (destination !== allowedRendererUrl) event.preventDefault();
  });
  if (!smokeTest) window.once('ready-to-show', () => window.show());
  window.on('closed', () => { if (mainWindow === window) mainWindow = null; });

  if (smokeTest) attachSmokeTest(window);
  if (configuredDevelopmentUrl && !app.isPackaged) void window.loadURL(developmentUrl);
  else void window.loadURL(packagedRendererUrl);
  return window;
}

function registerRendererProtocol(): void {
  const rendererRoot = path.resolve(dirname, '..', 'dist');
  protocol.handle(rendererScheme, (request) => {
    const filePath = resolveRendererFilePath(rendererRoot, request.url);
    return filePath
      ? net.fetch(pathToFileURL(filePath).href)
      : new Response('Not found', { status: 404 });
  });
}

function attachSmokeTest(window: BrowserWindow): void {
  const timeout = setTimeout(() => {
    void completeSmokeTest(1, 'Electron smoke test timed out after 30 seconds.');
  }, 30_000);
  window.webContents.once('did-fail-load', (_event, code, description) => {
    clearTimeout(timeout);
    void completeSmokeTest(1, `Renderer failed to load (${code}): ${description}`);
  });
  window.webContents.once('did-finish-load', () => {
    void (async () => {
      try {
        await waitForRendererBootstrap(window);
        clearTimeout(timeout);
        await completeSmokeTest(0, `${smokeReadyToken} Renderer bootstrap, settings load, startup scan, lazy details, and Desktop Host IPC are healthy.`);
      } catch (error) {
        clearTimeout(timeout);
        await completeSmokeTest(1, error instanceof Error ? error.message : String(error));
      }
    })();
  });
}

async function waitForRendererBootstrap(window: BrowserWindow): Promise<void> {
  const expectedRoot = smokeDataRoot ? path.join(smokeDataRoot, 'cache') : '';
  const deadline = Date.now() + 27_000;
  let detailsRequested = false;
  while (Date.now() < deadline) {
    const state = await window.webContents.executeJavaScript(
      '(() => { const shell = document.querySelector("[data-renderer-bootstrap]"); const root = document.querySelector("#root-path"); return { status: shell?.getAttribute("data-renderer-bootstrap") ?? "loading", settingsLoaded: shell?.getAttribute("data-settings-loaded") === "true", startupScan: shell?.getAttribute("data-startup-scan") ?? "", startupScanCount: Number(shell?.getAttribute("data-startup-scan-count") ?? "-1"), hostStatus: shell?.getAttribute("data-host-status") ?? "", rootValue: root instanceof HTMLInputElement ? root.value : "", fixtureVisible: document.body.textContent?.includes("Electron smoke fixture") === true, detailsVisible: document.body.textContent?.includes("Smoke page") === true, error: shell?.getAttribute("data-bootstrap-error") ?? "" }; })()',
      true,
    ) as { status: string; settingsLoaded: boolean; startupScan: string; startupScanCount: number; hostStatus: string; rootValue: string; fixtureVisible: boolean; detailsVisible: boolean; error: string };
    if (state.status === 'failed') {
      throw new Error(state.error || 'Renderer bootstrap failed.');
    }
    const bootstrapReady = state.status === 'ready' &&
        state.settingsLoaded &&
        state.startupScan === 'completed' &&
        state.startupScanCount === 1 &&
        (state.hostStatus === 'ok' || state.hostStatus === 'degraded') &&
        state.rootValue === expectedRoot &&
        state.fixtureVisible;
    if (bootstrapReady && !detailsRequested) {
      const focused = await window.webContents.executeJavaScript(
        '(() => { const row = [...document.querySelectorAll("[data-cache-row=true]")].find((item) => item.textContent?.includes("Electron smoke fixture")); if (!(row instanceof HTMLElement)) return false; row.click(); return true; })()',
        true,
      ) as boolean;
      if (!focused) throw new Error('Renderer did not expose the scanned smoke cache row.');
      detailsRequested = true;
    } else if (bootstrapReady && detailsRequested && state.detailsVisible) {
      return;
    }
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  throw new Error('Renderer did not finish Host-backed bootstrap.');
}

async function completeSmokeTest(exitCode: number, message: string): Promise<void> {
  if (smokeCompleted) return;
  smokeCompleted = true;
  const write = exitCode === 0 ? console.log : console.error;
  write(`[smoke] ${message}`);
  await bridge.dispose();
  if (smokeDataRoot) {
    try { rmSync(smokeDataRoot, { recursive: true, force: true }); } catch { /* Never prevent Electron from exiting. */ }
  }
  app.exit(exitCode);
}

if (supported && singleInstance) {
  app.on('second-instance', () => {
    if (!mainWindow) mainWindow = createWindow();
    if (mainWindow.isMinimized()) mainWindow.restore();
    mainWindow.show();
    mainWindow.focus();
  });

  app.whenReady().then(() => {
    Menu.setApplicationMenu(null);
    registerRendererProtocol();
    session.defaultSession.setPermissionRequestHandler((_contents, _permission, callback) => callback(false));
    session.defaultSession.setPermissionCheckHandler(() => false);
    unregisterIpc = registerIpc(bridge, () => mainWindow);
    mainWindow = createWindow();
  });

  app.on('activate', () => {
    if (!mainWindow) mainWindow = createWindow();
  });

  app.on('window-all-closed', () => {
    if (smokeTest && !smokeCompleted) {
      void completeSmokeTest(1, 'Electron smoke window closed before validation completed.');
      return;
    }
    app.quit();
  });
  app.on('before-quit', () => {
    unregisterIpc?.();
    unregisterIpc = null;
    void bridge.dispose();
  });
}
