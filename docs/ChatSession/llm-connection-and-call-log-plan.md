# LLM Connection 与 Call Log 基础设施设计备忘

> 状态：设计备忘。用途是支撑 `ChatSession.BacktestCli replay-rolling-summary`，并为后续 Galatea / FamilyChat 复用统一的 LLM connection 管理与完整调用日志打基础。

## 1. 动机

`memory-backtest-cli-plan.md` 的第二阶段需要接入真实 LLM API，反复调试 rolling summary prompt。只跑一次 summary 不够；必须保存每次调用的完整输入、输出、耗时、异常、prompt 版本和关联 replay epoch，才能比较不同 analyzer / maintainer 策略。

当前 Galatea 和 FamilyChat 已经各自有一套几乎相同的 connection 配置、factory、registry：

- `GalateaConnectionConfig` / `GalateaConnectionRegistry` / `DefaultGalateaCompletionClientFactory`
- `FamilyChatConnectionConfig` / `FamilyChatConnectionRegistry` / `DefaultFamilyChatCompletionClientFactory`

这说明 connection 管理已经是公共 Completion 层能力，而不是某个应用自己的临时逻辑。

## 2. 边界判断

推荐把公共 connection 管理放在 `prototypes/Completion`，而不是 `prototypes/ChatSession`。

理由：

- `ChatSession` 的职责是会话历史、compaction、MemoryPack、maintainer 编排。
- “从配置构造真实 `ICompletionClient`”属于 Completion client 的创建问题。
- Backtest CLI、Galatea、FamilyChat 都是 `ICompletionClient` 消费者。
- ChatSession 继续只依赖 `ICompletionClient`，更利于测试、mock、离线 replay 和后续 agent sandbox。

目标分层：

```text
Atelia.Completion
  CompletionConnectionConfig
  CompletionConnectionsFileConfig
  DefaultCompletionClientFactory
  CompletionConnectionRegistry
  LoggingCompletionClient

Atelia.ChatSession
  MemoryPack / maintainer substrate
  ChatSessionEngine / compaction / replay reader
  只接受 ICompletionClient，不读取 connections.json

ChatSession.BacktestCli
  读取 --connections
  选择 --connection
  包装 LoggingCompletionClient
  运行 replay-rolling-summary
```

## 3. 公共 connections.json 形状

建议公共配置形状基本复用 Galatea / FamilyChat 当前字段：

```json
{
  "defaultConnectionId": "local-deepseek",
  "connections": [
    {
      "id": "local-deepseek",
      "displayName": "Local DeepSeek",
      "kind": "openai-chat",
      "modelId": "deepseek-v4",
      "completionSurfaceId": "openai-chat/deepseek-v4",
      "baseAddress": "http://localhost:8888/",
      "apiKey": null,
      "baseAddressEnv": "DEEPSEEK_BASE_URL",
      "apiKeyEnv": "DEEPSEEK_API_KEY"
    }
  ]
}
```

字段含义：

- `id`：稳定连接 id，用于 CLI 参数和 UI 选择。
- `displayName`：人类可读名称。
- `kind`：后端族，首批支持 `openai-chat` / `openai-responses` / `anthropic`。
- `modelId`：模型 id，传给 completion request / client。
- `completionSurfaceId`：surface / dialect id，例如 `openai-chat/deepseek-v4`。
- `baseAddress`：默认 endpoint。
- `apiKey`：可选 inline key；不推荐提交真实 secret。
- `baseAddressEnv`：环境变量覆盖 endpoint。
- `apiKeyEnv`：环境变量覆盖 key。

加载时应解析 env override，但日志中不要写入 `apiKey` 的真实值。

## 4. Completion 层建议类型

```csharp
public sealed record CompletionConnectionsFileConfig(
    IReadOnlyList<CompletionConnectionConfig> Connections,
    string? DefaultConnectionId = null
);

public sealed record CompletionConnectionConfig(
    string Id,
    string DisplayName,
    string Kind,
    string ModelId,
    string CompletionSurfaceId,
    string BaseAddress,
    string? ApiKey = null,
    string? BaseAddressEnv = null,
    string? ApiKeyEnv = null
);
```

```csharp
public interface ICompletionClientFactory {
    ICompletionClient Create(CompletionConnectionConfig connection);
}
```

