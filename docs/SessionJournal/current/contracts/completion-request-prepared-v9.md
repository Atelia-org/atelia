# CompletionRequestPrepared v9：语义计划与完整结果提交

状态：当前源码合同；自动重试重构的集成验收与部署状态见 [实施方案](../../../Galatea/completion-auto-retry-refactor-plan.md)，本文不宣告最终验收完成。本页定义新 writer 的合同；[v7/v8 exact 合同](completion-request-prepared-v7.md)继续约束历史记录。

## 输入内容

ObservationAccepted 和 SystemPromptSetup 的 v2 body 使用明确的 `SessionInputContent`，区分 text 与 structured。structured 保存 schema ID 与 JSON 对象；不通过字符串外观推断类型。正文字符串保留原 Unicode、空白和换行，重复字段及非法 Unicode 拒绝。核心持有 JSON 值的生命周期，不依赖调用方 JsonDocument。

host 自行验证业务 schema，并通过 `ISessionInputProjector` 提供请求时投影。核心不引用 Galatea 或 md-json。未提供 projector 时，structured 不能用于发送；查询、setup 比较、undo、exact append proof、重开及审计仍能直接处理稳定内容。Text 在请求中按原文使用。

### 输入 wire 与公共接口

ObservationAccepted 的 event kind 仍为 `4`，SystemPromptSetup 仍为 `2`。两者的新 payload 都是
`{"v":2,"body":{"content":<content-envelope>}}`，其中 content 只允许以下两种形状：

```json
{"kind":"text","value":"原文"}
```

```json
{"kind":"structured","schemaId":"host.example.v1","value":{"body":"原文"}}
```

envelope 拒绝缺字段、多字段、重复字段和错误类型；structured value 必须是 object，其嵌套对象也拒绝重复键。
schemaId 必须是非空白合法 Unicode 字符串。Text Observation 仍须非空白；SystemPrompt 可为空文本。
旧 v1 body 的 `content` 是原 string，读取时映射为 Text，不嗅探其中的 JSON/Markdown，也不改写旧字节。
新 writer 即使收到 Text 也写 v2。

[`SessionInputContent`](../../../../prototypes/SessionJournal/SessionInputContent.cs) 通过 `Text(string)` /
`Structured(string, JsonElement)` 工厂创建；只有 string → Text 的便利转换。调用者显式检查 `IsStructured`，
然后访问 `TextValue` 或 `JsonValue`，错误种类的 getter 抛错，不能隐式降级为字符串。
`ToUtf8Json()` 和附带的 `JsonConverter` 输出相同机读 envelope；这不是面向 LLM 的 renderer。
相等比较包括种类、schemaId 及固定编码后的内容，保留 JSON 对象属性顺序，不承诺一般 JSON 的语义等价归一化。

`SessionRuntime.InputProjector` 是可选的 `ISessionInputProjector`。history planning 中的 structured Observation
通过 `SessionInputObservationMessage` 保留语义载荷；最终组装才转成 Completion 的 `ObservationMessage`。
public Create/Send、DesiredSetup/governing setup、audit facts、completed/retracted turn 的 `ObservationContent`
和 `SessionExpectedObservationTurnRequest.ExactObservationContent` 使用相同 typed 内容。exact append proof 仍同时
核对原 exact base、Observation 地址与分支状态；typed 相等不取代这些归属证明。

内容 envelope 的最大容器深度为 64：structured value 自身最多 63 层，加上内容 envelope 一层。新版 Observation/Setup v2 可以容纳外层日志包装，但不扩大 value 上限；其他事件及旧 schema 保留原有 64 层总深度。将内容再嵌入 API/report DTO 的调用者须为自己的外层预留深度，不能削去字段或扁平化内容；CLI 输出使用 128 层，旧日志 reader 的接受范围不因此改变。

## Prepared v9

event kind 不变。v9 body 精确包含八个顶层字段：

| 字段 | 持久语义 |
|:--|:--|
| `origin` | 既有 correlationId 与 reason |
| `execution` | 最后已分配的 tool execution sequence |
| `plan` | rawStartExclusive、rawRangeSha256、rawStartSetups 与 contributions |
| `setups` | governing runtime/system setup 的地址、schema 及 payload hash |
| `parameters` | modelId |
| `toolSet` | 有序 tool definitions、codec/hash 及必要 tool runtime identity |
| `recipe` | 语义聚合规则及实际请求摘要的编码合同 |
| `target` | 具体 connection 身份、clientName 与 apiSpecId |

`plan.contributions` 复用已经接纳的 `SessionContextContribution`，每项包含 carrier、blockKey、semanticHeading、exactText、contentCodecId、contentSha256、absorbedThrough。顺序、唯一 target、内容承诺、数量及字节上限继续校验；absorbedThrough 必须属于已验证的 raw 区间（包含起始 anchor）。重开不能重新运行候选选择或读取最新 cell 替换已选内容。

v9 不含 Prepared commitment、exactContextInputs、渲染的 ContextSnapshot 或备用 Markdown。recipe ID 为 `atelia.session-journal.semantic-artifact-tail.recipe.v1`。真实工具、来源或内容选择变化是新规划；纯布局变化不改变该计划。

