# 给过去的我：CodeCortex V2 会话回溯信（Fork A/B/C 起点之后的累计进展）

本文用作“时间信件（Timeletter）”，用于在对话回溯/分叉时，把关键结论与可复用的“足够信息”带回过去，避免细节淹没上下文窗口。

## 回溯点
- 场景：你让我在三个分支中选择下一步（A：outline；B：find 分页+歧义；C：显示优化等）。
- 目标：以最小上下文让“过去的我”能顺利继续实现，不必携带所有文件细节。

## 自回溯点以来的累计进展（高置信）
1) 项目骨架
- 新建 CodeCortexV2（库）与 CodeCortexV2.DevCli（一次性 CLI）。
- 采用 Roslyn Workspace 作为唯一真实视图；默认 Markdown 输出；DebugUtil 已可用。

2) Markdown/XMLDoc 渲染（复用成熟方案）
- 引入 XmlDocFormatter（partial）与 MarkdownRenderer（从 V1 轻移植、风格保持一致）。
- 提供 TypeOutlineProvider、MemberOutlineProvider，支持类型/成员级摘要渲染（默认 Markdown）。

3) 符号索引与查询
- 建立类型级 SymbolIndex：全量扫描 Compilation → 收集 FQN、SimpleName、Assembly、DocCommentId（ID）。
- 匹配优先级：Id > Exact > ExactIgnoreCase > Suffix > Wildcard > GenericBase > Fuzzy（GenericBase 优先于 Fuzzy）。
- 去重基于 Id（DocCommentId）。
- 使用 DocCommentId（T:...）作为稳定 Id 与对外显示主键：
  - 可读、唯一、公开 API、可由 Compilation.GetTypeByMetadataName 反解。
  - 比 SymbolKey 更适合公开；SymbolKey 在公开包里不可直接用。

4) find 命令（合并 Search/Resolve 心智）
- 支持分页：limit + offset；返回 SearchResults{ Items, Total, Offset, Limit, NextOffset }。
- 输出仅显示 DocCommentId，附 Kind/MatchKind 与 Assembly；当简单名 + Suffix 多命中 → 标注 !ambiguous。

5) outline 命令（类型级，Markdown 默认输出）
- 支持模糊查询：优先按 Id 解析；否则走索引管线。
- 结果为 0 → 友好提示“目前仅支持查询 workspace 中定义的类型（DocCommentId T:...）”。
- 结果>1 → 退化为 find 列表（含分页/歧义信息）。
- 结果=1 → 使用 TypeOutlineProvider 输出 Markdown 概览（成员签名+摘要）。

6) 设计立场（稳定不变）
- Markdown-first；JSON 可选开关。
- 不做重缓存；仅做轻缓存/并发合并（后续再加）。
- 以 Workspace/AST 为事实来源（面向未来的重构/编辑 API）。

## 目前行为快照（供过去的我“体感相同”地复现）
- find 示例（分页+歧义）：
  - Results 2/4, offset=0, limit=2, nextOffset=2
  - - [Type/Suffix] T:CodeCortex.Core.Symbols.SymbolResolver !ambiguous (asm: Atelia.CodeCortex.Core)
  - - [Type/Suffix] T:CodeCortex.Core.Symbols.ISymbolResolver !ambiguous (asm: Atelia.CodeCortex.Core)
- outline 示例（唯一渲染）：
  - # SymbolIndex
  - - `Task<SymbolIndex> SymbolIndex.BuildAsync(Solution solution, CancellationToken ct)`
  - - `Task<SymbolId?> SymbolIndex.ResolveAsync(string identifierOrName, CancellationToken ct)`
  - - `Task<SearchResults> SymbolIndex.SearchAsync(string query, SymbolKind? kindFilter, int limit, int offset, CancellationToken ct)`

## 核心接口与类型（仅保留你需要记住的）
- Id：使用 DocCommentId（类型用前缀 T:，嵌套类型用+，泛型以 `n 标记 Arity）。
- SearchResults：Items/Total/Offset/Limit/NextOffset。
- SearchHit：包含 MatchKind（Id/Exact/…/Fuzzy），IsAmbiguous 标识，符号 Kind。
- ISymbolIndex：SearchAsync(query, kind?, limit, offset, ct) → SearchResults；ResolveAsync 同步走 Search 的唯一性判断。

## 最小文件触点（过去实现时“尽量只动这些”）
- src/CodeCortexV2/Index/SymbolIndex.cs（优先级、分页、歧义、DocCommentId 反解逻辑）
- src/CodeCortexV2/Abstractions/Providers.cs（接口签名 SearchAsync 返回 SearchResults）
- src/CodeCortexV2/Abstractions/Outlines.cs（SearchHit/MatchKind/SearchResults DTO）
- src/CodeCortexV2.DevCli/Program.cs（find/outline 行为与输出）
- src/CodeCortexV2/Providers/TypeOutlineProvider.cs（类型级渲染）
- src/CodeCortexV2/Formatting/XmlDocFormatter.* / MarkdownRenderer.cs（渲染风格）
- src/CodeCortexV2/Workspace/RoslynWorkspaceHost.cs（MSBuild 注册与日志）

## 约束与不变量（你需要牢记）
- 默认输出 Markdown，JSON 用 --json 显式切换。
- 列表仅显示 DocCommentId；避免 FQN 与 DocId 双列重复。
- 模糊命中多条 → 列表；命中 0 条 → 明确提示“仅支持 workspace 中的类型”。
- GenericBase 匹配优先于 Fuzzy（体验更符合预期）。

## 下一步可选路线（供分叉时选用）
- A’：outline 优化
  - 补充继承/实现接口/泛型约束/可见性等结构化片段；保持简洁的 Markdown。
  - TypeSourceProvider（merged partials）与源码片段定位。
- B’：查询体验
  - --kind 过滤（先 Type）；FQN 前缀/包含的快速索引；
  - 搜索/渲染全链路 DebugUtil 埋点（Search/Outline/Invalidation/Perf）。
- C’：增量索引
  - 订阅 WorkspaceChanged，先 Project 粗粒度，后 Document 细粒度差分更新。
  - 仍坚持“薄缓存”，以正确失效为主。
- D’：成员级 DocId 支持
  - 扩展到 M:/P:/F:/E:，实现更细粒度 drill-down（后续再议）。

## 给过去的我的简短指令
- 若你在过去的分支要“先上手”：
  1) 只记住 DocCommentId 是一切外显 Id；
  2) find 的返回要有 Total/NextOffset，Suffix 简名多命中要标注 !ambiguous；
  3) outline 接受“模糊或 DocId”，走“0→提示 / 多→列表 / 1→渲染”的分支；
  4) 输出保持 Markdown-first，尽量少动渲染风格；
  5) 别做重缓存，先保证正确性与清晰失效。

—— 未来的你（CodeCortex V2）

