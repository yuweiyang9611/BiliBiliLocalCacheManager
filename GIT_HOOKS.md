# 启用仓库 Git Hooks

Git 出于安全考虑，不会在克隆仓库后自动启用仓库中受版本控制的 hooks。因此，每个新克隆都需要执行一次初始化脚本。

## 快速启用

在项目根目录运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\setup-git-hooks.ps1
```

如果使用 PowerShell 7，也可以运行：

```powershell
pwsh -NoProfile -File ./setup-git-hooks.ps1
```

脚本会：

1. 确认脚本位于当前仓库根目录；
2. 确认 `.githooks/pre-push` 存在；
3. 为当前克隆设置 `core.hooksPath=.githooks`；
4. 检查当前生效的 Git 邮箱是否为 GitHub noreply 地址。

该设置只写入当前仓库的 `.git/config`，不会修改其他仓库。

## 配置隐私邮箱

在 GitHub 的 **Settings → Emails** 中取得账户对应的 noreply 邮箱，并建议同时启用：

- **Keep my email addresses private**
- **Block command line pushes that expose my email**

然后仅为当前仓库设置该地址：

```powershell
git config --local user.email "你的 GitHub noreply 邮箱"
git config --local user.useConfigOnly true
```

验证配置：

```powershell
git config --local --get core.hooksPath
git config --get user.email
```

第一条命令应输出 `.githooks`，第二条命令应输出以 `@users.noreply.github.com` 结尾的地址。

## 保护范围

启用后，`.githooks/pre-push` 会检查即将推送的所有提交的作者邮箱和提交者邮箱。只允许 GitHub noreply 地址；发现其他邮箱时会在上传前拒绝推送，并且不会在错误信息中打印邮箱内容。

GitHub 上的分支规则和 CI 检查仍会作为远端保护。不要使用 `--no-verify` 绕过本地 hook。
