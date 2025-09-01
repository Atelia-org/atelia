<!--
  CodeCortex_Design.md  (Version 0.3 – P0/P1 Spec Refinements)
  0.3 变更摘要:
    * summaryState 重命名 semanticState；Outline 不入状态机
    * structureHash 排除 XML 文档；新增 xmlDocHash（文档改动不级联 semantic）
    * 明确 structure / impl / xmlDoc Hash 归一化输入与排序规范
    * index.json 增加 xmlDocHash / depthHint / configSnapshot
    * SCC 失效策略 clarified；组拆分占位
    * 失效传播基于结构（includeInternal 可配置）
    * Prompt 窗口原子写入 + Pinned 软预算
    * ops/<opId>.json schema 占位
    * LRU 访问持久化 access.json 占位
-->

# CodeCortex 顶层设计（本地“语义 IDE” for LLM Coder）

> 目标：将大型 .NET 代码库转化为 **LLM 友好、最小充分、可增量维护** 的结构/语义上下文，并提供受控的 AST 级读写能力，充当 AI Coder 的 “离线语义 IDE 服务层”。

---

## 1. 愿景与范围
一句话：让 LLM 与人类在“无完整交互式 IDE”环境下，也能稳定、低噪、最新地理解并安全修改真实 .NET 工程。

边界：
- 语言：首期仅 C#（Roslyn C# 编译器语义模型）。
- 输入：单/多 .sln 或 .csproj；后期支持增量附加引用程序集。
- 产物：结构 Outline、语义说明、Prompt 窗口聚合、（后期）受控编辑补丁。
- 不做：运行时行为分析 / 动态 Profiler（留待插件）。

非目标（当前阶段）：
- 跨语言（F# / VB）支持。
- 端到端自动重构完成后直接提交 PR（需要人工 Gate）。

## 2. 分层架构与阶段映射

| 层 / 能力 | 原始动机层次映射 | 当前阶段 | 说明 |
|-----------|------------------|----------|------|
| L0 项目加载 | 第1~2层前置 | P0 | MSBuildWorkspace 解析、过滤文件集 |
| L1 Outline 抽取 | 第1层 | P0 | 生成 per-type outline markdown |
| L2 缓存 + 增量 | 第2层 | P1 | hash + index.json + watcher + 状态机 |
| L3 服务化 RPC | 第3层 | P2 | 常驻进程 / JSON-RPC / CLI 前端 |
| L4 语义生成 | “语义功能” | P3 | 依赖拓扑 + LLM 语义缓存（semantic.md） |
| L5 Prompt 窗口 | 第5层 | P4 | pinned + recent + pack 合并裁剪 |
| L6 AST 编辑 | “编辑方向” | P5 | 受控变更管线（rename / extract） |
| L7 调查任务 / 高级分析 | “调查任务” | P6 | 复杂查询 → 生成并缓存调查结果 |

## 3. 术语与核心数据模型

| 术语 | 定义 | 文件/表示 |
|------|------|-----------|
| TypeId | 稳定内部类型ID（哈希截断） | index.types[].id / 文件名片段 |
| MemberId | 类型内成员稳定 ID | 组合 TypeId + 成员签名 hash |
| Outline | 类型结构摘要（公开 API + XML doc 摘要） | `types/<TypeId>.outline.md` |
| Semantic | 角色/生命周期/边界等 LLM 生成语义 | `types/<TypeId>.semantic.md` 或 SCC 合并文件 |
| SCC Group | 强连通分量组标识 | semanticGroupId (可选) |
| Pack | 一组类型上下文打包 | index.packs[] & `.context/packs/<name>.md` (后期) |
| Prompt Window | LLM 注入窗口聚合文本 | `.context/prompt_window.md` |
| semanticState | 语义文件生成状态机字段（仅语义，不含 outline） | none/pending/generating/cached/stale |
| Operation(Op) | 长任务跟踪实体 | `ops/<opId>.json` |
| Alias 映射 | 历史 TypeId→当前 TypeId | `aliases.json` |

