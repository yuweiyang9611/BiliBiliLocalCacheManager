#!/usr/bin/env node
'use strict';

// Development-build acceptance probe. Never pass a packaged application or a real cache directory.
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const { performance } = require('node:perf_hooks');
const { execFileSync } = require('node:child_process');

const repository = path.resolve(__dirname, '..');
const desktop = path.join(repository, 'BiliBiliLocalCacheManager.Desktop');
const options = new Map(process.argv.slice(2).map(argument => {
  const split = argument.indexOf('=');
  return split < 0 ? [argument, true] : [argument.slice(0, split), argument.slice(split + 1)];
}));
const count = Number(options.get('--count') ?? 10_000);
assert(Number.isInteger(count) && count >= 6_000 && count <= 50_000, '--count must be 6000..50000 to measure five filtered page transitions.');
const artifactRoot = path.join(repository, 'artifacts');
const output = path.resolve(String(options.get('--output') ?? path.join(artifactRoot, `desktop-requirements-${Date.now()}`)));
assert(output.startsWith(artifactRoot + path.sep), 'Output must be a child of repository artifacts/.');
const fixtureRoot = path.join(output, 'isolated-fixture');
const cacheRoot = path.join(fixtureRoot, 'cache');
const settingsPath = path.join(fixtureRoot, 'settings.json');
const hostPath = path.join(repository, 'BiliBiliLocalCacheManager.Desktop.Host', 'bin', 'Release', 'net10.0', 'BiliBiliLocalCacheManager.Desktop.Host.dll');
const mainPath = path.join(desktop, 'dist-electron', 'main.cjs');
const report = {
  kind: 'development-electron-real-host-synthetic-cache',
  startedAt: new Date().toISOString(),
  data: { videos: count, pagesPerVideo: 2, segments: count * 2, syntheticMedia: true },
  scope: 'Real renderer, preload, main IPC and .NET Host; no player, export, trash move or permanent deletion executed.',
  machine: { platform: process.platform, os: os.release(), cpu: os.cpus()[0]?.model, logicalProcessors: os.cpus().length, memoryBytes: os.totalmem(), node: process.version },
  searchMilliseconds: [],
  pageMilliseconds: [],
  screenshots: [],
  checks: [],
  errors: [],
};
let ownsOutput = false;

function distribution(values) {
  const ordered = [...values].sort((a, b) => a - b);
  return { samples: ordered.length, min: ordered[0], median: ordered[Math.floor(ordered.length / 2)], max: ordered.at(-1) };
}

async function buildFixture() {
  // Refuse reuse so a previous run or arbitrary user data cannot be overwritten.
  await fs.mkdir(path.dirname(output), { recursive: true });
  await fs.mkdir(output);
  ownsOutput = true;
  await fs.mkdir(fixtureRoot);
  const began = performance.now();
  let next = 0;
  await Promise.all(Array.from({ length: 16 }, async () => {
    while (next < count) {
      const index = next++;
      const avid = 800000 + index;
      for (let page = 1; page <= 2; page++) {
        const segment = path.join(cacheRoot, String(avid), `c_${page}`);
        const media = path.join(segment, 'lua.flv.bb2api.80');
        await fs.mkdir(media, { recursive: true });
        await fs.writeFile(path.join(segment, 'entry.json'), JSON.stringify({
          is_completed: true, total_bytes: 16, downloaded_bytes: 16,
          title: `Validation Bucket${index % 10} Video${String(index).padStart(5, '0')}`,
          type_tag: '80', cover: 'synthetic', prefered_video_quality: 80, total_time_milli: 60000,
          danmaku_count: 0, time_update_stamp: 1750000000000 + index, time_create_stamp: 1750000000000,
          avid, bvid: `BVFixture${index}`, owner_name: `Fixture creator ${index % 20}`,
          page_data: { cid: avid * 10 + page, page, part: `Fixture ${index} P${page}`, from: 'local', vid: 'fixture', tid: 0 },
        }));
        await fs.writeFile(path.join(media, '0.mp4'), 'synthetic-media');
      }
    }
  }));
  await fs.writeFile(settingsPath, JSON.stringify({ SchemaVersion: 2, RootPath: cacheRoot,
    RememberRootPath: true, ScanOnStartup: true, IncludeIncomplete: false, IncludePartName: false }));
  report.fixturePreparationMilliseconds = performance.now() - began;
}

async function screenshot(page, name) {
  const file = path.join(output, name + '.png');
  await page.screenshot({ path: file });
  const layout = await page.evaluate(() => {
    const dialog = document.querySelector('[role="dialog"]');
    const box = dialog?.getBoundingClientRect();
    return { viewport: { width: innerWidth, height: innerHeight },
      documentWidth: document.documentElement.scrollWidth, documentClientWidth: document.documentElement.clientWidth,
      dialog: box ? { left: box.left, top: box.top, right: box.right, bottom: box.bottom } : null,
      renderedCacheRows: document.querySelectorAll('[data-cache-row]').length };
  });
  report.screenshots.push({ file: path.basename(file), ...layout });
  assert(layout.documentWidth <= layout.documentClientWidth + 1, `Document horizontal overflow in ${name}`);
  if (layout.dialog) {
    assert(layout.dialog.left >= -1 && layout.dialog.top >= -1 &&
      layout.dialog.right <= layout.viewport.width + 1 && layout.dialog.bottom <= layout.viewport.height + 1,
    `Dialog exceeds viewport in ${name}`);
  }
}

