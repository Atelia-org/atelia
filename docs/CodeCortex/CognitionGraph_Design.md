# CodeCortex Cognition Graph 设计草案（通用缓存 + 依赖传播 + Diff 加速）

> 目标：为 Service v2 提供通用的“认知节点图（Cognition Graph）”，统一管理内容寻址缓存、依赖关系与失效传播，并为慢速的 LLM 语义分析提供“旧版输出 + 依赖 Diff”的增量加速能力。

## 1. 设计取向与关键决策
- 放弃 v1 增量主路径；v2 采用“按需构建 + 内容寻址缓存（CAS）+ 依赖图”的拉取式模型。
- 缓存键直接使用内容哈希（内容 + 配置/算法版本）。
- 统一的 Cognition Graph 管理所有“可缓存的中间产物/最终产物”，如：完整类型、outline 信息/文本、语义认知节点等。
- 依赖图支持“链式失效传播”，并记录“旧版输入/输出”，以便 LLM 层执行差异驱动的增量分析（重用未变部分）。

## 2. 节点种类（Node Kinds）
- SourceFile：源文件整体内容
- FilePart：文件片段（span/selectors，用于 partial/成员级别映射）
- CompleteType：逻辑整合后的完整类型（合并 partial）
- OutlineInfo：类型签名、Public 成员、文档等结构化信息
- OutlineMarkdown：面向 LLM 展示的 Outline 文本
- SemanticSummary：LLM 生成的类型/成员层级语义摘要（可逐层细化）
- Embedding/Index：向量索引或符号/反向索引数据
- AnyDerived：未来扩展（如 Metrics、CrossRef、CallGraph 等）

备注：节点粒度可按需细化，例如将 CompleteType/OutlineInfo/OutlineMarkdown 进一步拆分为“按成员”的子节点，以提升局部变更的可复用率。

## 3. 节点记录（数据模型）
每个节点统一记录：
- NodeId：稳定标识（建议 FQN + Kind + 可选子键，如成员名）
- Kind：节点类型
- Inputs：依赖列表（NodeId + selector/role）
- InputHashes：输入内容指纹（内容哈希或组合哈希）
- Params：算法/配置版本、渲染选项等
- Producer：生成器标识与版本（算法版本）
- Output：产出（文件路径/内嵌文本/二进制 blob）+ OutputHash
- Diagnostics：最近一次生成的日志/警告
- Timestamps：Created/LastUpdated
- History（可选）：保存若干旧版 Output/Hashes 以支持 Diff

## 4. 内容寻址缓存（CAS）
- CacheKey = Hash(Inputs.ContentHashes, Params, Producer.Version)
- 存取：TryGet(CacheKey) → miss 则 Compute → Put(CacheKey, Output)
- 容量可为 0（NullCache），正确性不受影响，性能下降可接受。
- 存储层建议：
  - BlobStore：.codecortex/cache/blobs/{first2}/{key}
  - MetaStore：SQLite 记录节点元数据与依赖关系（便于查询与 GC）

## 5. 依赖与失效传播
- 当 SourceFile/上游节点内容哈希变更：
  - 标记所有下游依赖为 Stale（不立即重算）
  - 请求到达时按需重算；或由后台 Prefetcher 批量重算热点
- 传播策略：
  - 强一致请求：本次请求覆盖到的链路必须 fresh（遇到 Stale 即触发重算）
  - 背景刷新：不阻塞请求的异步重建（P95 降低）

## 6. Diff 与增量重用（LLM 友好）
- 存储旧版 Inputs/Output 以计算 Diff：
  - 文件层：文本 Diff（Git/LibGit2 或文本 Diff 算法）
  - 语法层（可选）：Roslyn Syntax/Symbol 映射辅助“成员重用”
- Patch Prompt：为 LLM 生成“局部变更提示”，仅对变更的成员/片段发起重分析，未变部分直接复用旧 Output。
- 对齐机制：
  - 将 CompleteType/Outline 按成员切片存储，NodeId 稳定映射到成员（FQN#Member）
  - 当变更仅影响少量成员，SemanticSummary 仅重算这些成员节点

## 7. Overlay 与事务会话
- 引入 OverlaySession（会话/分支）：
  - 文档覆盖（未落盘）→ 生成 Overlay 版本的节点快照与缓存（命名空间带 SessionId）
  - 支持“设想方案 / 分支思考 / 事务编辑与分析”
  - 可选择将 OverlaySession 合并（落盘）或丢弃
- CLI 不使用 Overlay 也能工作；Agent 环境（Nexus）可直接写入 OverlayStore