### 3.1 index.json（精简 Schema，0.3 修订）
```jsonc
{
  "schemaVersion": "1.0",
  "generatedAt": "2025-09-01T12:00:00Z",
  "projects": [
    { "id": "P1", "name": "MemoTree.Core", "path": "src/MemoTree.Core/MemoTree.Core.csproj", "tfm": "net9.0", "hash": "<ProjectHash>" }
  ],
  "types": [
    {
      "id": "T_ab12cd34",
      "fqn": "MemoTree.Core.NodeStore",
      "projectId": "P1",
      "kind": "class",
      "file": "src/MemoTree.Core/NodeStore.cs",
  "structureHash": "9F2A441C",      // 结构（签名）哈希（排除 XML 文档）
  "implHash": "5BC19F77",            // 实现体哈希（方法体+内部私有改动）
  "xmlDocHash": "17AD90B2",          // 文档哈希（不进入 structureHash）
  "semanticState": "cached",
  "semanticGroupId": null,
  "depthHint": 2
    }
  ],
  "packs": [
    { "name": "core-memory", "tokenEstimate": 1820, "lastBuild": "2025-09-01T12:01:00Z", "typeIds": ["T_ab12cd34"] }
  ],
  "configSnapshot": {
    "hashVersion": "1",
    "structureHashIncludesXmlDoc": false,
    "includeInternalForDependencies": false
  }
}
```

### 3.2 状态机：semanticState（0.3 修订）
```
none ──enqueue──> pending ──dispatch──> generating ──success──> cached
  ^                         └─cancel/error──> pending (或记录失败计数) │
  │                                        invalidate                │
  └─────────<────────────── stale ◄─────────────── invalidate ◄──────┘
```
触发 invalidate 条件：
1. structureHash 变化 → outline 即时重新生成（不入状态机） + semantic stale
2. implHash 变化（structureHash 不变）→ semantic stale（outline 不变）
3. xmlDocHash 变化（structureHash 不变）→ 仅 outline 更新（不触发 semantic，除非配置允许）
4. 下游依赖者所依赖类型 structureHash 变化 → 下游 semantic stale

SCC：组内任一类型 structureHash 变化 → 组 semantic stale；仅 implHash 变化 → 组 semantic stale（整体再生）；拓扑改变导致组拆分将在下一轮调度预处理阶段识别并拆分为单类型任务（占位：P3 实现）。

### 3.3 Hash 策略（0.3 修订）
| 哈希 | 输入内容 | 目的 |
|------|----------|------|
| fileHash | 归一化源码文本 (LF) | 变更检测基础 |
| structureHash | 公开可见 API 结构：类型 + 可访问成员签名（public/protected/internal* 可配置）+ 可见性 + 特性；排序归一；排除 XML 文档 | 增量结构失效 |
| implHash | 全部成员主体 + 私有字段/属性初始化 + 常量值表达式；忽略空白/注释/声明顺序 | 语义失效 | 
| xmlDocHash | 提取合并的 XML 文档节点文本（空白归一） | 文档变更检测 |
算法：SHA256 → 前 8 bytes → Base32（去易混字符）→ 8 字符截断；冲突自动扩展至 12 字符并记录 `hash_conflicts.log`。

归一化要点：
1. 成员集合 Canonical 排序：Kind 优先级(Type > Field > Property > Event > Method)；同类按签名字典序。
2. implHash 构建时：忽略语法 trivia；分部类型合并后统一排序。
3. 默认参数值表达式并入 implHash（不影响 structureHash）。
4. `structureHashIncludesXmlDoc=false`（默认）时 XML 仅影响 xmlDocHash；可经配置切换行为。

### 3.4 Outline 文件格式（抽象）
```
# <FQN> <TypeId>
Kind: <kind> | File: <relative> | Assembly: <asm> | StructureHash: <structureHash> | ImplHash: <implHash>
XmlDocHash: <xmlDocHash>
XMLDOC: <raw first line> (可折叠)

Public API:
  + <signature1>
  + <signature2>

Implements: <interfaces>
DependsOn: <TypeFQN list (Top-N 公共依赖)>
```

### 3.5 Semantic 文件格式
```
# Semantic: <FQN> <TypeId>
StructureHash: <structureHash>
ImplHash: <implHash>
Dependencies: <TypeId...>

ROLE:
LIFECYCLE:
EDGE_CASES:
LIMITATIONS:
RECOMMENDATIONS:
```
环 (SCC) 情况：若多类型同组 → 使用 `semantic/<GroupHash>.md`，头部列出 `Types: <TypeId,...>`；组级失效按 3.2 规则整体再生（0.3 clarified）。

