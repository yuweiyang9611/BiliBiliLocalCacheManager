# 桌面发布验收记录

正式版本标签也会先生成公开预览 Release。完成真实桌面测试后，将 JSON 记录提交到默认分支的本目录，再从默认分支运行 promote-release，填写版本标签和记录路径。流程复核现有包，不重新构建；测试失败时保持预览状态，修复后使用新版本重新发布和验收。

记录格式如下。以下仅为结构示例，不能直接用于验收；必须填入实际结果。

```json
{
  "schemaVersion": 1,
  "tag": "v0.4.0",
  "commit": "<标签指向的完整 40 位提交 SHA>",
  "assets": [
    { "name": "<Release 中的包文件名>", "sha256": "<实际 64 位小写 SHA-256>" }
  ],
  "environments": [
    {
      "id": "windows-10",
      "systemVersion": "<真实系统版本及构建号>",
      "desktopVersion": "<真实桌面版本>",
      "sessionType": "native",
      "testedAt": "<含时区的 ISO 8601 时间>",
      "tester": "<测试人>",
      "results": [
        {
          "asset": "<测试过的安装包文件名>",
          "checks": [
            { "id": 1, "status": "passed", "defectUrl": null }
          ]
        }
      ]
    }
  ]
}
```

- assets 必须列出全部六个 CLI/桌面包，文件名和哈希均与 Release 及 SHA256SUMS.txt 一致。
- 必须包含 windows-10、windows-11、ubuntu-gnome、debian-gnome、debian-kde，以及 fedora-gnome 或 fedora-kde 至少一个。
- Windows 两项各验证 exe 和桌面 zip；Ubuntu/Debian 验证 deb；Fedora 验证 rpm。Linux 的 sessionType 必须为 xwayland，Windows 为 native。
- 每个包的 checks 必须完整列出 [桌面兼容性清单](../desktop-compatibility.md) 的 1–9 项，并全部通过。存在关联缺陷时填写 HTTPS 链接，否则明确写 null。
- 校验器检查记录完整性与包身份，不能替代真实机器或完整虚拟机上的人工测试。记录中不得填写推测的通过结果。

本地验证：

```powershell
node scripts/validate-desktop-acceptance.mjs docs/acceptance/v0.4.0.json artifacts/candidate v0.4.0 <commit>
node --test scripts/validate-desktop-acceptance.test.mjs
```
