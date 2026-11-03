// Reuses only a synthetic fixture retained by verify-desktop-requirements.cjs.
// Does not accept real cache roots. Build the Release Host before running.
const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const { spawn, execFileSync } = require('node:child_process');
const { createInterface } = require('node:readline');
const { performance } = require('node:perf_hooks');
const assert = require('node:assert/strict');
const repository = path.resolve(__dirname, '..');

(async () => {
  const fixture = await fs.realpath(process.argv[2] ?? '');
  const artifacts = await fs.realpath(path.join(repository, 'artifacts'));
  assert(fixture.startsWith(artifacts + path.sep), 'Fixture must be inside repository artifacts/.');
  assert.equal(path.basename(fixture), 'isolated-fixture');
  const previous = JSON.parse(await fs.readFile(path.join(fixture, '..', 'results.json'), 'utf8'));
  assert.equal(previous.kind, 'development-electron-real-host-synthetic-cache');
  assert.equal(previous.data.syntheticMedia, true);
  const output = await fs.mkdtemp(path.join(artifacts, 'scan-measurement-'));
  const hostPath = path.join(repository, 'BiliBiliLocalCacheManager.Desktop.Host', 'bin', 'Release', 'net10.0', 'BiliBiliLocalCacheManager.Desktop.Host.dll');
  await fs.access(hostPath);
  const report = { scope: 'Synthetic cache, sequential scans in one Host process; first sample is not an OS cold-cache guarantee.',
    source: { commit: execFileSync('git', ['rev-parse', 'HEAD'], { cwd: repository, encoding: 'utf8' }).trim(),
      dirty: Boolean(execFileSync('git', ['status', '--porcelain'], { cwd: repository, encoding: 'utf8' }).trim()) },
    machine: { platform: process.platform, os: os.release(), cpu: os.cpus()[0]?.model, memoryBytes: os.totalmem() },
    data: previous.data, scans: [], diagnosticsPath: path.join(output, 'diagnostics.zip') };
  const child = spawn('dotnet', [hostPath], { cwd: repository, windowsHide: true,
    env: { ...process.env, BILIBILI_LOCAL_CACHE_MANAGER_SETTINGS_PATH: path.join(output, 'settings.json'),
      BILIBILI_LOCAL_CACHE_MANAGER_TRANSCODE_CACHE_ROOT: path.join(output, 'transcode') }, stdio: ['pipe', 'pipe', 'pipe'] });
  const closed = new Promise(resolve => child.once('close', resolve));
  let stderr = '';
  child.stderr.on('data', chunk => { stderr = (stderr + chunk).slice(-4096); });
  let sequence = 0;
  const pending = new Map();
  const fail = error => { for (const call of pending.values()) { clearTimeout(call.timer); call.reject(error); } pending.clear(); };
  child.on('error', fail);
  child.on('close', code => fail(new Error(`Host exited (${code}): ${stderr}`)));
  const lines = createInterface({ input: child.stdout });
  lines.on('line', line => {
    try {
      const value = JSON.parse(line.replace(/^\uFEFF/, ''));
      if (!value.id || (!Object.hasOwn(value, 'result') && !Object.hasOwn(value, 'error'))) return;
      const call = pending.get(value.id);
      if (!call) return;
      pending.delete(value.id); clearTimeout(call.timer);
      if (value.error) call.reject(new Error(JSON.stringify(value.error)));
      else call.resolve(value.result);
    } catch (error) { fail(error); }
  });
  const request = (method, params) => new Promise((resolve, reject) => {
    const id = `measurement-${++sequence}`;
    const timer = setTimeout(() => { pending.delete(id); reject(new Error(`${method} exceeded ten minutes`)); }, 600000);
    pending.set(id, { resolve, reject, timer });
    child.stdin.write(JSON.stringify({ id, method, params }) + '\n', error => { if (error) fail(error); });
  });
  try {
    for (let index = 0; index < 3; index++) {
      const start = performance.now();
      const result = await request('scan', { rootPath: path.join(fixture, 'cache'), includeIncomplete: false, persistSettings: false, offset: 0, pageSize: 100 });
      assert.equal(result.totalItems, previous.data.videos);
      assert.equal(result.includedEntries, previous.data.segments);
      report.scans.push({ sample: index + 1, milliseconds: performance.now() - start, videos: result.totalItems, segments: result.includedEntries });
      console.log(JSON.stringify(report.scans.at(-1)));
    }
    await request('diagnostics.export', { outputPath: report.diagnosticsPath, rootPath: path.join(fixture, 'cache') });
  } finally {
    child.stdin.end();
    const timer = setTimeout(() => child.kill(), 5000);
    await closed;
    clearTimeout(timer); lines.close();
    await fs.writeFile(path.join(output, 'results.json'), JSON.stringify(report, null, 2));
  }
  console.log(JSON.stringify({ output, diagnosticsPath: report.diagnosticsPath }));
})().catch(error => { console.error(error); process.exitCode = 1; });
