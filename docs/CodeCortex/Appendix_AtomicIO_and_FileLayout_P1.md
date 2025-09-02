# Appendix: Atomic IO 与文件布局 (Phase 1)

## 1. 运行期目录结构
```
.codecortex/
  index.json
  types/<TypeId>.outline.md
  prompt/prompt_window.md
  logs/
    index_build.log
    incremental.log
    errors.log
    resolve_trace.log (可选)
    hash_conflicts.log (自动)
  tmp/
```
(若用户不希望放在隐藏目录，可配置根目录 `codecortex/`，本文档使用 `.codecortex/` 示例。)

## 2. 原子写协议
函数：`AtomicIO.Write(path, content)`
1. 生成临时文件：`path + ".tmp"`。
2. 使用 UTF8 (无 BOM) 写入；Flush；`FileStream.Flush(true)`。
3. Windows:
   - 若目标已存在：`File.Replace(tmp, target, backup?null)`；否则 `File.Move(tmp, target)`。
   - 失败重试：100ms / 500ms 最多 2 次。
4. 非 Windows：`File.Move(tmp, target, overwrite:true)`（先删除旧）。
5. 失败：记录 `errors.log`，保留 `.tmp` 供诊断。

## 3. 并发策略
- 所有写操作在单一“IO 执行队列”串行执行（`BlockingCollection<Action>` 或简单锁）。
- 读操作直接文件系统读取；不会阻塞写队列。
- 防止 outline 与 index 同时写：index 写入前确保没有待写 outline（队列顺序保证）。

## 4. 损坏恢复
- 启动时读取 `index.json` 失败（反序列化异常 / EOF）→ rename 为 `index.corrupt.<timestamp>.json`；执行 FullRescan。
- Outline 单文件损坏（读取异常）→ 在首次请求该类型 Outline 时重新生成覆盖。

## 5. 清理策略
- 孤儿 outline：每次 FullRescan 后扫描 `types/`，若 `<TypeId>` 不在 index → 删除；记录 `index_build.log`。
- 临时文件：启动时清理所有 `*.tmp` 超过 1 天的文件。

## 6. 日志格式
统一前缀：`<UTC ISO8601> <LEVEL> <Event> <Details...>`
示例：`2025-09-02T12:00:01Z INFO IndexBuild Types=1275 DurationMs=3120`
级别集合：INFO / WARN / ERROR。

## 7. 日志轮转（Phase1 简化）
- 单日志文件超过 5MB →  rename `*.1` （只保留 1 份历史）。
- 轮转操作与写入在同一 IO 队列执行，保证顺序。

## 8. 配置
```
{ "io": { "maxLogSizeMB": 5, "enableBackupIndex": false } }
```
Phase1 可硬编码；未来移入 config。

---
(End of Appendix_AtomicIO_and_FileLayout_P1)
