'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const { readFileSync, existsSync } = require('node:fs');
const { createRequire } = require('node:module');
const path = require('node:path');

const repository = path.resolve(__dirname, '..');
const desktop = path.join(repository, 'BiliBiliLocalCacheManager.Desktop');
const projectRequire = createRequire(path.join(desktop, 'package.json'));
const artifactRoot = path.join(repository, 'artifacts');

function inside(parent, child) {
  const relative = path.relative(parent, child);
  return relative !== '' && relative !== '..' && !relative.startsWith('..' + path.sep) && !path.isAbsolute(relative);
}

function electronExecutable() {
  const packageDirectory = path.dirname(projectRequire.resolve('electron/package.json'));
  const declared = projectRequire('electron/package.json').version;
  let executable;
  try {
    const relative = readFileSync(path.join(packageDirectory, 'path.txt'), 'utf8').trim();
    assert(relative && !path.isAbsolute(relative));
    executable = path.resolve(packageDirectory, 'dist', relative);
    assert(inside(path.join(packageDirectory, 'dist'), executable));
    assert.equal(readFileSync(path.join(packageDirectory, 'dist', 'version'), 'utf8').trim().replace(/^v/, ''), declared);
    assert(existsSync(executable));
  } catch {
    throw new Error('The declared Electron runtime is missing. Run npm run setup:e2e in BiliBiliLocalCacheManager.Desktop.');
  }
  return executable;
}

function electronEnvironment(overrides = {}) {
  const environment = { ...process.env, ...overrides };
  delete environment.ELECTRON_RUN_AS_NODE;
  delete environment.ELECTRON_OVERRIDE_DIST_PATH;
  delete environment.VITE_DEV_SERVER_URL;
  return environment;
}

async function createOutput(name) {
  const parent = path.resolve(process.env.BLCM_E2E_ARTIFACTS_DIR ?? path.join(artifactRoot, 'e2e'));
  assert(inside(artifactRoot, parent), 'E2E output must be inside repository artifacts/.');
  await fs.mkdir(parent, { recursive: true });
  assert(inside(await fs.realpath(artifactRoot), await fs.realpath(parent)), 'E2E output cannot escape artifacts/ through a link.');
  return fs.mkdtemp(path.join(parent, name + '-'));
}

async function removeFixture(output, fixture) {
  const resolvedOutput = await fs.realpath(output);
  const resolvedFixture = await fs.realpath(fixture);
  assert.equal(resolvedFixture, path.join(resolvedOutput, 'isolated-fixture'));
  assert(!(await fs.lstat(fixture)).isSymbolicLink());
  await fs.rm(resolvedFixture, { recursive: true });
}

async function captureFailure(page, output, report, error, application) {
  report.status = 'failed';
  (report.errors ??= []).push({ message: error.stack ?? String(error) });
  if (!page || page.isClosed()) return;
  try { await page.screenshot({ path: path.join(output, 'failure.png'), timeout: 5000 }); }
  catch {
    if (application) {
      const screenshot = await application.evaluate(async ({ BrowserWindow }) =>
        (await BrowserWindow.getAllWindows()[0].capturePage()).toPNG().toString('base64')).catch(() => null);
      if (screenshot) await fs.writeFile(path.join(output, 'failure.png'), Buffer.from(screenshot, 'base64'));
    }
  }
  report.failureState = await page.evaluate(() => ({ url: location.href, text: document.body?.innerText,
    bootstrap: document.querySelector('[data-renderer-bootstrap]')?.getAttribute('data-renderer-bootstrap') })).catch(() => null);
}

module.exports = { repository, desktop, projectRequire, artifactRoot, inside, electronExecutable,
  electronEnvironment, createOutput, removeFixture, captureFailure };
