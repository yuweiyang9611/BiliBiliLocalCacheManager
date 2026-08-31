# BiliBiliLocalCacheManager

用于扫描、搜索、播放、导出和安全管理本地哔哩哔哩缓存的工具集。项目提供 .NET 10 CLI，以及基于 Electron 44、内置 Chromium 和 React/TypeScript 的桌面应用；桌面界面通过受限的 IPC 与独立 .NET Host 通信，不在渲染进程中开放 Node.js。

## 主要能力

- 从 `根目录/avid/分段目录/entry.json` 建立缓存索引，识别新版 DASH、中期 DASH、旧版 Lua 和混合结构
- 按标题、分段名、UP 主、Bvid 或 Avid 搜索，并报告损坏条目、未完成分段和不可访问目录
- 对大缓存库使用带会话索引令牌的分页摘要；只有聚焦某条缓存时才分页解析分段和播放结构，列表采用有界虚拟渲染
- 使用系统默认播放器、mpv 或 VLC 播放，按队列逐项启动，避免批量弹窗
- 将单条或多选缓存导出为普通 MP4，并复用已有转码产物
- 统计原始缓存、转码缓存、应用回收站、总占用和预计可释放空间
- 默认把删除内容移动到应用回收站，支持列表、恢复、撤销和受保护的永久清理
- 分别控制是否记住缓存目录、是否在启动时自动扫描；自动扫描默认关闭，存储统计与回收站按页面懒加载
- 设置转码产物保留期与容量上限，并执行非阻塞后台维护
- 导出包含运行时、设置摘要、FFmpeg、转码缓存统计和最近事件的诊断 ZIP；最近事件会替换已知缓存路径、用户主目录、URL 与常见命名凭据

## 桌面架构

Electron 44 自带运行所需的 Chromium，不依赖系统浏览器。桌面包还携带针对目标平台自包含发布的 .NET 10 Host，因此最终用户不需要安装 Node.js 或 .NET Runtime。

安全边界如下：

- `contextIsolation` 与渲染器 sandbox 启用，`nodeIntegration` 关闭。
- Preload 只暴露显式允许的 API；任意导航、窗口创建和权限请求默认拒绝。
- 渲染器资源通过只映射打包目录的 `blcm://` 自定义安全协议加载，不授予 `file://` 额外权限。
- 打包时关闭 `ELECTRON_RUN_AS_NODE`、`NODE_OPTIONS` 和主进程调试参数，并限制应用只能从 ASAR 加载；Windows 包同时启用 ASAR 完整性校验。
- 缓存和媒体操作在独立 Host 进程中执行，主进程通过逐行 JSON RPC 调用。
- Host v2 响应在主进程边界做运行时结构校验；过期索引令牌不会触发隐式重扫，必须由用户显式重新扫描。
- 桌面应用仅支持单实例；第二次启动会聚焦已有窗口。
- Linux 构建固定使用 Chromium 的 X11/Ozone 后端；在 Wayland 桌面中依赖 XWayland 兼容层，不启用原生 Wayland 后端。

## 0.4.0 桌面发布目标

| 平台 | 架构与桌面环境 | 发布形式与范围 |
| --- | --- | --- |
| Windows 10 / 11 | x64 | 第一优先级，发布 NSIS 安装器与免安装 zip |
| Ubuntu 24.04 及更高版本 | x64，GNOME，XWayland | Linux 第一优先级，发布 deb |
| Debian 13 | x64，GNOME 或 KDE，XWayland | 目标支持，发布 deb |
| Fedora 43 | x64，至少 GNOME 或 KDE，XWayland | 目标支持，发布 rpm |

表中的桌面环境是兼容与发布目标；自动化覆盖范围和真实桌面会话测试的区别见“开发与验证”。

每次正式发布都应按 [桌面兼容性验收清单](docs/desktop-compatibility.md) 在真实机器或完整虚拟机中记录结果；Fedora 43 至少选择 GNOME 或 KDE 之一完成整套检查。

以下环境不在支持承诺内：原生 Wayland 会话、Alpine/musl、NixOS、Flatpak、Linux ARM64、macOS，以及上表未列出的 Linux 桌面组合。Linux 用户必须能运行 XWayland；应用会显式选择 Chromium 的 X11/Ozone 后端。

### Linux 运行依赖与安全限制

Linux 桌面包与 CLI 是 `linux-x64` 自包含发布，但 **FFmpeg 不随 Linux 包分发**。deb 声明 `ffmpeg` 依赖，rpm 声明 Fedora 官方 `ffmpeg-free` 依赖；直接使用 CLI 压缩包时需要自行安装。播放准备和 MP4 导出要求系统 `PATH` 中存在可执行的 `ffmpeg` 与 `ffprobe`，建议在启动前验证：

```bash
ffmpeg -version
ffprobe -version
```

