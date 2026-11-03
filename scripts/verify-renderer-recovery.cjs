#!/usr/bin/env node
'use strict';

// Real Electron rendering with fault-injected fixture APIs; no Host or user cache.
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { desktop, projectRequire, createOutput, electronExecutable, electronEnvironment,
  removeFixture, captureFailure } = require('./desktop-e2e-support.cjs');

(async () => {
  const output = await createOutput('renderer-recovery');
  const fixture = path.join(output, 'isolated-fixture');
  const report = { scope: 'Real Electron renderer; fixture API, no Host or user cache.', status: 'running', checks: [], errors: [] };
  let application, page;
  try {
    await fs.mkdir(fixture);
    await fs.access(path.join(desktop, 'dist', 'index.html'));
    const preload = path.join(fixture, 'preload.cjs');
    const entry = path.join(fixture, 'entry.cjs');
    await fs.writeFile(preload, `const {contextBridge}=require('electron');
const rootFault=process.argv.includes('--fixture-root-fault');
const settings={rootPath:${JSON.stringify(path.join(fixture, 'cache'))},rememberRootPath:true,scanOnStartup:true,includeIncomplete:false,keyword:'',splitKeywords:true,anyKeywords:false,includePartName:false,includeOwnerName:true,includeBvid:true,includeAvid:true,caseSensitive:false,matchMode:'contains',playerPreference:'system',transcodeCacheRetentionDays:30,transcodeCacheMaxSizeGigabytes:10};
const item={id:'100',avid:'100',title:{invalid:'injected-render-error'},ownerName:'Fixture',segmentCount:1,sizeBytes:16,durationSeconds:1,isAllCompleted:true};
const page={indexToken:'fixture',offset:0,pageSize:100,totalItems:1,hasMore:false,items:rootFault?{}:[item],issues:[],issuesTruncated:false};
contextBridge.exposeInMainWorld('cacheManager',{
 getOperationStates:async()=>[],onOperationState:()=>()=>{},onHostUnavailable:()=>()=>{},onProgress:()=>()=>{},
 getInitialState:async()=>({settings,settingsState:{sourceSchemaVersion:2},items:rootFault?{}:[],capabilities:{trashPurge:false}}),
 getDesktopInfo:async()=>({electronVersion:'fixture',displayBackend:process.platform==='linux'?'x11':'win32'}),health:async()=>({status:'ok'}),
 cancelSearch:async()=>false,cancelCacheDetails:async()=>false,scan:async()=>page,
 reloadWhenIdle:async()=>false
});`);
    await fs.writeFile(entry, `const {app,BrowserWindow}=require('electron');
if(app.isPackaged)throw new Error('Development build required');
const rootFault=process.argv.includes('--fixture-root-fault');
app.setPath('userData',${JSON.stringify(path.join(fixture, 'profile-'))}+(rootFault?'root':'page'));
app.disableHardwareAcceleration();
if(process.platform==='linux')app.commandLine.appendSwitch('ozone-platform','x11');
app.whenReady().then(()=>{
 const window=new BrowserWindow({width:1440,height:900,show:false,webPreferences:{preload:${JSON.stringify(preload)},sandbox:true,contextIsolation:true,additionalArguments:rootFault?['--fixture-root-fault']:[]}});
 return window.loadFile(${JSON.stringify(path.join(desktop, 'dist', 'index.html'))});
}).catch(error=>{console.error(error);app.exit(1);});
app.on('window-all-closed',()=>app.quit());`);
    const { _electron } = projectRequire('playwright');
    for (const scope of ['page', 'root']) {
      application = await _electron.launch({ executablePath: electronExecutable(),
        args: [entry, ...(scope === 'root' ? ['--fixture-root-fault'] : [])],
        cwd: desktop, env: electronEnvironment(), chromiumSandbox: true, timeout: 60_000 });
      page = await application.firstWindow({ timeout: 60_000 });
      page.setDefaultTimeout(30_000);
      const failure = page.locator(scope === 'root' ? '.render-failure-root' : '.render-failure');
      await failure.waitFor();
      for (const [width, height] of [[1440, 900], [1080, 720]]) {
        await application.evaluate(({ BrowserWindow }, size) => BrowserWindow.getAllWindows()[0].setSize(...size), [width, height]);
        // Window decorations differ between Windows and X11; wait for a resize,
        // then evaluate the actual content viewport rather than outer dimensions.
        await page.waitForTimeout(150);
        const layout = await failure.evaluate(panel => {
          const button = panel.querySelector('button');
          const rect = button.getBoundingClientRect();
          button.focus();
          return { overflow: document.documentElement.scrollWidth > innerWidth,
            buttonVisible: rect.left >= 0 && rect.top >= 0 && rect.right <= innerWidth && rect.bottom <= innerHeight,
            focusable: document.activeElement === button, sidebar: !!document.querySelector('.sidebar'), text: panel.textContent };
        });
        assert.equal(layout.overflow, false); assert.equal(layout.buttonVisible, true);
        assert.equal(layout.focusable, true); assert.equal(layout.sidebar, scope === 'page');
        assert(!layout.text.includes('injected-render-error'));
        // capturePage also works for a deliberately hidden Electron window,
        // which Chromium's screenshot command may treat as occluded on Windows.
        const screenshot = await application.evaluate(async ({ BrowserWindow }) =>
          (await BrowserWindow.getAllWindows()[0].capturePage()).toPNG().toString('base64'));
        await fs.writeFile(path.join(output, `${scope}-${width}.png`), Buffer.from(screenshot, 'base64'));
        report.checks.push({ scope, width, height, ...layout });
      }
      await failure.getByRole('button').click();
      if (scope === 'page') {
        await failure.waitFor();
        await page.getByRole('button', { name: '设置', exact: true }).click();
        await page.locator('.settings-layout').waitFor();
      } else {
        await page.getByRole('status').filter({ hasText: '后台任务或文件选择窗口尚未结束' }).waitFor();
      }
      await application.close(); application = undefined; page = undefined;
    }
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
    console.log(JSON.stringify({ output, status: report.status, checks: report.checks.length }));
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
