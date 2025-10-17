# 目标项目
`prototypes\LiveContextProto`, C# 控制台应用程序, dotnet 9.0, 实验性LLM Agent原型。

# 相关类型
- `prototypes\LiveContextProto\State\AgentState.cs`: [AgentState]
- `prototypes\LiveContextProto\Agent\LlmAgent.cs`: [LlmAgent]
- `prototypes\LiveContextProto\Agent\AgentOrchestrator.cs`: [AgentOrchestrator]
- `prototypes\LiveContextProto\Tools\ToolExecutor.cs`: [ToolExecutionRecord]
- `prototypes\LiveContextProto\State\History\HistoryEntry.cs`: [ToolResultsEntry]、[ModelOutputEntry]、[ModelInputEntry]
- `prototypes\LiveContextProto\Agent\ConsoleTui.cs`: [ConsoleTui]
- `prototypes\LiveContextProto\Context\ToolCall.cs`: [ToolCallRequest]

# 重构目标

## 1. 让LlmAgent成为状态机
可以方便的暂停/恢复执行, 暂停期间可以把状态序列化与跨进程边界(保存到磁盘再继续执行、转移到其他机器再继续执行等)。不怕工具调用循环期间出故障，可以接着检查点继续执行。

初步思路是手写状态机，而不引入外部状态机框架。
在[LlmAgent]中新增`List<ToolExecutionRecord> _pendingToolResults;`成员，用来记录已完成但尚未聚合成为[ToolResultsEntry]进入[AgentState.History]的工具调用结果。
为[LlmAgent]新增`DoStep()`函数，内部根据当前状态执行一个工作步骤。
关于[LlmAgent.AppendUserInput]/[ModelInputEntry]，把当前的由[ConsoleTui]推[LlmAgent]，改为从[LlmAgent.DoStep()]拉[ConsoleTui]中的新缓存变量。控制台输入到缓存与模型从缓存读取异步与解耦。为将来实现Agent自主持续循环(不再只是响应用户输入)打下结构基础。

### 设想状态
**WaitingInput**状态:
- 条件象限: [AgentState.History]的最后一条为[ModelOutputEntry]，且此[ModelOutputEntry.ToolCalls]为空。
- 行为: 调用外界传入的Handler(当前原型中应来自[ConsoleTui])，获取外界输入文本(此原型中为控制台输入的文本)。如果拿到了非空非空白字符串，则创建一个[ModelInputEntry]并添加到[AgentState.History]末尾，这将转换到**PendingInput**状态。其他情况(拿到null或空白字符串)，则不改变状态继续等待。

**PendingInput**状态:
- 条件象限: [AgentState.History]的最后一条为[ModelInputEntry]。
- 行为: 用当前的ProviderClient实例进行一次LLM调用。得到的[ModelOutputEntry]添加到[AgentState.History]末尾，这将转换到**WaitingInput**或**WaitingToolResults**状态(取决于LLM是否发出了工具调用)。若发生异常则不改变状态。

**WaitingToolResults**状态:
- 条件象限: [AgentState.History]的最后一条为[ModelOutputEntry]，且此[ModelOutputEntry.ToolCalls.Count]大于[LlmAgent._pendingToolResults.Count]。
- 行为: 执行首条尚未在[LlmAgent._pendingToolResults]中记录结果的[ToolCallRequest], 不论是跳过、失败、成功，都将结果记录入LlmAgent._pendingToolResults。这里留下了再次尝试的扩展点。这有可能转换到**ToolResultsReady**状态。

**ToolResultsReady**状态: [AgentState.History]的最后一条为[ModelOutputEntry]，且此[ModelOutputEntry.ToolCalls]中的每一条都能在[LlmAgent._pendingToolResults]中找到对应的结果，可以简化为检查Count相等。
- 行为: 取出全部[LlmAgent._pendingToolResults]来创建一条[ToolResultsEntry]添加到[AgentState.History]末尾。这将转换到**PendingToolResults**状态。

**PendingToolResults**状态:
- 条件象限: [AgentState.History]的最后一条为[ToolResultsEntry]。
- 行为: 目前与**PendingInput**状态的行为相同，调用一次LLM。

## 2. 简化程序结构和复杂性
目前的思路是把[AgentOrchestrator](prototypes\LiveContextProto\Agent\AgentOrchestrator.cs)类型融合进[LlmAgent]类型中。

## 追加设计补充

