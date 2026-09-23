import { createHash } from 'node:crypto';
import { createReadStream } from 'node:fs';
import { readFile, readdir } from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

export function expectedAssets(version, schemaVersion = 2) {
  requireValue(schemaVersion === 1 || schemaVersion === 2, 'Unsupported acceptance schema.');
  return [
    ...(schemaVersion === 1 ? [
      `BiliBiliLocalCacheManager-cli-v${version}-linux-x64.tar.gz`,
      `BiliBiliLocalCacheManager-cli-v${version}-win-x64.zip`,
    ] : []),
    `BiliBiliLocalCacheManager-${version}-linux-x64.deb`,
    `BiliBiliLocalCacheManager-${version}-linux-x64.rpm`,
    `BiliBiliLocalCacheManager-${version}-windows-x64.exe`,
    `BiliBiliLocalCacheManager-${version}-windows-x64.zip`,
  ].sort();
}

function requireValue(condition, message) {
  if (!condition) throw new Error(message);
}
function nonempty(value) { return typeof value === 'string' && value.trim().length > 0; }
function sameSet(left, right) {
  return left.length === right.length && new Set(left).size === left.length &&
    [...left].sort().every((value, index) => value === [...right].sort()[index]);
}

export async function validateAcceptance({ record, assetsDirectory, tag, commit }) {
  requireValue(/^v\d+\.\d+\.\d+$/.test(tag), 'Only stable version tags may be promoted.');
  requireValue((record.schemaVersion === 1 || record.schemaVersion === 2) && record.tag === tag,
    'Acceptance schema or tag does not match.');
  requireValue(/^[0-9a-f]{40}$/.test(commit) && record.commit === commit, 'Acceptance commit does not match the tag.');
  const expected = expectedAssets(tag.slice(1), record.schemaVersion);
  requireValue(Array.isArray(record.assets) && sameSet(record.assets.map(value => value.name), expected), 'Acceptance asset set is incomplete or duplicated.');
  const files = await readdir(assetsDirectory);
  requireValue(sameSet(files.filter(name => name !== 'desktop-acceptance.json'), [...expected, 'SHA256SUMS.txt']), 'Release asset set does not match.');
  const sums = (await readFile(path.join(assetsDirectory, 'SHA256SUMS.txt'), 'utf8')).replace(/^\uFEFF/, '').trim().split(/\r?\n/);
  const parsedSums = sums.map(line => /^([0-9a-fA-F]{64}) [ *](.+)$/.exec(line));
  requireValue(parsedSums.every(Boolean) && sameSet(parsedSums.map(match => match?.[2]), expected), 'SHA256SUMS.txt is invalid.');
  for (const asset of record.assets) {
    requireValue(/^[0-9a-f]{64}$/.test(asset.sha256), 'Invalid recorded SHA-256.');
    const hash = createHash('sha256');
    for await (const chunk of createReadStream(path.join(assetsDirectory, asset.name))) hash.update(chunk);
    const actual = hash.digest('hex');
    requireValue(actual === asset.sha256 && parsedSums.find(match => match[2] === asset.name)[1].toLowerCase() === actual,
      `Asset hash mismatch: ${asset.name}`);
  }
  requireValue(Array.isArray(record.environments), 'Environment results are required.');
  const ids = record.environments.map(environment => environment.id);
  const required = ['windows-10', 'windows-11', 'ubuntu-gnome', 'debian-gnome', 'debian-kde'];
  requireValue(new Set(ids).size === ids.length && required.every(id => ids.includes(id)) &&
    ids.some(id => id === 'fedora-gnome' || id === 'fedora-kde') &&
    ids.every(id => [...required, 'fedora-gnome', 'fedora-kde'].includes(id)), 'Desktop matrix is incomplete or duplicated.');
  for (const environment of record.environments) {
    requireValue(['systemVersion', 'desktopVersion', 'tester'].every(key => nonempty(environment[key])), 'Environment identity is incomplete.');
    const date = Date.parse(environment.testedAt);
    requireValue(typeof environment.testedAt === 'string' && /^\d{4}-\d{2}-\d{2}T.*(?:Z|[+-]\d{2}:\d{2})$/.test(environment.testedAt) &&
      Number.isFinite(date) && date <= Date.now(), 'A valid, non-future test date is required.');
    const windows = environment.id.startsWith('windows');
    requireValue(environment.sessionType === (windows ? 'native' : 'xwayland'), 'A real supported desktop session is required.');
    const suffixes = windows ? ['windows-x64.exe', 'windows-x64.zip'] :
      [environment.id.startsWith('fedora') ? 'linux-x64.rpm' : 'linux-x64.deb'];
    const packages = expected.filter(name => !name.includes('-cli-') && suffixes.some(suffix => name.endsWith(suffix)));
    requireValue(Array.isArray(environment.results) && sameSet(environment.results.map(result => result.asset), packages), 'Package coverage is incomplete.');
    for (const result of environment.results) {
      requireValue(Array.isArray(result.checks) && sameSet(result.checks.map(check => check.id), [1, 2, 3, 4, 5, 6, 7, 8, 9]),
        'All nine desktop checks must be recorded exactly once.');
      requireValue(result.checks.every(check => check.status === 'passed' &&
        (check.defectUrl === null || (typeof check.defectUrl === 'string' && /^https:\/\/[^\s]+$/.test(check.defectUrl)))),
        'Every check must pass and include a defectUrl or explicit null.');
    }
  }
  return { tag, commit, assets: expected.length, environments: ids.length };
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  try {
    const [recordPath, assetsDirectory, tag, commit] = process.argv.slice(2);
    console.log(JSON.stringify(await validateAcceptance({
      record: JSON.parse(await readFile(recordPath, 'utf8')), assetsDirectory, tag, commit,
    })));
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
