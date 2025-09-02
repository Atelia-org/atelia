# Appendix: Status 指标定义 (Phase 1)

## 1. Status RPC 返回示例
```
{
  "projects": 3,
  "typesIndexed": 1275,
  "initialIndexDurationMs": 3120,
  "lastIncrementalMs": 85,
  "watcherQueueDepth": 0,
  "outlineCacheHitRatio": 0.93,
  "memoryMB": 412
}
```

## 2. 字段定义
| 字段 | 含义 | 计算方式 | 采样时机 |
|------|------|----------|----------|
| projects | 当前已加载项目数 | 加载后缓存计数 | 每次 Status 调用即时读取 |
| typesIndexed | index.types[] 长度 | 直接读取 | 即时 |
| initialIndexDurationMs | 首次全量索引耗时 | `fullIndexEnd - fullIndexStart` | 首次全量完成后固定 |
| lastIncrementalMs | 最近一次增量处理耗时 | `ProcessBatch` 结束记录 | 增量结束更新 |
| watcherQueueDepth | 等待聚合的文件数 | pendingFiles.Count | Status 调用时原子读取 |
| outlineCacheHitRatio | Outline 请求命中缓存比例 | hit/(hit+miss); 运行期累计 | Status 调用时计算 |
| memoryMB | 进程近似托管内存使用 | `GC.GetTotalMemory(false)/1024/1024` | 即时 |

## 3. 统计窗口与重置
- outlineCacheHitRatio Phase1 采用运行期累计；若需要重置可在 CLI 加命令后续实现。
- 不做滑动窗口，简化。

## 4. 采集实现要点
- 所有计数器线程安全：使用 `Interlocked`。
- 增量耗时：`Stopwatch` 包裹 `ProcessBatch`。
- memoryMB：不强制触发 GC，以免扰动性能。

## 5. 性能告警（仅日志提示）
- initialIndexDurationMs > 5000 (Release) 或 >10000 (Debug) → logs/index_build.log 追加 `WARN SlowInitialIndex <ms>`。
- lastIncrementalMs > 300 → logs/incremental.log 追加 `WARN SlowBatch <files> <ms>`。
- watcherQueueDepth > 200 → incremental.log `WARN QueueDepth <n>`。

## 6. 未来扩展占位
预留字段（Phase2+）：semanticQueueLength, p95ResolveMs, uptimeSec。

---
(End of Appendix_Status_Metrics_Definition_P1)