### 状态语义与更正
- **WaitingInput**：当历史为空，或最后一条为`ModelOutputEntry`且无工具调用，或最后一条为`ToolResultsEntry`但 Agent 决定等待用户新输入时触发。此状态应写入的是新的 `ModelInputEntry`（原设想中提到的 `ModelOutputEntry` 为笔误，应当更正）。
- **PendingInput**：最后一条为 `ModelInputEntry`，表示已有输入待模型处理。完成 LLM 调用后要根据输出是否包含工具调用切换状态。
- **WaitingToolResults**：最后一条为含工具调用的 `ModelOutputEntry`，但 `_pendingToolResults.Count` 小于 `ToolCalls.Count`。
- **ToolResultsReady**：当前输出的所有工具调用都已有对应执行结果（数量一致且按 `ToolCallId` 匹配）。
- **PendingToolResults**：最后一条为 `ToolResultsEntry`，准备发起下一轮模型调用。

建议显式引入 `AgentRunState` 枚举并在 `DoStep()` 里通过私有的 `DetermineState()` 统一判断，同时记录切换日志方便调试。

### 内部数据结构
- `_pendingToolResults`：`List<ToolExecutionRecord>` 或 `Dictionary<string, ToolExecutionRecord>`；前者顺序简单，后者便于重试/去重，两者可组合（先存成字典，聚合时按 `ToolCalls` 顺序读取）。
- `_pendingProviderResponse`（可选）：当未来需要异步 Provider 调用时，可保留 Task 句柄，实现真正的可暂停调用链。

### 输入与输出契约
- 将 ConsoleTui 的输入改为**缓冲队列**或**拉取式委托**，例如向 LlmAgent 注入 `Func<string?> inputProvider`，或者提供 `TryDequeueUserInput(out string text)` 与 `EnqueueUserInput` 成对方法。
- 为 Agent 暴露事件或回调 `OnModelOutput`、`OnToolResults` 用于通知 UI 层展示内容，避免直接访问内部状态。
- `DoStep()` 返回布尔值或细化枚举（如 `StepResult.ProgressMade` / `StepResult.BlockedOnInput`），便于驱动循环。

### 工具执行流程
1. WaitingToolResults 状态中，取出第一个未执行的 `ToolCallRequest`。
2. 调用 `_toolExecutor.ExecuteAsync`，把返回的 `ToolExecutionRecord` 存入 `_pendingToolResults`。
3. 若执行失败且允许重试，可在记录里追加 retry 次数、错误信息。
4. 一旦数量匹配进入 ToolResultsReady，将 `_pendingToolResults` 投影为 `HistoryToolCallResult` 并调用 `_state.AppendToolResults`。

### 错误处理与恢复
- Provider 调用异常时保留在当前状态，并在历史中追加 `ToolResultsEntry` 或 metadata 记录错误（必要时也可追加伪造的 `ModelOutputEntry` 标记失败）。
- 工具执行异常只要写入 `_pendingToolResults` 即可，`ToolExecutionStatus.Failed` 会在聚合时被统计，支持后续重试。
- 建议为 `_pendingToolResults` 加入持久化模型（序列化有用字段 `ToolCallId`, `Status`, `Result`, `Metadata`），配合 `AgentState` 保存快照，实现进程重启后的继续执行。

### 序列化与持久化
- 引入 `AgentSnapshot`（包含 `AgentState`、`_pendingToolResults`、`CurrentState`、默认调用参数等），序列化为 JSON 即可在暂停后写入磁盘。
- 恢复时通过工厂方法：`LlmAgent.FromSnapshot(snapshot, dependencies)` 重建实例，重新注入 Provider、工具目录等运行时依赖。
- 若未来需要跨机器迁移，确保快照中不包含与本地资源绑定的引用（例如 TextReader/TextWriter）。

### 并发与线程安全
- 当前 ConsoleTui 单线程即可；如需后台循环（例如定时任务或自主动作），需用锁保护 `_pendingToolResults` 与输入缓冲。
- 封装 `DoStep()` 内部操作为不可重入（例如使用 `Interlocked.Exchange` 标记正在执行），避免多线程驱动导致状态紊乱。

### 遥测与调试
- 保留现有 `DebugUtil` 类别：`History` 记录状态切换、History 追加；`Provider` 跟踪模型调用；`Tools` 记录工具执行。
- 状态转换时可以统一打印：`DebugUtil.Print("History", $"[StateMachine] {previous}->{next}")`。
- 为未来可视化提供状态变化钩子，例如向外暴露一个事件流。

## 重构计划（AI Coder 分步骤参考）

