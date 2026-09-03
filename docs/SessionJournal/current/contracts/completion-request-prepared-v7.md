# CompletionRequestPrepared v7 — provider-maximum output semantics

状态：Current；Prepared v7-only writer/current recovery，Prepared v5 historical read-only verification，
withdrawn v6 unsupported。

本文是 [SessionJournal Contract R2](session-journal-contract-r2.md) 之后的 raw/recovery successor。
R2 的 Prepared v5 表格、tag 与 evidence 仍记录当时事实，不由本文改写。

## 1. 决策

SessionJournal current execution 有意不支持 caller/Host 选择输出 token ceiling：

- `CompletionRequest` 与 `SessionRuntime` 没有 `MaxTokens` / `MaxOutputTokens` 参数；
- current Prepared v7 的 `parameters` 仅包含 `modelId`；
- provider-neutral canonical request v2 仅包含 `modelId`、`systemPrompt`、`context`、`tools`；
- provider optional limit field 在 omission 表示 unlimited/model maximum 时必须省略；
- provider 若必须发送数字，或 omission 会选择更低的 model-varying default，则具体 provider client
  必须使用 selected model 的 exact provider maximum。

此处“maximum”是 provider/model capability，不是业务预算。SessionJournal 不持有、覆盖或冻结其数值，
从而避免已产生费用的 generation 被本地预算截断而得不到正常结果。

`MaximumCanonicalRequestBytes` 保留。它约束 provider 调用前的 exact input/context bytes，不截断输出，
也不会在付费 generation 途中终止请求。Timeout、caller cancellation 与 provider terminal `Incomplete`
同样不是本次合同删除的 caller-selected output ceiling。

## 2. Current v7 accepted language

`CompletionRequestPrepared` 的 event kind 仍为 `8`，current body schema version 为 `7`。body 继续是
exact nine-field object：

```text
origin
execution
plan
setups
parameters
toolSet
recipe
target
commitment
```

`parameters` 的 exact canonical shape 是：

```json
{"modelId":"<exact runtime model id>"}
```

`maxTokens`、`maxOutputTokens` 及其他 unknown/missing/duplicate/wrong-type fields 全部 fail closed。

Identifiers：

| Fact | Current identifier |
|---|---|
| Prepared body | `7` |
| recipe | `atelia.session-journal.coherent-artifact-tail.recipe.v1` |
| canonical request | `atelia.completion-request.canonical-json.v2` |
| tool definitions | `atelia.tool-definition.canonical-json.v1` |

Recipe v1 保持不变，因为 coherent artifact-tail 的 selection、aggregation 与 expansion 没变。
Canonical request 升 v2，因为 output-ceiling field 已从 committed bytes 删除。Current writer 对 exact
canonical v2 bytes 计算 `commitment.byteLength` 与 SHA-256，recovery 必须重建同一 bytes。

raw-range、artifact snapshot、history semantic commitment 与 context-contribution hash domain 都不升级。
raw-range 本来就纳入每个 event 的 actual body schema version，因此 mixed v5/v7 lineage 保持可验证。

## 3. Historical v5 isolation

Existing Prepared v5 raw events 不重写、不迁移、不改变 `EventAddress`。这对 append-only history、
`AgentActionProduced` Parent lineage 与 CharacterMemory 等外部 Action-address provenance 是必要条件。

Current binary 只通过 distinct internal historical types 读取 v5：

```text
HistoricalCompletionRequestPreparedV5Body
HistoricalSessionRequestParametersV5.LegacyMaxTokens
SessionPreparedRequestV5HistoricalVerifier
SessionRequestV5HistoricalCanonicalizer
```

`LegacyMaxTokens` 只进入 historical canonical-json-v1 bytes，用于验证旧 commitment。Historical verifier：

- 没有 encoder/writer；
- 不返回 `CompletionRequest`；
- 不接受或创建 completion client；
- 不把 legacy ceiling 归一化为 current body；
- 不能用于 provider dispatch。

Lineage/state-machine/setup/audit 只消费 cap-free `SessionPreparedManifestView`。Full audit、selected-lineage
audit、offline validation、completed-turn projection、history planning、tail fold 与 governing-setup checkpoint
可以跨 completed v5；随后生成的新 Prepared 必须是 v7。

Prepared v6 曾属于已撤回的 supplemental-context candidate，current reader/writer 都明确拒绝，数字不复用。
其他旧版与 future version 同样 unsupported。

## 4. Recovery

只有 current v7 能由 `SessionPreparedRequestReconstructor` 生成 dispatchable `CompletionRequest`。

若 selected head 是 historical v5 Prepared，或是其后的 active `CompletionAttemptStarted`：

1. 先用 historical canonical-json-v1 验证 raw range、setup、exact context、tool/target 与 commitment；
2. corruption 仍按 corruption 报告；
3. valid v5 随后明确 fail closed；
4. 不返回 `FrozenCompletionRequired`，不绑定 current target/client，不调用 provider，不追加 Started/Failed。

Adapter fingerprint rotation 是额外防线，不是 v5 execution gate。即使 caller 提供匹配的旧 identity，
v5 也不得恢复调用。

已完成 v5 Action 是历史事实：Idle session 可以继续 append v7。若旧 completion 已成功产生含 tool-call 的
Action，现有 frozen tool-runtime identity 仍控制该工具；工具完成后的新 completion request 必须写 v7，
不会复用 v5 ceiling。Known-failed v5 turn 仍可通过既有 explicit abandon 操作处理。

## 5. Verification owners

实现 owner：

- `prototypes/SessionJournal/SessionEventCodec.cs`
- `SessionRequestManifestCodec.cs` 与 `SessionRequestManifestV5HistoricalCodec.cs`
- `SessionRequestCanonicalizer.cs` 与 `SessionRequestV5HistoricalCanonicalizer.cs`
- `SessionPreparedRequestReconstructor.cs` 与 `SessionPreparedRequestV5HistoricalVerifier.cs`
- audit、selected-lineage、execution-tail、tail projection 与 governing-setup resolver。

Focused evidence 位于 `tests/SessionJournal.Tests`：v7 exact golden、v5 numeric/null strict decode、legacy
ceiling tamper、v6 rejection、active v5 zero-dispatch refusal，以及 completed v5 → current v7 mixed-lineage
audit/offline/recent projection。Public-surface tests守门 `SessionRuntime` 不再暴露 caller-selected output cap。
