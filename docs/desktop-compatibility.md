# 桌面兼容性验收清单

本清单是 0.4.x 正式发布的人工验收要求。CI 的 Xvfb 检查验证包安装和 Chromium X11/Ozone 启动路径，但不能替代真实 GNOME、KDE 与 XWayland 会话。当前 GitHub workflow 不自动核验本清单的测试记录，发布维护者必须在触发发布前完成并保存记录。

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
3. 使用系统播放器播放，再分别验证可用环境中的 mpv/VLC；确认单页、多页队列和取消准备。
4. 导出单页和多选 MP4，检查文件名、覆盖确认、音视频与缓存产物复用。
5. 移入应用回收站、Ctrl+Z 撤销、列表和恢复；Linux 必须确认界面没有永久清理入口，CLI 非 dry-run 永久删除被拒绝。
6. 查看转码缓存统计，执行策略清理与双重确认清空，确认活动产物不会被误删。
7. 分别验证“记住缓存目录”和“启动时自动扫描”：默认及仅记住目录时不应扫描，预先保存一个非空搜索词也不得触发隐式扫描；只有显式启用启动扫描时才应在重启后扫描。关闭记忆后，本次会话仍可使用当前目录，下次启动不应恢复该路径。播放器和清理策略也应正确持久化。
8. 导出诊断 ZIP，确认可打开且不包含已知缓存根路径、用户主目录、URL 或常见命名凭据明文。
9. 断开网络后重复启动、扫描和已存在产物播放，确认桌面壳不依赖系统浏览器或在线 Web 内容。

## 记录

每个矩阵项记录系统版本、桌面版本、会话类型、包文件 SHA-256、测试日期、测试人、通过/失败和缺陷链接。任一必测项失败，或 Fedora 43 未完成至少一种真实 GNOME/KDE XWayland 会话检查时，不应标记该版本为满足 0.4.x 桌面发布目标。