1. **阶段 0：安全网与基线**
   - 记录当前 `AgentOrchestrator` 与 `ConsoleTui` 行为（交互脚本或快照日志），避免后续重构引入回归。
   - 为关键方法增补 `DebugUtil` 日志（例如 `InvokeProvider` 开始/结束），后续状态机迁移时可对比输出。
   - 预留最小单元测试雏形（如构造假的 provider 返回固定内容），为后续状态验证提供落脚点。

2. **阶段 1：内联 Orchestrator 但保持旧接口** _(2025-10-17 已完成)_
   - 在 `LlmAgent` 内新增 `InvokePipelineAsync`（沿用原 `AgentOrchestrator.InvokeAsync` 逻辑，当前为 `internal` 以支撑过渡期包装），`InvokeProvider` 调用它并维持现有返回类型，确保 UI 层无感知。
   - 临时让 `AgentOrchestrator` 退化为只调用 `LlmAgent.InvokePipelineAsync` 的薄包装，等整体迁移完成后再删除该类型。
   - 搬运 `NormalizeToolCalls`、`ModelOutputAccumulator`、`ToolResultMetadataHelper` 等辅助方法与日志语句，确认行为一致后运行阶段 0 的脚本对比输出。

3. **阶段 2：状态机骨架与过渡执行路径**
   - 新建 `AgentRunState` 枚举、`_pendingToolResults` 字段及 `DetermineState()`，但暂时只在 `InvokeProvider` 内部驱动 `DoStep()`，对 `ConsoleTui` 仍暴露旧的 `AppendUserInput`/`InvokeProvider` 组合。
   - 实现 `DoStep()` 的 switch 框架与最小分支（例如仅覆盖 `PendingInput`→模型调用、`WaitingToolResults`→执行单个工具），每次执行后返回 `StepOutcome`（如 `ProgressMade`、`BlockedOnInput`），并打印状态切换日志。
   - 为 `_pendingToolResults` 建立映射结构和清理流程，验证在原同步调用链里不会留下脏数据，再跑阶段 0 的回归脚本确认历史写入顺序未变。

4. **阶段 3：输入缓冲与 UI 适配**
   - 向 `LlmAgent` 注入 `TryDequeueUserInput`/`EnqueueUserInput` 或委托接口，使 `DoStep()` 可以在 `WaitingInput` 状态下主动拉取输入；临时实现可以由 `ConsoleTui` 透传现有 `TextReader`。
   - 在 `ConsoleTui.Run` 循环中改为：读取用户指令→入队→反复调用 `DoStep()` 直到返回 `BlockedOnInput`，并通过 Agent 暴露的事件/回调（例如 `OnModelOutput`) 更新 UI。
   - 保持 `/history`、`/reset` 等命令直接操作 `AgentState`，确认并发访问不会引发竞态（必要时引入简单锁）。

5. **阶段 4：工具执行状态完善**
   - 在 `WaitingToolResults` 分支按顺序调用 `_toolExecutor.ExecuteAsync`，将结果写入 `_pendingToolResults`，失败与跳过也要落盘。
   - 当计数吻合时进入 `ToolResultsReady` 分支，构造 `ToolResultsEntry` 写入历史并清空 `_pendingToolResults`，随后令 `DoStep()` 继续流转到 `PendingToolResults`。
   - 补充日志与单元测试覆盖失败/重试场景，确保状态判定与历史一致。

6. **阶段 5：接口收尾**
   - 当状态机驱动稳定后，删除 `AgentOrchestrator` 和 `LlmAgent.InvokeProvider` 的旧实现；`Program.cs` 直接构造新的 `LlmAgent` 并用循环驱动 `DoStep()`。
   - 清理不再使用的输入推送 API（如 `AppendUserInput`），同步更新 `ConsoleTui` 与其他调用方。

7. **阶段 6：测试与恢复能力**
   - 扩展单元测试覆盖：状态转换矩阵、工具执行失败路径、快照恢复（序列化 `_pendingToolResults` 与 `AgentState`）。
   - 设计最小“暂停/恢复”集成测试：执行业务半途保存快照，重新载入后确认可继续执行。

8. **阶段 7：文档与后续演进**
   - 更新开发文档、README，说明新的状态机流程、事件回调与快照格式。
   - 记录潜在后续任务（异步 Provider 调用、并行工具执行、多 Agent 协同等），并保留捕捉指标的 `DebugUtil` 类别供未来可视化。

完成以上步骤后，再考虑扩展性的功能（如后台任务或分布式恢复），确保主路径稳定再逐步迭代。
