#!/usr/bin/env node
'use strict';

// Synthesized keyboard input through real Electron, preload, IPC and Host.
// All media, settings and profile data belong to this new synthetic fixture.
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { repository, desktop, projectRequire, createOutput, electronExecutable, electronEnvironment,
  removeFixture, captureFailure } = require('./desktop-e2e-support.cjs');

async function bounded(promise, milliseconds, message) {
  let timer;
  try { return await Promise.race([promise, new Promise((_, reject) => { timer = setTimeout(() => reject(new Error(message)), milliseconds); })]); }
  finally { clearTimeout(timer); }
}

(async () => {
  const output = await createOutput('keyboard');
  const fixture = path.join(output, 'isolated-fixture');
  const root = path.join(fixture, 'cache');
  const settingsPath = path.join(fixture, 'settings.json');
  const report = { scope: 'Real Electron/Host and synthesized keyboard input; synthetic metadata only; no player, export or deletion.',
    status: 'running', checks: [], screenshots: [], errors: [] };
  let application, page;
  try {
    await fs.mkdir(root, { recursive: true });
    const hostPath = path.join(repository, 'BiliBiliLocalCacheManager.Desktop.Host', 'bin', 'Release', 'net11.0', 'BiliBiliLocalCacheManager.Desktop.Host.dll');
    await fs.access(hostPath);
    await fs.access(path.join(desktop, 'dist-electron', 'main.cjs'));
    for (let video = 0; video < 60; video++) {
      const avid = 990100 + video;
      for (let part = 1; part <= 2; part++) {
        const segment = path.join(root, String(avid), `c_${part}`);
        const media = path.join(segment, 'lua.flv.bb2api.80');
        await fs.mkdir(media, { recursive: true });
        await fs.writeFile(path.join(segment, 'entry.json'), JSON.stringify({ is_completed: true, total_bytes: 16, downloaded_bytes: 16,
          title: `键盘验证缓存 ${String(video).padStart(2, '0')}`, type_tag: '80', cover: 'synthetic', prefered_video_quality: 80, total_time_milli: 1000,
          danmaku_count: 0, time_update_stamp: 1750000000000, time_create_stamp: 1750000000000,
          avid, bvid: `BVKeyboardFixture${video}`, owner_name: 'Fixture',
          page_data: { cid: avid * 10 + part, page: part, part: `键盘验证 P${part}`, from: 'local', vid: 'fixture', tid: 0 } }));
        await fs.writeFile(path.join(media, '0.mp4'), 'synthetic-media');
      }
    }
    await fs.writeFile(settingsPath, JSON.stringify({ SchemaVersion: 2, RootPath: root, RememberRootPath: true, ScanOnStartup: true }));
    const wrapper = path.join(fixture, 'entry.cjs');
    await fs.writeFile(wrapper, `const {app,ipcMain,dialog}=require('electron');
if(app.isPackaged)throw new Error('Development build required');
app.setPath('userData',${JSON.stringify(path.join(fixture, 'profile'))});app.disableHardwareAcceleration();
globalThis.__blcmKeyboardCalls=[];
const handle=ipcMain.handle.bind(ipcMain);
ipcMain.handle=(channel,listener)=>handle(channel,(event,...args)=>{globalThis.__blcmKeyboardCalls.push(channel);return listener(event,...args);});
dialog.showOpenDialog=async()=>({canceled:true,filePaths:[]});
require(${JSON.stringify(path.join(desktop, 'dist-electron', 'main.cjs'))});`);
    const { _electron } = projectRequire('playwright');
    application = await _electron.launch({ executablePath: electronExecutable(), args: [wrapper], cwd: desktop,
      env: electronEnvironment({ CACHE_MANAGER_HOST_PATH: hostPath, BILIBILI_LOCAL_CACHE_MANAGER_SETTINGS_PATH: settingsPath,
        BILIBILI_LOCAL_CACHE_MANAGER_TRANSCODE_CACHE_ROOT: path.join(fixture, 'transcode') }), chromiumSandbox: true, timeout: 60_000 });
    page = await application.firstWindow({ timeout: 60_000 });
    page.setDefaultTimeout(30_000);
    page.on('pageerror', error => report.errors.push({ message: error.message }));
    await page.waitForFunction(() => document.querySelector('[data-renderer-bootstrap]')?.getAttribute('data-renderer-bootstrap') === 'ready', undefined, { timeout: 90_000 });
    console.log(JSON.stringify({ stage: 'renderer-ready', output }));
    const callCounts = () => application.evaluate(() => {
      const names = { scan: 'scan', export: 'export:media', play: 'play', move: 'trash:move', restore: 'trash:restore', purge: 'trash:purge-snapshot' };
      return Object.fromEntries(Object.entries(names).map(([name, suffix]) => [name,
        globalThis.__blcmKeyboardCalls.filter(channel => channel === 'cache-manager:' + suffix).length]));
    });
    for (const [width, height] of [[1440, 900], [1080, 720]]) {
      console.log(JSON.stringify({ stage: 'keyboard-checks', width, height }));
      await application.evaluate(({ BrowserWindow }, size) => BrowserWindow.getAllWindows()[0].setSize(...size), [width, height]);
      await page.waitForTimeout(150);
      const first = page.getByRole('button', { name: '查看 键盘验证缓存 00 的分 P' });
      await first.focus();
      await page.keyboard.press('End');
      await page.waitForFunction(() => document.activeElement?.getAttribute('aria-label') === '查看 键盘验证缓存 59 的分 P');
      await page.keyboard.press('Enter');
      await page.locator('.segment-panel').getByText('键盘验证 P1', { exact: true }).waitFor();
      assert.equal(await page.evaluate(() => document.activeElement?.getAttribute('aria-label')), '查看 键盘验证缓存 59 的分 P');
      assert(await page.locator('[data-cache-row]').count() < 25);
      await page.keyboard.press('Home');
      await page.waitForFunction(() => document.activeElement?.getAttribute('aria-label') === '查看 键盘验证缓存 00 的分 P');
      await page.keyboard.press('Enter');
      await page.locator('.segment-panel').getByText('键盘验证 P1', { exact: true }).waitFor();
      await page.keyboard.press('Shift+Tab');
      assert.equal(await page.evaluate(() => document.activeElement?.getAttribute('aria-label')), '选择 键盘验证缓存 00');
      await page.keyboard.press('Space');
      const deletion = page.getByRole('button', { name: '删除', exact: true });
      await deletion.focus(); await page.keyboard.press('Enter');
      const dialog = page.getByRole('dialog');
      await dialog.waitFor();
      assert.equal(await dialog.getByRole('button', { name: '取消', exact: true }).evaluate(element => element === document.activeElement), true);
      await page.keyboard.press('Tab');
      assert.equal(await dialog.getByRole('button', { name: '确认', exact: true }).evaluate(element => element === document.activeElement), true);
      await page.keyboard.press('Tab');
      assert.equal(await dialog.getByRole('button', { name: '关闭', exact: true }).evaluate(element => element === document.activeElement), true);
      await page.keyboard.press('Shift+Tab');
      assert.equal(await dialog.getByRole('button', { name: '确认', exact: true }).evaluate(element => element === document.activeElement), true);
      assert.equal(await page.locator('#root').evaluate(element => element.inert), true);
      await page.locator('.sidebar button').first().evaluate(element => element.focus());
      assert.equal(await dialog.evaluate(element => element.contains(document.activeElement)), true);
      const before = await callCounts();
      await page.keyboard.press('F5');
      await page.keyboard.press('Control+E');
      await page.keyboard.press('Control+Z');
      await page.keyboard.press('Control+F');
      await page.keyboard.press('Delete');
      assert.deepEqual(await callCounts(), before);
      assert.equal(await dialog.evaluate(element => element.contains(document.activeElement)), true);
      const layout = await dialog.evaluate(element => {
        const rect = element.getBoundingClientRect();
        return { overflow: document.documentElement.scrollWidth > innerWidth,
          dialogVisible: rect.left >= 0 && rect.top >= 0 && rect.right <= innerWidth && rect.bottom <= innerHeight,
          viewportWidth: innerWidth, viewportHeight: innerHeight };
      });
      assert.equal(layout.overflow, false); assert.equal(layout.dialogVisible, true);
      const screenshot = `keyboard-${width}.png`;
      console.log(JSON.stringify({ stage: 'capture', width, height }));
      const captured = await bounded(application.evaluate(async ({ BrowserWindow }) =>
        (await BrowserWindow.getAllWindows()[0].capturePage()).toPNG().toString('base64')), 10_000, 'Native keyboard screenshot timed out.');
      await fs.writeFile(path.join(output, screenshot), Buffer.from(captured, 'base64'));
      report.screenshots.push({ screenshot, width, height, ...layout });
      await page.keyboard.press('Escape');
      await dialog.waitFor({ state: 'detached' });
      assert.equal(await deletion.evaluate(element => element === document.activeElement), true);
      assert.equal(await page.locator('#root').evaluate(element => element.inert), false);
      await page.keyboard.press('F5');
      await page.waitForFunction(() => !document.querySelector('.modal') && document.querySelector('[data-renderer-ready="true"]') &&
        ![...document.querySelectorAll('button')].find(button => button.textContent.includes('扫描缓存'))?.disabled);
      await page.waitForFunction(() => !document.body.innerText.includes('正在扫描缓存…'));
      assert.equal((await callCounts()).scan, before.scan + 1);
      report.checks.push({ width, height, keyboardDetailsAndVirtualFocus: true, modalFocusAndInert: true,
        backgroundShortcutsBlocked: true, escapeRestoresFocus: true });
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
    console.log(JSON.stringify({ output, ...report }, null, 2));
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
