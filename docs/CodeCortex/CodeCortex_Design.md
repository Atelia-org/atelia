# CodeCortex 顶层设计（面向 LLM Coder 的本地“语义 IDE”）

> 目标：把大型 .NET 代码库转化为 **LLM 友好、最小充分、可增量维护** 的结构与语义上下文，并在后期提供受控的语法/语义级变更（重构 & 代码生成），成为 AI Coder 的“离线 IDE 语义服务器 + 上下文编排器”。

## 1. 愿景一句话
让 LLM 与人类开发者在没有完整 IDE 交互的情况下，也能高效、低噪、最新地理解并操作真实工程。

## 2. 核心价值支柱
| 支柱 | 说明 | 关键度量 |
|------|------|----------|
| 精准结构 | 基于 Roslyn 真实语义（非纯文本/向量近似） | 结构正确率（随机抽样校验）> 99% |
| 增量更新 | 哈希 + 依赖图 + 受影响传播 | 修改后可见延迟 < 3s（中型项目） |
| 稳定引用 | 稳定 Type/Member ID，避免语义漂移 | ID 再生成稳定率 100%（非破坏性变更） |
| 语义增强 | LLM 生成“超出 Roslyn”的行为/角色/用法说明 | 语义回答命中率提升 vs baseline |
| 上下文预算 | Token 预算裁剪与打包策略 | 有效信息密度（信息/Token）提高 > 3x |
| 受控编辑 | 未来提供安全的 AST 级重构/生成 | 编辑失败回滚率 < 0.5% |

## 3. 分阶段路线图
| 阶段 | 里程碑 | 主要产物 |
|------|--------|----------|
| P0 原型 | 单项目 Outline | `outline` 命令 + `.context/types/*.outline.md` |
| P1 缓存/增量 | index + hash + 变更检测 | `index.json` + 增量生成器 |
| P2 服务化 | 常驻 service + JSON-RPC | 服务进程 + CLI 查询/pack |
| P3 语义层 | 语义分析任务 / 依赖图传播 | `*.semantic.md` / 调查任务队列 |
| P4 Prompt 窗口 | 动态上下文显示/Pin/滚动 | `.context/prompt_window.txt` |
| P5 编辑 Alpha | 只读→受控 AST 操作（重命名 / 提取） | `edit` 命令 + Patch 校验管线 |
| P6 高级智能 | 自动调查 / 模式识别 / 用法综合 | 结构+语义混合推理缓存 |

## 4. 体系结构视图
```
┌──────────────────────────────────────────────┐
│                   CLI / Frontends            │
│  (Terminal, Agent Plugin, VS Code Proxy)     │
└──────────────┬───────────────────────────────┘
               │ JSON-RPC (StreamJsonRpc)
┌──────────────▼───────────────────────────────┐
│              CodeCortex Service              │
│  - Request Router                            │
│  - Cache Manager (.context/*)                │
│  - Task / Op Registry                        │
│  - File Watchers (project + deps)            │
└───────┬──────────┬──────────┬────────────────┘
        │          │          │
  ┌─────▼───┐ ┌────▼────┐ ┌───▼────────┐
  │Roslyn   │ │Assembly │ │Semantic    │
  │Extraction││Introspect││Analysis    │
  │(Project) │ │(Dll/Pdb)│ │(LLM Jobs)  │
  └─────┬────┘ └────┬────┘ └────┬───────┘
        │           │          │
        ▼           ▼          ▼
   Outline Gen  ─→ Index ─→ Packs ─→ Prompt Window
        │                      ▲          │
        └──────────Hash/Diff───┴──────────┘
```

## 5. 关键数据结构（Schema 草案）
### 5.1 index.json
```json
{
  "version": 1,
  "generatedAt": "2025-09-01T12:00:00Z",
  "projects": [
    { "id": "P1", "name": "MemoTree.Core", "path": "src/MemoTree.Core/MemoTree.Core.csproj", "tfm": "net9.0", "hash": "..." }
  ],
  "types": [
    { "id": "T_ab12cd34", "projectId": "P1", "fqn": "MemoTree.Core.NodeStore", "kind": "class", "file": "src/.../NodeStore.cs", "hash": "...", "summaryState": "none" }
  ],
  "packs": [
    { "name": "core-memory", "tokenEstimate": 1820, "lastBuild": "2025-09-01T12:01:00Z", "typeIds": ["T_ab12cd34"] }
  ]
}
```

