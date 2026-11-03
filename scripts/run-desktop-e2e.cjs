#!/usr/bin/env node
'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { spawn } = require('node:child_process');
const { repository, desktop, createOutput, electronEnvironment } = require('./desktop-e2e-support.cjs');

const argumentsList = process.argv.slice(2);
const suite = argumentsList.find(value => value.startsWith('--suite='))?.slice(8) ?? 'smoke';
assert(['smoke', 'requirements', 'all'].includes(suite), '--suite must be smoke, requirements or all.');
assert(argumentsList.every(value => value === '--skip-build' || value === '--keep-fixture' || /^--suite=/.test(value) || /^--count=\d+$/.test(value)), 'Unknown E2E option.');

(async () => {
  const output = await createOutput('run');
  const report = { status: 'running', suite, platform: process.platform, startedAt: new Date().toISOString(), steps: [] };
  const environment = electronEnvironment({ BLCM_E2E_ARTIFACTS_DIR: output });
  async function run(name, executable, args, cwd = repository) {
    const log = path.join(output, name + '.log');
    const chunks = [];
    const step = { name, command: [executable, ...args], log: path.basename(log), status: 'running' };
    report.steps.push(step);
    const code = await new Promise((resolve, reject) => {
      const child = spawn(executable, args, { cwd, env: environment, windowsHide: true,
        detached: process.platform !== 'win32', stdio: ['ignore', 'pipe', 'pipe'] });
      const deadline = setTimeout(() => {
        step.timedOut = true;
        chunks.push(Buffer.from(`\n${name} exceeded its E2E step deadline.\n`));
        if (child.exitCode !== null || child.signalCode !== null) {
          child.stdout.destroy(); child.stderr.destroy();
          return;
        }
        // Stop only this step's process tree, including its isolated Electron/Host.
        if (process.platform === 'win32') {
          const killer = spawn('taskkill.exe', ['/pid', String(child.pid), '/t', '/f'], { windowsHide: true, stdio: 'ignore' });
          killer.on('error', () => child.kill('SIGKILL'));
        } else {
          try { process.kill(-child.pid, 'SIGKILL'); } catch { child.kill('SIGKILL'); }
        }
      }, name === 'desktop-requirements' ? 10 * 60_000 : 4 * 60_000);
      child.stdout.on('data', chunk => { chunks.push(chunk); process.stdout.write(chunk); });
      child.stderr.on('data', chunk => { chunks.push(chunk); process.stderr.write(chunk); });
      child.once('error', error => { clearTimeout(deadline); step.status = 'failed'; step.error = String(error); reject(error); });
      child.once('close', code => { clearTimeout(deadline); resolve(code); });
    }).finally(() => fs.writeFile(log, Buffer.concat(chunks)));
    step.exitCode = code;
    step.status = code === 0 && !step.timedOut ? 'passed' : 'failed';
    assert(!step.timedOut, `${name} timed out; see ${log}`);
    assert.equal(code, 0, `${name} failed; see ${log}`);
  }
  try {
    await run('setup', process.execPath, [path.join(__dirname, 'setup-desktop-e2e.cjs')]);
    if (!argumentsList.includes('--skip-build')) {
      await run('build-host', 'dotnet', ['build', path.join(repository, 'BiliBiliLocalCacheManager.Desktop.Host', 'BiliBiliLocalCacheManager.Desktop.Host.csproj'), '--configuration', 'Release', '--nologo']);
      // npm_execpath avoids Windows .cmd shell quoting and works from npm on both platforms.
      assert(process.env.npm_execpath, 'Run this entry with npm run test:e2e.');
      await run('build-desktop', process.execPath, [process.env.npm_execpath, 'run', 'build'], desktop);
    }
    if (suite !== 'requirements') {
      await run('renderer-recovery', process.execPath, [path.join(__dirname, 'verify-renderer-recovery.cjs')]);
      await run('task-history', process.execPath, [path.join(__dirname, 'verify-task-history.cjs')]);
      await run('keyboard', process.execPath, [path.join(__dirname, 'verify-keyboard.cjs')]);
    }
    if (suite !== 'smoke') {
      const options = argumentsList.filter(value => value === '--keep-fixture' || value.startsWith('--count='));
      await run('desktop-requirements', process.execPath, [path.join(__dirname, 'verify-desktop-requirements.cjs'), ...options]);
    }
    report.status = 'passed';
  } catch (error) {
    report.status = 'failed'; report.error = error.stack ?? String(error); process.exitCode = 1;
  } finally {
    report.finishedAt = new Date().toISOString();
    await fs.writeFile(path.join(output, 'results.json'), JSON.stringify(report, null, 2));
    console.log(JSON.stringify({ status: report.status, output }));
  }
})();
