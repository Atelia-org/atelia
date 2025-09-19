# Appendix: 增量处理流程伪代码 (Phase 1)

## 1. Watcher 策略
- 监控后缀：`*.cs`
- Debounce 聚合窗口：400ms (可配置, env: CODECORTEX_DEBOUNCE_MS)
- 事件归类：Created / Changed / Deleted / Renamed
- Renamed 拆分：OldPath -> Deleted, NewPath -> Created（同一批内归并）

## 2. 数据结构
```
pendingFiles: HashSet<string>
lastEventTime: DateTime
processing: bool (单线程防重入)
```

## 3. 主循环伪代码
```csharp
OnFsEvent(path, kind):
  if !IsCs(path) return;
  lock(pendingFiles) { pendingFiles.Add(Normalize(path)); lastEventTime = UtcNow; }
  ScheduleIfNotQueued();

TimerTick(): // 100ms 周期
  if processing return;
  if (UtcNow - lastEventTime) < DebounceWindow return;
  HashSet<string> batch;
  lock(pendingFiles) { batch = pendingFiles.Clone(); pendingFiles.Clear(); }
  if batch.Count == 0 return;
  processing = true;
  try { ProcessBatch(batch); }
  finally { processing = false; }
```

## 4. Batch 处理步骤
```csharp
ProcessBatch(files):
  changedProjects = ResolveProjects(files)
  comp = IncrementalRecreateCompilation(changedProjects) // 或全量引用缓存
  affectedTypes = CollectTypesFromFiles(comp, files)
  foreach type in affectedTypes.GroupBy(TypeId):
     partialFiles = AllFilesForType(type)
     hashesNew = ComputeHashes(type.Symbol, partialFiles, config)
     hashesOld = index.Lookup(type.Id)?
     diff = Compare(hashesOld, hashesNew)
     if diff.AnyChanged:
        outlineChanged = ShouldRewriteOutline(diff)
        if outlineChanged:
           outline = BuildOutline(type.Symbol, hashesNew, opts)
           AtomicWriteOutline(type.Id, outline)
        UpdateIndexTypeRecord(type.Id, hashesNew, outlineChanged)
  CleanupRemovedTypes(comp)
  AtomicWriteIndex()
  LogIncrementalSummary(files.Count, affectedTypes.Count, elapsed)
```

## 5. 变化判定
```
Compare(old, new):
  structureChanged = old.Structure != new.Structure
  publicImplChanged = old.PublicImpl != new.PublicImpl
  internalImplChanged = old.InternalImpl != new.InternalImpl
  xmlChanged = old.XmlDoc != new.XmlDoc
  cosmeticChanged = old.Cosmetic != new.Cosmetic
```
Outline 重写条件：任一上述布尔为 true（可配置忽略 cosmetic）。

## 6. Removed Types 清理
```
CleanupRemovedTypes(comp):
  currentSet = HashSet( EnumerateAllTypeIds(comp) )
  foreach t in index.Types:
     if t.Id not in currentSet:
        index.Remove(t.Id)
        DeleteOrphanOutline(t.Id)
        Log("Removed", t.Id, t.Fqn)
```

## 7. Index 写入与恢复
```
AtomicWriteIndex():
  tmp = index.json.tmp
  File.WriteAllText(tmp, Serialize(index))
  File.Replace(tmp, index.json, index.json.bak?) // Win; *nix: File.Move(tmp, index.json, overwrite)
```
启动恢复：
1. 若 `index.json` 不存在 → 全量构建。
2. 若损坏（JSON 反序列化失败）→ rename 为 `index.corrupt.<ts>.json` 全量重建。
3. 若 configSnapshot 与当前配置不兼容（影响结构）→ 备份现有，删除并重建。

## 8. 锁与并发
- Watcher 线程只向 pendingFiles 写入。
- 处理线程串行；不并发重入。
- Outline / index 写入使用 AtomicIO（参见其附录）。

## 9. 指标采集点
- initialIndexDurationMs：首轮全量构建耗时。
- lastIncrementalMs：`ProcessBatch` 计时。
- watcherQueueDepth：`pendingFiles.Count`（tick 时采样）。

## 10. 错误处理
- 单类型 Outline 失败：记录错误；不覆盖旧文件；index 保留旧 hash（或移除？Phase1：保留旧）。
- ProcessBatch 异常：写入 `logs/incremental.log` + errors.log；下次事件重新尝试。
- Storm Fallback：若 batch 文件数 > 500 → 触发 FullRescan(); 记录原因。

## 11. FullRescan 简化
```
FullRescan():
  Scan all projects -> New compilation
  Enumerate all types -> Recompute all hashes
  Rebuild index.types[]
  Rewrite all outlines
  AtomicWriteIndex()
```

---
(End of Appendix_Incremental_Flow_Pseudocode_P1)
