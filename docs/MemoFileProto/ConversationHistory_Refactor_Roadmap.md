# Memo: Conversation History 抽象重构实施路线图 · LiveContextProto
*版本：0.1-draft · 更新日期：2025-10-13*

> 本路线图围绕新建的 `prototypes/LiveContextProto` 项目展开，目标是在一张白纸上验证蓝图 V2 所定义的三阶段流水线（AgentState → Context Projection → Provider Router）。蓝图详见《Memo: Conversation History 抽象重构蓝图 · V2》，Roadmap 聚焦“如何一步步落地”。

---

## 文档用途与边界
- **定位**：指导 LiveContextProto 在多个短迭代内完成最小可行实现（MVP）并积累设计经验，供后续回流 MemoFileProto 主线。
- **适用场景**：工程/架构双角色共同参考，明确下一阶段应交付的代码、验证手段与学习目标。
- **不涵盖内容**：现网部署、持久化方案、面向最终产品的界面与运维流程；这些议题仍由蓝图中的 Deferred/TBD 章节承接。

## Proto 概览
- **项目位置**：`prototypes/LiveContextProto`
- **技术栈**：.NET 9.0 Console App，使用 `dotnet new console --framework net9.0` 作为基础。
- **设计假设**：
	- 单线程循环驱动 Agent（与 MemoFileProto 当前模型一致）。
	- 无持久化需求，所有状态驻留内存，可通过 DebugUtil 观察。
	- Provider 模拟优先，以快速验证上下游契约；真实 SDK 接入延后。

## 执行策略总览
> 状态图例：[ ] = 规划中，[>] = 进行中，[x] = 完成。

| 阶段 | 状态 | 核心目标 | 关键交付物 |
| --- | --- | --- | --- |
| Phase 0：环境与骨架 | [x] | 创建 LiveContextProto 项目与基础目录，搭建最小 Console 驱动循环 | 项目骨架、DebugUtil 初始化、`AgentState` 空壳 |
| Phase 1：History & Context MVP | [x] | 实现 HistoryEntry 分层 + RenderLiveContext 最小路径，输出可观察的上下文列表 | `AgentState` 语义化追加 API、上下文快照打印、基础单测 |
| Phase 2：Provider Stub & Router | [x] | 引入 `IProviderClient` 接口与路由占位，实现模拟模型流式输出 | Stub Provider、`ModelOutputDelta` 聚合器、回写历史逻辑 |
| Phase 3：Window & LiveInfo 扩展 | [x] | 接入 Window 装饰与 Memory Notebook 快照，验证渲染装饰器 | Window 注入策略、Notebook Mock、上下文断言测试 |
| Phase 4：工具链与诊断 | [x] | 模拟工具调用生命周期，采集 DebugUtil/Metadata，并总结迁移指南 | 工具调用流水、诊断日志回顾、经验小结文档 |

## 阶段规划细节

### Phase 0：环境与骨架（[x]）
- **目标**：搭建基础框架，确保后续迭代无需再处理基础设施问题。
- **任务要点**：
	1. 使用 `dotnet new console` 创建项目，配置 `Directory.Build.props` 继承、全局隐式命名空间等基础设置。
	2. 引入 `DebugUtil`，验证日志落盘与类别控制正常。
	3. 定义 `AgentLoop`（可同步）驱动：示范读取输入、调用 Agent、打印结果。
	4. 搭建最小版 `AgentState`、`HistoryEntry` 占位类型，保留 TODO 标记。
- **交付验收**：项目已可编译运行并输出占位 Agent 信息，`ATELIA_DEBUG_CATEGORIES=History` 时可看到初始化日志与 AgentLoop 启动/退出记录。
- **经验沉淀**：LiveContextProto 复用了共享 `Directory.Build.props` 与 `DebugUtil`，无需额外脚本；与 MemoFileProto 相比，入口更精简（仅保留 Echo 循环），后续可在 Phase 1 补齐系统命令解析。

### Phase 1：History & Context MVP（[x]）
- **目标**：让 AgentState 能追加/渲染基础上下文，覆盖蓝图中"历史只追加、上下文纯读"的核心原则。
- **任务要点**：
	- [x] 按蓝图引入 `HistoryEntry` 派生（ModelInput/ModelOutput/ToolResults），通过统一时间戳注入保持有序性。
	- [x] 落地 `AppendModelInput/AppendModelOutput/AppendToolResults` 语义化入口，并新增 DebugUtil 打点。
	- [x] 实现 `RenderLiveContext()`，在保持系统指令首位的同时对最新可装饰条目注入 Window，并复用历史实例。
	- [x] 编写 xUnit 测试覆盖时间戳注入、上下文顺序及 Window 装饰幂等性。
	- [x] Console Demo：命令行新增 `/tool sample|fail` 与 `/demo conversation`，可一键构造两轮对话、Notebook 快照与工具结果，便于观察 Window 注入效果。