async function waitForResults(page, keyword, expectedCount) {
  await page.waitForFunction(({ keyword, expectedCount }) => {
    const total = document.querySelector('.cache-panel .panel-heading span')?.textContent?.trim();
    const title = document.querySelector('[data-cache-row] .title-cell')?.textContent ?? '';
    return total === `${expectedCount} 项` && title.includes(keyword);
  }, { keyword, expectedCount }, { timeout: 30_000, polling: 'raf' });
}

async function runUi(electron) {
  const launchEnvironment = { ...process.env,
    CACHE_MANAGER_HOST_PATH: hostPath,
    BILIBILI_LOCAL_CACHE_MANAGER_SETTINGS_PATH: settingsPath,
    BILIBILI_LOCAL_CACHE_MANAGER_TRANSCODE_CACHE_ROOT: path.join(fixtureRoot, 'transcode'),
  };
  delete launchEnvironment.ELECTRON_RUN_AS_NODE;
  delete launchEnvironment.VITE_DEV_SERVER_URL;
  const wrapper = path.join(output, 'isolated-entry.cjs');
  await fs.writeFile(wrapper, `const {app}=require('electron');
if(app.isPackaged) throw new Error('Only an unpackaged development build is allowed.');
app.setPath('userData',${JSON.stringify(path.join(fixtureRoot, 'electron-profile'))});
app.disableHardwareAcceleration();
require(${JSON.stringify(mainPath)});
`);
  const electronExecutable = require(path.join(desktop, 'node_modules', 'electron'));
  const started = performance.now();
  const application = await electron.launch({ executablePath: electronExecutable, args: [wrapper], env: launchEnvironment, timeout: 60_000 });
  let page;
  try {
    report.source = { baseCommit: execFileSync('git', ['rev-parse', 'HEAD'], { cwd: repository, encoding: 'utf8' }).trim(),
      dirtyWorktree: Boolean(execFileSync('git', ['status', '--porcelain'], { cwd: repository, encoding: 'utf8' }).trim()) };
    page = await application.firstWindow({ timeout: 60_000 });
    page.on('pageerror', error => report.errors.push({ source: 'renderer', message: error.message }));
    await page.waitForFunction(() => document.querySelector('[data-renderer-bootstrap]')?.getAttribute('data-startup-scan') === 'completed',
      undefined, { timeout: 180_000 });
    await waitForResults(page, 'Validation', count);
    report.startupThroughScanMilliseconds = performance.now() - started;
    assert.equal(await page.locator('#root-path').inputValue(), cacheRoot);
    const isolation = await application.evaluate(({ app }) => ({ userData: app.getPath('userData'), packaged: app.isPackaged }));
    assert.equal(isolation.userData, path.join(fixtureRoot, 'electron-profile'));
    assert.equal(isolation.packaged, false);
    report.isolation = isolation;
    const search = page.getByPlaceholder('搜索标题、UP 主、BV 号或 AV 号');
    await search.evaluate(element => element.addEventListener('input', () => { window.__lastRequirementInput = performance.now(); }));
    for (let bucket = 0; bucket < 5; bucket++) {
      const keyword = `Bucket${bucket}`;
      await search.fill(keyword);
      await waitForResults(page, keyword, Math.floor(count / 10) + (bucket < count % 10 ? 1 : 0));
      report.searchMilliseconds.push(await page.evaluate(() => performance.now() - window.__lastRequirementInput));
    }
    const pagination = page.locator('[aria-label="缓存分页"]');
    for (let index = 1; index <= 5; index++) {
      const next = pagination.getByRole('button', { name: '下一页', exact: true });
      await next.evaluate(element => element.addEventListener('click', () => { window.__lastRequirementPage = performance.now(); }, { once: true }));
      const previousTitle = await page.locator('[data-cache-row] .title-cell').first().textContent();
      await next.click();
      await page.waitForFunction(({ index, previousTitle }) => {
        const range = document.querySelector('[aria-label="缓存分页"] > span')?.textContent ?? '';
        const title = document.querySelector('[data-cache-row] .title-cell')?.textContent;
        return range.startsWith(`${index * 100 + 1}–`) && title !== previousTitle;
      }, { index, previousTitle }, { polling: 'raf' });
      report.pageMilliseconds.push(await page.evaluate(() => performance.now() - window.__lastRequirementPage));
    }
    report.searchSummary = distribution(report.searchMilliseconds);
    report.pageSummary = distribution(report.pageMilliseconds);
    report.performanceTargets = { searchMilliseconds: 1000, pageMilliseconds: 500,
      searchObservedWithinTarget: report.searchSummary.max <= 1000, pageObservedWithinTarget: report.pageSummary.max <= 500,
      note: 'Local observations, not portable CI timing gates; search includes the 350 ms input debounce.' };

    await search.fill('');
    await waitForResults(page, 'Validation', count);
    await page.locator('[data-cache-row] input[type="checkbox"]').first().check();
    await pagination.getByRole('button', { name: '下一页', exact: true }).click();
    await page.waitForFunction(() => document.querySelector('[aria-label="缓存分页"] > span')?.textContent?.startsWith('101–'));
    await page.locator('[data-cache-row] input[type="checkbox"]').first().check();
    await page.getByRole('button', { name: '查看已选清单' }).click();
    assert.equal(await page.locator('[role="dialog"] [aria-label="已选清单"] > li').count(), 2);
    report.checks.push('Video selections survive pagination and both appear in the complete selection list.');
    await screenshot(page, '01-desktop-video-selection');
    await page.getByRole('button', { name: '关闭', exact: true }).click();

    await page.locator('[data-cache-row]').first().locator('.title-cell').click();
    await page.locator('.segment-panel tbody tr').first().waitFor();
    const firstPart = page.locator('.segment-panel tbody input[type="checkbox"]').first();
    await firstPart.check();
    await page.locator('[data-cache-row]').nth(1).locator('.title-cell').click();
    const selectedTitle = await page.locator('[data-cache-row]').nth(1).locator('.title-cell').textContent();
    await page.waitForFunction(title => document.querySelector('.segment-panel .panel-heading span')?.textContent?.startsWith(title), selectedTitle);
    await page.locator('.segment-panel tbody input[type="checkbox"]').first().check();
    await page.getByRole('button', { name: '查看已选清单' }).click();
    const selectedParts = page.locator('[role="dialog"] [aria-label="已选清单"] > li');
    assert.equal(await selectedParts.count(), 2);
    assert((await selectedParts.allTextContents()).every(text => /P1/.test(text)));
    report.checks.push('Part selection replaces video mode and remains selected across two different videos.');
    await screenshot(page, '02-desktop-part-selection');
    await page.getByRole('button', { name: '关闭', exact: true }).click();
    await page.getByRole('button', { name: '删除', exact: true }).click();
    assert.equal(await page.locator('[role="dialog"] [aria-label="操作范围"] > li').count(), 2);
    await screenshot(page, '03-desktop-delete-confirmation');
    report.checks.push('Part deletion shows the exact two-item scope; confirmation was cancelled without mutating cache.');
    await application.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].setSize(1080, 720));
    await page.waitForTimeout(200);
    await screenshot(page, '04-minimum-window-confirmation');
    await page.getByRole('button', { name: '取消', exact: true }).click();
    await screenshot(page, '05-minimum-window-library');
    await application.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].setSize(1440, 900));
    await page.waitForTimeout(200);
    await screenshot(page, '06-desktop-library');
    report.processMetrics = await application.evaluate(({ app }) => app.getAppMetrics().map(value => ({
      type: value.type, cpu: value.cpu, memory: value.memory,
    })));
    assert.equal(report.errors.length, 0, 'Renderer emitted errors.');
  } catch (error) {
    if (page) await page.screenshot({ path: path.join(output, 'failure.png') }).catch(() => {});
    throw error;
  } finally {
    await application.close();
  }
}

