# MemoPod DeepSeek V4 Flash candidate evidence

状态：**NotRun**  
日期：2026-08-20  
source baseline：`6f2000d6a65d`（WP-06/PRIV-01之后；本文随Track C2
provider-free source slice提交）

本文只登记MemoPod Track C2候选route的provider-free source证据、官方协议事实与未来authenticated
canary门禁。没有执行真实HTTP，没有读取现存secret，也没有形成GO/No-Go、价格、质量或cache命中结论。

## 1. Candidate route lock

live runner只接受strict Completion connections V1中的一个exact ID，并在构造client前要求：

| Field | Required value |
|:--|:--|
| `kind` | `openai-chat` |
| `modelId` | `deepseek-v4-flash` |
| `completionSurfaceId` | `openai-chat/deepseek-v4` |
| `reasoningEffort` | `disabled` |
| resolved origin | `https://api.deepseek.com/`，无userinfo、alternate port、path、query或fragment |
| credential source | nonblank `apiKeyEnv`，且其resolved value nonblank |

lookup固定为`CompletionConnectionRegistry.TryGet(exactId)`成功后才允许`GetClient(exactId)`；不调用
`Resolve`，未知ID不回退default。production candidate使用`DefaultCompletionClientFactory`直接构造owned
provider client，不包`LoggingCompletionClient`或HTTP exchange sink。

## 2. Official facts verified before execution

DeepSeek官方Chat Completion文档当前列出：

- OpenAI Chat endpoint支持model `deepseek-v4-flash`；`thinking.type`接受`enabled|disabled`；
- `stream_options.include_usage=true`会在`[DONE]`前产生一个`choices: []`的usage chunk；
- named `tool_choice`形状可强制一个具体function；
- usage含`prompt_tokens`、`completion_tokens`、`prompt_cache_hit_tokens`与
  `prompt_cache_miss_tokens`，并声明`prompt_tokens = hit + miss`。

来源：

- [DeepSeek Create Chat Completion](https://api-docs.deepseek.com/api/create-chat-completion)
- [DeepSeek Context Caching](https://api-docs.deepseek.com/guides/kv_cache)
- [DeepSeek Tool Calls](https://api-docs.deepseek.com/guides/tool_calls)

当前同一官方request schema没有列出`parallel_tool_calls`。本仓converter在
`allowParallelToolCalls=false`时会发出`parallel_tool_calls:false`；provider-free golden只能证明local
wire，不能证明live route接受该字段。它是authenticated canary的显式compatibility gate。

## 3. Provider-free source evidence

以下均不触网：

- Frozen MemoPod → `RecallAsync` → real `DeepSeekV4ChatClient` / OpenAI Chat converter → fake HTTP/SSE →
  C1 hit/miss parser → ID validation/hydration；
- request golden固定`thinking.type=disabled`、`stream_options.include_usage=true`、required named
  `recall_memos`与current `parallel_tool_calls:false`；
- fake terminal usage `prompt=100, hit=80, miss=20, completion=7`被规范化为
  `UncachedInputTokens=20`、`CacheReadInputTokens=80`、`CacheCreationInputTokens=null`、
  `OutputTokens=7`、cache observation `Partial`；
- exact connection miss不fallback，route policy失败发生在client construction之前；
- fake-first CLI不调用live safety gate、config loader或client factory；
- 1–8 repeated query files是1–8次显式调用，无retry；bounds覆盖prompt bytes、max tokens、delay与case
  label；
- content-free evidence serializer显式保留nullable usage/selection字段；secret、endpoint、query与exception
  canary不进入stdout、stderr或evidence。

这些测试证明source wiring与privacy gates，不证明DeepSeek服务接受request、产生cache命中或选择质量合格。

## 4. Content-free JSONL contract

每个已经进入capturing client的调用最多且恰好输出一行
`atelia.memo-pod.deepseek-v4-flash-candidate.v1`：

```text
schema, caseLabel, callIndex,
connectionId, kind, modelId, completionSurfaceId, clientName, apiSpecId,
podId, activeMemoCount,
frozenPromptSha256, frozenPromptUtf8Bytes, queryUtf8Bytes,
maxResults, maxPromptUtf8Bytes, maxTokens, delayMilliseconds, elapsedMilliseconds,
outcome,
promptCacheRequestStatus, promptCacheSupportStatus, promptCacheObservationStatus,
uncachedInputTokens?, cacheCreationInputTokens?, cacheReadInputTokens?, outputTokens?,
selectedCount?, selectedIds?
```

nullable token/selection字段在JSON中显式保留`null`，不以零或空集伪装权威观测。禁止字段包括Topic、Memo
exact text、query、system prompt、raw request/response、CLI args、diagnostics、exception、endpoint配置与secret。
prompt bytes/hash来自Recall传给client的single shared Frozen `ObservationMessage`；wrapper只保存bytes/hash，
不保存request或正文，并校验hash等于`MemoRecallResult.FrozenPromptSha256`。

## 5. Authenticated canary gate — still NotRun

未来执行必须同时满足：

1. 使用disposable synthetic Pod与disposable cwd，不读取用户Pod；
2. 使用Release build；在任何Completion config/client materialization前，显式设置
   `ATELIA_DEBUG_FILE_LEVEL=ERROR`和`ATELIA_DEBUG_CONSOLE_LEVEL=ERROR`；
3. operator明确授权读取一个candidate-only `apiKeyEnv`并发起真实调用；
4. 先验证single call接受`thinking.type=disabled`、named tool choice、`stream_options.include_usage`与
   `parallel_tool_calls:false`；任何400/协议失败都保持NotRun/Failed，不删字段重试；
5. 再按预先命名的cold/warm/repeated case执行；每个repeated query都是显式新调用，runner没有retry；
6. 只保存content-free JSONL。人工quality评估使用现场synthetic fixture，不把正文或判断依据提交到tracked
   evidence；
7. 只有同时取得route compatibility、authoritative usage与人工selection检查，才另行审阅是否从NotRun改为
   Passed或No-Go。

## 6. Claims deliberately absent

- 没有real request、响应、latency或token record；
- 没有cold/warm cache hit、prefix reuse或cache retention结论；
- 没有费用/价格计算或“廉价”声明；
- 没有precision、recall、恶意正文稳健性或large corpus质量结论；
- 没有生产激活、Galatea接入或WP-07C readiness结论。
