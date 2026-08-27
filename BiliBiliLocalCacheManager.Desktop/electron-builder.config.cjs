const path = require('node:path');

const targetRid = process.env.DESKTOP_HOST_RID ||
  (process.platform === 'win32' ? 'win-x64' : 'linux-x64');
const hostPublishDirectory = process.env.DESKTOP_HOST_PUBLISH_DIR ||
  path.resolve(__dirname, '..', 'BiliBiliLocalCacheManager.Desktop.Host', 'bin', 'Release', 'net10.0', targetRid, 'publish');

async function applyStrictElectronFuses(context) {
  const { flipFuses, FuseVersion, FuseV1Options } = await import('@electron/fuses');
  const executableName = context.electronPlatformName === 'win32'
    ? '哔哩哔哩本地缓存管理器.exe'
    : 'bilibili-local-cache-manager';
  await flipFuses(path.join(context.appOutDir, executableName), {
    version: FuseVersion.V1,
    strictlyRequireAllFuses: true,
    [FuseV1Options.RunAsNode]: false,
    [FuseV1Options.EnableCookieEncryption]: true,
    [FuseV1Options.EnableNodeOptionsEnvironmentVariable]: false,
    [FuseV1Options.EnableNodeCliInspectArguments]: false,
    [FuseV1Options.EnableEmbeddedAsarIntegrityValidation]: true,
    [FuseV1Options.OnlyLoadAppFromAsar]: true,
    [FuseV1Options.LoadBrowserProcessSpecificV8Snapshot]: false,
    [FuseV1Options.GrantFileProtocolExtraPrivileges]: false,
    [FuseV1Options.WasmTrapHandlers]: true,
  });
}

module.exports = {
  appId: 'io.github.bilibililocalcachemanager',
  productName: '哔哩哔哩本地缓存管理器',
  asar: true,
  forceCodeSigning: process.platform === 'win32' && Boolean(process.env.CSC_LINK?.trim()),
  afterPack: applyStrictElectronFuses,
  electronFuses: {
    runAsNode: false,
    enableCookieEncryption: true,
    enableNodeOptionsEnvironmentVariable: false,
    enableNodeCliInspectArguments: false,
    enableEmbeddedAsarIntegrityValidation: true,
    onlyLoadAppFromAsar: true,
    loadBrowserProcessSpecificV8Snapshot: false,
    grantFileProtocolExtraPrivileges: false,
  },
  files: [
    'dist/**/*',
    'dist-electron/**/*',
    'package.json',
  ],
  extraResources: [
    {
      from: hostPublishDirectory,
      to: 'host',
      filter: ['**/*'],
    },
    {
      from: '../LICENSE',
      to: 'LICENSE',
    },
  ],
  directories: {
    output: 'release',
  },
  win: {
    icon: 'build/icon.ico',
    target: [
      { target: 'nsis', arch: ['x64'] },
      { target: 'zip', arch: ['x64'] },
    ],
    artifactName: 'BiliBiliLocalCacheManager-${version}-windows-x64.${ext}',
  },
  nsis: {
    oneClick: false,
    allowToChangeInstallationDirectory: true,
    deleteAppDataOnUninstall: false,
  },
  linux: {
    icon: 'build/icon.png',
    maintainer: 'BiliBiliLocalCacheManager contributors <noreply@github.com>',
    vendor: 'BiliBiliLocalCacheManager contributors',
    target: [
      { target: 'deb', arch: ['x64'] },
      { target: 'rpm', arch: ['x64'] },
    ],
    artifactName: 'BiliBiliLocalCacheManager-${version}-linux-x64.${ext}',
    category: 'Utility',
    executableName: 'bilibili-local-cache-manager',
    desktop: {
      entry: {
        Name: '哔哩哔哩本地缓存管理器',
        Comment: '扫描、播放、导出与清理本地 B 站缓存',
      },
    },
  },
  deb: {
    fpm: ['--depends=ffmpeg'],
  },
  rpm: {
    fpm: ['--depends=ffmpeg-free'],
  },
};
