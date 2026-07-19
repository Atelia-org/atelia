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

已确认边界：

- 公共 Completion 层负责 `connections.json` 的加载、环境变量覆盖、默认连接推导和基础校验。
- 公共 `CompletionConnectionRegistry` 只做真实 client 构造与缓存；不内置 Galatea / FamilyChat 的 turn 行为、UI 策略或 think repair。
- Galatea / FamilyChat 当前的 think repair / `<think>` prefill 实验功能可直接删除。实测效果不好，且只针对特定模型偶发异常，不再作为公共抽象或应用层长期能力保留。
- `kind` 与 `completionSurfaceId` 的完整合法矩阵暂不固定。厂商、本地推理框架和 API 中转服务差异太大，第一版只做基础字段校验；无效组合允许在创建 client 或实际调用时失败。
- 日志与 report 一律不得保存真实 `apiKey`。即使连接配置使用 inline `apiKey`，call log 中也只能记录是否存在 key、env var 名称或 redacted 标记。

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

加载时由 Completion 层解析 env override。解析规则：

- `baseAddressEnv` / `apiKeyEnv` 非空时，从对应环境变量读取覆盖值。
- env var 未设置或为空时 fail fast，避免悄悄退回错误 endpoint 或空 key。
- `defaultConnectionId` 为空时默认使用 `connections[0].id`。
- 基础校验覆盖：连接列表非空、id 非空且不重复、displayName/kind/modelId/completionSurfaceId/baseAddress 非空、defaultConnectionId 存在。
- `apiKey` 是否必填不在公共层强制。部分本地服务不需要 key；具体 provider client 负责把空白 key 规范化为无 key。
- 日志中不要写入 `apiKey` 的真实值。

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

建议补充一个 loader 类型，避免每个应用继续复制 `connections.json` 读取、env override 和校验逻辑：

```csharp
public static class CompletionConnectionConfigLoader {
    public static CompletionConnectionsFileConfig LoadFile(string path);
}
```

`LoadFile` 返回已经解析 env override、补齐默认连接、完成基础校验后的配置对象。第一轮可以只让 Backtest CLI 使用这些公共类型；Galatea / FamilyChat 后续再迁移，避免一次改动跨太多应用层文件。

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

边界：`LoggingCompletionClient` 是语义层日志，不是 provider wire log。它记录 canonical `CompletionRequest`、聚合后的 `CompletionResult` / exception，以及 backtest epoch metadata；它不承诺保存 provider-native HTTP request / response 的完整字节级内容。需要 HTTP 层法证或 replay 时，继续复用 / 扩展 `CompletionHttpTransportFactory` 现有 golden log 能力。

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
- 日志不应保存真实 `apiKey`；connection snapshot 中不要出现 inline key 值。
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
- 每次 threshold 触发一个 epoch，分析窗口为 split point 之前、即将滑出上下文窗口的 prefix。
- 每个 epoch 写一行 JSONL report，并写一个完整 call log JSON。

## 7. 施工顺序

推荐小步推进，第一批先解决公共 connection 能力与 Backtest CLI 的最小接入：

1. 在 `Atelia.Completion` 中添加公共 connection config / loader / factory / registry。
2. 给 loader 添加基础验证：默认连接推导、重复 id、必填字段、env override 缺失报错、secret redaction 边界。
3. 给 `ChatSession.BacktestCli` 增加 `Atelia.Completion` 引用。
4. 给 `ChatSession.BacktestCli` 加 `llm-smoke --connections <path> [--connection <id>]`，只做最小真实调用，验证公共 loader/factory/registry 可用。
5. 添加 `LoggingCompletionClient`，先确保每次调用能落盘语义层 request / response / exception，且不保存 `apiKey`。
6. 先复用 `IMemoryBlockMaintainer` / `CompletionMemoryBlockMaintainer` 的 block-transform 契约，由 backtest runner 构造 sliding-out prefix `RecentHistorySlice`。
7. 实现 `replay-rolling-summary`，生成 JSONL report + call log dir。
8. 用真实 Galatea export 做第一次 prompt 调试。
9. 再迁移 Galatea / FamilyChat 到公共 `CompletionConnectionRegistry`。

首批可处理的弃用、重构、迁移、bug fix：

- **可立即做**：新增公共 Completion connection loader/factory/registry；Backtest CLI 引用 Completion；实现 `llm-smoke`；实现语义层 `LoggingCompletionClient` 的第一版；确保 call log 不泄露 `apiKey`；让 rolling summary backtest 复用 block maintainer 契约，并显式传入 sliding-out prefix window。
- **可顺手删**：Galatea / FamilyChat 的 think repair / `<think>` prefill 相关类型、UI 开关、turn options 字段和 decorator。删除时应作为单独提交/单独变更批次，避免和公共 connection 抽取混在一起。
- **稍后迁移**：Galatea / FamilyChat 的 connection config、factory、registry 切到公共 Completion 类型。迁移前先让 Backtest CLI 跑通公共路径，降低一次性改动范围。
- **暂不做**：固定 `kind` 与 `completionSurfaceId` 的完整合法矩阵；把 call log 扩展成 provider wire log；强制禁止 inline `apiKey`。inline key 后续应迁移到 env var，但第一批只保证日志不泄漏。

第一批验收标准：

- Backtest CLI 能通过 `llm-smoke --connections <path>` 从公共 loader 读取连接、选择默认连接并完成一次最小 completion 调用。
- `--connection <id>` 能选择非默认连接；未知 id 报清晰错误。
- env override 缺失、重复 connection id、空 `baseAddress` 等基础配置错误在加载阶段 fail fast。
- call log 文件包含 request / response 或 exception、elapsed、connection snapshot、context metadata，但不包含真实 `apiKey`。
- Galatea / FamilyChat 迁移前，原应用仍可继续使用旧 connection 代码；think repair 删除作为独立变更处理。

## 8. 未决问题

- `LoggingCompletionClient` 的 metadata 注入方式：构造时固定 context，还是每次调用通过 async-local / request wrapper 传入？第一版 CLI 可构造 per-epoch decorator，简单优先。
- `CompletionRequest` / response 是否已有稳定 JSON 形状？若没有，先记录 canonical object 的序列化结果，后续再固定 schema。
- token estimator 是否继续使用 CLI 内部近似估算？第一版可继续用近似值；如果 backtest 依赖更精确触发点，再把 `ChatSessionTokenEstimator` 暴露为 public helper。
- call log 目录是否允许覆盖？建议默认要求目录不存在或为空；调试时可加 `--overwrite-call-log-dir`。

已暂缓的问题：

- `kind` 与 `completionSurfaceId` 的完整合法矩阵暂不维护；只做基础字段校验。
- inline `apiKey` 暂不禁止；第一批只要求日志和 report 不泄露真实值。
- provider wire log 不纳入 `LoggingCompletionClient` 第一版；需要时走 Completion HTTP golden log。
