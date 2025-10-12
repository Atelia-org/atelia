# LiveContextProto

最小原型用于验证 Conversation History 重构 V2 的三段式流水线（AgentState → Context Projection → Provider Router）。

## 运行

- 可选：设置调试类别以观察日志

```powershell
$env:ATELIA_DEBUG_CATEGORIES="History,Provider"
```

- 启动控制台

```powershell
dotnet run --project prototypes/LiveContextProto/LiveContextProto.csproj
```

## 控制台命令
- 直接输入文本：通过 Stub Provider 触发一次模型调用，并将输出/工具结果回写至历史。
- /history：打印当前上下文（包含系统指令、用户输入、助手输出、工具结果与 LiveScreen 装饰）。
- /reset：清空历史与 LiveInfo。
- /notebook view|set <内容>|clear：查看/设置/清空记忆笔记；下次渲染会以 LiveScreen 装饰附加到最新输入或工具结果。
- /liveinfo list|set <节名称> <内容>|clear <节名称>：查看或维护附加 LiveInfo 节（例如 Planner 摘要），LiveScreen 会在 Notebook 后附加展示。
- /stub <script> [文本]：使用指定脚本触发一次调用；未提供文本时直接使用当前上下文。
  - 内置脚本：default、fail、multi（位于 `Provider/StubScripts`）。
- /tool sample|fail：追加模拟工具结果（与 Provider 独立）。
- /demo conversation：构造一段示例对话与 Notebook 快照。
- /exit：退出。

## 结构
- `State/`：AgentState 与 HistoryEntry 分层（ModelInput/ModelOutput/ToolResults），`RenderLiveContext()` 负责上下文投影与 LiveScreen 装饰。
- `Provider/`：
  - `IProviderClient`：统一模型调用接口（返回 `IAsyncEnumerable<ModelOutputDelta>`）。
  - `ProviderRouter`：按策略选择 Provider，并生成 `ModelInvocationDescriptor`。
  - `ModelOutputAccumulator`：聚合 delta → `ModelOutputEntry`/`ToolResultsEntry`，并回填 `TokenUsage` 元数据。
  - `Stub/StubProviderClient`：从 JSON 脚本产生增量，支持占位符 `{{last_user_input}}`。

## 测试

```powershell
dotnet test prototypes/LiveContextProto.Tests/Atelia.LiveContextProto.Tests.csproj
```

覆盖点：
- 时间戳注入与上下文顺序
- LiveScreen 装饰只应用于最新一条可装饰消息
- 增量聚合（内容/工具调用/TokenUsage）与错误路径（仅 ExecuteError）
