# LiveContextProto

最小原型用于验证 Conversation History 重构 V2 的三段式流水线（AgentState → Context Projection → Provider Router）。

## 运行

- 配置 Anthropic API Key（必需），可追加模型/规格：

```powershell
$env:ANTHROPIC_API_KEY="sk-..."
$env:ANTHROPIC_MODEL="claude-3-5-sonnet-20241022"   # 可选，默认同此值
$env:ANTHROPIC_SPEC="messages-v1"                   # 可选
```

- 可选：设置调试类别以观察日志

```powershell
$env:ATELIA_DEBUG_CATEGORIES="History,Provider,Tools"
```

- 启动控制台

```powershell
dotnet run --project prototypes/LiveContextProto/LiveContextProto.csproj
```

## 控制台命令
- 直接输入文本：触发 Anthropic Provider 调用，并将输出/工具结果回写至历史。
- /history：打印当前上下文（包含系统指令、用户输入、助手输出、工具结果与 Window 装饰），并递归展示每条消息的 Metadata 摘要（例如耗时、失败计数、per-call 诊断）。
- /reset：清空历史并重置记忆笔记。
- /notebook view|set <内容>|clear：查看/设置/清空记忆笔记；下次渲染会以 Window 装饰附加到最新输入或工具结果。
- /exit：退出。

> 小贴士：命令输出的 Metadata 会跳过重复的 `token_usage` 字段，若需更详细的调试日志，可同时开启 `ATELIA_DEBUG_CATEGORIES=History,Provider,Tools`。

想要继续使用脚本化 stub、示例工具或 `/demo` 命令，请改用伴随项目 `prototypes/LiveContextProto.Demo`。

## 结构
- `State/`：AgentState 与 HistoryEntry 分层（ModelInput/ModelOutput/ToolResults），`RenderLiveContext()` 负责上下文投影与 Window 装饰。
- `Provider/`：
  - `IProviderClient`：统一模型调用接口（返回 `IAsyncEnumerable<ModelOutputDelta>`）。
  - `ProviderRouter`：按策略选择 Provider，并生成 `ModelInvocationDescriptor`。
  - `ModelOutputAccumulator`：聚合 delta → `ModelOutputEntry`/`ToolResultsEntry`，并回填 `TokenUsage` 元数据。
  - `Anthropic/AnthropicProviderClient`：对接 Anthropic Messages API 的流式客户端实现。
- `Tools/`：
  - `ToolExecutor`：根据 `ToolCallRequest` 查找注册的工具适配器，记录耗时并生成 `ToolCallResult`。
  - `ToolResultMetadataHelper`：为工具结果追加统计信息（调用数量、失败数量、耗时等）。

## 测试

```powershell
dotnet test prototypes/LiveContextProto.Tests/Atelia.LiveContextProto.Tests.csproj
```

覆盖点：
- 时间戳注入与上下文顺序
- Window 装饰只应用于最新一条可装饰消息
- 增量聚合（内容/工具调用/TokenUsage）与错误路径（仅 ExecuteError）
- ToolExecutor 自动执行模型声明的工具调用，并将耗时/失败信息写入 Metadata
