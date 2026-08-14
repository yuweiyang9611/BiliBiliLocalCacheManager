# BiliBiliLocalCacheManager

用于扫描、搜索、播放和管理本地哔哩哔哩缓存的 .NET 工具集，提供 CLI 与 WPF 两种入口。

## 主要能力

- 从 `根目录\avid\分段目录\entry.json` 构建内存索引
- 按标题、分段名、UP 主、Bvid、Avid 搜索
- 识别新版 DASH、中期 DASH、旧版 Lua 和混合缓存结构
- 使用系统默认播放器、mpv 或 VLC 播放缓存
- 把缓存导出成按标题命名的普通 MP4，支持单条另存与多选批量导出，命中转码缓存时秒出
- 按 avid 查看和删除缓存；CLI 与 WPF 一样默认移入应用回收站，`--permanent` 才永久删除，另有 `--dry-run`
- CLI `trash` 子命令组：列出、还原、统计和永久清空应用回收站
- 在 WPF 中查看分段详情、空间统计、更新时间和批量操作
- 扫描/建索引过程中显示进度并支持取消
- 使用可控页面队列逐项启动播放器，避免批量弹窗
- FFmpeg 参与播放准备时显示独立进度窗口、完成百分比和预计剩余时间，并允许取消转码。
- 复用持久化转码缓存；相同源视频再次播放时直接使用已有产物，不再重复运行 FFmpeg
- 在窗口显示后执行非阻塞缓存维护，并在播放成功后自动应用保留期与容量策略
- 在同一“存储管理”区域查看原始缓存、转码缓存、应用回收站和预计可释放空间
- WPF 删除默认进入应用回收站，支持撤销最近一次批量删除，也可确认后永久清理其中由本应用管理的条目
- 导出经过路径、令牌和媒体标题脱敏的诊断包，便于排查设置、存储与 FFmpeg 问题

## 可靠性策略

### 扫描报告

扫描不会因为单个损坏条目而中断。CLI 和 WPF 会报告：

- 已收录分段数
- 跳过的未完成分段数
- 无法解析的 `entry.json` 数量
- 无法访问的目录数量

具体问题明细默认最多保留 100 条，统计总数不受此限制。

### 播放产物

需要转封装的媒体会写入：

```text
%LOCALAPPDATA%\BiliBiliLocalCacheManager\TranscodeCache
```

输出文件按处理配置版本、缓存结构、规范化源路径、大小和修改时间计算指纹。标题等展示元数据变化不会造成缓存失效；源文件未变化时，WPF 与 CLI 跨重启复用已有产物。

生成过程使用唯一临时文件并在成功后原子替换；同一产物使用进程内与跨进程锁，避免多个实例重复生成。等待其他实例生成同一产物时会显示可取消的进度窗口，纯缓存命中仍直接播放。若源媒体在 FFmpeg 运行期间继续变化，本次结果会被丢弃，不会进入缓存。

默认值为保留 30 天、总空间上限 20 GB；清理时会：

- 删除超过保留天数未使用的播放产物
- 总空间超过容量上限时，从最旧文件开始清理
- 临时保护刚创建、刚复用以及本次运行中最近交给播放器的产物；保护集合最多 8 项、最长 24 小时，并受容量上限约束（最新一项除外）
- 统计并清理可安全取得生成锁的残留 `.building-*` 文件；正在生成的文件不会被删除
- 将清理范围严格限制在上述受管目录

WPF 可直接设置“保留天数”（1–1825 天，最长 5 年）和“容量上限 (GB)”（1–128 GB）。设置会保存到本机用户配置，并用于下一次手动或后台维护。程序先显示主窗口，再在后台执行启动维护；成功启动播放器后也会请求一次合并后的后台维护，不阻塞主要操作。缓存管理区会显示当前数量、占用和按现有策略预计可释放的空间，并提供“打开缓存”“按策略清理”和确认后的“一键清空”；这些操作不会删除 B 站原始缓存。

FFmpeg 默认采用流复制完成转封装。DASH 会先快速探测音频编码：AAC 音视频全部直接复制，非 AAC 只转换音频并保持视频流不变；已有可靠时长元数据时跳过额外的视频时长分析。线程数、编码 preset 和 GPU 编码对当前流复制路径没有加速作用。

运行时、CI 和 Release 共用仓库根目录的 `ffmpeg-bundle.json`，其中固定 BtbN 月末长期保留构建的 tag、asset、精确 URL 与 SHA-256。默认运行时始终使用并校验这份固定 bundle，不会被 PATH 中任意版本的 FFmpeg 覆盖；只有显式设置 `BILIBILI_LOCAL_CACHE_MANAGER_USE_SYSTEM_FFMPEG=1` 才会选择系统安装，显式 `BILIBILI_LOCAL_CACHE_MANAGER_FFMPEG_ARCHIVE_PATH` 仍可用于受控离线包。首次需要转封装时的下载、SHA-256 校验和解压均通过同一进度窗口显示阶段、百分比和预计剩余时间，并可取消；未完成的下载或解压不会被标记为可用安装。

