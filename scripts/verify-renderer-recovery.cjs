// Run with the repository's Electron executable after npm run build.
// Injects malformed fixture data, never starts a Host or reads a real cache.
const { app, BrowserWindow } = require('electron');
const fs = require('node:fs/promises');
const path = require('node:path');
const assert = require('node:assert/strict');
const output = path.resolve(__dirname, '..', 'artifacts', `renderer-recovery-${Date.now()}`);
app.setPath('userData', path.join(output, 'profile'));
app.disableHardwareAcceleration();
app.on('window-all-closed', () => {});

async function waitFor(window, expression) {
  for (let attempt = 0; attempt < 100; attempt++) {
    if (await window.webContents.executeJavaScript(expression)) return;
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw new Error(`Timed out: ${expression}`);
}

app.whenReady().then(async () => {
  await fs.mkdir(output, { recursive: true });
  const preload = path.join(output, 'preload.cjs');
  await fs.writeFile(preload, `const {contextBridge}=require('electron');
const rootFault=process.argv.includes('--fixture-root-fault');
const settings={rootPath:'D:\\\\SyntheticCache',rememberRootPath:true,scanOnStartup:true,includeIncomplete:false,keyword:'',splitKeywords:true,anyKeywords:false,includePartName:false,includeOwnerName:true,includeBvid:true,includeAvid:true,caseSensitive:false,matchMode:'contains',playerPreference:'system',transcodeCacheRetentionDays:30,transcodeCacheMaxSizeGigabytes:10};
const item={id:'100',avid:'100',title:{invalid:'injected-render-error'},ownerName:'Fixture',segmentCount:1,sizeBytes:16,durationSeconds:1,isAllCompleted:true};
const page={indexToken:'fixture',offset:0,pageSize:100,totalItems:1,hasMore:false,items:rootFault?{}:[item],issues:[],issuesTruncated:false};
contextBridge.exposeInMainWorld('cacheManager',{
 getOperationStates:async()=>[],onOperationState:()=>()=>{},onHostUnavailable:()=>()=>{},onProgress:()=>()=>{},
 getInitialState:async()=>({settings,settingsState:{sourceSchemaVersion:2},items:rootFault?{}:[],capabilities:{trashPurge:false}}),
 getDesktopInfo:async()=>({electronVersion:'fixture',displayBackend:'win32'}),health:async()=>({status:'ok'}),
 cancelSearch:async()=>false,cancelCacheDetails:async()=>false,scan:async()=>page,
 reloadWhenIdle:async()=>false
});`);
  const checks = [];
  for (const scope of ['page', 'root']) {
    const window = new BrowserWindow({ width: 1440, height: 900, show: false,
      webPreferences: { preload, sandbox: true, contextIsolation: true,
        additionalArguments: scope === 'root' ? ['--fixture-root-fault'] : [] } });
    try {
      await window.loadFile(path.resolve(__dirname, '..', 'BiliBiliLocalCacheManager.Desktop', 'dist', 'index.html'));
      await waitFor(window, `!!document.querySelector('.render-failure${scope === 'root' ? '-root' : ''}')`);
      for (const [width, height] of [[1440, 900], [1080, 720]]) {
        window.setSize(width, height);
        await new Promise(resolve => setTimeout(resolve, 150));
        const layout = await window.webContents.executeJavaScript(`(() => {
          const panel=document.querySelector('.render-failure');
          const button=panel.querySelector('button');
          const rect=button.getBoundingClientRect();
          button.focus();
          return {overflow:document.documentElement.scrollWidth>innerWidth,
            buttonVisible:rect.left>=0&&rect.top>=0&&rect.right<=innerWidth&&rect.bottom<=innerHeight,
            focusable:document.activeElement===button,
            sidebar:!!document.querySelector('.sidebar'),text:panel.textContent};
        })()`);
        assert.equal(layout.overflow, false);
        assert.equal(layout.buttonVisible, true);
        assert.equal(layout.focusable, true);
        assert.equal(layout.sidebar, scope === 'page');
        assert(!layout.text.includes('injected-render-error'));
        await fs.writeFile(path.join(output, `${scope}-${width}.png`), (await window.webContents.capturePage()).toPNG());
        checks.push({ scope, width, height, ...layout });
      }
      if (scope === 'page') {
        await window.webContents.executeJavaScript(`document.querySelector('.render-failure button').click()`);
        await waitFor(window, `!!document.querySelector('.render-failure')`);
        await window.webContents.executeJavaScript(`[...document.querySelectorAll('.nav-item')].find(button=>button.textContent.includes('设置')).click()`);
        await waitFor(window, `!!document.querySelector('.settings-layout')`);
      } else {
        await window.webContents.executeJavaScript(`document.querySelector('.render-failure button').click()`);
        await waitFor(window, `document.querySelector('.render-failure [role="status"]')?.textContent.includes('后台任务或文件选择窗口尚未结束')`);
      }
    } finally { window.destroy(); }
  }
  await fs.writeFile(path.join(output, 'results.json'), JSON.stringify({ checks, scope: 'Real Electron renderer; fixture API, no Host or user cache.' }, null, 2));
  console.log(JSON.stringify({ output, checks: checks.length }));
  app.exit(0);
}).catch(error => { console.error(error); app.exit(1); });
