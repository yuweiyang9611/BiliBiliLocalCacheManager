import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtemp, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { expectedAssets, validateAcceptance } from './validate-desktop-acceptance.mjs';

async function fixture(t) {
  const assetsDirectory = await mkdtemp(path.join(tmpdir(), 'blcm-acceptance-'));
  t.after(() => rm(assetsDirectory, { recursive: true, force: true }));
  const tag = 'v0.4.0', commit = 'a'.repeat(40);
  const assets = [];
  for (const name of expectedAssets('0.4.0')) {
    const bytes = Buffer.from(name);
    await writeFile(path.join(assetsDirectory, name), bytes);
    assets.push({ name, sha256: createHash('sha256').update(bytes).digest('hex') });
  }
  await writeFile(path.join(assetsDirectory, 'SHA256SUMS.txt'), assets.map(asset => asset.sha256 + '  ' + asset.name).join('\n'));
  const environments = ['windows-10', 'windows-11', 'ubuntu-gnome', 'debian-gnome', 'debian-kde', 'fedora-gnome'].map(id => ({
    id, systemVersion: id, desktopVersion: 'Test desktop', sessionType: id.startsWith('windows') ? 'native' : 'xwayland',
    testedAt: '2026-01-01T00:00:00Z', tester: 'Fixture only',
    results: assets.filter(asset => !asset.name.includes('-cli-') &&
      (id.startsWith('windows') ? /windows-x64\.(exe|zip)$/.test(asset.name) :
        asset.name.endsWith(id.startsWith('fedora') ? '.rpm' : '.deb'))).map(asset => ({
          asset: asset.name, checks: Array.from({ length: 9 }, (_, index) => ({ id: index + 1, status: 'passed', defectUrl: null })),
        })),
  }));
  return { assetsDirectory, tag, commit, record: { schemaVersion: 1, tag, commit, assets, environments } };
}

test('accepts the complete desktop matrix and both Windows packages', async t => {
  assert.equal((await validateAcceptance(await fixture(t))).environments, 6);
});
for (const [name, mutate] of [
  ['missing desktop', record => record.environments.pop()],
  ['missing Windows zip', record => record.environments[0].results.pop()],
  ['missing check', record => record.environments[0].results[0].checks.pop()],
  ['failed check', record => { record.environments[0].results[0].checks[0].status = 'failed'; }],
  ['wrong commit', record => { record.commit = 'b'.repeat(40); }],
  ['wrong asset hash', record => { record.assets[0].sha256 = '0'.repeat(64); }],
  ['duplicate environment', record => record.environments.push(record.environments[0])],
  ['container session', record => { record.environments[2].sessionType = 'xvfb'; }],
  ['future test date', record => { record.environments[0].testedAt = '2999-01-01T00:00:00Z'; }],
]) {
  test('rejects ' + name, async t => {
    const options = await fixture(t);
    mutate(options.record);
    await assert.rejects(validateAcceptance(options));
  });
}
test('rejects replacement packages', async t => {
  const options = await fixture(t);
  await writeFile(path.join(options.assetsDirectory, options.record.assets[0].name), 'replacement');
  await assert.rejects(validateAcceptance(options), /hash mismatch/);
});
test('rejects prerelease tags and extra release assets', async t => {
  const options = await fixture(t);
  await assert.rejects(validateAcceptance({ ...options, tag: 'v0.4.0-rc.1' }), /stable/);
  await writeFile(path.join(options.assetsDirectory, 'unexpected.exe'), '');
  await assert.rejects(validateAcceptance(options), /asset set/);
});
