# Resident Codebase Agent Mesh
**作者**: GitHub Copilot
**日期**: 2025-11-17
**状态**: Draft / Discussion

---

## 1. 背景与动机

### 1.1 观察到的瓶颈
- **上下文构建成本高**：单体 Agent 每次接入任务都要重新定位相关文件、文档与历史讨论，IO 与 token 在"找信息"阶段被大量消耗。
- **KV Cache 生命周期短**：当前对代码库的深度理解以会话为单位，当会话结束或模型切换时缓存被清空，无法复用。
- **历史与文件交错膨胀**：执行任务时 user/assistant/tool 往返混杂，尤其涉及多轮编辑时，旧版文件内容滞留在对话历史中，进一步推高成本。

### 1.2 目标
1. **让代码库"有驻场智能"**：以长期存在的 Resident Agent 表示代码库的不同子域，使理解随代码演化而持续积累。
2. **提升检索命中率**：通过结构化的依赖索引与意图路由，减少"从 0 开始"式的模糊遍历。
3. **升级交互范式**：允许编排 Agent 群体进行批量问询、分层过滤与响应汇总，将粗筛与精炼拆分给不同规模的模型。
4. **拥抱 Atelia 现有能力**：与 `memo_*` 编辑、任务栈、LiveContextProto 工具生态兼容，形成互补。

---

## 2. 总体结构

```
┌───────────────────────────────────────────────────────────┐
│                    Orchestrator / Lead Agent              │
│                    (现有 Agent/Core)                      │
├───────────────────────┬───────────────────────────────────┤
│Query Router + Filters │ Response Reducer                  │
├───────────────────────┴───────────────────────────────────┤
│                 Broadcast Fabric / Message Bus            │
├───────────────┬───────────────┬───────────────┬───────────┤
│ Resident A    │ Resident B    │ Resident C    │ ...       │
│ (Data layer)  │ (Diagnostics) │ (LiveContext) │           │
├───────────────┴───────────────┴───────────────┴───────────┤
│ Shared Stores: KV Cache Pool · Semantic Graph · Doc Watch │
└───────────────────────────────────────────────────────────┘
```

### 2.1 角色说明
- **Orchestrator**：现有主 Agent，会根据任务状态决定何时"呼叫研发大楼"。
- **Query Router**：低成本模型 + 规则集合，负责意图识别、候选 Agent 过滤与 fan-out 规划。
- **Resident Agents**：长期驻守的子 Agent，绑定一组文件/文档 + 能力集合，并持久缓存对该子域的理解。
- **Response Reducer**：将多个 Resident 响应进行聚合、冲突检测、优先级排序，再反馈给 Orchestrator。
- **Shared Stores**：
   - **KV Cache Pool**：为每个 Resident 保留热身 prompt 与最近若干任务的 KV 片段，实现"快速复位"。
  - **Semantic Graph**：跨 Resident 的依赖、调用、数据流关系图。
  - **Doc Watch**：监听代码/文档变更，触发对应 Resident 重新索引。

---

## 3. Resident Agent 形态

| 维度 | 设计要点 |
| --- | --- |
| **Scope** | 依据模块、边界上下文或关注点（如 Data、Diagnostics、LiveContextProto）划分，大小以"单 Agent 可以在 32-64k tokens 内完整讲明白"为目标。 |
| **Memory Layering** | ① 原始文档切片 + 嵌入索引；② 长摘要（手写 + 自动）；③ KV 热启动块；④ 行为日志（完成的任务、API 约束）。 |
| **Tool Belt** | Resident 可注册只读工具（结构化索引、面向该子域的 `memo_read` 快捷入口）以及只写在自己负责区域的 `MethodToolWrapper` 工具。 |
| **Health Signals** | 维护 freshness score（自上次同步以来的 diff 体积）、负载指标以及自我评估"我能否回答？"的置信度。 |

Resident 的运行模型：
1. **长期存在**（独立进程或持久会话），而不是随请求创建/销毁。
2. **周期性吸收代码变更**：通过 git hook 或定时任务读取 diff，更新自己的向量索引与摘要。
3. **保留最近 N 次答复的 KV**：命中后可以只追加新指令，让模型继承此前的"局部脑图"。

---

## 4. 请求生命周期

