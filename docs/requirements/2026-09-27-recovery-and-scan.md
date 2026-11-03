# 2026-09-27 界面恢复与扫描测量

基于 `7bbe23e8d6696e9ecfc8ac7fde1437df3d775803` 的工作区修改。该记录不是正式安装包或真实媒体播放验收。

## 已实现

- 页面错误边界位于 App 内部，重建页面不重建 App，不丢弃选择、取消状态或进行中的操作互斥。
- 根错误边界与初始化恢复使用受信任的 `reloadWhenIdle` IPC。主进程统计完整调用周期，包含 Host 请求发起前的原生目录选择/确认窗口；存在未结束请求时拒绝重载。
- Host 退出后的未知结果留在主进程，界面重载后仍恢复核对要求；恢复不自动重试播放、导出或删除。
- 操作状态的订阅、快照与实时事件合并抽入独立模块。快照返回前收到的终态仍优先，不能重新引入已解除的限制。
- 扫描显示中文阶段和已处理数量。没有百分比时不伪造进度值。
- `recent-events.json` 中增加 `ScanTiming`：等待索引锁、索引构建、快照/分 P 身份捕获、首屏摘要、总耗时。计时不包含响应序列化、IPC 或界面绘制，不影响索引令牌或身份复核规则。

## 验证证据

最终 `npm run check` 通过：13 个文件、216 项前端测试，类型检查与生产构建通过。Host 全量 108 项契约测试通过，包含诊断计时字段及不泄露缓存根目录的断言。`git diff --check` 与两个新增脚本的语法检查通过。

中途一轮 Host 全量回归在回收站取消测试 `restore=true, failAfter=3` 中出现数组越界；原夹具没有核验准备出的回收站集合。针对性 4 项复跑通过，随后增加准备操作成功及集合完整性的断言，最终全量通过。此次没有捕获原始准备失败原因，不声称已修复一个确定的生产缺陷；新增断言用于后续准确报告准备阶段的问题。

真实 Electron 44 渲染器通过隔离的故障数据触发页面/根错误边界，在 1440×900 和 1080×720 两种窗口尺寸下检查：无横向溢出、恢复按钮在视口内且可获得焦点、页面失败后可切换至设置、根恢复遭拒时显示等待原因。四张截图与 JSON 保留于本地 `artifacts/renderer-recovery-1790495976405/`。此脚本使用夹具 API，不执行真实 Host、播放器或磁盘变更；Host/原生对话框恢复保护另由 IPC 测试覆盖。

复现界面验证（先完成前端生产构建，再通过 Electron 执行；运行环境不能设置 `ELECTRON_RUN_AS_NODE`）：

```powershell
$env:ELECTRON_RUN_AS_NODE = $null
Start-Process ./BiliBiliLocalCacheManager.Desktop/node_modules/electron/dist/electron.exe -ArgumentList './scripts/verify-renderer-recovery.cjs' -WindowStyle Hidden -Wait
```

## 扫描观察

环境：Windows x64，Intel Core i7-14650HX，约 64 GiB RAM，Node 24.20.0，.NET SDK 10.0.401。数据为 10,000 视频、20,000 分 P 的合成 metadata 和占位媒体；不是完整真实媒体库。

真实 Electron 端到端夹具准备约 38.4 秒，启动扫描在脚本 180 秒等待上限处未完成；失败截图仍处于身份验证阶段。该轮未取得搜索/翻页样本，不能记为验收通过。记录位于 `artifacts/desktop-requirements-1790495728166/results.json`。

随后在同一保留夹具上以独立 Host 顺序扫描三次：

| 样本 | 请求至响应 | 索引构建 | 快照及身份捕获 | 首屏摘要 |
| --- | ---: | ---: | ---: | ---: |
| 1 | 70,272.1 ms | 22,395.50 ms | 47,600.82 ms | 4.52 ms |
| 2 | 23,579.2 ms | 13,234.61 ms | 10,340.27 ms | 0.06 ms |
| 3 | 18,987.6 ms | 7,104.15 ms | 11,880.23 ms | 0.25 ms |

三次都返回 10,000 视频、20,000 分 P；锁等待均不足 0.1 ms。原始响应计时和诊断 ZIP 位于 `artifacts/scan-measurement-bcv1up/`。三次结果受 JIT、文件系统缓存、杀毒扫描及机器负载等因素影响，本次没有控制这些变量；不能据此宣称优化倍数、硬件无关 SLA，或认定 Electron 是瓶颈。快照和身份捕获是值得继续分析的成本，尚未细分到具体系统调用。

复现测量：

