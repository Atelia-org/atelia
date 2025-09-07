# CodeCortex V2 设计纲要（Roslyn 驱动的 LLM 友好代码理解层）

> 目标：先把“基于 Roslyn 的代码理解与操作层”打磨得对 AI Coder 极度友好，提供快速鸟瞰、符号检索、结构化 Outline 与 AST 级编辑，避免以“文本文件”为中心的低效交互。通用认知图与持久化缓存稍后再引入。

---

## 1. 顶层意图（Intent）
- 让 AI Coder 在任何既有 workspace 上“秒级掌握结构”，并能“安全、原子”地进行重命名、成员迁移/重排等典型重构。
- 将“真实世界”定义为 Roslyn Workspace（包含编辑器 overlay），读写均基于 AST 而非裸文本。
- 输出为 LLM 友好格式：以“简洁 JSON + 可选 Markdown 渲染”为主，严格控制 token 预算。

非目标（当前阶段）：
- 不做重型持久化缓存或复杂的依赖追踪；outline 即时计算为主，局部微缓存为辅。
- 不实现通用 Cognition Graph；只为后续纳入该层做好接口与事件预留。

---

## 2. 关键场景与能力
1) 鸟瞰（Birdview）与快速定位
- 内存符号索引：按名称前缀/包含检索，返回轻量信息（Kind/Name/Namespace/SymbolId/Score）。
- 命名空间/程序集级聚合 Outline：汇总类型与摘要（可分页/节流），少量关键成员可由 Attribute 明确透出。

2) 结构化 Outline（渲染为 Markdown）
- 成员级：签名 + XML Doc 摘要（params/returns 简化）+ 定位信息。
- 类型级：类型摘要 + 成员摘要列表；支持 partial 合并视角。
- Type 源码视图：合并后的“逻辑完整 Type 源码”（按声明文件稳定排序，插入分隔注释）。

3) AST 级安全重构（后续接口）
- 重命名、成员重排/迁移、插入/删除成员等，通过 Workspace/Document/Editor API 原子提交。
- 与 outline/search 一致使用 SymbolId 进行定位，避免 fragile 文本定位。

4) Overlay 与即时响应
- 支持编辑器 overlay（Document.WithText）与 FSW，WorkspaceChanged 触发增量更新与失效。

---

## 3. 架构概览
- Workspace/Overlay 层：
  - MSBuildWorkspace 加载 Solution；自定义 Workspace 接受 overlay。
  - 事件：Workspace.WorkspaceChanged + FileSystemWatcher（兜底）。

- SymbolIndex（内存）：
  - 构建可搜索的符号目录；Key 使用 Roslyn SymbolKey（可序列化/跨版本解析）。
  - 提供 prefix/contains 搜索，返回基础元信息与 SymbolId。

- Providers（拉取式，按需计算）：
  - MemberOutlineProvider：成员签名 + XML Doc 摘要 + DeclaredIn。
  - TypeOutlineProvider：类型摘要 + 成员摘要列表。
  - Namespace/AssemblyOutlineProvider：下辖类型 summary 的聚合；按需分页/节流。
  - TypeSourceProvider：合并 partial 的逻辑完整 Type 源码视图。

- Cache 层（轻缓存，默认小 LRU）：
  - Singleflight 合并并发计算；可选 MemoryCache（100–500 项，TTL 30–120s）。
  - CacheKey = SymbolKey + DocumentChecksum + ProviderVersion + OptionsHash。
  - 失效：WorkspaceChanged 按 Document 粒度清除；FSW 为兜底。

- 日志与度量：
  - 统一 DebugUtil（类别：Search/Outline/Source/Overlay/Invalidation/Perf）。
  - 埋点 p50/p95；对热点路径做采样日志。

---

## 4. API 面向 AI Coder（最小稳定面）
- 搜索 Search(query: string, kind?: filter) → [SearchHit]
- 解析 Resolve(symbolRef: name|SymbolId) → SymbolId（消歧）
- 概览 Outline(symbolId, level: member|type|namespace|assembly, opts)
- 源码 TypeSource(typeId, opts)

DTO 建议（概略）：
```csharp
record SymbolRef(string SymbolId, string Name, string Kind, string Namespace, string Assembly);
record MemberOutline(string Kind, string Name, string Signature, string? Summary, Location DeclaredIn);
record TypeOutline(string Name, string? Summary, IReadOnlyList<MemberOutline> Members);
record TypeSource(string Name, string FullMergedText, IReadOnlyList<Part> Parts);
```

CLI（CodeCortex.Cli）映射：
- `search "pattern"` → SearchHit JSON 列表
- `resolve "TypeName|SymbolId"` → SymbolId JSON（单个）
- `outline "SymbolId" [--level type|member|namespace|assembly] [--md]` → Outline JSON 或 Markdown
- `source "TypeId"` → TypeSource JSON / Markdown 源码

---

## 5. 关键实现要点
- partial 合并：
  - Roslyn 已将多个声明合并为单一 INamedTypeSymbol；文本层需按声明文件稳定排序合并。
  - 在合并文本中插入“文件分隔注释”，保留追溯性。

