当前状态来源的唯一真相是AgentState.History的最后一条消息与缓存的工具结果列表。

WaitingInput:
    主要策略变化点。是否进入新循环，进入新循环的条件，新输入信息的来源，是否触发附加逻辑。

PendingInput:
    固定调用模型。扩展点，刷新ModelInputEntry中的Live信息，比如时间戳或外部库存。
PendingToolResults:
    固定调用模型。扩展点，刷新ToolResultsEntry中的Live信息，比如时间戳或外部库存。

WaitingToolResults: 固定执行工具。
ToolResultsReady: 固定创建ToolResultsEntry


AgentEngine
- History
- StateMachine
- 工具结果缓存
- 工具调用
- 工具与APP配置
- RenderLiveContext
- ProviderClient调用

AgentEngine外部:
- LlmProfile动态切换
- 工具动态添加移除
- IApp动态添加移除
- 处理WaitingInput状态的业务逻辑

命名空间分层规划

产品：
- Atelia.Agent
  - CharacterAgent

组件库:
- Atelia.Agent.Apps
  - MemoryNotebookApp
  - TextEditor
- Atelia.Agent.SubAgents
  - RecapMaintainer
  - Daemons / Analyzers

引擎外观:
- Atelia.Agent.Core
  - AgentEngine
  - ITool
  - IApp
  - ToolAttribute, ToolParamAttribute, MethodToolWrapper
  - LlmProfile

引擎实现:
- Atelia.Agent.Core.History
  - AgentState
  - HistoryEntry
- Atelia.Agent.Core.Tool
  - ToolExecutor

LlmProviders抽象:
- Atelia.Agent.Core.Context
  - IContextMessage, LlmRequest
  - IProviderClient

LlmProviders实现:
- Atelia.LlmProviders
  - Anthropic: Messages V1 API
  - OpenAI: V1 API。不包括Responses API。
  - Gemini