## WPF 使用体验

- **扫描与搜索**：扫描或自动建立搜索索引时显示已处理分段数，可随时点击“取消”。
- **播放队列**：“播放所选”只启动第一项，其余页面进入队列；使用“播放下一项”逐项推进，或“清空队列”终止后续启动。
- **统一存储管理**：同时显示原始缓存、转码缓存、应用回收站、总占用和预计可释放空间；统计在后台刷新，新请求会取消已过时的扫描。应用回收站会分别显示已验证条目与“旧版未验证”条目的条目数、文件数和占用；旧版未验证占用单独列示，不并入已验证总占用或可释放空间。
- **转码缓存**：可设置保留天数和容量上限；启动和播放后的维护不会占用全局操作状态，手动清理仍会显示明确结果。
- **缓存命中体验**：命中已有转码产物时直接启动播放器，不再闪现 FFmpeg 进度窗口。
- **播放器偏好**：可选择系统默认优先、仅系统默认、mpv 或 VLC。
- **安全删除**：支持多选缓存，默认移动到缓存根目录下的 `.BiliBiliLocalCacheManager-Trash`；“撤销删除”可恢复最近一次操作，“清空回收站”则在确认后永久删除其中由本应用管理的条目。同一缓存根目录的移动、恢复、统计和彻底清空会跨实例串行。新条目使用 v1 身份元数据（avid、相对原路径、UTC 删除时间和唯一 ID）；Windows 上的移动、恢复和删除都绑定已校验的物理句柄。永久清理开始后会同时保留条目内状态和回收站根目录日志，根日志还绑定卷序列号与文件 ID；只有条目目录确认删除后才删除根日志，因此状态文件丢失、进程中断或日志删除失败都可安全重试，同名替换目录不会被误删。界面显示的已释放空间按清理前后真实净减少量计算，不会把本次临时创建的状态文件高报为收益。带有效元数据的旧版条目继续支持，并可在缓存根目录迁移后识别。缺少元数据但目录名符合旧版格式的条目会标记为“旧版未验证”，普通清空不会删除；仅在第二次明确确认后才会纳入永久清理。损坏、身份不匹配或未来 SchemaVersion 的条目始终保留并报告失败。
- **空间摘要**：状态区显示当前列表、未完成缓存和已选择缓存的数量与空间；大小和更新时间列支持排序。
- **设置持久化**：缓存根目录、搜索选项、播放器偏好和转码缓存策略保存在 `%LOCALAPPDATA%\BiliBiliLocalCacheManager\settings.json`。设置文件带有 SchemaVersion；旧值会迁移并提示，较新版本文件不会被旧程序覆盖，损坏文件会先备份再恢复默认值。若另一个实例已修改同版本设置，旧实例会停止自动维护并提示重启，不会用整份旧设置覆盖新值。
- **诊断导出**：底部“导出诊断”生成 ZIP，包含运行环境、设置迁移状态、存储摘要、FFmpeg 来源和近期事件；缓存路径、用户目录、令牌、URL 私密部分及已知媒体标题会先脱敏。导出诊断不会初始化或下载 FFmpeg。