- **交付验收**：`dotnet test` 通过，控制台 `/history` 展示系统消息、用户输入、占位助手输出、模拟工具结果与（存在笔记时的）Window 装饰，命令 `/demo conversation` 生成的脚本日志可复现验收场景。
- **经验沉淀**：确认 Window 始终装饰最新的输入或工具结果，Notebook 更新走受控 API；工具结果追加后 Demo/命令行均能复现上下游契约。
- **学习目标**：筹备 Phase 2 Provider Stub：固化 `ModelOutputEntry`/`ToolResultsEntry` 聚合示例，并梳理路由与增量解析待办。

### Phase 2：Provider Stub & Router（[x]）
- **目标**：引入统一的 Provider 接口与路由逻辑，先以 Stub 模拟流式输出，确保调用流程闭环。
- **任务要点**：
	- [x] 定义 `IProviderClient.CallModelAsync`（基于 `IAsyncEnumerable<ModelOutputDelta>`）。
	- [x] 实现 `ProviderRouter`：根据策略标签选择 Stub Provider 并输出模型元信息。
	- [x] 编写 Stub Provider：从预设 JSON 脚本读取增量并支持占位符替换，生成 `ModelOutputDelta` 流。
	- [x] 聚合 delta：通过 `ModelOutputAccumulator` 汇总文本/工具调用/TokenUsage，回写 `AgentState`。
	- [x] Console Demo：默认用户输入即触发 Stub 流式调用，展示上下文→模型输出→历史回写的闭环。
- **交付验收**：
	- [x] 控制台实时打印 Stub 模型输出、工具声明与聚合结果，`/history` 可复现相同序列。
	- [x] Debug 日志覆盖 Router 解析、delta 聚合与 Stub 脚本执行。
	- [x] 新增单元测试验证 delta 聚合与 Orchestrator 回写路径。
- **经验沉淀**：Stub Provider 机制已验证完整的流式调用流程，包括内容片段、工具调用声明、工具结果与 TokenUsage 聚合。控制台命令 `/stub <script>` 可自由切换脚本场景，为后续真实 Provider 接入奠定基础。

### Phase 3：Window & LiveInfo 扩展（[x]）
- **目标**：在 MVP 上引入 Window 装饰与 Memory Notebook 投影，验证接口装饰协作。
- **任务要点**：
	- [x] 实现 Notebook Mock（内存字段），提供更新接口 `UpdateMemoryNotebook`。
	- [x] 在 AgentState 中追加 Notebook LiveInfo 字段，并在 `RenderLiveContext` 中附加 Window。
	- [x] 增加测试覆盖：Notebook 更新时 Window 变化；保持历史条目不受影响。
	- [x] Console Demo：通过 `/notebook` 命令展示 Notebook 编辑 → 下一轮上下文 Window 更新。
	- [x] 补充更多 LiveInfo 扩展场景测试（如系统指令热更新、多种 LiveInfo 组合）。
	- [x] 验证跨 Provider 的 Window 兼容性（准备真实 Provider 适配前的验收）。
- **交付验收**：
	- [x] Window Decorator 生效，在最新输入或工具结果条目上成功注入。
	- [x] 上下文列表保持可读性，装饰器透明访问 `InnerMessage`。
	- [x] 跨多个 LiveInfo 场景的集成测试通过。
- **学习目标**：明确 Notebook 事件化前的协作模式，为蓝图 Deferred 项提供依据；评估 LiveInfo 自动记账的接口设计需求。

### Phase 4：工具链与诊断（[x]）
- **目标**：模拟工具调用生命周期，采集诊断信息，并形成迁移建议。
- **任务要点**：
	- [x] 扩展 Stub Provider 支持输出工具调用增量，回写 `ToolCallRequest`。
	- [x] 实现工具执行器（可同步），返回 `ToolCallResult`，包含耗时/状态。
	- [x] 在 AgentState 中记录 Metadata（token 占位、工具耗时），控制台 `/history` 与调用结果同步展示摘要，并输出 DebugUtil 日志。
	- [x] 汇总本阶段经验，撰写迁移建议（如何将 LiveContextProto 成果落地 MemoFileProto），详见 `prototypes/LiveContextProto/Phase4_Diagnostics_Summary.md`。
- **交付验收**：Console Demo 展示模型调用→工具执行→历史回写；日志包含耗时信息。
- **学习目标**：验证工具调用配对流程、诊断信息裁剪策略。

## 迭代节奏与反馈机制
- **节奏建议**：每阶段 3-5 日内完成一个最小可交付增量，阶段结束召开 30 分钟设计回顾。
- **验证手段**：
	- 单元测试：围绕 AgentState/Provider 契约构建快照或断言。
	- Console 手动演示：用于观察上下文渲染与 DebugUtil 打点。
	- 代码审查：聚焦是否遵守蓝图不变量（历史只追加、上下文纯读、Provider 只读消费）。
- **反馈回路**：每阶段总结“设计假设是否被验证/推翻”“是否需要回写蓝图”，形成 1-2 条经验要点。

