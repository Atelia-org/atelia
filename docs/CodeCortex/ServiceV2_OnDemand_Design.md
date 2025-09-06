# CodeCortex Service v2（On-Demand 拉取式架构）设计草案

> 目标：以“请求驱动、叠加覆盖（overlay）、内容寻址缓存”为核心的服务端新架构，借鉴 Roslyn LSP 的快照与按需计算理念；Watcher 退居为“预热/失效标记”，缓存作为可选加速层（容量可为 0）。

## 1. 背景与问题
- v1 的增量管线依赖 FileSystemWatcher → Classify → Impact → Processor 的“先推后用”，一旦任一环节不同步（尤其 Roslyn Solution 未同步磁盘文本）会导致查询使用旧快照。
- 需求侧强调：任何时刻“没有缓存也必须正确”；一致性优先于性能；缓存是可选的加速层。
- LLM 友好性：结果可持久化为 Markdown（outline 等），但不应成为正确性的唯一来源。

## 2. 设计原则
- 拉取式（Pull-based）：每个请求都能独立计算正确结果。
- 请求级快照：在请求开始时构造不可变的 Solution 快照，并叠加打开文档/磁盘最新文本（overlay）。
- 内容寻址缓存（可选）：输入相同→输出相同；miss 时计算并回写；容量可为 0。
- Watcher 退居：仅做“失效标记/热点预热”，不阻塞正确性。
- 可观测性：对命中/构建/预热/取消提供充分打点。

## 3. 高层架构
- WorkspaceManager（长驻 Roslyn Workspace/Solution）
- DocumentOverlayStore（打开文档/变更文本的内存覆盖，来自 LSP 式 didOpen/didChange，或直接读磁盘）
- RequestCoordinator（基于文件/类型粒度的请求级别串行与取消）
- RoslynFacade（封装符号解析、项目/文档检索、WithDocumentText 叠加）
- CacheStore（内容寻址缓存：IResultCache，支持 0 容量）
- OutlineService/ResolveService/SearchService（请求处理层，均按需构建，命中缓存则返回）
- Prefetcher（由 Watcher 触发的“失效标记 + 可选预热”）
- Telemetry/Debug（OnDemand/Cache/Prefetch/Request/Resolve/Outline）

## 4. 请求生命周期（以 GetOutline 为例）
1) 接收请求（TypeId 或 FQN）→ 解析参与文件集合（来自索引或按需解析）
2) 计算 CacheKey：Hash(文件指纹集合, ConfigVersion, AlgoVersion, RequestKind)
3) cache.TryGet(key) 命中→返回
4) 未命中/不新鲜：构造请求级 Solution 快照
   - 基于当前长驻 Solution
   - 对参与文件应用覆盖：
     - 打开文档：取 DocumentOverlayStore 的最新文本
     - 未打开：读取磁盘文本，做 SourceText 覆盖
5) Resolve 符号 → 计算结构/实现哈希 → 生成 outline 文本
6) cache.Set(key, result, metadata)（可选）→ 返回

注：请求级 Solution 为短生命不可变对象；不回写至全局 Solution，避免全局状态撕裂。

## 5. CacheKey 与新鲜度
- 文件指纹：首版采用 (LastWriteTimeUtc, Length)；后续可升级为内容哈希（并引入“路径→哈希”的 memo 表避免重复读盘）。
- 版本组成：
  - ConfigVersion（用户配置影响计算结果）
  - AlgoVersion（算法版本：哈希器/渲染变化）
  - RequestKind（Outline/Resolve/Search 等）
- 内容寻址：输入相同→输出相同，可复用/可重放。
- 容量为 0：cache.TryGet 永远 miss，正确性不受影响，仅性能下降。

## 6. Watcher 策略（退居）
- 监听 Add/Change/Delete/Rename，产出最小粒度的“失效标记”（按文件/类型映射）。
- 可选预热：
  - 只对近期热点键（LRU）做异步预构建
  - 背压：限制并发与 CPU 使用
- 不直接写出结果，不与请求路径争用锁；仅作为加速层。

## 7. 服务 API（v2）
- Outline
  - Request：{ typeId | fqn, options? }
  - Response：{ text, metadata: { cacheHit, inputs, algoVersion, durationMs } }
- ResolveSymbol
  - Request：{ typeId | fqn }
  - Response：{ location(s), kind, project, assembly, cacheHit, durationMs }
- Search（双路径）
  - 快路径：命中索引
  - 慢路径：基于请求快照按需扫描（确保正确）