## 4. 符号路径与 ID 策略

### 4.1 符号路径语法
```
TypePath      = Namespace '.' TypeName ( '+' NestedType )*
MemberPath    = TypePath '.' MemberName [ '(' OverloadSignature? ')' ]
Wildcard      = 可在任意段使用 * 或 ?
Generic       = TypeName '<T1,...>' 可省略类型实参（解析时忽略）
Case          = 不区分大小写
```

### 4.2 解析优先级
1. 精确匹配 (FQN)
2. 后缀唯一匹配（`NodeStore` → 唯一）
3. 通配符匹配（`NodeStore.Get*`）
4. 模糊/编辑距离（距离阈值 ≤ 2）

消歧排序：命名空间深度 > 公共访问级别 > 最近访问 LRU 权重 > 名称长度。（LRU 可选持久化：`access.json`；缺失时跨重启结果可能轻微抖动）

### 4.3 性能结构
索引：
```
nameIndex:  symbolLower -> [TypeId/MemberRef]
suffixIndex: reversedName trie
recentLRU:   512 容量 (TypeId)
cache:       路径解析结果 5 分钟 TTL
```

### 4.4 ID 定义
- TypeId = `T_` + Base32( SHA256(FQN + Kind + Arity) )[0:8] （冲突扩展）
- MemberId = TypeId + '_' + Base32( SHA256(CanonicalSignature) )[0:6] （冲突同策略：扩展长度并记录日志）
CanonicalSignature 规范：
```
[Accessibility] [Modifiers sorted] ReturnType DeclaringType.MemberName(<ParamType1,ParamType2,...>) [GenericArity] [NullableAnnotations]
```
忽略：参数名称、文档注释；可选参数默认值不进入 CanonicalSignature，但其表达式体被 implHash 捕获。

## 5. 增量与失效传播

### 5.1 事件来源
- 文件系统 watcher（.cs）
- 编辑操作提交回写
- 外部命令触发（手动 invalidate）

### 5.2 处理流程
```
FsChange Batch(≤800ms) -> Parse Changed Files -> Recompute fileHash ->
  For each Type in file:
    Recompute structureHash & implHash
    if structureHash changed: outline regenerate + semantic stale
    else if implHash changed: semantic stale
Propagate: reverseDependencyGraph（基于结构引用；includeInternal 可配置） → mark downstream semantic stale
Enqueue semantic jobs with priority = (refCount / (1 + depth))
```

### 5.3 任务调度
```
MaxConcurrentSemantic = min(4, logicalProcessors/2)
Queue = 按 priority desc + FIFO 次序
Retry: 指数退避，最多 3 次；失败后记录 error 字段
```

## 6. RPC 与 CLI 契约（v1）

所有外部调用使用“符号路径优先”，允许可选 `id` 短路；响应统一包含 `"resolved": { path, typeId, memberId? }`。

| 方法 | 请求参数（关键） | 响应 | 说明 |
|------|------------------|------|------|
| ResolveSymbol | path | {candidates[], resolved?} | 模糊/通配符解析（不生成） |
| GetOutline | path or typeId | outlineText | 若缺失则生成并缓存 |
| GetMemberOutline | memberPath or memberId | text | 精细成员级输出（节省 token） |
| SearchSymbols | query, limit | [SymbolRef] | 名称/前缀/模糊混合搜索 |
| FindUsages | path/id, maxResults | [UsageSnippet] | 用法片段（后期实现） |
| GetSemantic | path/id | semanticText | 若 stale → 触发任务并返回旧缓存 + 状态 |
| BuildPack | name, filterSpec | PackResult | 构建或重建 pack 文件 |
| ListPacks | - | [PackInfo] | 列出现有 packs |
| GetPromptWindow | - | windowText | 返回当前窗口内容 |
| Pin | path/id | success | 加入 pinned 列表 |
| Unpin | path/id | success | 移除 pinned |
| Dismiss | path/id | success | 临时从 recent 区移除 |
| Investigate | question, scopePaths[] | opId | 创建调查操作（异步） |
| StreamOp | opId | event stream | 长任务进度事件 |
| Status | - | 服务状态指标 | 供 CLI `status` 命令 |

