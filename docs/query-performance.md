# 查询复用验证

2026-09-12，在 Windows x64、.NET 10.0.12 / SDK 10.0.401 上运行 IndexSnapshotTests 的合成索引夹具，搜索词为 video，连续读取 50 页，每页 100 条。

| 条目数 | 首次搜索 | 原逻辑 50 页 | 缓存结果 50 页 | 原逻辑分配字节 | 缓存结果分配字节 |
| --- | ---: | ---: | ---: | ---: | ---: |
| 10,000 | 0.90 ms | 94.70 ms | 0.15 ms | 78,108,000 | 61,600 |
| 50,000 | 72.30 ms | 546.35 ms | 0.95 ms | 451,267,440 | 61,600 |

这是单次进程中的内存查询微基准，首次 5 万条搜索包含冷启动/JIT 开销。它比较旧的逐页搜索排序与新结果引用复用，不包含磁盘扫描、IPC、摘要序列化或 UI 绘制；分配字节不是常驻内存或峰值内存。预取消查询分别用时 0.03 ms 和 0.47 ms。

确定性测试另行验证相同查询返回同一结果集合、最多保留八个查询、LRU 淘汰、取消与索引失效。CI 不将本机耗时设为性能通过阈值。

```powershell
dotnet test BiliBiliLocalCacheManager.Desktop.Host.Tests -c Release --filter "FullyQualifiedName~IndexSnapshotTests" --logger "console;verbosity=detailed"
```
