# Now: CodeCortex V2 当前工作集

## 当前意图
- 将“查→看”的体验闭环稳定：find 精准分页与歧义标注已完成；outline 支持模糊并在唯一时渲染。

## 不变约束
- Markdown-first；JSON 通过 --json 切换
- 对外 Id = DocCommentId（T:...）；列表只显示 DocId，不重复 FQN
- 搜索优先级：Id > Exact > ExactIgnoreCase > Prefix > Contains > Suffix > Wildcard > GenericBase > Fuzzy（GenericBase 高于 Fuzzy）
- 先正确、再优化；薄缓存；以 Workspace/AST 为事实来源

## 最近完成
- find：SearchResults 返回 Items/Total/Offset/Limit/NextOffset，分页信息完整
- find：简单名 + Suffix 多命中时标注 !ambiguous
- find：新增 --kind 过滤（type 有效，其余为预留）
- find：新增 FQN 前缀/包含 匹配（Prefix/Contains），对含点查询优先触发
- Debug：Search 类别性能埋点，记录 query/kind/limit/offset 与 total/page/elapsed
- outline：模糊查询 → 0 提示 / 多条回退列表 / 唯一渲染 Markdown
- CLI 列表：显示 DocCommentId，Kind/MatchKind/Assembly 作为辅助
- find：Prefix/Contains 支持“去泛型 FQN 视图”（Phase 1），泛型名按基本名匹配更友好


## 正在进行（候选二选一）
- A’ Outline 风格优化：继承/实现接口/泛型约束块，保持简洁
- B’ Find 体验增强（续）：排序微调与 p50/p95 观测

## 下一个拐点（触发条件）
- 若用户频繁按“用 FQN 精确查找”：优先做前缀/包含索引
- 若用户更多打开 Outline 阅读：优先做 Outline 结构化块

## 快速命令
```bash
ccv2 .\Atelia.sln find <query> [--limit N] [--offset M] [--json]
ccv2 .\Atelia.sln outline <query-or-id> [--limit N] [--offset M] [--json|--md]
```