Ubuntu 与 Debian 使用系统 `ffmpeg` 包；Fedora 43 的官方 `ffmpeg-free` 同时提供 `ffmpeg` 和 `ffprobe`，但支持的编解码器范围较窄。若媒体需要额外编解码器，请使用与系统包管理器兼容的可信软件源。

Linux 当前禁用不可逆删除：

- 桌面端不能永久清空应用回收站。
- CLI 在 Linux 上拒绝执行非 dry-run 的 `--permanent` 删除；`--dry-run` 仍可用于检查目标。
- 移入应用回收站、列出和恢复仍受支持。

这一限制会持续到 Unix 物理目录身份校验达到与 Windows 句柄绑定删除相同的安全保证。

## 可靠性与数据安全

扫描不会因为单个损坏条目中断，具体问题明细默认最多保留 100 条，统计总数不受此限制。

需要转封装的媒体写入当前用户的本地应用数据目录。桌面端默认保留 30 天、总空间上限 10 GB；清理只处理受管目录中的过期或超限产物，并保护正在生成、最近创建、最近复用和刚交给播放器的文件。产物根据处理配置、缓存结构、规范化源路径、大小和修改时间生成指纹；源媒体在处理期间发生变化时，本次结果会被丢弃。

Windows 使用仓库 `ffmpeg-bundle.json` 指定并校验的 FFmpeg bundle。Linux 始终把 FFmpeg 视为系统依赖，不下载 Windows bundle。

打包后的桌面应用在启动 Host 前会移除开发、测试、Host 路径、FFmpeg 下载覆盖以及 .NET 启动钩子/分析器等运行时注入变量；Windows 的本地 FFmpeg 归档覆盖也必须与内置清单的 SHA-256 匹配。这样，继承自启动终端的环境变量不能替换或注入打包应用执行的 Host 与 FFmpeg。

应用回收站使用版本化身份元数据。Windows 永久清理会进行物理句柄、卷与文件 ID 复核，并保留可恢复的清理日志直到删除提交；桌面端还要求永久清理请求显式绑定当前缓存根目录，并携带界面所显示的完整、非空条目集合。Core 会在取得同根目录跨进程变更锁后再次核对该集合；目录变化、列表过期、损坏、身份不匹配或未来 SchemaVersion 的条目都会使整批操作被安全拒绝。

桌面端默认不会在启动时扫描磁盘。“记住缓存目录”与“启动时自动扫描”是两个独立选项；从旧版设置首次升级且存在已保存目录时，应用会要求明确选择今后的行为。保存的搜索词不会隐式触发扫描；只有本会话已明确扫描当前目录后才会自动筛选。即使选择不记住目录，当前会话中已验证的目录仍可继续使用，下次启动时才会忘记。

## 项目结构

- `BiliBiliLocalCacheManager.Core/`：索引、搜索、扫描报告和安全删除逻辑
- `BiliBiliLocalCacheManager.Playback/`：缓存结构识别、媒体整理、FFmpeg 与产物生命周期
- `BiliBiliLocalCacheManager.Cli/`：命令行界面
- `BiliBiliLocalCacheManager.Desktop/`：Electron 主进程、Preload、React 渲染器和前端测试
- `BiliBiliLocalCacheManager.Desktop.Host/`：桌面应用的 .NET JSON-lines Host
- `BiliBiliLocalCacheManager.Desktop.Host.Tests/`：桌面 Host 协议、持久化和诊断脱敏契约测试
- `BiliBiliLocalCacheManager.Core.Tests/`、`Playback.Tests/`、`Cli.Tests/`：.NET 回归测试

## 开发与验证

开发环境固定使用：

- .NET SDK `10.0.400`
- Node.js 24 与仓库锁定的 npm 依赖

验证 .NET 与 Electron：

```powershell
dotnet restore BiliBiliLocalCacheManager.slnx
dotnet build BiliBiliLocalCacheManager.slnx --configuration Release --no-restore
# Windows
dotnet test BiliBiliLocalCacheManager.slnx --configuration Release --no-build --filter "Category!=FFmpegIntegration"
# Linux
dotnet test BiliBiliLocalCacheManager.slnx --configuration Release --no-build --filter "Category!=FFmpegIntegration&Category!=WindowsOnly"

Push-Location BiliBiliLocalCacheManager.Desktop
npm ci
npm run check
Pop-Location
```

本地开发桌面应用：

```powershell
dotnet build BiliBiliLocalCacheManager.Desktop.Host/BiliBiliLocalCacheManager.Desktop.Host.csproj
Push-Location BiliBiliLocalCacheManager.Desktop
npm ci
npm run dev
Pop-Location
```

真实 FFmpeg 集成测试默认关闭。Windows 可从共享清单准备经过 SHA-256 校验的固定归档，然后显式运行：

```powershell
$archive = ./scripts/prepare-ffmpeg-integration.ps1 -EnvironmentFile ""
$env:BILIBILI_RUN_FFMPEG_INTEGRATION_TESTS = "1"
$env:BILIBILI_LOCAL_CACHE_MANAGER_FFMPEG_ARCHIVE_PATH = $archive
dotnet test BiliBiliLocalCacheManager.Playback.Tests/BiliBiliLocalCacheManager.Playback.Tests.csproj --configuration Release --filter "Category=FFmpegIntegration"
```