### 5.2 类型 Outline 文件（`types/<TypeId>.outline.md`）
```
# MemoTree.Core.NodeStore <T_ab12cd34>
Kind: class  | File: src/.../NodeStore.cs | Assembly: MemoTree.Core | Hash: 9F2A441C

/// XML doc 原文
/// AI-SUMMARY: (可空，后置填充)

Public API:
  + ctor NodeStore(IMemoryBackend backend)
  + Task<Node?> GetAsync(NodeId id, CancellationToken ct)
  + IAsyncEnumerable<Node> EnumerateAsync(PrefixQuery query, CancellationToken ct)

Implements: IMemoryReader, IDisposable
DependsOn: MemoIndex, MemoryBackend
```

### 5.3 语义文件（`types/<TypeId>.semantic.md`）
```
# Semantic: NodeStore <T_ab12cd34>
SourceHash: 9F2A441C
Dependencies: T_ff991122 T_7788aa33

ROLE: 维护从 NodeId → Node 的持久化/缓存映射
LIFECYCLE:
  1. 构造时加载最小元数据索引
  2. 读路径命中内存后短路
  3. 写路径追加 WAL → 后台合并
EDGE CASES:
  - 空索引启动
  - 节点损坏（校验失败）
LIMITATIONS:
  - 未实现并发写冲突合并
RECOMMENDATIONS:
  - 引入写批次压缩
```

### 5.4 操作 / 任务状态（`ops/<opId>.json`）
```json
{
  "id": "op_b73f1", "kind": "semantic", "target": "T_ab12cd34",
  "state": "running", "started": "2025-09-01T12:05:00Z",
  "dependsOn": ["T_ff991122"],
  "progress": { "phase": "llm-call", "current": 1, "total": 3 }
}
```

## 6. ID 策略
| 实体 | 组成 | 示例 | 变更策略 |
|------|------|------|----------|
| TypeId | `Base32(Hash(FQN+Kind+Arity))[:8]` | T_ab12cd34 | 重命名→新 ID（旧映射保留别名） |
| MemberId | `TypeId + '_' + ShortHash(Signature)` | M_ab12cd34_ef90 | 签名改变→新 ID |
| PackId | Pack 名 slug | core-memory | 可重建 |

别名表（aliases.json）保留历史 ID → 现行 ID，防止历史引用失效。

## 7. 增量与失效传播
1. 监听文件改动 → 计算新 Hash；未变直接跳过。
2. Type Hash 改变 → 标记语义文件 stale → 其依赖者（反向依赖图）入队。
3. 批处理窗口（默认 800ms）内聚合多次改动再执行。
4. LLM 语义任务：工作队列按“被引用次数 / stale 深度”优先级调度。

## 8. RPC 接口（初稿）
| 方法 | 描述 |
|------|------|
| OutlineType(fqn or TypeId) → OutlineText | 返回 outline（必要时触发生成） |
| SearchTypes(query, limit) → [TypeRef] | 名称/前缀/模糊匹配 |
| BuildPack(name, filterSpec) → PackResult | 构建/重建打包上下文 |
| ListPacks() → [PackInfo] | 列出现有 pack |
| StreamOp(opId) → progress events | 监听长任务进度 |
| GetSemantic(typeId) | 获取语义文件（生成或返回缓存） |
| Investigate(question, scope) → opId | 触发调查（多类型） |
| Pin(typeId) / Unpin(typeId) | 管理 Prompt 窗口固定 |
| GetPromptWindow() | 返回当前滚动窗口文本 |