1. **Intent Capture**：Orchestrator 根据任务上下文生成结构化查询（目标、优先级、需要的 artefact 类型等）。
2. **Cheap Filter Pass**：Query Router 使用小模型对所有 Resident 执行 "Can you answer?" 询问，仅返回 `can_answer`, `confidence`, `hints`，不展开细节。
3. **Dependency-Aware Fan-out**：依据 Semantic Graph，对高置信度 Resident 批次下发正式请求；必要时按照依赖拓扑排序（例如先问 Data-layer，再问 Agent-Core）。
4. **Resident Response**：每个 Resident 返回：
   - 摘要结论
   - 引用的文件/行范围
   - 可选的执行提议（如"调用 Tool X 完成修改"）
   - 新的观察（写回共享图谱）
5. **Reducer 聚合**：合并相同结论、标记冲突、生成建议执行计划或链接进一步工具调用。
6. **KV 冷热管理**：对表现良好的 Resident，将本轮 KV block 与新摘要存回缓存池；若长期未命中则降级释放资源。

---

## 5. 依赖链与响应链

### 5.1 依赖图谱
- 边类型：`imports`, `runtime-call`, `shared-config`, `doc-refers`。
- 图谱存放在 `SemanticGraphStore`，由增量解析器（Roslyn + 定制规则）持续更新。
- Resident 订阅与自己相关的节点，图更新时触发"局部重建"。

### 5.2 修改响应链
1. Orchestrator 准备对模块 M 做修改 → Router 查图得到受影响的 Resident 列表。
2. 先通知"近耦合" Resident（同模块、共享配置）进入 **Guard 模式**，记录潜在影响点。
3. 编辑完成后广播变更摘要，Resident 依据 guard 记录更新自己的摘要或触发验证（如运行自有单元测试、lint）。
4. 若出现冲突（Resident 声称自身假设被破坏），Reducer 生成回执，让 Orchestrator 决定是否回滚或追加任务。

---

## 6. 与现有 Atelia 体系的结合

| 现有能力 | 合作方式 |
| --- | --- |
| `memo_*` 编辑流水线 | Resident 可在返回响应时附带"建议 diff"，并触发 Orchestrator 使用 `memo_begin_task` + `memory_notebook_*` 执行。 Resident 只需要描述"在哪里""改什么"。 |
| 任务栈 / 思维树 | Orchestrator 在 fan-out 时为每个 Resident 子对话创建任务节点，完成后将摘要折叠写入思维树，保留可追溯性。 |
| LiveContextProto 工具注册 | Resident 可暴露专属工具，通过 `MethodToolWrapper` 注册到 LiveContextProto，方便 Orchestrator 按需调用。 |
| DebugUtil Telemetry | Router 与 Resident 统一用 DebugUtil 打日志（类别如 `AgentMesh.Router`、`AgentMesh.Resident.Data`）便于追踪批量问询。 |

---

## 7. 落地路线图

1. **Phase A - Prototype Router (1 周)**
   - 实现 Query Router + Resident registry（静态配置）。
   - 用 2-3 个示例 Resident（如 Data、Diagnostics）手工撰写摘要，验证批量问询流程。
2. **Phase B - Persistent Memory (2 周)**
   - 引入 KV Cache 池管理器，支持 Resident 会话复用。
   - 接入 Doc Watch，记录 diff → 重建摘要 → 验证。
3. **Phase C - Dependency Graph + Guard Rails (2-3 周)**
   - 构建 SemanticGraphStore，支持"修改响应链"。
   - Resident 可以订阅/报警，Reducer 能输出冲突报告。
4. **Phase D - Tooling Integration (2 周)**
   - Resident 直接触发 `memo_*`、LiveContextProto 工具链。
   - 增加小模型"可回答过滤"与大模型"正文响应"双阶段。

---

## 8. 开放问题

1. **资源隔离**：常驻会话如何与主编排 Agent 共存，避免 GPU/CPU 互相抢占？
2. **一致性模型**：Resident 的长期摘要如何与最新代码保持强一致，尤其在大幅 refactor 期间？
3. **安全边界**：Resident 是否允许直接修改文件，还是只建议？若允许，如何执行 code review？
4. **缓存淘汰策略**：KV block 何时失效？按时间、命中率还是依赖变更量？
5. **多租户**：未来如果需要服务多个项目或分支，Resident 的部署拓扑如何扩展？

---

## 9. 下一步讨论提纲
- 调研现有 LLM 供应商的 KV 复用 API（Anthropic Claude 3.5 Sonnet、OpenAI GPT-4.1 等），评估 Resident 的运行成本。
- 设计 Query Router 的 DSL，让 Orchestrator 能描述"我要查什么"而不是硬编码 prompt。
- 评估是否需要将 Resident 输出分级：`fact`, `hypothesis`, `actionable`，方便 Reducer 决策。
- 与团队同步：Resident 划分方案（按目录、按功能、按技术栈）及维护职责。
