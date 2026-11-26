import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { access, mkdir, mkdtemp, readdir, readFile, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { createInterface } from 'node:readline';

// Run the actual apphost executable: dotnet Host.dll has different Windows long-path behavior.
assert.equal(process.platform, 'win32');
const executable = path.resolve(process.argv[2]);
const exportArgs = process.argv.slice(3);
assert(exportArgs.length === 0 || (exportArgs.length === 1 && exportArgs[0] === '--export'), 'Unknown regression option');
assert.equal(path.extname(executable).toLowerCase(), '.exe');
await access(executable);
const fixture = await mkdtemp(path.join(os.tmpdir(), 'blcm-published-paths-'));
try {
  const child = spawn(executable, ['--json-lines'], {
    windowsHide: true, timeout: exportArgs.length ? 180_000 : 60_000, stdio: ['pipe', 'pipe', 'inherit'],
    env: { ...process.env, BILIBILI_LOCAL_CACHE_MANAGER_SETTINGS_PATH: path.join(fixture, 'settings.json'),
      BILIBILI_LOCAL_CACHE_MANAGER_TRANSCODE_CACHE_ROOT: path.join(fixture, 'transcode') },
  });
  const closed = once(child, 'close');
  const replies = createInterface({ input: child.stdout })[Symbol.asyncIterator]();
  let sequence = 0;
  async function request(method, params) {
    const id = `request-${++sequence}`;
    child.stdin.write(JSON.stringify({ id, method, params }) + '\n');
    for (;;) {
      const { value, done } = await replies.next();
      assert(!done, `Published Host exited before responding to ${method}`);
      const response = JSON.parse(value.replace(/^\uFEFF/, ''));
      if (response.id !== id) continue;
      assert(!response.error, JSON.stringify(response.error));
      return response.result;
    }
  }
  try {
    let deepRoot = fixture;
    while (deepRoot.length < 320) deepRoot = path.join(deepRoot, 'long-cache-directory');
    for (const rootPath of [path.join(fixture, 'short'), deepRoot]) {
      const part = path.join(rootPath, '101', 'c_1');
      const entry = path.join(part, 'entry.json');
      const title = 'Published Host path fixture';
      const metadata = JSON.stringify({ avid: 101, title, is_completed: true, total_bytes: 1, downloaded_bytes: 1,
        total_time_milli: 1000, page_data: { cid: 1011, page: 1, part: 'Fixture P1' } });
      await mkdir(part, { recursive: true });
      await writeFile(entry, metadata);
      const scan = await request('scan', { rootPath, persistSettings: false });
      assert.deepEqual(scan.items.map(item => [item.avid, item.title]), [['101', title]],
        `Missing entry at path length ${entry.length}: ${JSON.stringify(scan.issues)}`);
      assert.equal(scan.unavailablePartIdentities, 0, JSON.stringify(scan.issues));
      assert.equal(scan.hasWarnings, false, JSON.stringify(scan.issues));
      const moved = await request('trash.move', { rootPath, indexToken: scan.indexToken,
        targets: [{ avid: '101', pageIndexes: [1] }] });
      assert.deepEqual(moved.moved, ['101:P1'], JSON.stringify(moved.items));
      assert.equal(moved.entryIds.length, 1);
      await assert.rejects(access(entry), { code: 'ENOENT' });
      const restored = await request('trash.restore', { rootPath, entryIds: moved.entryIds });
      assert.deepEqual(restored.restored, moved.entryIds, JSON.stringify(restored.items));
      assert.equal(await readFile(entry, 'utf8'), metadata);
      console.log(`Published Host scanned, moved and restored AV101 P1 at path length ${entry.length}.`);
    }
    if (exportArgs.length) await verifyExport(request, fixture);
  } finally {
    child.stdin.end();
    const [code] = await closed;
    assert.equal(code, 0, 'Published Host did not exit cleanly');
  }
} finally {
  await rm(fixture, { recursive: true, force: true });
}

async function verifyExport(request, fixture) {
  const clip = await readFile(new URL('../BiliBiliLocalCacheManager.Desktop.Host.Tests/Fixtures/one-second.mp4', import.meta.url));
  const rootPath = path.join(fixture, 'source');
  for (const page of [1, 2]) {
    const part = path.join(rootPath, '101', 'c_' + page);
    const media = path.join(part, 'lua.flv.bb2api.80');
    const bytes = page === 1 ? clip : Buffer.from('damaged media');
    await mkdir(media, { recursive: true });
    await writeFile(path.join(media, '0.mp4'), bytes);
    await writeFile(path.join(part, 'entry.json'), JSON.stringify({ avid: 101, title: 'Clip', is_completed: true,
      total_bytes: bytes.length, downloaded_bytes: bytes.length, total_time_milli: 1000, prefered_video_quality: 80, type_tag: '80',
      page_data: { cid: 1010 + page, page, part: 'P' + page } }));
  }
  const { indexToken } = await request('scan', { rootPath, persistSettings: false });
  for (const length of [230, 300]) {
    let outputPath = path.join(fixture, 'export-' + length);
    // At 230 the destination is short, but the Host's staging directory crosses MAX_PATH.
    while (outputPath.length < length) {
      const remaining = length - outputPath.length - 1;
      if (remaining === 0) { outputPath += 'd'; break; }
      outputPath = path.join(outputPath, 'd'.repeat(Math.min(40, remaining)));
    }
    await mkdir(outputPath, { recursive: true });
    const run = pageIndexes => request('export', { rootPath, indexToken, outputPath, targets: [{ avid: '101', pageIndexes }] });
    const success = await run([1]).catch(async error => {
      assert.deepEqual(await readdir(outputPath), [], `Export initialization left files behind: ${error.message}`);
      throw error;
    });
    assert.equal(success.published, true, JSON.stringify(success));
    assert.equal(success.exportedCount, 1);
    assert.equal(success.outputPath, path.join(outputPath, 'cache-export'));
    const published = path.join(success.outputPath, 'Clip_AV101', 'P001_P1.mp4');
    assert.deepEqual(await readFile(published), clip);
    const failed = await run([1, 2]);
    assert.equal(failed.published, false);
    assert.equal(failed.exportedCount, 0);
    assert.deepEqual(failed.failures.map(item => [item.avid, item.pageIndex]), [['101', 2]]);
    assert.deepEqual(await readdir(outputPath), ['cache-export'], 'Failed export left a staging directory or partial batch');
    assert.deepEqual(await readFile(published), clip, 'Rollback affected the successful batch');
    console.log(`Published Host exported exact MP4 bytes and rolled back a failed batch at destination length ${outputPath.length}.`);
  }
}
