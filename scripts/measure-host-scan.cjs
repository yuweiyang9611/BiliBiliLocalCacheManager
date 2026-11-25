// Reuses only a synthetic fixture retained by verify-desktop-requirements.cjs.
// Does not accept real cache roots. An optional second argument selects a separately built Host DLL for comparison.
const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const { execFileSync } = require('node:child_process');
const { measurementSource, identifyHost, startHost, summaryDigest } = require('./scan-measurement-support.cjs');
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
  const hostPath = await fs.realpath(process.argv[3] ?? path.join(repository, 'BiliBiliLocalCacheManager.Desktop.Host', 'bin', 'Release', 'net11.0', 'BiliBiliLocalCacheManager.Desktop.Host.dll'));
  const report = { scope: 'Synthetic cache, sequential scans in one Host process; first sample is not an OS cold-cache guarantee.',
    source: { ...measurementSource(), hostPath, scope: 'Measurement script checkout' },
    measuredHost: await identifyHost(hostPath),
    machine: { platform: process.platform, os: os.release(), cpu: os.cpus()[0]?.model, memoryBytes: os.totalmem() },
    data: previous.data, scans: [], diagnosticsPath: path.join(output, 'diagnostics.zip') };
  const { request, close } = startHost(hostPath, output);
  try {
    let lastScan;
    for (let index = 0; index < 3; index++) {
      const requestId = `scan-${index + 1}`;
      const start = performance.now();
      const result = await request('scan', { rootPath: path.join(fixture, 'cache'), includeIncomplete: false, persistSettings: false, offset: 0, pageSize: 100 }, requestId);
      const milliseconds = performance.now() - start;
      assert.equal(result.totalItems, previous.data.videos);
      assert.equal(result.includedEntries, previous.data.segments);
      report.scans.push({ sample: index + 1, requestId, milliseconds, videos: result.totalItems, segments: result.includedEntries,
        invalidEntries: result.invalidEntries, unavailablePartIdentities: result.unavailablePartIdentities });
      console.log(JSON.stringify(report.scans.at(-1)));
      lastScan = result;
    }
    await request('diagnostics.export', { outputPath: report.diagnosticsPath, rootPath: path.join(fixture, 'cache') });
    const contents = process.platform === 'win32'
      ? execFileSync('tar.exe', ['-xOf', report.diagnosticsPath, 'recent-events.json'], { encoding: 'utf8', windowsHide: true })
      : execFileSync('unzip', ['-p', report.diagnosticsPath, 'recent-events.json'], { encoding: 'utf8' });
    const events = JSON.parse(contents);
    for (const sample of report.scans) {
      const event = events.find(e => e.category === 'ScanTiming' && e.message.startsWith(`request=${sample.requestId};`));
      assert(event, `Missing ScanTiming for ${sample.requestId}`);
      const fields = Object.fromEntries(event.message.split('; ').map(field => field.split('=')));
      sample.timings = {};
      for (const name of ['waitMs', 'buildMs', 'snapshotMs', 'pageMs', 'totalMs']) {
        const value = Number(fields[name]);
        assert(Number.isFinite(value), `Missing ${name} for ${sample.requestId}`);
        sample.timings[name] = value;
      }
    }
    // Compare every published summary between implementations without adding paging to a timed scan.
    report.summarySha256 = await summaryDigest(lastScan, request);
  } finally {
    await close();
    await fs.writeFile(path.join(output, 'results.json'), JSON.stringify(report, null, 2));
  }
  console.log(JSON.stringify({ output, diagnosticsPath: report.diagnosticsPath }));
})().catch(error => { console.error(error); process.exitCode = 1; });