## 风险与缓解
| 风险 | 影响 | 缓解措施 |
| --- | --- | --- |
| Stub Provider 过于简单，无法揭示真实问题 | Phase 2/4 经验无法迁移回主线 | 在 Phase 2 收尾时引入 1 个真实 API 调用 Smoke Test，或至少模拟同样的数据结构复杂度 |
| 工具调用流程过度抽象 | 难以评估 ToolResultsEntry 设计 | Phase 4 引入最少两个工具（成功/失败），验证配对与错误路径 |
| Notebook/Window 实现偏离蓝图约束 | 回流 MemoFileProto 时需要重写 | Phase 3 明确记录“Proto 特有简化点”，并在经验总结中标记不可直接迁移的部分 |
| 迭代间缺乏验证 | 问题延迟暴露 | 每阶段强制 `dotnet test` + Demo 录屏/日志留存 |

## 依赖与配套工作
- **共用工具**：`DebugUtil`、测试框架、CI（可选）。
- **需要更新的文档**：
	- 蓝图 V2 如需增补，请在阶段总结后更新相关章节。
	- 若 Stub Provider 引入新契约，需在蓝图“Provider 客户端设计要点”处补充。
- **协作接口**：后续若引入实际模型调用，需与凭据管理/环境配置团队对齐。

## 里程碑追踪
- 2025-10-13：**Phase 3 完成验收（[x]）**，Window 现支持多节 LiveInfo 聚合，新增 `/liveinfo` 命令与 `UpdateLiveInfoSection` API；单元测试覆盖系统指令热更新、LiveInfo 组合以及跨 Provider Window 兼容性，AgentOrchestrator Stub Provider 验收同步通过。
### 本轮更新
- 2025-10-13：**Phase 4 完成验收（[x]）**，控制台现可直接查看模型输出与工具结果的 Metadata 摘要（含耗时、失败计数、per-call 诊断），Stub Provider/ToolExecutor 流程整合完成；新增文档 `prototypes/LiveContextProto/Phase4_Diagnostics_Summary.md` 汇总迁移建议。
- 2025-10-13：**Phase 4 启动（[>]）**，默认 Stub 脚本改为仅声明工具调用，内置 `ToolExecutor` 执行 `memory.search`/`diagnostics.raise`，将耗时、失败计数与每次调用元数据写入 `ToolResultsEntry.Metadata`；新增单元测试覆盖“Provider 仅声明工具调用”路径，Console `/tool` 命令与 Stub Provider 共用同一工具流水线。
- 2025-10-13：**Phase 3 完成验收（[x]）**，新增 `UpdateLiveInfoSection` API 与 `/liveinfo` 控制台命令，Window 支持多节聚合，并补齐系统指令热更新、跨 Provider 兼容与多 LiveInfo 组合的单元测试。
- 2025-10-13：**Phase 2 完成验收（[x]）**，所有交付物已实现并通过测试：`IProviderClient`、`ProviderRouter`、Stub Provider（支持 JSON 脚本与占位符）、`ModelOutputAccumulator`（聚合内容/工具调用/TokenUsage）、Console Demo 流式调用闭环。**Phase 3 基础功能已实现（[>]）**，Window 装饰器与 Memory Notebook 集成已验证，控制台 `/notebook` 命令可演示 LiveInfo 动态注入；尚需补充更多扩展场景测试与跨 Provider 兼容性验收。
- 2025-10-13：Phase 2 启动（[>]），落地 `IProviderClient`、`ProviderRouter`、Stub JSON 脚本与 `ModelOutputAccumulator`；控制台改为通过 Stub Provider 流式生成响应，并新增对应单元测试。
- 2025-10-12：Phase 1 Demo 扩展，新增 `/tool` 与 `/demo` 命令、工具结果样例及 Window 测试用例，控制台脚本可直接复现双轮对话流程。
- 2025-10-12：LiveContextProto Phase 1 更新，`RenderLiveContext` 支持 Window 注入；新增 `/notebook` 命令、占位助手回写与 xUnit 测试覆盖相关行为。
- 2025-10-12：Phase 1 启动，落地 HistoryEntry 分层、语义化追加 API、上下文渲染与 xUnit 测试，控制台 `/history` 命令展示最新上下文。
- 2025-10-12：完成 Phase 0 骨架实现，新增 `prototypes/LiveContextProto` 控制台项目与 AgentState 占位结构，并将 DebugUtil 接入最小循环。
- 2025-10-12：重写路线图，转向 LiveContextProto 原型，定义 5 个阶段与各阶段验收标准、风险缓解策略。

### 既往里程碑
- 无（路线图 V2 首次立项）。

### 下一步计划
- 根据 Phase 4 总结文档梳理 MemoFileProto 主线迁移 checklist，识别阻塞项并安排回写节奏。
- 扩充 Stub Provider 脚本库，增加多工具/失败/高延迟场景，覆盖 ToolExecutor 失败路径与聚合器边界测试。
- 梳理真实 Provider 适配需求（OpenAI/Anthropic），评估工具执行元数据的透传方式并筹备对接测试桩。