- 签名与显示：
  - 自定义统一的 SymbolDisplayFormat（短、稳定、无噪声）。
  - 示例：不含 containing type/assembly 的短名，参数类型使用简名，泛型参数简化。

- XML Doc 处理：
  - `ISymbol.GetDocumentationCommentXml()` → 解析 summary/param/returns，容忍缺失。
  - 提供 raw/xml 与精炼/markdown 两种输出模式（按需选择）。

- Overlay 优先：
  - 文本获取优先来源于当前 Solution 的 Document（含 overlay），避免直接读文件。

- 失效策略：
  - WorkspaceChanged → 定位受影响 Document → 关联的 SymbolId 缓存清除。
  - 命名空间/程序集聚合：按下辖 Document 粒度局部重算，避免全量。

- 并发与可取消：
  - 全链路 async + CancellationToken；singleflight 避免 N 倍重复计算。

- 性能 SLO：
  - search p95 < 50ms（热路径）；type outline p95 < 100ms；大类型 < 250ms。
  - namespace/assembly birdview 局部重算 < 500ms。

---

## 6. CLI 与输出格式
- 所有命令默认输出 JSON；`--md` 开启 Markdown 渲染（outline/source）。
- JSON 字段顺序稳定、命名短而明确；避免冗长文本，必要时分页/截断（带 nextToken）。
- 统一错误与诊断：返回 errorCode + message + hints；DebugUtil 分类日志可关联 traceId。

---

## 7. 与 Roslyn AST 编辑的接口预留（后续）
- Rename(symbolId, newName)
- ReorderMembers(typeId, newOrder: string[] memberIds)
- MoveMember(memberId, targetTypeId)
- Insert/Remove members, Add/Remove attributes, etc.
- 事务/批量：BatchEdit(actions[]) → 应用到 Workspace 并返回 diff 摘要。

保障：
- 全部以 SymbolId 定位，编辑基于 Syntax/Semantic API；提交前做编译检查与可选格式化。
- 输出编辑前后 outline 对比，便于 AI Coder/人类审阅。

---

## 8. 缓存与依赖（当前阶段的立场）
- 不上重缓存；依赖追踪仅限于 Document→Symbol 的近似映射用于失效。
- 可选轻缓存：MemoryCache LRU + TTL，吸收抖动（并发/重复请求）。
- 真正的“认知图”缓存/依赖追踪留待后续（见下一节）。

---

## 9. 未来扩展：Cognition Graph（持久化认知图）
- 认知节点（CognitionNode）：
  - question: 这个认知回答了什么问题？
  - inputs: 形成该认知依赖的输入认知节点（含 Roslyn/文档事实节点）。
  - text: 该认知的文本表示（可为 outline/解释/决策）。
  - meta: author(model/user)、time、evidence、confidence、embedding/version 等。
- 特殊节点：Unknown/Conflict/Assumption，显式入图，便于后续求证与修正。
- 图操作：
  - 增量维护：输入节点变更 → 依赖链上的派生认知失效/标注过期。
  - 内容寻址：Key = ProviderVersion + InputsHash + Options；支持去重与重用。
- 与本层集成：
  - Providers 的输出均可转存为认知节点，形成从“事实（Roslyn/文件）→ 解释/策略”的谱系。

---

## 10. 风险与权衡
- 解决方案/项目规模极大时，首次加载与索引构建成本上升：
  - 策略：懒加载、按项目/文件分区索引、冷启动预热队列。
- 多目标/多 TFMs 的编译语义差异：
  - 策略：针对 active TFM 构建索引；其余按需切换并缓存。
- Overlay 与 FSW 的一致性：
  - 策略：以 WorkspaceChanged 为准，FSW 作兜底校验；日志追踪不一致事件。

---

## 11. 里程碑（建议）
1) MVP-Search/Resolve：内存索引 + JSON 输出；CLI: search/resolve。
2) MVP-Type/Member Outline：签名+XML Doc 摘要；CLI: outline（--level）/--md。
3) TypeSource 合并：合并 partial 文本视图；CLI: source。
4) Overlay/FSW + 轻缓存：WorkspaceChanged 失效；singleflight + 小 LRU。
5) Namespace/Assembly Birdview：聚合与分页；关键成员 Attribute 透出。
6) 基础 AST Edit 接口：Rename/Move/Order，返回前后差异摘要。

---

## 12. 参考实践
- Roslyn：Workspaces/SymbolFinder/SymbolKey/Incremental Generators 的增量理念。
- 构建/缓存：Bazel/Nix 的内容寻址与精准失效（思想借鉴，轻量实现）。
- 数据流/增量：Salsa/Adapton 的查询依赖图理念（未来 Cognition Graph 适配）。
- CodeCortex CLI：建议保留 `status/search/outline/resolve`，新增 `source`。

---

## 13. 度量与验收
- 延迟 SLO：search p95 < 50ms；type outline p95 < 100ms；特大类型 < 250ms。
- 正确性：SymbolId 定位稳定；overlay 一致；XML Doc 缺失容忍。
- 体验：Markdown 输出结构化清晰，默认精简；JSON 可供程序消费。

> 以上为 CodeCortex V2 的顶层意图、架构与实现要点。后续当“通用 Cognition Graph”需求清晰后，可作为上层持久化与依赖传播引擎与本层无缝对接。