### 6.1 错误格式
```
{ "error": { "code": "SymbolNotFound", "message": "'NodeStroe' not found", "suggestions": ["NodeStore","MemoTree.Core.NodeStore"] } }
```
错误代码枚举：SymbolNotFound, AmbiguousSymbol, AccessDenied, Busy, InvalidFilter, InternalError.

## 7. 语义任务调度

SCC 组合：对强连通分量一次性收集所有 outline（仅结构 hash），合并 prompt 发送给 LLM。

优先级 = 引用计数 / (1 + depth)；depth = 最短路径到叶节点（无出边）层数，多父取最小；depthHint 缓存懒更新。

生成步骤：
1. Gather Context：目标类型(或组) + 直接依赖的 ROLE 摘要（若已有）。
2. 模板填充 → 调用 LLM（maxTokens = 配置/每类型 upper）。
3. 结果解析（YAML 或段落标签）→ 字段齐全性校验。
4. 写入 semantic 文件（临时文件后原子 rename）；更新 semanticState=cached；反向依赖 stale 标记已处理计数减一。

失败策略：记录 `lastError`；进入 pending，指数退避 (2^attempt * baseDelay)。

## 8. Prompt 窗口策略

配置：`promptWindow.maxChars`（字符预算），Pinned 软预算（默认不超过 60%）；超出时提示并不再追加新 pinned 文本直到释放空间；Recent 区域裁剪。

裁剪伪代码：
```
output = []
emitAll(pinned sorted by addedTime)
remaining = maxChars - size(output)
recent = sortBy(lastAccess desc)
for t in recent:
  if remaining <= 0: break
  block = outline(t)
  if block.size > remaining: block = truncateSemantic(block, remaining)
  append(block)
  remaining -= block.size
writeFileAtomic(output)
```
优先保留：Outline > Semantic ROLE > LIFECYCLE > LIMITATIONS > RECOMMENDATIONS（尾部可截断；不足预算时仅保 Outline + ROLE）。

Pack 集成：若激活某 Pack，则其类型在 recent 排序中权重 +W（默认 1.5x）。

## 9. 编辑管线（预研规格）

阶段：
1. 解析 & 符号定位
2. 生成 Roslyn CodeAction / 自定义 SyntaxRewriter
3. Dry-run：内存应用 → 编译（获取 Diagnostics）
4. 诊断过滤（允许集：Info/Warning 可忽略列表）
5. 补丁生成：统一 diff（UTF8 LF）
6. 原子写入 & 触发增量
7. 回滚：失败时用临时快照恢复

事务：后期支持 `BeginEditBatch` → 多操作合并为单次写回；批次期间 watcher 事件抑制（聚合后统一 diff）。

## 10. Pack 过滤语法与配置

### 10.1 过滤语法（EBNF）
```
expr     = term { ("OR"|"or"|"||") term } ;
term     = factor { ("AND"|"and"|"&&") factor } ;
factor   = ["!"] primary | '(' expr ')';
primary  = ( 'namespace:' | 'type:' | 'access:' | 'tag:' | 'depends:' ) token;
token    = /[A-Za-z0-9_.*+?\-]+/ ;
默认 AND；通配符支持 * ?；大小写不敏感。
```

### 10.2 构建算法
1. 初选：过滤器匹配类型集合。
2. 依赖补全：包含直接公共依赖（结构 hash 不重复）。
3. Token 预算：估算= outlineTokens + semanticTokens；超限按“引用次数 & 依赖深度”排序裁剪 semantic，必要时剔除尾部类型。
4. 顺序：拓扑排序（上游依赖先出现）。
5. 缓存：pack 文件 hash 不变即复用。

## 11. Telemetry & 运行监控

指标：
- symbol.resolve.ms (P50/P95)
- outline.gen.count / outline.cache.hitRatio
- semantic.queue.length / semantic.gen.ms / semantic.fail.count
- prompt.budget.usage.ratio
- edit.apply.success.rate / edit.rollback.count

Status RPC 返回关键 P95、队列深度、内存占用。告警阈值：解析 P95 >100ms；队列长度>50；内存>2GB。Schema 占位：`{ uptimeSec, projects, typesIndexed, semanticQueueLength, p95ResolveMs, memoryMB, recentSemanticFailures }`。

## 12. 风险与缓解

