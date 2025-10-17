# LiveContextProto 状态机重构 · 阶段 0 记录

## 今日目标
- 捕获当前控制台交互的行为快照，为后续对比提供基线。
- 在关键调用点补充 `DebugUtil` 日志，便于观察状态转换前后的上下文。
- 创建最小的测试工程骨架，为未来的状态机行为测试预留落脚点。

## 运行快照
以下输出采集自 `dotnet run --project prototypes/LiveContextProto/LiveContextProto.csproj` 的初次启动。

```
17:06:58.568 [DBG History] LiveContextProto bootstrap starting
17:06:58.595 [DBG History] AgentState initialized with instruction length=100
17:06:58.621 [DBG Provider] [Anthropic] Client initialized base=http://localhost:4000/anthropic/, version=2023-06-01
17:06:58.622 [DBG Provider] ProviderRouter initialized with routes=1
17:06:58.630 [DBG Tools] ToolExecutor initialized handlerCount=1
=== LiveContextProto Anthropic Runner ===
命令：/history 查看上下文，/reset 清空，/notebook view|set，/exit 退出。

输入任意文本将调用当前配置的模型，并把输出回写到历史。

[system] You are LiveContextProto, a placeholder agent validating the Conversation History refactor skeleton.

17:06:58.632 [DBG History] ConsoleTui intro displayed
user>
```

> 由于缺少本地 Anthropic 代理，输入后会触发 `HttpRequestException`；保留该行为用于后续对比。

## 新增日志钩子
- `LlmAgent.InvokeProvider`：打印开始/结束信息，包含策略 ID、历史长度、输出段数和工具调用数量。
- `ConsoleTui.AppendUserInput`：记录输入长度，方便定位用户交互事件。
- `ConsoleTui.InvokeProviderAndDisplay`：在调用前后输出策略信息，并在失败时串联异常消息。

这些日志仍受 `ATELIA_DEBUG_CATEGORIES` 控制，可通过 `Provider` 与 `History` 类别查看完整链路。

## 测试骨架
- 新增 `tests/Atelia.LiveContextProto.Tests` 项目（xUnit，net9.0），并引用 `prototypes/LiveContextProto`。
- 初步编写 `BaselineSnapshotTests`，验证 `AgentState.AppendModelInput` 会将条目写入历史，为未来扩展状态机测试提供模板。

## 后续建议
- 录制含失败返回的完整交互脚本，并保存到本目录以跟踪重构期间的输出差异。
- 根据新的日志输出更新调试指南，确保在切换到状态机后仍能比对上下文。
- 扩展测试桩，构造假 `IProviderClient` 以模拟多轮调用，逐步覆盖状态转换矩阵。

## 阶段 1：内联 Orchestrator 但保持旧接口

### 完成事项
- 将 `AgentOrchestrator.InvokeAsync` 的核心逻辑迁移至 `LlmAgent.InvokePipelineAsync`，复用原有 `NormalizeToolCalls`、工具执行与日志语句，保持调用流程与日志输出顺序稳定。
- 为 `LlmAgent` 新增对 `ProviderRouter` 的直接依赖，使其能够独立解析调用计划并驱动模型请求。
- 将 `AgentOrchestrator` 精简为面向 `LlmAgent` 的薄包装，为后续完全移除该类型保留过渡期接口。
- 更新 `Program.cs` 以使用新的 `LlmAgent` 构造函数，减少初始化链路复杂度。

### 验证
- 运行 `dotnet test tests/Atelia.LiveContextProto.Tests/Atelia.LiveContextProto.Tests.csproj`，结果通过（见日志时间戳 17:43:52）。

### 备注与后续
- `InvokePipelineAsync` 暂设为 `internal` 以支撑过渡期包装，待状态机骨架完成后可重新收紧可见性。
- 下一阶段将聚焦于注入状态枚举、挂接 `_pendingToolResults` 并搭起 `DoStep()` 骨架。

## 阶段 2：状态机骨架与过渡执行路径

### 完成事项
- 在 `LlmAgent` 内新增 `AgentRunState` 枚举与 `_pendingToolResults` 字典，统一记录未聚合的工具执行结果，并引入 `MaxStepsPerInvocation` 作为安全护栏。
- 重写 `InvokePipelineAsync` 为逐步驱动模式：通过新的 `DetermineState()` 与 `DoStep()` 框架分解模型调用、工具执行与结果聚合，每次成功推进都会记录到 `RunLoopContext`。
- 为模型调用（`PendingInput` / `PendingToolResults`）、工具执行（`WaitingToolResults`）与结果汇总（`ToolResultsReady`）分别实现处理函数，并在聚合阶段复用原有 `ToolResultMetadataHelper` 逻辑。
- 新增 `StateMachine` 日志类别，输出状态切换、工具执行、工具结果写入等调试信息，以便后续比对。