- Status/Metrics：命中率、P50/P95、缓存大小、预热队列长度等

备注：与 v1 共存期可走不同端口或不同路由前缀（/v2/...）。

## 8. 并发、取消与一致性
- 请求级取消：若 Watcher 标记了更新，先前版本的长时请求可被取消（CancellationToken）。
- 粒度化串行：针对同一文档/类型的请求串行化，减少重复计算与抖动。
- 快照一致性：同一请求内所有文档基于同一 overlay 快照，不受请求外变更影响。

## 9. 目录规划（建议）
- src/CodeCortex.ServiceV2/
  - Hosting: V2Host.cs, V2Endpoints.cs
  - Workspace: WorkspaceManager.cs, RoslynFacade.cs
  - Overlay: DocumentOverlayStore.cs
  - Cache: IResultCache.cs, FileSystemResultCache.cs, NullCache.cs
  - Services: OutlineService.cs, ResolveService.cs, SearchService.cs
  - Prefetch: WatcherPrefetcher.cs, HotsetManager.cs
  - Telemetry: DebugCategories.cs, Metrics.cs

## 10. 与 Roslyn LSP 的对应关系
- LSP didOpen/didChange → DocumentOverlayStore（打开文档覆盖）
- LSP 请求构造快照 → 请求级 Solution overlay
- LSP pull diagnostics → 拉取式请求主干 + 后台增量仅加速
- LSP 持久化（SQLite/Checksum）→ 我们的内容寻址缓存（文件系统/SQLite 皆可）

## 11. 风险与缓解
- 冷请求延迟：长驻 Workspace + 文档级 overlay；热点预热；指标驱动优化
- 大型解决方案：仅一次加载；必要时项目子集加载（未来可选）
- 源生成器/跨项目：仍能在请求级快照上得到语义；慢但正确
- 编码与行尾：统一使用 UTF-8 + 规范化策略（在 SourceText 层面处理）

## 12. 度量与调试
- Debug 类别：OnDemand, Cache, Prefetch, Request, Resolve, Outline
- 关键日志：cacheHit, durationMs, key 前缀、覆盖文档数、预热效果
- 指标：命中率、P50/P95、请求失败率、取消率、预热命中率

## 13. 里程碑（建议）
1) M1：最小可用（GetOutline 按需化 + IResultCache（可 0 容量）+ 指标/日志）
2) M2：Resolve/Outline 全量切换；Watcher 改为失效标记 + 有限预热
3) M3：Search 引入慢路径后备；优化内容指纹与缓存持久化
4) M4：并发/取消完善；热点管理与背压
5) M5：性能与稳定性优化；单元测试/E2E 覆盖完善

## 14. 测试计划
- 单元测试
  - Overlay：WithDocumentText 覆盖优先级（打开文档 vs 磁盘）
  - Cache：同输入必定命中；不同输入必定 miss；容量 0 行为
  - Outline 构建：结构变更触发；实现/文档变更不触发（按当前策略）
- E2E 测试
  - 无 Watcher 也能正确响应（正确性基线）
  - 有 Watcher 预热时的命中率提升与 P95 改善
  - 新增/删除/重命名/partial class 场景

## 15. 迁移与并行运行
- v1 与 v2 并行一段时间：
  - CLI 新增 `--server=v2` 开关或使用默认端口差异
  - 对比 status/metrics，验证 v2 的命中率与延迟
- 稳定后弃用 v1 增量主路径，仅保留其日志作为对照参考（短期内）

---

## 附：GetOutline 请求级按需构建伪代码

```csharp
public async Task<OutlineResult> GetOutlineAsync(TypeRef type, Options opt, CancellationToken ct) {
    var files = await _resolver.GetFilesForTypeAsync(type, ct); // 索引或按需解析
    var key = CacheKey.Compute(files, opt.ConfigVersion, AlgoVersion.OutlineV2, kind: "Outline");
    if (_cache.TryGet(key, out var cached)) return cached.With(hit: true);

    var baseSol = await _workspace.GetBaseSolutionAsync(ct);
    var sol = await _overlay.ApplyAsync(baseSol, files, ct); // 打开文档优先，其次读磁盘

    var symbol = await _roslyn.ResolveAsync(sol, type, ct);
    var hashes = _hasher.Compute(symbol, files, opt.HashConfig);
    var text = _outline.Render(symbol, hashes, opt.RenderOptions);

    var result = new OutlineResult(text, inputs: key.Inputs, cacheHit: false);
    _cache.Set(key, result);
    return result;
}
```


