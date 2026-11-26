// Compare repeated full scans over fresh synthetic caches. No user cache path is accepted.
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const { performance } = require('node:perf_hooks');
const { createOutput, removeFixture } = require('./desktop-e2e-support.cjs');
const { measurementSource, identifyHost, hostRuntime, startHost, summaryDigest } = require('./scan-measurement-support.cjs');

const videos = 350;
const profiles = [
  { name: 'incomplete', candidates: 2, listed: 3, included: 6, ambiguous: 0 },
  { name: 'multi-candidate', candidates: 4, listed: videos, included: videos * 4, ambiguous: videos },
  { name: 'complete', candidates: 2, listed: videos, included: videos * 2, ambiguous: 0 },
];

async function createCache(root, profile) {
  for (let avid = 1; avid <= videos; avid++) {
    for (let n = 0; n < profile.candidates; n++) {
      const dir = path.join(root, String(avid), 'candidate_' + n);
      const page = profile.ambiguous ? 1 : n + 1;
      await fs.mkdir(dir, { recursive: true });
      await fs.writeFile(path.join(dir, 'entry.json'), JSON.stringify({
        avid, title: 'Video ' + avid, is_completed: avid <= profile.listed, total_bytes: 256,
        downloaded_bytes: 256, type_tag: '80', prefered_video_quality: 80 + n, total_time_milli: 1000,
        time_update_stamp: 1750000000000, page_data: { cid: avid * 10 + page, page, part: 'Part ' + page },
      }));
    }
  }
}

async function measureProfile(root, profile, hosts) {
  await createCache(root, profile);
  const rounds = [], latest = {};
  const versions = Object.keys(hosts);
  for (let round = 0; round <= 6; round++) {
    const row = {};
    // Each version takes every position equally often across the six timed rounds.
    for (let i = 0; i < versions.length; i++) {
      const version = versions[(i + round) % versions.length];
      const start = performance.now();
      const scan = await hosts[version].request('scan', {
        rootPath: root, includeIncomplete: false, persistSettings: false, offset: 0, pageSize: 100,
      });
      row[version] = performance.now() - start;
      assert.equal(scan.totalItems, profile.listed);
      assert.equal(scan.includedEntries, profile.included);
      assert.equal(scan.skippedIncompleteEntries, videos * profile.candidates - profile.included);
      assert.equal(scan.unavailablePartIdentities, profile.ambiguous);
      assert.equal(scan.invalidEntries, 0);
      assert.equal(scan.inaccessibleDirectories, 0);
      latest[version] = scan;
    }
    if (round > 0) rounds.push(row);
  }
  let summarySha256;
  const medianMs = {};
  for (const version of versions) {
    const samples = rounds.map(row => row[version]).sort((a, b) => a - b);
    medianMs[version] = (samples[2] + samples[3]) / 2;
    const digest = await summaryDigest(latest[version], hosts[version].request);
    if (summarySha256 !== undefined) assert.equal(digest, summarySha256, profile.name);
    summarySha256 = digest;
  }
  return { ...profile, videos, rounds, medianMs, summarySha256 };
}

(async () => {
  const dlls = process.argv.slice(2);
  assert(dlls.length === 2 || dlls.length === 3, 'Usage: node scripts/compare-scan-profiles.cjs <baseline.dll> <candidate.dll> [reference.dll]');
  const output = await createOutput('scan-profiles');
  const fixture = path.join(output, 'isolated-fixture');
  await fs.mkdir(fixture);
  const hosts = {};
  const report = { harness: measurementSource(), binaries: {}, profiles: [],
    machine: { platform: process.platform, os: os.release(), cpu: os.cpus()[0]?.model, node: process.version } };
  try {
    for (const [i, dll] of dlls.entries()) {
      const version = ['baseline', 'candidate', 'reference'][i];
      const identity = await identifyHost(dll);
      const state = path.join(fixture, 'host-' + version);
      await fs.mkdir(state);
      report.binaries[version] = identity;
      hosts[version] = startHost(identity.hostPath, state);
    }
    for (const profile of profiles) {
      const result = await measureProfile(path.join(fixture, profile.name), profile, hosts);
      report.profiles.push(result);
      console.log(profile.name, result.medianMs);
    }
    for (const [version, host] of Object.entries(hosts))
      Object.assign(report.binaries[version], await hostRuntime(host.request));
    await fs.writeFile(path.join(output, 'results.json'), JSON.stringify(report, null, 2));
  } finally {
    await Promise.all(Object.values(hosts).map(host => host.close()));
    await removeFixture(output, fixture);
  }
  console.log(output);
})().catch(error => { console.error(error); process.exitCode = 1; });