## 新发送顺序与历史 Started v2

历史 Started v2 body 精确保存（新执行不再写此事件）：

```json
{
  "canonicalRequestCodecId": "atelia.completion-request.canonical-json.v2",
  "byteLength": 123,
  "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
}
```

示例中的长度与摘要仅示意字段类型。度量对象是交给 ICompletionClient 的请求经既有 canonical codec 编码后的字节；这不证明 provider native HTTP wire。

执行顺序：

1. 接纳 typed Observation、选择语义上下文与真实执行边界，提交 Prepared。
2. 从已提交计划读取内容，使用当前 projector 在内存组装最终请求，并核对实际请求限额。
3. 在内存调用 provider；调用开始、失败与重试不写 Started/Failed，不迁移其摘要到新 Action。
4. 仅完整且合法的结果提交为 AgentActionProduced v2，再执行其中的工具。渲染、限额或环境失败保持原 Prepared。

纯生成可按 Host 策略重算，允许重复计费，不证明远端 exactly-once。Action 提交结果不明确则必须
poison/reopen，按 selected lineage 判断结果是否已提交，不能直接重新生成。业务停止或明确非成功输入
由安全生成边界上的 TurnEnded 收口；它不删除已提交工具事实，也不代替环境错误的 pending 状态。

## 恢复版本矩阵

| Prepared | Started | 处理 |
|:--|:--|:--|
| v9 | 无 | 验证原计划，由当前 projector 投影；生成成功后 Action v2 直接接 Prepared |
| v9 | v2 | 严格验证历史内容及尝试链，恢复同一语义计划；成功后 Action v2 接当前历史 Started 尾 |
| v9 | v1 | 拒绝：缺失新格式要求的发送证据 |
| v7/v8 | 无或 v1 | 按旧 recipe 逐字重建并验证原 Prepared commitment；不写新 Started，提交 Action v2 |
| v7/v8 | v2 | 若读取到这种组合，Started 的 canonical codec 和 commitment 都必须等于旧 Prepared；仍走旧 exact 恢复 |
| v5 | v1 | 只做原格式审计，不能 dispatch |
| v5 | v2 | 拒绝：不能将 canonical-v1 证据误标为 canonical-v2，即使摘要/长度相同 |

Prepared 与合法历史 Started 尾统一为 AwaitingCompletion，不再有 Refuse/RestartWithNewAttempt。
历史 Started 的字节、地址与摘要保留，新调用不追加尝试事件。旧 v7/v8 仍按上表 exact 恢复。
历史 Action v1 必须接合法 Started；新 Action v2 可接 Prepared 或合法历史 Started 尾，不能接 Failed。
旧 Failed 不自动生成；验证完整工具批次与来源后，仅允许显式 TurnEnded(Stopped) 收口。

新格式审计检查 schema、raw hash、setup 引用、工具顺序、内容来源和 Prepared/Started/Action 归属。它验证摘要的格式与归属，不能仅凭摘要重建或证明历史 renderer 的全文输出。旧版本的 exact 验证能力与执行权限保持原合同。

这里的 schema 验证只包含核心 event/content envelope、recipe、工具及 contribution 合同，不解释 host 的业务
schemaId。一个核心格式合法但业务 schema 未知的输入仍可被核心读取、查询、比较和审计；这不证明业务来源
真实或授权成立。需要业务解释的 host 查询/投影器应明确报告 unsupported，不能靠普通 JSON 字符串兜底发送。
业务 schema 未知与核心未来 event/body version 未知是两个边界；后者仍按 codec 拒绝。

`SessionHistorySemanticCommitment` 保留既有 aggregate codec ID；Text Observation 使用原 history-message
算法，structured Observation 使用独立 `structured-observation` domain 加固定 content envelope，均不调用
projector。TurnEnded 使用独立 `turn-ended` domain 保存结束原因的语义贡献，不伪造 Action。
离线 report 的输入摘要区分 Text UTF-8 与 structured 机读 envelope，见
[offline validation report](offline-validation-report-v3.md)；摘要不宣称记录了历史 LLM 提示全文。

## 验收入口

[SessionStructuredInputTests](../../../../tests/SessionJournal.Tests/SessionStructuredInputTests.cs)覆盖新的 typed 输入与发送边界；历史 fixture 必须明确生成旧 schema，不能只用升级后的 current writer 重新生成数据，再声称证明了 v7/v8 恢复。具体运行结果与剩余跨域验收只记录在实施工作单。

核心实现入口为 [`SessionEventCodec`](../../../../prototypes/SessionJournal/SessionEventCodec.cs)、
[`SessionRequestManifestCodec`](../../../../prototypes/SessionJournal/SessionRequestManifestCodec.cs)、
[`SessionPreparedRequestReconstructor`](../../../../prototypes/SessionJournal/SessionPreparedRequestReconstructor.cs) 和
[`SessionJournalEngine`](../../../../prototypes/SessionJournal/SessionJournalEngine.cs)。本页不宣告 Galatea、辅助 LLM
或真实实例迁移已完成。