## 9. Prompt 窗口策略
- 结构：Pinned 区 + Recent 区（按最近访问时间）
- 限额：字符阈值（默认 60k）→ 超出时从 Recent 尾部裁剪
- 输出合成顺序：Pinned → 最近使用类型 Outline → 最近 semantic 片段 → Pack meta
- LLM Friendly 头部：
```
# CodeCortex WINDOW (GeneratedAt=...)
TYPES (Pinned=2 / Recent=8) | BudgetUsed=54,321 chars
```

## 10. 编辑阶段（预研）
安全管线：
1. 用户/LLM 发起 edit 请求（Target: MemberId / TypeId）
2. 解析意图 → 生成 Roslyn 变换（SyntaxRewriter / CodeAction）
3. Dry-run：应用到 Workspace（内存）→ 编译 → 诊断过滤（阻断高风险警告/错误）
4. 生成 Patch（Unified Diff）→ 审批（可人工/自动策略）
5. 真正写回磁盘 → 触发增量管线 → 自动刷新相关上下文

## 11. LLM 语义生成策略
- 依赖调度：拓扑层次 + 强连通分量整体（环内一次性汇总提供上下文）
- Prompt 模板（示例）：
```
SYSTEM: 你是资深 C# 架构师，基于提供的类型 Outline 输出职责/角色/生命周期/边界/风险。
USER:
<OUTLINE:NodeStore>
...outline...
<DEPENDENCIES>
TypeA: summary
TypeB: summary
```
- 结果解析：用 YAML 分区 → 验证所需字段齐全

## 12. 配置（CodeCortex.yaml）
```yaml
version: 1
include:
  - src/**.cs
exclude:
  - **/bin/**
  - **/obj/**
semantic:
  provider: openai
  model: gpt-4o-mini
  maxTokensPerType: 256
packs:
  - name: core-memory
    filter: "namespace:MemoTree.Core"
    tokenBudget: 4000
promptWindow:
  maxChars: 60000
  pinned: []
```

## 13. 关键决策列表（追踪表）
| 决策 | 选项 | 结论 | 复审点 |
|------|------|------|--------|
| 项目加载 | MSBuildWorkspace vs Buildalyzer | 先 MSBuild，后期叠加 Buildalyzer 预热 | P2 |
| RPC 协议 | StreamJsonRpc(JSON) | ✅ | P4 评估二进制 |
| Type ID | Hash(FQN) 截断 | ✅ | 观察冲突率 |
| 语义缓存格式 | Markdown + FrontMatter? | 纯 Markdown + header 行 | P3 再评 YAML |
| Prompt 窗口存储 | 单文本文件 | ✅ | P5 分块 | 
| 编辑变更验证 | Roslyn 编译 + 诊断白名单 | ✅ | P5 |
| 生成器产物 | 默认过滤 | ✅ | 引入白名单 P2 |

## 14. 风险与缓解
| 风险 | 等级 | 缓解 |
|------|------|------|
| 大型解决方案初次加载慢 | 中 | 延迟按需提取 + 缓存快照 | 
| 语义文件过多 IO | 中 | 目录分层 / 批写 / hash 跳过 |
| LLM 成本失控 | 高 | 优先级队列 + 手动触发模式 |
| Prompt 窗口污染（旧信息残留） | 中 | 每次重建窗口写入生成时间 + 过期检测 |
| ID 冲突（截断） | 低 | 冲突检测，必要时扩展长度 |

## 15. 度量与 Telemetry（后期）
- extraction.duration.ms
- types.outline.count / stale.count
- semantic.queue.length / throughput
- prompt.budget.usage.ratio
- edit.apply.success / rollback.count

## 16. 最小落地路线（下一个 Sprint）
1. 创建 Contracts 项（基础 DTO + 接口）
2. `outline` 命令：加载 .csproj → 输出单类型 Outline
3. `.context/index.json` + 写入一个类型 outline 文件
4. Watcher：监控文件更改 → 重建受影响类型
5. CLI 查询：按 FQN 返回 outline 文本

---
（此文档为活动设计文档，随着阶段推进补全 Schema 细节与性能数据。）
