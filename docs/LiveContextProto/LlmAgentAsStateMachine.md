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
 - 行为: 调用外界传入的Handler(当前原型中应来自[ConsoleTui])，获取外界输入文本(此原型中为控制台输入的文本)。如果拿到了非空非空白字符串，则创建一个[ModelOutputEntry]并添加到[AgentState.History]末尾，这将转换到**PendingInput**状态。其他情况(拿到null或空白字符串)，则不改变状态继续等待。

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
