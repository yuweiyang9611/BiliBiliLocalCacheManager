// Actual Electron + preload + Host round trip, using a newly created synthetic
// cache only. Moves one fixture part to application trash, then restores it.
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { repository, desktop, projectRequire, createOutput, electronExecutable, electronEnvironment,
  removeFixture, captureFailure } = require('./desktop-e2e-support.cjs');

(async () => {
  const output = await createOutput('task-history');
  const fixture = path.join(output, 'isolated-fixture');
  const root = path.join(fixture, 'cache');
  const settingsPath = path.join(fixture, 'settings.json');
  const profile = path.join(fixture, 'profile');
  const report = { scope: 'Real Electron/Host, synthetic part trash move and restore, two app launches; no player or permanent deletion.', status: 'running', checks: [], screenshots: [], errors: [] };
  let application, page;
  try {
  await fs.mkdir(root, { recursive: true });
  const hostPath = path.join(repository, 'BiliBiliLocalCacheManager.Desktop.Host', 'bin', 'Release', 'net11.0', 'BiliBiliLocalCacheManager.Desktop.Host.dll');
  await fs.access(hostPath);
  await fs.access(path.join(desktop, 'dist-electron', 'main.cjs'));
  for (let page = 1; page <= 2; page++) {
    const segment = path.join(root, '990002', `c_${page}`);
    const media = path.join(segment, 'lua.flv.bb2api.80');
    await fs.mkdir(media, { recursive: true });
    await fs.writeFile(path.join(segment, 'entry.json'), JSON.stringify({ is_completed: true, total_bytes: 16, downloaded_bytes: 16,
      title: '任务历史验证缓存', type_tag: '80', cover: 'synthetic', prefered_video_quality: 80, total_time_milli: 1000,
      danmaku_count: 0, time_update_stamp: 1750000000000, time_create_stamp: 1750000000000,
      avid: 990002, bvid: 'BVHistoryFixture', owner_name: 'Fixture',
      page_data: { cid: 9900020 + page, page, part: `Fixture P${page}`, from: 'local', vid: 'fixture', tid: 0 } }));
    await fs.writeFile(path.join(media, '0.mp4'), 'synthetic-media');
  }
  await fs.writeFile(settingsPath, JSON.stringify({ SchemaVersion: 2, RootPath: root, RememberRootPath: true, ScanOnStartup: true }));
  const wrapper = path.join(output, 'entry.cjs');
  await fs.writeFile(wrapper, `const {app}=require('electron');
if(app.isPackaged)throw new Error('Development build required');
app.setPath('userData',${JSON.stringify(profile)});app.disableHardwareAcceleration();
require(${JSON.stringify(path.join(desktop, 'dist-electron', 'main.cjs'))});`);
  const { _electron } = projectRequire('playwright');
  const env = electronEnvironment({ CACHE_MANAGER_HOST_PATH: hostPath,
    BILIBILI_LOCAL_CACHE_MANAGER_SETTINGS_PATH: settingsPath, BILIBILI_LOCAL_CACHE_MANAGER_TRANSCODE_CACHE_ROOT: path.join(fixture, 'transcode') });
  const launch = async () => {
    application = await _electron.launch({ executablePath: electronExecutable(), args: [wrapper], env, cwd: desktop, chromiumSandbox: true, timeout: 60_000 });
    page = await application.firstWindow({ timeout: 60_000 });
    page.setDefaultTimeout(30_000);
    page.on('pageerror', error => report.errors.push({ message: error.message }));
    await page.waitForFunction(() => document.querySelector('[data-renderer-bootstrap]')?.getAttribute('data-renderer-bootstrap') === 'ready', undefined, { timeout: 60_000 });
    return page;
  };
    page = await launch();
    // Exercise the real registered IPC guard while a native dialog is pending,
    // before any HostCall exists. The stub never opens an OS dialog.
    await application.evaluate(({ dialog }) => {
      globalThis.__blcmE2EOriginalDialog = dialog.showOpenDialog;
      dialog.showOpenDialog = () => new Promise(resolve => { globalThis.__blcmE2EReleaseDialog = resolve; });
    });
    try {
      await page.evaluate(() => { window.__blcmE2EPendingDialog = window.cacheManager.chooseRootDirectory(); });
      assert(await application.evaluate(async () => {
        for (let attempt = 0; attempt < 100; attempt++) {
          if (globalThis.__blcmE2EReleaseDialog) return true;
          await new Promise(resolve => setTimeout(resolve, 10));
        }
        return false;
      }), 'The native dialog IPC did not reach its test stub.');
      assert.equal(await page.evaluate(() => window.cacheManager.reloadWhenIdle()), false);
    } finally {
      await application.evaluate(({ dialog }) => {
        dialog.showOpenDialog = globalThis.__blcmE2EOriginalDialog;
        globalThis.__blcmE2EReleaseDialog?.({ canceled: true, filePaths: [] });
        delete globalThis.__blcmE2EOriginalDialog; delete globalThis.__blcmE2EReleaseDialog;
      });
    }
    assert.equal(await page.evaluate(() => window.__blcmE2EPendingDialog), null);
    await page.evaluate(() => { window.__blcmE2EReloadMarker = true; });
    await page.evaluate(() => window.cacheManager.health());
    await Promise.all([
      page.waitForEvent('domcontentloaded'),
      page.evaluate(() => { void window.cacheManager.reloadWhenIdle().catch(() => undefined); }).catch(error => {
        if (!/Execution context was destroyed|Cannot find context/.test(String(error))) throw error;
      }),
    ]);
    await page.waitForFunction(() => document.querySelector('[data-renderer-bootstrap]')?.getAttribute('data-renderer-bootstrap') === 'ready', undefined, { timeout: 60_000 });
    assert.equal(await page.evaluate(() => window.__blcmE2EReloadMarker), undefined);
    report.checks.push('Real main-process IPC refuses reload during a pending native dialog, then reloads after it settles.');
    const result = await page.evaluate(async rootPath => {
      const api = window.cacheManager;
      const scan = await api.scan({ rootPath, includeIncomplete: false });
      const moved = await api.moveToTrash(rootPath, scan.indexToken, [{ avid: '990002', pageIndexes: [1] }]);
      return { moved, history: await api.getTaskHistory() };
    }, root);
    assert.equal(result.moved.moved.length, 1);
    assert.equal(result.moved.failed.length, 0);
    assert.equal(result.history.entries[0].operation, 'trash.move');
    assert.equal(result.history.entries[0].status, 'succeeded');
    assert.equal(result.history.warning, undefined);
    const movedId = result.history.entries[0].id;
    report.checks.push('Actual Host part move is persisted before its IPC response completes.');
    await application.close(); application = undefined;
    page = await launch();
    const restored = await page.evaluate(async ({ rootPath, entryIds }) => {
      const before = await window.cacheManager.getTaskHistory();
      const restored = await window.cacheManager.restoreTrash(rootPath, entryIds);
      return { before, restored, after: await window.cacheManager.getTaskHistory() };
    }, { rootPath: root, entryIds: result.moved.entryIds });
    assert(restored.before.entries.some(item => item.id === movedId && item.status === 'succeeded'));
    assert.equal(restored.restored.restored.length, 1);
    assert.equal(restored.after.entries[0].operation, 'trash.restore');
    assert.equal(restored.after.entries[0].status, 'succeeded');
    await fs.access(path.join(root, '990002', 'c_1', 'entry.json'));
    report.checks.push('History survives a full application restart; the real fixture part is restored.');
    await page.getByRole('button', { name: '诊断', exact: true }).click();
    await page.getByRole('heading', { name: '任务历史' }).waitFor();
    await page.locator('.history-entry').first().locator('summary').click();
    await page.waitForFunction(() => document.querySelectorAll('.toast').length === 0);
    for (const [width, height] of [[1440, 900], [1080, 720]]) {
      await application.evaluate(({ BrowserWindow }, size) => BrowserWindow.getAllWindows()[0].setSize(...size), [width, height]);
      await page.locator('.task-history').scrollIntoViewIfNeeded();
      const layout = await page.evaluate(() => ({ overflow: document.documentElement.scrollWidth > innerWidth,
        historyOverflow: document.querySelector('.task-history').scrollWidth > document.querySelector('.task-history').clientWidth,
        count: document.querySelectorAll('.history-entry').length }));
      assert.equal(layout.overflow, false); assert.equal(layout.historyOverflow, false); assert.equal(layout.count, 2);
      const screenshot = `history-${width}.png`;
      await page.screenshot({ path: path.join(output, screenshot) });
      report.screenshots.push({ screenshot, width, height, ...layout });
    }
    assert.equal(report.errors.length, 0, 'Unexpected renderer error.');
    report.status = 'passed';
  } catch (error) {
    await captureFailure(page, output, report, error, application); process.exitCode = 1;
  } finally {
    if (application) await application.close().catch(error => { report.status = 'failed'; report.errors.push({ message: String(error) }); process.exitCode = 1; });
    if (report.status === 'passed') {
      try { await removeFixture(output, fixture); }
      catch (error) { report.status = 'failed'; report.errors.push({ message: `Fixture cleanup failed: ${error}` }); process.exitCode = 1; }
    }
    await fs.writeFile(path.join(output, 'results.json'), JSON.stringify(report, null, 2));
  }
  console.log(JSON.stringify({ output, ...report }, null, 2));
})().catch(error => { console.error(error); process.exitCode = 1; });
