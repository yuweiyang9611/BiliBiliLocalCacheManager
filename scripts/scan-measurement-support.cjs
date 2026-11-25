const assert = require('node:assert/strict');
const { spawn, execFileSync } = require('node:child_process');
const { createHash } = require('node:crypto');
const fs = require('node:fs/promises');
const path = require('node:path');
const { createInterface } = require('node:readline');

const repository = path.resolve(__dirname, '..');

function measurementSource() {
  return { commit: execFileSync('git', ['rev-parse', 'HEAD'], { cwd: repository, encoding: 'utf8' }).trim(),
    dirty: Boolean(execFileSync('git', ['status', '--porcelain'], { cwd: repository, encoding: 'utf8' }).trim()) };
}

async function identifyHost(dll) {
  const hostPath = await fs.realpath(dll);
  const sha256 = {};
  for (const name of [path.basename(hostPath), 'BiliBiliLocalCacheManager.Core.dll', 'BiliBiliLocalCacheManager.Playback.dll']) {
    const bytes = await fs.readFile(path.join(path.dirname(hostPath), name));
    sha256[name] = createHash('sha256').update(bytes).digest('hex');
  }
  return { hostPath, sha256 };
}

function startHost(hostPath, output) {
  const child = spawn('dotnet', [hostPath], { cwd: repository, windowsHide: true,
    env: { ...process.env, BILIBILI_LOCAL_CACHE_MANAGER_SETTINGS_PATH: path.join(output, 'settings.json'),
      BILIBILI_LOCAL_CACHE_MANAGER_TRANSCODE_CACHE_ROOT: path.join(output, 'transcode') }, stdio: ['pipe', 'pipe', 'pipe'] });
  const closed = new Promise(resolve => child.once('close', resolve));
  let stderr = '', terminalError;
  child.stderr.on('data', chunk => { stderr = (stderr + chunk).slice(-4096); });
  let sequence = 0;
  const pending = new Map();
  const fail = error => {
    terminalError = error;
    for (const call of pending.values()) { clearTimeout(call.timer); call.reject(error); }
    pending.clear();
  };
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
  return {
    request(method, params, id = `measurement-${++sequence}`) {
      return new Promise((resolve, reject) => {
        if (terminalError) return reject(terminalError);
        const timer = setTimeout(() => { pending.delete(id); reject(new Error(`${method} exceeded ten minutes`)); }, 600000);
        pending.set(id, { resolve, reject, timer });
        child.stdin.write(JSON.stringify({ id, method, params }) + '\n', error => { if (error) fail(error); });
      });
    },
    async close() {
      child.stdin.end();
      const timer = setTimeout(() => child.kill(), 5000);
      await closed; clearTimeout(timer); lines.close();
    }
  };
}

async function summaryDigest(scan, request) {
  const digest = createHash('sha256');
  let page = scan;
  let offset = 0;
  for (;;) {
    digest.update(JSON.stringify(page.items));
    offset += page.items.length;
    if (!page.hasMore) break;
    assert(page.items.length > 0 && offset < scan.totalItems);
    page = await request('search', { keyword: '', indexToken: scan.indexToken, pageSize: scan.pageSize, offset });
    assert.equal(page.indexToken, scan.indexToken);
  }
  assert.equal(offset, scan.totalItems);
  return digest.digest('hex');
}

module.exports = { measurementSource, identifyHost, startHost, summaryDigest };