(async () => {
  let failed;
  try {
    await fs.access(mainPath);
    await fs.access(hostPath);
    if (process.platform === 'win32') {
      try { report.machine.disks = JSON.parse(execFileSync('powershell.exe', ['-NoProfile', '-Command',
        'Get-PhysicalDisk | Select-Object FriendlyName,MediaType,BusType,Size | ConvertTo-Json -Compress'], { encoding: 'utf8' })); }
      catch { report.machine.disks = 'Unavailable'; }
    }
    const runtimeModules = path.join(os.homedir(), '.cache', 'codex-runtimes', 'codex-primary-runtime', 'dependencies', 'node', 'node_modules');
    const playwright = require(require.resolve('playwright', { paths: [String(options.get('--playwright-modules') ?? runtimeModules), desktop] }));
    await buildFixture();
    await runUi(playwright._electron);
    report.status = 'completed';
  } catch (error) {
    failed = error;
    report.status = 'failed';
    report.errors.push({ source: 'verification', message: error.stack ?? String(error) });
    process.exitCode = 1;
  } finally {
    report.finishedAt = new Date().toISOString();
    if (ownsOutput) await fs.writeFile(path.join(output, 'results.json'), JSON.stringify(report, null, 2));
    // Only our newly created fixture directory can be removed; screenshots and measurements remain.
    if (ownsOutput && !options.has('--keep-fixture') && !failed) {
      const resolved = await fs.realpath(fixtureRoot);
      assert(resolved === fixtureRoot && resolved.startsWith(output + path.sep));
      await fs.rm(resolved, { recursive: true });
    }
    console.log(JSON.stringify({ status: report.status, output, search: report.searchSummary, paging: report.pageSummary, errors: report.errors }, null, 2));
  }
})();