```powershell
node scripts/verify-desktop-requirements.cjs --count=10000 --keep-fixture
# 将路径替换为上一步创建的目录；端到端失败也会保留夹具及记录。
node scripts/measure-host-scan.cjs artifacts/desktop-requirements-<timestamp>/isolated-fixture
```

测量脚本只接受仓库 artifacts 下带合成数据记录的夹具，在新建目录内隔离设置和转码产物；不接受用户缓存根目录。其诊断阶段计时保存在 ZIP 的 `recent-events.json`。

## 后续完善：跨重启任务历史

主进程记录播放、导出、移入回收站、恢复和永久清理操作。提交 Host 前先写入开始记录，取得结果后先记录已校验的结果，再向调用方返回；历史写入失败通过历史页警告报告，不能把成功的磁盘操作变成失败结果。正常退出会等待历史写入完成。

记录保存在 Electron userData 下的 `task-history.json`，最近 30 天最多 100 项，失败明细每项任务最多 20 条；不保存索引令牌、转码许可或重试凭据。完整记录来自本机观察，不是后续操作授权。启动时仍为 running 的条目标记为 unknown，不伪造完成时间；仅保留真实发布的导出路径。损坏及未来版本文件保持原样，只为本次会话记内存历史并说明限制。过期记录在启动、读取或更新时清理并写回磁盘。

诊断页实时更新任务历史，展开查看路径、失败信息及数量；历史任务没有重试入口。历史保留本机路径及失败详情，与“下次启动恢复缓存目录”的设置独立，也不进入诊断 ZIP。

验证：完整前端检查 15 个文件、228 项测试通过；最终保留期/布局修正后，44 项受影响测试、类型检查与生产构建再次通过。覆盖文件重启、未知结果、部分取消、并发写入、容量与期限、损坏/未来文件、记录失败、晚到的旧读取结果，以及落盘和 Host 提交/响应的先后顺序。

`node scripts/verify-task-history.cjs` 经真实 Electron、Preload、主进程及 Host 对新建合成缓存执行：移动单个分 P、关闭并重启应用、读取保留的成功记录、恢复该分 P。两次运行通过，最终截图和 JSON 在 `artifacts/task-history-1790497694816/`。1440×900、1080×720 下历史内容无横向溢出。该验证不执行播放器、永久删除或用户缓存操作。

## 万视频端到端再次验证

再次运行原脚本和原 180 秒等待上限：`node scripts/verify-desktop-requirements.cjs --count=10000 --keep-fixture`。本轮创建新的等规模合成库，夹具准备 13.2 秒、冷启动至扫描完成 130.5 秒。五次搜索包含防抖的中位数 374.5 ms、最大 398.4 ms；五次翻页中位数 31.5 ms、最大 32.0 ms。跨页视频选择、跨视频分 P 选择与删除确认范围均通过，未捕获 renderer 错误。

原始 JSON 和六张截图位于 `artifacts/desktop-requirements-1790497437883/`。这证明此前超时没有在本轮稳定复现，不能证明某处性能缺陷已被修复；本轮没有改变索引/身份捕获算法。仍保留前述失败记录，不比较成优化倍数，也不将本机结果提升为跨机器 SLA。

## 严格 Windows 构建及安装包验证

复用本机已有的精确 .NET SDK 10.0.400，仅为本次进程配置 PATH 与 DOTNET_ROOT。完整执行 `build-release.ps1 -Version 0.4.1 -OutputRoot artifacts/verification-win-2026-09-27 -RunFfmpegIntegrationTests`，未跳过测试或打包自检。

- .NET 构建无警告/错误，Core 176、Playback 129、Host 108 项通过；真实固定 FFmpeg 集成测试 8 项通过。
- 前端 228 项、类型检查和生产构建通过；发布验收校验器 17 项通过。
- 自包含 Host 自检、Electron fuses 核验、打包后 Renderer/Preload/Host 自检通过。
- 对本轮最终 ZIP 和 NSIS 安装器执行 `test-windows-electron-packages.ps1`，ZIP 解压启动、NSIS 临时目录安装、启动及卸载全部通过。未跳过安装步骤。

验收产物保存在 `artifacts/verification-win-2026-09-27/`，是未提交工作区构建的本地验证包，未上传或发布，Authenticode 状态为 NotSigned。`verification.json` 保留环境、测试数与资产身份；构建和安装日志分别为 `artifacts/build-verification-2026-09-27.log`、`artifacts/package-verification-2026-09-27.log`。

| 产物 | SHA-256 |
| --- | --- |
| Windows NSIS exe | `f1b064bd2b26b84935d7df98bebe065957555200c5c1bea13765ebd9cc0c1e13` |
| Windows portable zip | `0ca1320e3da5c3b9ee832cd3fd424646f45498af0c0b1211fea1cadf29fde8ca` |

