import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtemp, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { expectedAssets, validateAcceptance } from './validate-desktop-acceptance.mjs';

async function fixture(t, schemaVersion = 2) {
  const assetsDirectory = await mkdtemp(path.join(tmpdir(), 'blcm-acceptance-'));
  t.after(() => rm(assetsDirectory, { recursive: true, force: true }));
  const tag = 'v0.4.0', commit = 'a'.repeat(40);
  const assets = [];
  const names = [
    'BiliBiliLocalCacheManager-0.4.0-linux-x64.deb',
    'BiliBiliLocalCacheManager-0.4.0-linux-x64.rpm',
    'BiliBiliLocalCacheManager-0.4.0-windows-x64.exe',
    'BiliBiliLocalCacheManager-0.4.0-windows-x64.zip',
    ...(schemaVersion === 1 ? [
      'BiliBiliLocalCacheManager-cli-v0.4.0-linux-x64.tar.gz',
      'BiliBiliLocalCacheManager-cli-v0.4.0-win-x64.zip',
    ] : []),
  ];
  for (const name of names) {
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
  return { assetsDirectory, tag, commit, record: { schemaVersion, tag, commit, assets, environments } };
}

test('accepts the historical six-package schema with the complete desktop matrix', async t => {
  const result = await validateAcceptance(await fixture(t, 1));
  assert.equal(result.environments, 6);
  assert.equal(result.assets, 6);
});
test('accepts the desktop-only four-package schema with both Windows packages', async t => {
  assert.deepEqual(await validateAcceptance(await fixture(t, 2)), {
    tag: 'v0.4.0', commit: 'a'.repeat(40), assets: 4, environments: 6,
  });
});
test('defaults to the four desktop release assets', () => {
  assert.deepEqual(expectedAssets('0.4.0'), [
    'BiliBiliLocalCacheManager-0.4.0-linux-x64.deb',
    'BiliBiliLocalCacheManager-0.4.0-linux-x64.rpm',
    'BiliBiliLocalCacheManager-0.4.0-windows-x64.exe',
    'BiliBiliLocalCacheManager-0.4.0-windows-x64.zip',
  ]);
});
for (const [schemaVersion, mismatchedSchema] of [[1, 2], [2, 1]]) {
  test(`rejects schema ${schemaVersion} assets recorded as schema ${mismatchedSchema}`, async t => {
    const options = await fixture(t, schemaVersion);
    options.record.schemaVersion = mismatchedSchema;
    await assert.rejects(validateAcceptance(options), /asset set/);
  });
}
test('rejects unsupported acceptance schemas instead of guessing an asset profile', async t => {
  const options = await fixture(t);
  options.record.schemaVersion = 3;
  await assert.rejects(validateAcceptance(options), /schema/);
  assert.throws(() => expectedAssets('0.4.0', 3), /schema/);
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
