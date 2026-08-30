import { BrowserWindow, Menu, app, dialog, net, protocol, session } from 'electron';
import { mkdirSync, mkdtempSync, rmSync } from 'node:fs';
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
if (smokeTest) app.disableHardwareAcceleration();

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
        const rendererReady = await waitForRendererReady(window);
        if (!rendererReady) throw new Error('React renderer did not mount.');
        const health = await bridge.call<{ status?: string }>('health', {}, 15_000).promise;
        if (health?.status !== 'ok' && health?.status !== 'degraded') {
          throw new Error('Desktop Host returned an invalid health status.');
        }
        clearTimeout(timeout);
        await completeSmokeTest(0, 'Electron renderer and Desktop Host are healthy.');
      } catch (error) {
        clearTimeout(timeout);
        await completeSmokeTest(1, error instanceof Error ? error.message : String(error));
      }
    })();
  });
}

async function waitForRendererReady(window: BrowserWindow): Promise<boolean> {
  for (let attempt = 0; attempt < 100; attempt++) {
    const ready = await window.webContents.executeJavaScript(
      'document.querySelector("[data-renderer-ready=true]") !== null',
      true,
    );
    if (ready) return true;
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  return false;
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

  app.on('window-all-closed', () => app.quit());
  app.on('before-quit', () => {
    unregisterIpc?.();
    unregisterIpc = null;
    void bridge.dispose();
  });
}