上述自动检查不代表 Windows 10/11 与 Linux 全部人工条目通过，未向 `docs/acceptance` 填入通过记录。

## 收尾核对与最终候选包

收尾核对发现历史记录把 Host 已明确确认的取消也归为 unknown。新增两个大小写取消代码用例，先观察 `unknown != cancelled` 失败，再修正为与现有界面相同的 `cancelled` / `CANCELLED` 判定。Host 丢失结果、畸形响应和其他无法确认的异常仍记录为 unknown。最终前端 230 项测试、类型检查及生产构建通过。

修复只影响 Electron 主进程的历史摘要。重新打包到 `artifacts/verification-win-2026-09-27-final/`；包内 Host 与前述精确 SDK 验证的 Host 哈希均为 `8a21638d19a80c4571d6f8f4960f0c6b2b3ff4330862188935c75da9dc4dfbfe`，未重建或改动 .NET 代码。旧候选保留用于追溯，新试用应使用 final 目录。

| 最终产物 | SHA-256 |
| --- | --- |
| Windows NSIS exe | `d04c5f63ef08b38d739b44a1b52802dec808d81ba0dc529839bf84e0f0ebcbc6` |
| Windows portable zip | `fb6f10a772c676ee69f39c205c86534833fb02bec714444e4217298fffe8bc47` |

所有 Electron fuses 再次核对通过；最终 ZIP 解压启动与 NSIS 安装、启动、卸载全部通过，日志为 `artifacts/package-final-verification-2026-09-27.log`。final 目录的 `verification.json` 保存修正版的测试数、组件及包身份。Authenticode 状态仍为 NotSigned，未正式发布。

### 本轮改进证据对照

| 改进方向 | 已取得证据 | 验证范围 |
| --- | --- | --- |
| 真实安装包验证 | 精确 SDK 构建、FFmpeg 集成、fuses、ZIP/NSIS 安装启动卸载 | 当前 Windows 11 自动验证；不等于完整人工矩阵 |
| 启动扫描体验 | 中文阶段和真实计数、分阶段诊断、万视频多 P 测量 | 成功和超时样本均保留；未宣称扫描算法加速 |
| 任务状态维护 | 独立快照/事件同步模块，旧结果与终态竞态回归 | 保留原有互斥、取消、结果未知语义 |
| 界面异常恢复 | 页面与根错误边界、主进程空闲重载检查 | 单元/IPC 测试及两种尺寸的真实 Electron 验证 |
| 跨重启任务历史 | 有界原子写入、未知结果、确认取消、过期清理及故障说明 | 自动测试及实际 Host 移动/重启/恢复闭环 |

完整发布验收仍须执行 [桌面兼容性清单](../desktop-compatibility.md) 的 1–9 项及 [A43–A44](2026-09-22-desktop-acceptance.md)；本轮不能为缺少环境或观察结果的项目填入通过。当前已取得 Windows 11 自动化证据，尚未获得 Windows 10、Ubuntu GNOME、Debian GNOME/KDE、Fedora GNOME/KDE 的真实桌面环境或相应人工记录。所需环境已向用户询问，未收到补充。无需反复运行已经通过的本机测试代替这些缺失证据。

## 未完成范围

真实安装包的 Windows/Linux 完整桌面矩阵、播放器效果验收、索引持久化或增量扫描仍未完成。此次未放宽任何磁盘身份检查，也没有将自动化证据填入人工验收记录。

## 2026-11-04 提交前复核与任务历史修正

审查发现回收站任务历史只保留失败条目标识，丢弃了 Host 已返回的具体原因和 AV/分 P 信息。现按移动目标或恢复条目身份匹配结构化明细，保留失败顺序和总数；缺少明细的旧响应仍逐项使用通用提示。

新增 7 项通过实际落盘、重新创建存储并读取的回归，覆盖移动/恢复、分 P/整视频、乱序及缺失明细、旧响应和缺失 AV 元数据，以及前 20 项失败明细和 1024 字符错误文本上限。先观察原问题导致两项测试失败，再验证修复通过。

本次 `npm run check` 通过：237 项前端测试、类型检查及生产构建。Host 全量 108 项契约测试及三个新增验证脚本的 `node --check` 通过；本次 Host 测试使用 .NET SDK 10.0.403，不替代上文精确 SDK 的发布验证。

本次未重新打包或执行真实 Electron 窗口验收。上文 `verification-win-2026-09-27-final` 候选包早于此次修正，不包含新的失败明细修复；其历史哈希和验收范围保持原记录。