```csharp
public sealed class CompletionConnectionRegistry : IDisposable {
    public IReadOnlyList<CompletionConnectionConfig> Connections { get; }
    public string DefaultConnectionId { get; }
    public CompletionConnectionConfig Resolve(string? requestedId);
    public ICompletionClient GetClient(string connectionId);
}
```

第一轮可以只让 Backtest CLI 使用这些类型；Galatea / FamilyChat 后续再迁移，避免一次改动跨太多应用层文件。

## 5. LoggingCompletionClient

Backtest 需要一个装饰器包住真实 client：

```csharp
public sealed class LoggingCompletionClient : ICompletionClient
```

职责：

- 每次 completion 调用分配递增 call id。
- 捕获完整 canonical request。
- 捕获 response 或 exception。
- 记录开始时间、结束时间、耗时。
- 记录 connection id、model id、api spec id、completion surface id。
- 接收 backtest epoch metadata，例如 event ordinal、epoch index、maintainer id、target block。
- 将日志写入 `--call-log-dir`。

推荐每次调用一个 JSON 文件：

```text
gitignore/backtest/rolling-summary-calls/
  0001.json
  0002.json
  0003.json
```

单文件形状草案：

```json
{
  "schema": "atelia.completion.call-log.v1",
  "callId": 1,
  "timestampUtc": "2026-07-20T00:00:00Z",
  "elapsedMs": 12345,
  "connection": {
    "id": "local-deepseek",
    "kind": "openai-chat",
    "modelId": "deepseek-v4",
    "completionSurfaceId": "openai-chat/deepseek-v4",
    "baseAddress": "http://localhost:8888/"
  },
  "context": {
    "command": "replay-rolling-summary",
    "epochIndex": 3,
    "eventOrdinal": 57,
    "maintainerId": "rolling-summary",
    "targetCarrier": "System",
    "targetBlockId": "session.rolling-summary"
  },
  "request": {},
  "response": {},
  "exception": null
}
```

注意：

- 日志应保存 prompt 文本快照或 prompt 文件 hash，便于复现实验。
- 日志不应保存真实 `apiKey`。
- JSONL backtest report 只引用 call log 文件路径，不内嵌完整 request / response。

## 6. Backtest CLI 接入参数

`replay-rolling-summary` 建议参数：

```bash
dotnet run --project prototypes/ChatSession.BacktestCli -- replay-rolling-summary \
  --input <legacy-export.json> \
  --threshold-tokens 12000 \
  --connections <connections.json> \
  --connection <connection-id> \
  --system-prompt <summary-system.md> \
  --prompt <summary-user.md> \
  --output gitignore/backtest/rolling-summary.jsonl \
  --call-log-dir gitignore/backtest/rolling-summary-calls
```

默认策略：

- replay mode 使用 `ignore-original-compaction`。
- target block 初版使用 `MemoryPackCarrier.System / session.rolling-summary`。
- 每次 threshold 触发一个 epoch。
- 每个 epoch 写一行 JSONL report，并写一个完整 call log JSON。

## 7. 施工顺序

推荐小步推进：

1. 在 `Atelia.Completion` 中添加公共 connection config / factory / registry。
2. 给 `ChatSession.BacktestCli` 加 `--connections` / `--connection`，先实现一个 `llm-smoke` 或在 `replay-rolling-summary` 中构造 client。
3. 添加 `LoggingCompletionClient`，先确保每次调用能落盘 request / response / exception。
4. 实现 `RollingSummaryMaintainer` 专用版本，先不强行复用 `CompletionMemoryBlockMaintainer`。
5. 实现 `replay-rolling-summary`，生成 JSONL report + call log dir。
6. 用真实 Galatea export 做第一次 prompt 调试。
7. 再评估 Galatea / FamilyChat 是否迁移到公共 `CompletionConnectionRegistry`。

## 8. 未决问题

- `LoggingCompletionClient` 的 metadata 注入方式：构造时固定 context，还是每次调用通过 async-local / request wrapper 传入？第一版 CLI 可构造 per-epoch decorator，简单优先。
- `CompletionRequest` / response 是否已有稳定 JSON 形状？若没有，先记录 canonical object 的序列化结果，后续再固定 schema。
- token estimator 是否继续使用 CLI 内部近似估算？第一版可继续用近似值；如果 backtest 依赖更精确触发点，再把 `ChatSessionTokenEstimator` 暴露为 public helper。
- call log 目录是否允许覆盖？建议默认要求目录不存在或为空；调试时可加 `--overwrite-call-log-dir`。
