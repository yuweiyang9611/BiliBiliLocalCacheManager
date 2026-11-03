#!/usr/bin/env node
'use strict';

// Bootstrap only the runtime belonging to the lockfile's Electron package.
// Playwright drives this Electron; no separate browser download is needed.
const { spawnSync } = require('node:child_process');
const path = require('node:path');
const { desktop, projectRequire, electronExecutable, electronEnvironment } = require('./desktop-e2e-support.cjs');

if (!['win32', 'linux'].includes(process.platform) || process.arch !== 'x64') {
  throw new Error('Desktop E2E supports Windows/Linux x64, matching the application.');
}
projectRequire('playwright');
try { console.log(`Electron ready: ${electronExecutable()}`); }
catch {
  const installer = path.join(path.dirname(projectRequire.resolve('electron/package.json')), 'install.js');
  const result = spawnSync(process.execPath, [installer], { cwd: desktop, env: electronEnvironment(), stdio: 'inherit', windowsHide: true });
  if (result.error) throw result.error;
  if (result.status !== 0) process.exit(result.status ?? 1);
  console.log(`Electron ready: ${electronExecutable()}`);
}
if (process.platform === 'linux' && !process.env.DISPLAY) {
  console.log('Run the E2E entry under X11/XWayland or xvfb-run --auto-servernum.');
}