- **导出 MP4**：底部“导出 MP4”或右键菜单可把所选缓存导出成普通 MP4。单条会弹出另存对话框，多条则选择目标文件夹并按“标题”或“标题 - P2 分段名”命名；文件名会清洗非法字符并自动去重。导出复用播放的转码产物，已生成过的内容不会重复转码。
- **主题与外观**：跟随 Windows 深浅色设置，窗口标题显示版本号，列表额外显示 UP 主、BV 号和时长。
- **键盘操作**：F5 扫描、Ctrl+F 聚焦关键字、Enter 搜索、Delete 删除、Ctrl+Z 撤销、Ctrl+E 导出、Esc 取消；双击列表行直接播放。
- **省心启动**：记住的缓存目录会在启动时自动扫描，选完目录也会立即扫描；FFmpeg 在后台预热，首次播放不再等待下载。
- **出错可追**：未处理异常会写入 `%LOCALAPPDATA%\BiliBiliLocalCacheManager\CrashReports\` 并提示，不再直接闪退。

CLI 的 `delete` 默认移入应用回收站，可用 `trash restore` 还原；只有加 `--permanent` 才永久删除。两条路径在非交互终端下都必须显式加 `--yes` 才会执行。

兼容提示：v0.3.0 等仅识别旧版目录格式的构建会忽略新建的 v1 回收站条目；降级不会自动删除这些条目，但也不能用旧版恢复或清空。请使用支持 v1 回收站元数据的版本处理。

## 项目结构

- `BiliBiliLocalCacheManager.Core/`：缓存索引、扫描报告、搜索与删除逻辑
- `BiliBiliLocalCacheManager.Playback/`：缓存结构识别、媒体整理、产物生命周期与播放
- `BiliBiliLocalCacheManager.Cli/`：命令行界面
- `BiliBiliLocalCacheManager.Wpf/`：WPF 桌面界面
- `BiliBiliLocalCacheManager.Core.Tests/`：索引、搜索、删除与扫描报告测试
- `BiliBiliLocalCacheManager.Playback.Tests/`：播放产物和目录结构回归测试
- `BiliBiliLocalCacheManager.Cli.Tests/`：CLI 行为测试
- `BiliBiliLocalCacheManager.Wpf.Tests/`：ViewModel 与交互桌面 UI 测试

本地 `AI_Record/` 资料被保留，但不会提交到 Git。

## 开发与验证

项目使用 .NET 10，SDK 版本由 `global.json` 固定在 `10.0.300` feature band。

```powershell
dotnet restore BiliBiliLocalCacheManager.slnx
dotnet build BiliBiliLocalCacheManager.slnx --configuration Release --no-restore
dotnet test BiliBiliLocalCacheManager.slnx --configuration Release --no-build --filter "Category!=UI&Category!=FFmpegIntegration"
```

真实 FFmpeg 集成测试默认不下载外部工具。以下脚本从共享清单准备精确版本，最多重试 3 次并验证 SHA-256，然后可显式执行 5 个媒体集成场景：

```powershell
$archive = ./scripts/prepare-ffmpeg-integration.ps1 -EnvironmentFile ""
$env:BILIBILI_RUN_FFMPEG_INTEGRATION_TESTS = "1"
$env:BILIBILI_LOCAL_CACHE_MANAGER_FFMPEG_ARCHIVE_PATH = $archive
dotnet test BiliBiliLocalCacheManager.Playback.Tests/BiliBiliLocalCacheManager.Playback.Tests.csproj --configuration Release --filter "Category=FFmpegIntegration"
```

WPF UI 自动化测试需要 Windows 交互式桌面环境。Windows CI 与标签 Release 都从同一清单准备 FFmpeg，并运行上述 5 个真实媒体集成测试；更新 FFmpeg 只需在一次经过校验的变更中修改该清单。

## CLI 示例

```powershell
dotnet run --project BiliBiliLocalCacheManager.Cli -- scan --root "D:\BilibiliDownload"
dotnet run --project BiliBiliLocalCacheManager.Cli -- search "关键词" --root "D:\BilibiliDownload"
dotnet run --project BiliBiliLocalCacheManager.Cli -- play 187742 --root "D:\BilibiliDownload" --segment 1
dotnet run --project BiliBiliLocalCacheManager.Cli -- delete 187742 --root "D:\BilibiliDownload" --dry-run

# 默认移入应用回收站（可还原），需要确认或 --yes
dotnet run --project BiliBiliLocalCacheManager.Cli -- delete av187742 --root "D:\BilibiliDownload" --yes
dotnet run --project BiliBiliLocalCacheManager.Cli -- trash list --root "D:\BilibiliDownload"
dotnet run --project BiliBiliLocalCacheManager.Cli -- trash restore 187742 --root "D:\BilibiliDownload"

# 永久删除必须显式声明
dotnet run --project BiliBiliLocalCacheManager.Cli -- delete 187742 --root "D:\BilibiliDownload" --permanent --yes
```

Windows CI 位于 `.github/workflows/ci.yml`，负责还原、Release 构建、非 UI 测试、CLI/WPF 发布和产物上传。

## 下载与发布

正式发布产物面向 Windows x64，采用自包含单文件发布，目标计算机无需预先安装 .NET：

- `BiliBiliLocalCacheManager-wpf-v<版本>-win-x64.zip`：桌面版，推荐普通用户使用
- `BiliBiliLocalCacheManager-cli-v<版本>-win-x64.zip`：命令行版
- `SHA256SUMS.txt`：两个 ZIP 的 SHA-256 校验值

本地生成发布包：

```powershell
powershell -ExecutionPolicy Bypass -File ./scripts/build-release.ps1
```

指定版本或跳过重复测试：

```powershell
powershell -ExecutionPolicy Bypass -File ./scripts/build-release.ps1 -Version 0.3.0
powershell -ExecutionPolicy Bypass -File ./scripts/build-release.ps1 -Version 0.3.0 -SkipTests
```

产物写入 `artifacts/release/`。下载后可在该目录验证校验值：

```powershell
Get-Content SHA256SUMS.txt
Get-FileHash .\BiliBiliLocalCacheManager-wpf-v0.3.0-win-x64.zip -Algorithm SHA256
```

推送符合 `v<主版本>.<次版本>.<修订版本>` 格式的标签会触发 `.github/workflows/release.yml`，完成测试、打包并创建 GitHub Release。例如：

```powershell
git tag v0.3.0
git push origin v0.3.0
```

发布前请同步更新 `Directory.Build.props` 中的 `VersionPrefix` 和 `CHANGELOG.md`。仓库当前尚未附带开源许可证；对外分发前应先明确并加入所选许可证。