### 验证
- 运行 `dotnet test tests/Atelia.LiveContextProto.Tests/Atelia.LiveContextProto.Tests.csproj`（18:01:17，本地 Windows 环境），现有测试通过且日志显示新的状态机流水线未破坏历史写入顺序。

### 待办与后续
- Console UI 仍通过 `AppendUserInput` 推送输入；下一阶段需要改造为缓冲/拉取模式并用 `DoStep()` 驱动循环。
- `_pendingToolResults` 目前只支持顺序执行与聚合，后续可扩展失败重试策略与快照持久化格式。

## 阶段 3：输入缓冲与 UI 适配

### 完成事项
- 在 `LlmAgent` 内引入 `ConcurrentQueue<PendingUserInput>`，提供 `EnqueueUserInput` 与 `DoStepAsync`，由状态机自行拉取待处理输入，并通过 `AgentStepResult` 将新增的 `ModelInputEntry`、`ModelOutputEntry`、`ToolResultsEntry` 回传给调用方。
- 新增 `_defaultInvocationOptions`、`_activeInvocationOptions` 与 `_activeRunLoop`，并实现 `EnsureRunLoopContext()`，使模型调用、工具执行与结果聚合都能在可恢复的运行上下文内推进。
- 扩展 `StepOutcome` / `AgentStepResult` 结构体，统一承载输入、输出与工具结果；补充 `ResetInvocation`、`LogStateIfChanged` 等辅助方法，保证状态切换日志与清理逻辑一致。
- 重写 `ConsoleTui` 输入流程，将“推送式”调用改为“入队 + 水位驱动”，循环调用 `DoStepAsync()` 直至状态机阻塞于新的用户输入，并拆分输出展示方法以复用原有渲染逻辑。
- 更新 `Program.cs` 以向 `LlmAgent` 注入默认的 `LlmInvocationOptions`，并保持 `ConsoleTui` 的构造函数不变。

### 验证
- 运行 `dotnet test tests/Atelia.LiveContextProto.Tests/Atelia.LiveContextProto.Tests.csproj`（18:42:39，本地 Windows 环境），现有测试通过，新增日志显示 `DoStepAsync` 驱动流程正常落盘。

### 后续建议
- 为 `ConsoleTui` 引入更细粒度的提示（如多轮输出之间的分隔），便于观察状态机的分步推进；同时可以考虑在 `AgentStepResult` 中补充工具执行失败时的错误提示文本。
- 计划中的阶段 4/5 可继续清理 `AgentOrchestrator` 旧包装并探索输入队列的持久化格式，为跨进程恢复做准备。

## 阶段 4：工具执行状态完善

### 完成事项
- 在 `LlmAgent` 状态机路径中复核 `WaitingToolResults` 与 `ToolResultsReady` 分支，确认单个调用仅调度一条待执行工具，并在聚合阶段清空 `_pendingToolResults`。
- 新增 `AgentStateMachineToolExecutionTests`，覆盖一次完整的工具调用生命周期（成功路径 + 模型回合续跑）与失败路径（`ExecuteError` 生成、失败计数统计）。
- 校验工具元数据落盘：测试中断言 `tool_call_count`、`tool_failed_count`、`per_call_metadata` 等字段写入正确，并验证 `HistoryToolCallResult` 与 `ToolResultsEntry.ExecuteError` 的内容生成。

### 验证
- 运行 `dotnet test tests/Atelia.LiveContextProto.Tests/Atelia.LiveContextProto.Tests.csproj`（18:56:37，本地 Windows 环境），新增用例全部通过并伴随状态机日志验证实际执行顺序。

### 备注与后续
- 下一阶段可着手删除残余的 `AgentOrchestrator` 包装与旧的 `InvokeProvider` 推模式 API，收敛对外接口。
- `_pendingToolResults` 持久化与快照格式仍待设计，计划在快照/恢复能力落地时一并处理。

## 阶段 5：接口收尾

### 完成事项
- 移除 `LlmAgent.InvokeProvider` 与对应的 `AgentInvocationOutcome` 结构，统一对外暴露 `EnqueueUserInput` + `DoStepAsync` 拉模式。
- 清空 `AgentOrchestrator` 中的旧包装逻辑，并在 `LlmAgent` 内内联 `AgentInvocationResult` 定义，避免跨文件耦合。
- 将 Console UI 的帮助方法更名为 `ProcessUserInput`，彻底退出历史上的 `AppendUserInput` 推送语义。

### 验证
- 运行 `dotnet test tests/Atelia.LiveContextProto.Tests/Atelia.LiveContextProto.Tests.csproj`（19:08:12，本地 Windows 环境），确保接口裁剪后编译与测试通过。

### 备注与后续
- 下一阶段聚焦 `_pendingToolResults` 快照设计与跨进程恢复，视情况将占位的 `AgentOrchestrator.cs` 文件从工程中完全移除。
- Console UI 仍以单线程轮询驱动，可在后续阶段探索事件/回调型 UI 适配器以支持图形界面或远程驱动。
