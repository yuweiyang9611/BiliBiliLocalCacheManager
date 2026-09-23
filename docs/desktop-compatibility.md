# 桌面兼容性验收清单

本清单是 0.4.x 正式发布的人工验收要求。CI 的 Xvfb 检查验证包安装和 Chromium X11/Ozone 启动路径，但不能替代真实 GNOME、KDE 与 XWayland 会话。所有标签先生成公开预览 Release；真实桌面验收记录提交后，由 promote-release 自动核验必测矩阵和包 SHA-256，再将同一批安装包转正。记录格式及流程见 [验收记录说明](acceptance/README.md)。

## 必测矩阵

| 系统 | 桌面与会话 | 最低要求 |
| --- | --- | --- |
| Windows 10 x64 | 原生桌面 | 完整检查 |
| Windows 11 x64 | 原生桌面 | 完整检查 |
| Ubuntu 24.04+ x64 | GNOME、Wayland 会话中的 XWayland | 完整检查 |
| Debian 13 x64 | GNOME 与 KDE、Wayland 会话中的 XWayland | 两种桌面各完成完整检查 |
| Fedora 43 x64 | GNOME 或 KDE、Wayland 会话中的 XWayland | 至少一种桌面完成完整检查 |

原生 Wayland 后端、Alpine、NixOS、Flatpak、Linux ARM64 与 macOS 不在本清单范围内。

## 环境确认

Linux 测试必须使用物理机或带完整桌面的虚拟机，不使用容器或 Xvfb 代替。安装 deb/rpm 后执行：

    printf 'desktop=%s session=%s display=%s wayland=%s\n' \
      "$XDG_CURRENT_DESKTOP" "$XDG_SESSION_TYPE" "$DISPLAY" "$WAYLAND_DISPLAY"
    pgrep -a Xwayland
    bilibili-local-cache-manager --smoke-test
    ffmpeg -version
    ffprobe -version

预期 XDG_SESSION_TYPE=wayland、DISPLAY 非空、存在 Xwayland 进程，且应用自检退出码为 0。应用自身固定选择 Chromium 的 X11/Ozone 后端。

Windows 分别使用最终 NSIS 安装器和免安装 zip；Linux 使用最终 deb/rpm，不能用源码开发服务器代替。

## 完整检查

1. 安装或解压后从桌面菜单与命令行各启动一次，确认单实例、窗口显示、中文文本、缩放和关闭行为正常。
2. 选择包含新版 DASH、旧版 Lua、未完成和损坏条目的样例缓存；确认扫描、搜索、空搜索恢复列表、排序和错误摘要。
3. 使用系统播放器播放，再分别验证可用环境中的 mpv/VLC；确认失败不换播放器、列表/P 序号顺序、单页/多页队列和取消准备，以及跨页、跨视频分 P 选择。
4. 导出单 P 和多选 MP4，检查统一批次/视频/P 层级、重名另存、转码确认、音视频与可信产物复用；拒绝确认或失败时整批不发布，准备期间搜索不改变任务目标。
5. 整视频和所选分 P 移入应用回收站、Ctrl+Z 精确撤销、列表和恢复；分 P 恢复拒绝目标冲突但不因父目录存在而失败。Windows 清空需输入确认文字；Linux 不开放不可逆删除，Host/IPC 也不能绕过限制。独立 CLI 不再作为新版本验收入口。
6. 查看转码缓存统计，执行策略清理与双重确认清空，确认活动产物不会被误删。
7. 分别验证“记住缓存目录”和“启动时自动扫描”：新配置有已记住的有效目录时默认扫描，关闭后不扫描；已有文件保留历史值和迁移选择，非空搜索词不得绕过关闭选项。关闭记忆后，本次会话仍可使用当前目录，下次启动不恢复该路径。播放器、分 P 名称搜索开关和清理策略也应正确持久化。
8. 导出诊断 ZIP，确认可打开且不包含已知缓存根路径、用户主目录、URL 或常见命名凭据明文。
9. 断开网络后重复启动、扫描和已存在产物播放，确认桌面壳不依赖系统浏览器或在线 Web 内容。

## 记录

每个矩阵项记录系统版本、桌面版本、会话类型、包文件 SHA-256、测试日期、测试人、通过/失败和缺陷链接。任一必测项失败，或 Fedora 43 未完成至少一种真实 GNOME/KDE XWayland 会话检查时，不应标记该版本为满足 0.4.x 桌面发布目标。