## 8. 与 Service v2 的接口与运行时关系
- Service v2 的各服务（Outline/Resolve/Search）不直接操作 v1 索引；
  - 通过 Graph API 请求节点：
    - Graph.GetOrBuild(NodeId, Params) → 返回 Output（命中则快，miss 则构建）
  - 由 Graph 内部执行：依赖解析 → CacheKey 计算 → TryGet → 计算 → Put → 记录依赖与历史
- Watcher：
  - 仅做节点失效标记 + 可选预热（对热点节点调用 GetOrBuild）

## 9. 最小实现范围（M1-M3）
- M1（最小可用）：
  - 节点：SourceFile, CompleteType, OutlineInfo, OutlineMarkdown
  - Graph 元数据：SQLite（Nodes/Edges/Meta）+ FileSystem BlobStore
  - On-Demand：GetOutline(type) 贯通 Graph → markdown
  - CacheKey：内容哈希（文件内容 SHA-256）+ 算法/配置版本
  - Prefetcher：可选禁用（默认 Off）
- M2（增量与扩展）：
  - 加入 FilePart/成员粒度节点，CompleteType 与 Outline* 支持成员切片
  - Watcher：文件变更 → 精确失效（可定位到受影响的成员）
  - Search：快路径（索引）+ 慢路径（按需）
- M3（LLM 增量）：
  - SemanticSummary 节点（成员级）
  - 历史结果与 Diff 存储 + Patch Prompt 生成
  - 背景重建与背压（队列与并发限制）

## 10. 数据库模型（示意）
- Tables：
  - Nodes(NodeId PK, Kind, ParamsJson, Producer, OutputRef, OutputHash, CreatedAt, UpdatedAt)
  - Inputs(NodeId, InputNodeId, Role, Selector, InputHash, PRIMARY KEY(NodeId, InputNodeId, Role))
  - History(NodeId, Version, OutputRef, OutputHash, InputsHash, CreatedAt)
  - Indexes：按 Kind、UpdatedAt、Producer 便于检索与 GC

## 11. API 草案（内部 Graph API）
- GetOrBuild(NodeId id, BuildParams p, Ct)
- TryGet(NodeId id, BuildParams p) → Output/Meta or null
- InvalidateByInput(NodeId inputId)
- UpsertNode(NodeRecord)
- RegisterOutputBlob/ResolveBlob(ref)

## 12. 目录规划（建议）
- src/CodeCortex.ServiceV2/
  - Graph/Abstractions：INodeStore, IEdgeStore, IBlobStore, IGraphEngine
  - Graph/Storage：SqliteNodeStore, FileBlobStore
  - Graph/Compute：Builders（CompleteTypeBuilder, OutlineBuilder, SemanticBuilder ...）
  - Overlay：DocumentOverlayStore, SessionManager
  - Services：OutlineService, ResolveService, SearchService
  - Prefetch：WatcherAdapter, HotsetManager
  - Telemetry：Metrics, DebugCategories

## 13. 与 Git 的结合（可选但推荐）
- 用 Git/LibGit2 记录“快照提交”，天然获得 diff/历史；
- Graph 的 History 可引用 Git commitId，利于跨版本审核与回溯；
- 不强依赖：即使无 Git，也能基于哈希与文本 Diff 工作。

## 14. 关键权衡
- 复杂度：Graph 引入后一次性抽象较多，但统一了缓存/依赖/历史/增量重用路径，长期可维护性更好。
- 性能：按需为主，冷请求延迟↑；以常驻 Workspace + 局部 overlay + 热点预热缓解。
- 一致性：以“内容哈希 + 算法/配置版本”为单一真相；Overlay 会话建立隔离命名空间。

## 15. 小示例（Node 记录 & CacheKey）
```csharp
public sealed record NodeRecord(
    string NodeId,
    string Kind,
    IReadOnlyList<NodeInput> Inputs,
    string ParamsJson,
    string Producer,
    string? OutputRef,
    string? OutputHash,
    DateTime CreatedAt,
    DateTime UpdatedAt);
```

```csharp
static string ComputeCacheKey(IEnumerable<string> inputHashes, string algoVer, string paramsHash) {
    var concat = string.Join("|", inputHashes) + "|" + algoVer + "|" + paramsHash;
    return Sha256Hex(concat);
}
```

---

以上为首版草案，聚焦“统一缓存 + 依赖传播 + LLM 增量”。待你确认后，我将把 ServiceV2_OnDemand_Design.md 与本稿对齐合并，并产出 M1 的工程化任务清单与验收标准。