| 风险 | 等级 | 缓解 |
|------|------|------|
| 初次加载慢 | 中 | 渐进解析 + 按需 outline 生成 + 缓存快照 |
| 语义生成成本 | 高 | 优先级队列 + 手动触发模式 + 配额限制 |
| Token 预算溢出 | 中 | 分级裁剪 + 语义段落优先级 |
| ID 冲突 | 低 | 启动检测 + 自动扩展长度 + 冲突日志 |
| 依赖环复杂 | 中 | SCC 合并一次性生成 |
| 编辑 race 条件 | 中 | 单写入线程 + watcher 抑制窗口 |
| 缓存老化 | 低 | hash 对比 + stale 标记 + 背景刷新 |

## 13. 路线图与最小落地 (Next Sprint)

P0→P1 最小任务：
1. 项目加载 & 过滤（MSBuildWorkspace）
2. 单类型 outline 提取命令：`codecortex outline <symbol>`
3. index.json（structureHash/implHash 计算）
4. watcher + 800ms 批处理 + 结构/实现分类
5. 符号解析器（精确+后缀+通配符）
6. CLI: GetOutline + ResolveSymbol

预留（后续）：semantic job skeleton / prompt window writer stub。

## 14. 决策记录 (ADR 摘要)

| 决策 | 结论 | 理由 | 复审点 |
|------|------|------|--------|
| 外部接口采用符号路径 | ✅ | 人类与 LLM 自然引用 | 性能 P1 验证 |
| Hash 截断 8 字符 | ✅ | 可读 + 足够低冲突 | 冲突>0 → 延长 |
| 语义缓存 Markdown | ✅ | Git diff 友好 | P3 评 YAML 前言 |
| JSON-RPC StreamJsonRpc | ✅ | 生态成熟 | P4 评二进制 |
| Prompt 单文件 | ✅ | 简单 / 低维护 | P5 评分块 |
| 编辑前 dry-run 编译 | ✅ | 安全 | 按诊断白名单 |

## 15. 工作流示例（精简）

### 15.1 新功能探索（简化）
```
GetOutline("MemoTree.Core.Node") → 结构
SearchSymbols("import", 10) → 候选
GetSemantic("MemoTree.Core.NodeStore") → 行为限制
Investigate("How are large batches processed?", scope=["MemoTree.Core"]) → opId
StreamOp(opId) → 完成后 semantic 缓存
```

### 15.2 性能排查
```
GetOutline("...QueryProcessor.Execute")
FindUsages("...BTreeIndex.Search", 5)
GetSemantic("...BTreeIndex")
SearchSymbols("cache", 10)
Investigate("Query bottlenecks?", scope=["MemoTree.Query"]) → semantic 扩展
```

## 16. 错误代码对照
| code | 说明 | 建议补充字段 |
|------|------|--------------|
| SymbolNotFound | 未找到符号 | suggestions[] |
| AmbiguousSymbol | 多匹配 | candidates[] |
| AccessDenied | 类型/成员不可访问 | publicAlternatives[] |
| Busy | 系统索引/加载中 | progress% |
| InvalidFilter | pack 过滤语法错误 | position, hint |
| InternalError | 未分类内部异常 | traceId |

## 17. 与最初动机映射

| 初稿层次 (RoslynForLLM) | 当前章节 | 差异说明 |
|--------------------------|----------|----------|
| 类型 outline 生成 | 3 / 4 | 增补 hash 分层结构/实现 |
| 缓存+git 友好 | 3 / 5 | 引入 structureHash/implHash 双层 |
| service + CLI | 2 / 6 | 统一 RPC 契约+错误格式 |
| Prompt 文件窗口 | 8 | 增强裁剪算法与优先级 |
| 语义增强 | 7 | 引入 SCC / 优先队列 | 
| 编辑 AST | 9 | 设定安全管线草案 |
| 调查任务 | 7 / 15 | 统一 Investigate 为 Op |

保持“符号路径优先、可读缓存文件、增量新鲜度”三原始原则。

## 18. 后续改进候选
1. Outline/semantic JSON 索引加速（可选二级缓存）。
2. Pack token 估算采用模型特定分词器差异化策略。
3. Investigate 结果结构化（结论 / 证据 / 路径引用）。
4. 编辑操作安全白名单配置化。
5. 提供 Graph 导出（Mermaid / DOT）。

---

（文档结束 / version 0.3）
