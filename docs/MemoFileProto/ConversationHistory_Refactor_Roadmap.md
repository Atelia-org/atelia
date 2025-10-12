# Memo: Conversation History 抽象重构实施路线图 · LiveContextProto
*版本：0.1-draft · 更新日期：2025-10-12*

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
> 状态图例：🟡 = 规划中，🟢 = 进行中，✅ = 完成。

| 阶段 | 状态 | 核心目标 | 关键交付物 |
| --- | --- | --- | --- |
| Phase 0：环境与骨架 | 🟢 | 创建 LiveContextProto 项目与基础目录，搭建最小 Console 驱动循环 | 项目骨架、DebugUtil 初始化、`AgentState` 空壳 |
| Phase 1：History & Context MVP | 🟡 | 实现 HistoryEntry 分层 + RenderLiveContext 最小路径，输出可观察的上下文列表 | `AgentState` 语义化追加 API、上下文快照打印、基础单测 |
| Phase 2：Provider Stub & Router | 🟡 | 引入 `IProviderClient` 接口与路由占位，实现模拟模型流式输出 | Stub Provider、`ModelOutputDelta` 聚合器、回写历史逻辑 |
| Phase 3：LiveScreen & LiveInfo 扩展 | 🟡 | 接入 LiveScreen 装饰与 Memory Notebook 快照，验证渲染装饰器 | LiveScreen 注入策略、Notebook Mock、上下文断言测试 |
| Phase 4：工具链与诊断 | 🟡 | 模拟工具调用生命周期，采集 DebugUtil/Metadata，并总结迁移指南 | 工具调用流水、诊断日志回顾、经验小结文档 |

## 阶段规划细节

### Phase 0：环境与骨架（🟢）
- **目标**：搭建基础框架，确保后续迭代无需再处理基础设施问题。
- **任务要点**：
	1. 使用 `dotnet new console` 创建项目，配置 `Directory.Build.props` 继承、全局隐式命名空间等基础设置。
	2. 引入 `DebugUtil`，验证日志落盘与类别控制正常。
	3. 定义 `AgentLoop`（可同步）驱动：示范读取输入、调用 Agent、打印结果。
	4. 搭建最小版 `AgentState`、`HistoryEntry` 占位类型，保留 TODO 标记。
- **交付验收**：项目可编译运行，输出“Hello AgentState”级别信息；`ATELIA_DEBUG_CATEGORIES=History` 时可看到初始化日志。
- **经验沉淀**：记录与 MemoFileProto 共用配置的差异（如启动脚本、调试脚本）。

### Phase 1：History & Context MVP（🟡）
- **目标**：让 AgentState 能追加/渲染基础上下文，覆盖蓝图中“历史只追加、上下文纯读”的核心原则。
- **任务要点**：
	1. 按蓝图引入 `HistoryEntry` 派生（ModelInput/ModelOutput/ToolResults），实现时间戳自动注入。
	2. 完成 `AppendModelInput/AppendModelOutput/AppendToolResults` 等语义化入口，带 DebugUtil 打点。
	3. 实现 `RenderLiveContext()`：反向遍历、注入系统指令、可选 LiveScreen 装饰。
	4. 编写 xUnit 测试覆盖：条目顺序、LiveScreen 装饰、上下文幂等。
	5. Console Demo：模拟两轮对话，打印上下文快照。
- **交付验收**：`dotnet test` 通过，控制台输出显示上下文列表与 LiveScreen（若配置）。
- **学习目标**：理解时间戳注入、装饰器透明性，验证设计文档是否充分。

### Phase 2：Provider Stub & Router（🟡）
- **目标**：引入统一的 Provider 接口与路由逻辑，先以 Stub 模拟流式输出，确保调用流程闭环。
- **任务要点**：
	1. 定义 `IProviderClient.CallModelAsync`（基于 `IAsyncEnumerable<ModelOutputDelta>`）。
	2. 实现 `ProviderRouter`：根据策略标签选择 Stub Provider。
	3. 编写 Stub Provider：从脚本/预设 JSON 读取增量，生成 `ModelOutputDelta` 流。
	4. 聚合 delta：在 Orchestrator 中合并为 `ModelOutputEntry` 和 `ToolResultsEntry`，回写 AgentState。
	5. Console Demo：展示请求上下文→ Stub 响应 → 历史回写 的全流程。
- **交付验收**：
	- Demo 输出体现模型输出与工具占位（无真实工具）。
	- Debug 日志展示 Router 选择、delta 聚合过程。
- **学习目标**：检验蓝图中的 Provider 契约是否易于实现；沉淀 Stub 工具以便回归测试。

### Phase 3：LiveScreen & LiveInfo 扩展（🟡）
- **目标**：在 MVP 上引入 LiveScreen 装饰与 Memory Notebook 投影，验证接口装饰协作。
- **任务要点**：
	1. 实现 Notebook Mock（例如从文件读取或内存集合），提供更新接口。
	2. 在 AgentState 中追加 Notebook LiveInfo 字段，并在 `RenderLiveContext` 中附加 LiveScreen。
	3. 增加测试覆盖：Notebook 更新时 LiveScreen 变化；保持历史条目不受影响。
	4. Console Demo：展示 Notebook 编辑 → 下一轮上下文 LiveScreen 更新。
- **交付验收**：LiveScreen Decorator 生效；上下文列表保持可读性；测试验证装饰器透明性。
- **学习目标**：明确 Notebook 事件化前的协作模式，为蓝图 Deferred 项提供依据。

### Phase 4：工具链与诊断（🟡）
- **目标**：模拟工具调用生命周期，采集诊断信息，并形成迁移建议。
- **任务要点**：
	1. 扩展 Stub Provider 支持输出工具调用增量，回写 `ToolCallRequest`。
	2. 实现工具执行器（可同步），返回 `ToolCallResult`，包含耗时/状态。
	3. 在 AgentState 中记录 Metadata（token 占位、工具耗时），并输出 DebugUtil 日志。
	4. 汇总本阶段经验，撰写迁移建议（如何将 LiveContextProto 成果落地 MemoFileProto）。
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
| Notebook/LiveScreen 实现偏离蓝图约束 | 回流 MemoFileProto 时需要重写 | Phase 3 明确记录“Proto 特有简化点”，并在经验总结中标记不可直接迁移的部分 |
| 迭代间缺乏验证 | 问题延迟暴露 | 每阶段强制 `dotnet test` + Demo 录屏/日志留存 |

## 依赖与配套工作
- **共用工具**：`DebugUtil`、测试框架、CI（可选）。
- **需要更新的文档**：
	- 蓝图 V2 如需增补，请在阶段总结后更新相关章节。
	- 若 Stub Provider 引入新契约，需在蓝图“Provider 客户端设计要点”处补充。
- **协作接口**：后续若引入实际模型调用，需与凭据管理/环境配置团队对齐。

## 里程碑追踪
### 本轮更新
- 2025-10-12：重写路线图，转向 LiveContextProto 原型，定义 5 个阶段与各阶段验收标准、风险缓解策略。

### 既往里程碑
- 无（路线图 V2 首次立项）。

### 下一步计划
- 启动 Phase 0：创建 `prototypes/LiveContextProto` 项目骨架，完成 DebugUtil 接入与最小 AgentLoop。
- 编写 Phase 0 清单对应的 Issue/任务条目，安排责任人与时间窗口。
- 准备阶段验收模板（用于记录 Demo 输出、测试结果和经验要点）。