`.github/workflows/ci.yml` 使用 .NET `10.0.400` 与 Node.js 24 构建和测试 .NET/Electron，并在 Windows 2025 与 Ubuntu 24.04 runner 上分别打包、检查 Electron fuses、运行打包后自检。自检会加载真实渲染器、读取隔离设置、通过 Preload/IPC 调用内置 Host，并扫描一条临时缓存夹具。Ubuntu 24.04 还使用 Xvfb smoke 源码构建；Debian 13 与 Fedora 43 容器会分别安装实际 deb/rpm，再以 Xvfb smoke 强制 X11 路径。稳定的 `ci-required` 汇总检查只有在隐私检查、完整构建/测试/打包矩阵和发行版安装包自检全部成功时才通过。Xvfb 是独立 X11 server，这些检查不等同于真实 GNOME/KDE XWayland 会话验证。真实桌面检查是项目发布清单中的人工验收要求，但当前 GitHub workflow 不自动核验测试记录，发布维护者必须在触发发布前完成。

## CLI 示例

```powershell
dotnet run --project BiliBiliLocalCacheManager.Cli -- scan --root "D:\BilibiliDownload"
dotnet run --project BiliBiliLocalCacheManager.Cli -- search "关键词" --root "D:\BilibiliDownload"
dotnet run --project BiliBiliLocalCacheManager.Cli -- play 187742 --root "D:\BilibiliDownload" --segment 1

# 默认移入应用回收站（可恢复）
dotnet run --project BiliBiliLocalCacheManager.Cli -- delete av187742 --root "D:\BilibiliDownload" --yes
dotnet run --project BiliBiliLocalCacheManager.Cli -- trash list --root "D:\BilibiliDownload"
dotnet run --project BiliBiliLocalCacheManager.Cli -- trash restore 187742 --root "D:\BilibiliDownload"

# Windows 上永久删除必须显式声明；Linux 只允许 --dry-run
dotnet run --project BiliBiliLocalCacheManager.Cli -- delete 187742 --root "D:\BilibiliDownload" --permanent --yes
```

## 下载与发布

正式发布覆盖 `win-x64` 与 `linux-x64`，必须在对应原生操作系统上构建，不支持从 Windows 交叉生成 Linux Electron 包。

- `BiliBiliLocalCacheManager-<版本>-windows-x64.exe`：Windows NSIS 桌面安装器
- `BiliBiliLocalCacheManager-<版本>-windows-x64.zip`：Windows 免安装桌面包
- `BiliBiliLocalCacheManager-<版本>-linux-x64.deb`：Linux deb 桌面包
- `BiliBiliLocalCacheManager-<版本>-linux-x64.rpm`：Linux rpm 桌面包
- `BiliBiliLocalCacheManager-cli-v<版本>-win-x64.zip`：Windows CLI
- `BiliBiliLocalCacheManager-cli-v<版本>-linux-x64.tar.gz`：Linux CLI
- `SHA256SUMS-<rid>.txt`：本地脚本生成的当前平台校验值；GitHub Release 会合并为 `SHA256SUMS.txt`

当前流水线没有仓库内置的代码签名证书；本地脚本与未配置发布密钥的 CI 所生成的 Windows 可执行文件默认未签名，可能触发 SmartScreen。维护者可通过 GitHub Actions secrets `WINDOWS_CSC_LINK` 与 `WINDOWS_CSC_KEY_PASSWORD` 提供 electron-builder 兼容的证书；一旦配置证书，构建会强制签名主程序、内置 Host 与安装器，签名失败即终止。SHA-256 校验值可检查下载完整性，但不能替代 Authenticode 发布者身份验证。

在当前原生平台生成对应产物：

```powershell
pwsh ./scripts/build-release.ps1 -Version 0.4.0
```

Windows Release 可加 `-RunFfmpegIntegrationTests`；`-SkipTests` 与该选项不能同时使用。产物写入 `artifacts/release/`。推送 `v0.4.0` 形式的标签会触发 `.github/workflows/release.yml`，分别生成 Windows 与 Linux 包，合并校验值并创建 GitHub Release。

发布前必须同步更新 `Directory.Build.props`、`BiliBiliLocalCacheManager.Desktop/package.json` 和 `CHANGELOG.md` 中的版本信息。

## 许可证

本项目采用 [MIT License](LICENSE)。CLI 与桌面发布产物均包含许可证文本。

## 仓库历史与隐私说明

本公开仓库由原私有仓库的当前源码快照迁移而来。原私有仓库的历史元数据包含不应公开的个人隐私信息，因此公开迁移时删除旧历史并重新初始化。后续提交统一使用 GitHub `noreply` 邮箱，仓库检查会拒绝不符合要求的提交。
