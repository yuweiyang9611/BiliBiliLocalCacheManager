// Measures local-mutation refresh against a full scan of the same synthetic library.
const fs = require('node:fs/promises');
const path = require('node:path');
const assert = require('node:assert/strict');
const { spawn, execFileSync } = require('node:child_process');
const { createInterface } = require('node:readline');
const { performance } = require('node:perf_hooks');
const { repository, createOutput, removeFixture } = require('./desktop-e2e-support.cjs');

(async () => {
  const count = Number(process.argv[2] ?? 500);
  assert(Number.isInteger(count) && count >= 2 && count <= 10000);
  const output = await createOutput('mutation-refresh');
  const fixture = path.join(output, 'isolated-fixture');
  const root = path.join(fixture, 'cache');
  for (let avid = 1; avid <= count; avid++) for (let page = 1; page <= 2; page++) {
    const directory = path.join(root, String(avid), `c_${page}`);
    await fs.mkdir(directory, { recursive: true });
    await fs.writeFile(path.join(directory, 'entry.json'), JSON.stringify({ is_completed: true,
      title: `Fixture ${avid}`, total_bytes: 16, downloaded_bytes: 16, guessed_total_bytes: 16, total_time_milli: 1000,
      time_update_stamp: 1750000000000, time_create_stamp: 1750000000000, avid, bvid: `BV${avid}`, cover: 'fixture',
      type_tag: '80', owner_name: 'Fixture', page_data: { cid: avid * 10 + page, page, part: `Part ${page}`, from: 'local' } }));
  }
  const child = spawn('dotnet', [path.join(repository, 'BiliBiliLocalCacheManager.Desktop.Host/bin/Release/net10.0/BiliBiliLocalCacheManager.Desktop.Host.dll')],
    { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'], env: { ...process.env,
      BILIBILI_LOCAL_CACHE_MANAGER_SETTINGS_PATH: path.join(fixture, 'settings.json'),
      BILIBILI_LOCAL_CACHE_MANAGER_TRANSCODE_CACHE_ROOT: path.join(fixture, 'transcode') } });
  const closed = new Promise(resolve => child.once('close', resolve));
  const pending = new Map(); let sequence = 0; let stderr = '';
  const lines = createInterface({ input: child.stdout });
  child.stderr.on('data', chunk => { stderr = (stderr + chunk).slice(-4096); });
  const fail = error => { for (const call of pending.values()) { clearTimeout(call.timer); call.reject(error); } pending.clear(); };
  child.on('error', fail); child.on('close', code => fail(new Error(`Host exited ${code}: ${stderr}`)));
  lines.on('line', line => {
    try {
      const value = JSON.parse(line.replace(/^\uFEFF/, ''));
      const call = pending.get(value.id);
      if (!call) return;
      pending.delete(value.id); clearTimeout(call.timer);
      if (value.error) call.reject(new Error(JSON.stringify(value.error))); else call.resolve(value.result);
    } catch (error) { fail(error); }
  });
  const request = (method, params, id = `measure-${++sequence}`) => new Promise((resolve, reject) => {
    const timer = setTimeout(() => { pending.delete(id); reject(new Error(`${method} timed out`)); }, 120000);
    pending.set(id, { resolve, reject, timer });
    child.stdin.write(JSON.stringify({ id, method, params }) + '\n', error => { if (error) fail(error); });
  });
  const samples = [];
  const diagnosticsPath = path.join(output, 'diagnostics.zip');
  const scan = async (name, incremental = false, expectedReads = count * 2, expectedMode = incremental) => {
    const id = `measure-${++sequence}`;
    const start = performance.now();
    const result = await request('scan', { rootPath: root, includeIncomplete: false, persistSettings: false, incremental, pageSize: 100 }, id);
    const milliseconds = performance.now() - start;
    // Read the timing before pagination evicts it from the bounded event history.
    await request('diagnostics.export', { rootPath: root, outputPath: diagnosticsPath });
    const content = process.platform === 'win32'
      ? execFileSync('tar.exe', ['-xOf', diagnosticsPath, 'recent-events.json'], { encoding: 'utf8', windowsHide: true })
      : execFileSync('unzip', ['-p', diagnosticsPath, 'recent-events.json'], { encoding: 'utf8' });
    const event = JSON.parse(content).find(event => event.category === 'ScanTiming' && event.message.startsWith(`request=${id};`));
    assert(event, `Missing ScanTiming for ${id}`);
    const fields = Object.fromEntries(event.message.split('; ').map(field => field.split('=')));
    assert.equal(fields.incremental, expectedMode ? 'True' : 'False', name);
    assert.equal(Number(fields.readEntries), expectedReads, name);
    samples.push({ name, requestId: id, milliseconds, videos: result.totalItems, parts: result.includedEntries,
      incremental: expectedMode, readEntries: Number(fields.readEntries) });
    const items = [...result.items];
    while (items.length < result.totalItems) {
      const page = await request('search', { keyword: '', indexToken: result.indexToken, pageSize: 100, offset: items.length });
      assert(page.items.length > 0 && page.indexToken === result.indexToken);
      items.push(...page.items);
    }
    return { ...result, items };
  };
  try {
    let current = await scan('initial-full');
    for (let iteration = 0; iteration < 3; iteration++) {
      const moved = await request('trash.move', { rootPath: root, indexToken: current.indexToken, targets: [{ avid: '1', pageIndexes: [1] }] });
      assert.equal(moved.moved.length, 1);
      const local = await scan(`move-${iteration}-incremental`, true, 1);
      current = await scan(`move-${iteration}-full`, false, count * 2 - 1);
      assert.deepEqual(local.items, current.items); assert.equal(local.includedEntries, current.includedEntries);
      const restored = await request('trash.restore', { rootPath: root, entryIds: moved.entryIds });
      assert.equal(restored.restored.length, 1);
      const localRestore = await scan(`restore-${iteration}-incremental`, true, 2);
      current = await scan(`restore-${iteration}-full`);
      assert.deepEqual(localRestore.items, current.items); assert.equal(localRestore.includedEntries, current.includedEntries);
      assert.equal(current.includedEntries, count * 2);
    }
    await fs.mkdir(path.join(root, String(count), 'missing-metadata'));
    current = await scan('incomplete-baseline');
    const moved = await request('trash.move', { rootPath: root, indexToken: current.indexToken, targets: [{ avid: '1', pageIndexes: [1] }] });
    assert.equal(moved.moved.length, 1);
    const fallback = await scan('move-fallback', true, count * 2 - 1, false);
    assert.equal(fallback.includedEntries, count * 2 - 1);
    await fs.writeFile(path.join(output, 'results.json'), JSON.stringify({ scope: 'Synthetic metadata, sequential warm-process observations; no cross-machine SLA.', count, samples }, null, 2));
    console.log(JSON.stringify({ output, samples }, null, 2));
  } finally {
    child.stdin.end(); const timer = setTimeout(() => child.kill(), 5000);
    await closed; clearTimeout(timer); lines.close();
    await removeFixture(output, fixture);
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
