// Times a real single-AV mutation/refresh on two Host builds. Only uses newly created cache data.
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const { performance } = require('node:perf_hooks');
const { createOutput, removeFixture } = require('./desktop-e2e-support.cjs');
const { measurementSource, identifyHost, hostRuntime, readScanTimings, startHost, summaryDigest } = require('./scan-measurement-support.cjs');

const videos = 12_000;
const rounds = 10;
async function createLibrary(root) {
  for (let avid = 1; avid <= videos; avid++) {
    for (let page = 1; page <= (avid === 1 ? 2 : 1); page++) {
      const dir = path.join(root, String(avid), 'c_' + page);
      await fs.mkdir(dir, { recursive: true });
      await fs.writeFile(path.join(dir, 'entry.json'), JSON.stringify({
        avid, title: 'Video ' + avid, is_completed: true, total_bytes: 10, downloaded_bytes: 10,
        time_update_stamp: 1750000000000, page_data: { cid: avid * 10 + page, page, part: 'Part ' + page },
      }));
    }
  }
}

function median(values) {
  const sorted = [...values].sort((a, b) => a - b);
  const middle = Math.floor(sorted.length / 2);
  return (sorted[middle] + sorted[Math.floor((sorted.length - 1) / 2)]) / 2;
}

(async () => {
  const [baseline, candidate] = process.argv.slice(2);
  assert(process.argv.length === 4, 'Usage: node scripts/compare-incremental-refresh.cjs <baseline.dll> <candidate.dll>');
  const output = await createOutput('incremental-comparison');
  const scratch = await fs.mkdtemp(path.join(os.tmpdir(), 'blcm-incremental-'));
  const fixture = path.join(scratch, 'isolated-fixture');
  const root = path.join(fixture, 'cache');
  await fs.mkdir(fixture);
  const params = { rootPath: root, persistSettings: false, includeIncomplete: false, pageSize: 100 };
  const report = { harness: measurementSource(), platform: process.platform, cpu: os.cpus()[0]?.model, videos,
    binaries: {}, samples: [], summarySha256: {}, medians: {} };
  const hosts = {}, tokens = {};
  try {
    await createLibrary(root);
    for (const [name, dll] of Object.entries({ baseline, candidate })) {
      const state = path.join(fixture, name);
      await fs.mkdir(state);
      const binary = await identifyHost(dll);
      const host = hosts[name] = startHost(binary.hostPath, state);
      report.binaries[name] = { ...binary, ...await hostRuntime(host.request) };
      const scan = await host.request('scan', params);
      assert.equal(scan.hasWarnings, false);
      assert.equal(scan.totalItems, videos);
      assert.equal(scan.includedEntries, videos + 1);
      tokens[name] = scan.indexToken;
    }
    for (let round = 0; round <= rounds; round++) {
      for (const name of (round % 2 ? ['candidate', 'baseline'] : ['baseline', 'candidate'])) {
        const { request } = hosts[name];
        const moved = await request('trash.move', { rootPath: root, indexToken: tokens[name], targets: [{ avid: '1', pageIndexes: [2] }] });
        assert.deepEqual(moved.moved, ['1:P2']);
        assert.equal(moved.entryIds.length, 1);
        for (const phase of ['move', 'restore']) {
          const parts = phase === 'move' ? 1 : 2;
          if (phase === 'restore') {
            const restored = await request('trash.restore', { rootPath: root, entryIds: moved.entryIds });
            assert.deepEqual(restored.restored, moved.entryIds);
          }
          const id = `scan-${round}-${phase}`;
          const start = performance.now();
          const scan = await request('scan', { ...params, incremental: true }, id);
          const requestMs = performance.now() - start;
          assert.equal(scan.hasWarnings, false);
          assert.equal(scan.totalItems, videos);
          assert.equal(scan.includedEntries, videos - 1 + parts);
          assert.equal(scan.items[0].avid, '1');
          assert.equal(scan.items[0].segmentCount, parts);
          tokens[name] = scan.indexToken;
          const zip = path.join(fixture, name, 'timings.zip');
          await request('diagnostics.export', { rootPath: root, outputPath: zip });
          const timing = readScanTimings(zip).get(id);
          assert(timing && timing.incremental === 'True' && Number(timing.readEntries) === parts, 'Expected a single-AV incremental scan');
          const buildMs = Number(timing.buildMs);
          assert(Number.isFinite(buildMs));
          if (round) report.samples.push({ round, name, phase, readEntries: parts, buildMs, requestMs });
          if (round === rounds) {
            const digest = await summaryDigest(scan, request);
            if (report.summarySha256[phase]) assert.equal(digest, report.summarySha256[phase]);
            report.summarySha256[phase] = digest;
          }
        }
      }
    }
    for (const name of Object.keys(hosts)) {
      const samples = report.samples.filter(sample => sample.name === name);
      report.medians[name] = { buildMs: median(samples.map(s => s.buildMs)), requestMs: median(samples.map(s => s.requestMs)) };
    }
    await fs.writeFile(path.join(output, 'results.json'), JSON.stringify(report, null, 2));
    console.log(JSON.stringify({ output, medians: report.medians }));
  } finally {
    await Promise.all(Object.values(hosts).map(host => host.close()));
    await removeFixture(scratch, fixture);
    await fs.rmdir(scratch);
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
