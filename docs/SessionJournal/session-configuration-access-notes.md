# SessionJournal Configuration Access Notes

> 状态：CS-3A Implemented / CS-3B/CS-3C Handoff
> 日期：2026-07-26
> 相关文档：[SessionJournal 主干设计基线](session-journal-trunk-design.md)、[ChatSession 事件源与长期上下文架构路线图](../ChatSession/event-sourced-session-architecture-roadmap.md)

## 1. 结论

CS-5-lite 完成后，主线已进入 roadmap 的 **最小 CS-3：可恢复 Completion**。CS-3A 已落地 minimal
`ContextPlan`、canonical request manifest 与 governing setup checkpoint；下一步分别由 CS-3B 引入
artifact + raw suffix 的 tail context projection，由 CS-3C 实现 prepared request 的确定性 reopen
driver。

**赞同把已提交的 ContextPlan / request manifest 用作重启时的 governing setup 加速点。**准确场景是：

1. 进程正常运行时，内存状态已经持有当前 governing `runtime-config-setup` 与
   `system-prompt-setup` 地址。
2. 每次调用 LLM 前，把精确 `ContextPlan` 与 canonical request manifest 提交到 raw event chain，并将
   这两个地址引用写入该 event。
3. 进程重启后，从 ref head 沿 Parent 回溯。遇到最近一份已提交的 plan/manifest 时，读取其中的两个地址，
   分别一次定址读取 setup payload，而不再继续回溯到 session root。

这里没有循环依赖：重启时使用的是**程序终止前已经提交**的 plan/manifest checkpoint，而不是试图读取一个
尚未构造的未来 plan。对下一次新请求而言，它也是 previous committed checkpoint；对崩溃于 request
prepared 之后的同一次请求而言，它就是 current in-flight request 的恢复事实。

“一跳”需要区分两个层次：

- 从 checkpoint payload 中的地址到两个 setup event，是各一次直接 address dereference。
- 从 ref head 找到最近 checkpoint，仍要扫描 checkpoint 之后的少量 raw events；复杂度是
  `O(distance from head to nearest usable checkpoint or newer setup)`，不保证一个 Parent hop。

只要每次 completion 前都提交 checkpoint，正常重启只遍历最后一次 request 之后的局部尾段。首次请求、
legacy import、rewind 到首份 checkpoint 之前，以及无 checkpoint 的分支仍必须走 authoritative parent
scan。不应为此提前实现 CS-6 的完整 Context Planner；CS-3 只需锁定最小 plan/manifest recovery contract。

因此近期方案是：

```text
authoritative parent scan
+ nearest committed ContextPlan / request manifest as an on-chain checkpoint
+ dependency-closed raw suffix
+ minimal ContextPlan / canonical request manifest
```

独立 projection cache 不是当前前置条件。只有在 tail projection 落地后，benchmark 仍证明“首次、无
manifest 的长历史 governing setup scan”不可接受，才单独设计它的信任与校验合同。

## 2. 当前实现事实

`SessionJournal` 的 sticky setup 已拆成两个彼此独立的完整 snapshot：

- `runtime-config-setup`：`SessionRuntimeConfiguration`，包含 model id、completion surface、schema。
- `system-prompt-setup`：完整 system prompt。
- `session-created`：初始化完成 marker，空 body。初始化顺序为
  `runtime-config-setup -> system-prompt-setup -> session-created`。

`SessionJournalEngine.ResolveGoverningSetup(head)` 的 CS-3A 实现为：

1. 从给定 `head` 沿 authoritative `EventFrameHeader.Parent` 回溯。
2. 每步只调用 `ReadEventHeaderPreview`，不解码中间 payload。
3. 分别收集 checkpoint 之后最近的 `runtime-config-setup` 与 `system-prompt-setup`。
4. 若尚有缺项且命中最近的 `completion-request-prepared`，只读取该 manifest，并从其 setup refs
   逐字段补缺；若两个 setup 已在尾段直接命中，则不读取 manifest payload。
5. setup ref 直接读取目标 event，校验 kind、body schema version，以及 `ReadEvent` 返回的完整、
   解压后的 logical envelope bytes 的 SHA-256。
6. 无 checkpoint 时继续回溯到 root；缺少任一 setup 或 checkpoint 引用校验失败时 fail-fast。

manifest 的 setup lineage 绑定是 **validated writer invariant**：append manifest 前必须用
`GoverningSetupCursor` 证明两个地址是 `Header.Parent` 当时的 governing setup，并以同一个 expected
Parent 做 ref CAS。reopen 不再用 O(N) scan 重证祖先关系，否则会抵消 checkpoint 的收益；它信任 checked
raw manifest 的语义事实，同时独立验证所引用 payload 的 kind/schema/hash。

同时要注意：`Project()` / `ReplayHistory()` 当前仍通过 `ReadChronologicalChain` 解码完整 raw chain。
在 tail-only projection 尚未接入正式请求路径以前，只优化 setup resolver 并不能消除整体 O(N) replay；
真正的近期性能切口必须包含 tail context projection。

## 3. 必须分开的三个问题

| 问题 | 输出 | 正确性来源 | 近期方案 |
| --- | --- | --- | --- |
| Governing setup 定位 | 两个 setup 地址及 snapshot | raw Parent chain | header-only scan；最近 plan/manifest 是链上 checkpoint |
| Completion context 物化 | artifact header + dependency-closed raw suffix | artifact provenance + raw events | CS-3 tail projector |
| Execution recovery | 当前 phase、pending tool、attempt 等 | raw operational events / request manifest | CS-3 无工具最小合同；CS-4 扩展 tool-loop |

把三者合并成“给 reducer 一个 config seed”会遗漏真实状态，也会让 ContextPlan、projection cache 和
execution checkpoint 的职责混在一起。

## 4. Governing setup resolver 的正确形状

### 4.1 两个字段独立解析

runtime config 与 system prompt 可以在不同位置更新。resolver 必须逐字段保留“当前 head 向后看到的
第一个 setup”，checkpoint 只能补齐尚未找到的字段：

```text
resolveGoverningSetup(head):
    runtimeSetup = null
    promptSetup = null

    for event in walkParents(head):
        header = ReadEventHeaderPreview(event)

        if header.kind == runtime-config-setup and runtimeSetup == null:
            runtimeSetup = event

        if header.kind == system-prompt-setup and promptSetup == null:
            promptSetup = event

        if header.kind == completion-request-prepared:
            manifest = readAndValidatePlanManifest(event)
            if runtimeSetup == null:
                runtimeSetup = manifest.governingRuntimeConfigSetup
            if promptSetup == null:
                promptSetup = manifest.governingSystemPromptSetup

        if runtimeSetup != null and promptSetup != null:
            break

    require runtimeSetup and promptSetup
    read and validate both setup payloads
```

例如最近 plan/manifest 之后只发生了新的 runtime setup，扫描必须采用新 runtime setup，同时从 checkpoint
补 prompt；不能把 manifest 的整对 setup 无条件覆盖到当前 head。

### 4.2 Manifest checkpoint 的必要不变量

roadmap 已明确允许 MVP 将 `context-plan-committed` 与 `completion-request-prepared` 合并为一个 payload。
近期推荐保留 `completion-request-prepared` 这个 event kind，并在 payload 内嵌 minimal `ContextPlan`；
这个单一 raw event 同时承担 plan 审计、request 恢复与 governing setup prefix checkpoint。

`basedOnRawHead` / `RawEndInclusive` **不进入 payload**。按照 trunk 的 header/body 去重不变量，它们都
由 manifest frame 的 `Header.Parent` 唯一表达；materialized plan view 可在解码时注入这个地址。payload
至少明确：

```text
governingRuntimeConfigSetup
governingSystemPromptSetup
```

并在 append 前保证：

- cursor 的 `validForHead` 就是即将成为 manifest `Header.Parent` 的地址。
- 两个 setup 地址是这个 Parent lineage 上各自最近的 setup。
- 两个地址解码为预期 kind/schema。
- manifest 自己不改变 governing setup。

当前 `SessionJournalEngine` 尚未持有 governing addresses；这必须成为 CS-3A 的显式状态合同，而不能当作
已有事实：

```text
GoverningSetupCursor {
    validForHead
    runtimeConfigSetup
    systemPromptSetup
}
```

- reopen 时先由 resolver 建立 cursor。
- 构造 plan/manifest 前，必须满足 `cursor.validForHead == expectedParent`；commit 也必须以同一个
  `expectedParent` 做 CAS。
- 普通 event 成功 append/commit 后，推进 `validForHead`，两个 setup pointers 不变。
- setup event 成功 append/commit 后，推进 `validForHead` 并替换对应 pointer。
- ref CAS/commit 失败、observed head 不一致或 branch 切换时，cursor 立即失效，按新 head 重新 resolve。

这样写 checkpoint 时无需再扫描一次全历史，也不会把易陈旧的进程内字段误当成无条件权威。
checkpoint 将实际用于构造 request 的状态固化为 raw fact。重启后回溯在当前 Parent 链上遇到它时，已经
检查过 checkpoint 之后是否存在更新的 setup；checkpoint 只补齐仍缺失的字段。

若以后拆分成两个 event，canonical request manifest 仍是“实际 request 使用了什么”的恢复权威；
ContextPlan 不应再保存一份可能分叉的独立引用。MVP 合并 event 则只存一组 setup refs，由 plan 解释和
request recovery 共同引用。

### 4.3 冷启动恢复与新请求规划的时序

首次启动或当前链上还没有 checkpoint 时：

```text
raw head
-> resolve current governing setup
-> choose artifact + dependency-closed raw suffix
-> build minimal ContextPlan
-> build and persist canonical request manifest
-> send completion
```

进程在上述持久化之后终止并重启时：

```text
ref head
-> scan short raw tail
-> find nearest committed plan/manifest
-> dereference its two governing setup addresses
-> rebuild termination-time in-memory setup state
-> recover in-flight request, or plan the next request
```

因此同一个 event 既是本次已准备 request 的恢复入口，也是下一次新请求的 prefix checkpoint。唯一不能做的
是：在首次构造它之前，反过来依赖这个尚不存在的 event。

### 4.4 分支与 rewind

Parent scan 天然限定 current lineage：

- divergent branch 上的 manifest 不会被遇到，因此不会被复用。
- rewind 到 manifest 之前时，该 manifest 同样不可达。
- 从 manifest 之后分叉时，只要 checkpoint event 仍在共同祖先链上，就可以安全复用。
- manifest 之后若有任一新 setup，扫描会先命中新 setup，再只从 manifest 补另一字段。

这比“按 branch 名拿一个 latest manifest”更稳；checkpoint 的适用性来自真实 Parent 可达性，而不是
可变命名或时间戳。

## 5. 对“hint 永远不可能给错答案”的修正

仅验证：

```text
checkpoint.validForHead 位于 current Parent chain
```

只能证明 checkpoint **适用于这条分支的某个祖先位置**，不能证明其
`runtimeConfigSetup` / `systemPromptSetup` 真的是该位置之前各自最近的 setup。

一个语法有效、`validForHead` 也可达、但把地址写成更老 setup 的 derived cache，会静默返回旧配置。
读取目标 payload 并验证 kind 也无法发现“它是同 kind、但不是 latest”的错误。因此：

- raw request manifest 可以作为强 checkpoint，因为它是不可变 request fact，append 时必须验证
  governing refs；raw 损坏应 fail-fast。
- derived artifact 的 `Governing*` 字段首先是 artifact provenance，不自动成为 raw resolver 的
  correctness source。
- 任意可删除 cache 若直接携带 setup refs，就必须明确其信任/校验模型；不能仅靠“命中祖先”声称
  arbitrary corruption 只会变慢、绝不会给错。

换言之，early-exit checkpoint 是一份“已验证的 prefix summary”，不是因为改名为 hint 就不参与
语义。近期优先复用 raw canonical manifest，正是为了避免新增一份较弱的 setup 真源。

## 6. 为什么暂不先做独立 setup cache

候选 cache 可以长成：

```json
{
  "schema": "atelia.session-journal.governing-setup-checkpoint.v1",
  "coveredThrough": "<EventAddress>",
  "runtimeConfigSetup": "<EventAddress>",
  "systemPromptSetup": "<EventAddress>"
}
```

但它现在不是最佳的第一步：

1. 正式请求仍在 full replay；resolver cache 不是当前最大的 O(N) 来源。
2. `coveredThrough` 若只在 config 改变时更新，稳定 config 时它会长期停在 root，完全不能提供
   near-head early exit。
3. 若要持续 near-head，`coveredThrough` 必须在普通 append、周期 checkpoint 或成功 resolve 后前移；
   因而“正文只在 config 改变时更新”与“O(1) 退出票”不能同时成立。
4. derived cache 的语义损坏不能仅靠 Parent 可达性证明安全。
5. CS-3 之后每次 completion 本来就会产生带 governing refs 的 raw plan/manifest checkpoint，通常已
   足够靠近 head。

若真实 benchmark 证明首次请求的 fallback scan 仍不可接受，再比较：

- 带明确校验/信任模型的 SessionJournal compiled cache。
- EventJournal 层可验证的 sparse traversal summary。
- 专用 raw checkpoint event。
- dedicated ref。

在没有数据前，不应为了避免一次 bootstrap scan，先引入会与 request manifest 重叠的第二套长期机制。

## 7. Tail reducer 不能只 seed config

### 7.1 当前 reducer 的长程状态

当前 `SessionReducer` 不只依赖 `config`：

- `SessionRuntimeConfiguration`。
- `systemPrompt`。
- `sessionCreated`；tail 通常不再包含 root 的 `session-created` marker。
- `ToolExecutionSequenceCheckpoint`；它在整个 session 内单调递增，恢复时用于
  `ToolSession.RestoreExecutionSequence`。
- 若 tail 从未闭合 tool turn 中间开始，还需要 `openAction`、已观察 tool results、
  pending operation id / started 状态等。

因此简单增加：

```text
Reduce(SessionRuntimeConfiguration seededConfig, tailEvents)
```

仍会在首个 observation/action 上因 `sessionCreated == false` 失败，并会把 tool execution sequence
错误地从 0 开始。

### 7.2 CS-3 应先限制为 dependency-closed 无工具 tail

CS-3 是无工具 Completion，可以先把边界收窄：

- `RawStartExclusive` 必须使 suffix 不从 tool execution/result 中间开始。
- suffix 开头应位于明确的 replay-safe boundary，例如 setup/observation，或包含其依赖的 fresh action。
- 若所选 artifact anchor 不满足边界，优先选择更早的 replay-safe artifact；没有可用候选时退化为
  full-raw replay。
- 不默认把 raw start 向前跨过 artifact anchor。有损 recap + overlap 会把已经被摘要吸收的 raw 内容
  再次注入 request；只有未来 planner/renderer 明确建模“可控重复”并有对应语义测试时才允许。
- seed 必须明确表达 session 已初始化，以及 boundary as-of 的两个 setup。
- current-head governing setup 与 boundary seed 是两个概念：若 tail 内可能出现 setup change，
  generic fold 应以 boundary as-of setup 为 seed，再让 tail events 更新它；不能把未来的 head setup
  注入更早边界。

是否给现有 `SessionReducer.Reduce` 增加完整 seed，还是建立专用
`SessionTailContextProjector`，应在 CS-3B 通过 parity tests 后决定。不要先假设“一处重载”就能保持
execution 与 context 两种语义。

### 7.3 CS-5-lite anchor 不是天然 reducer boundary

当前 Derived Recap Artifact 的：

- `AnchorRawEvent` 是 producer 已吸收范围的 coverage high-watermark。
- `GoverningRuntimeConfigSetup` / `GoverningSystemPromptSetup` 是按 artifact 的
  `SourceRawHead` resolve，而不是按 `AnchorRawEvent` resolve。

因此不能把 artifact 的 `Governing*` refs 当成 anchor 位置的 reducer seed。

另外，rolling split 可能让 fragment 结束在声明 tool calls 的 `AgentActionProduced`，而
`(AnchorRawEvent, currentHead]` 从 `ToolExecutionStarted` / `ToolResultObserved` 开始。这样的 tail
缺少 `openAction`，现有 reducer 会正确拒绝。

ContextPlan 必须验证 `RawStartExclusive` 是 dependency-closed boundary，而不是机械令
`RawStartExclusive = AnchorRawEvent`。不安全时应换用更早的 safe artifact 或 full-raw fallback；
不能把透明 overlap 当成 reducer 实现细节。

## 8. 推荐的下一步工作包

### CS-3A：Minimal Plan/Manifest Checkpoint（已实施）

范围：

- 采用 roadmap 允许的 MVP 合并 event，同时表达 minimal `ContextPlan` 与
  `CompletionRequestPrepared` manifest。
- 一开始就定义完整、可恢复同一 canonical request 的 manifest schema，包括：
  - 由 frame `Header.Parent` 表达的 plan raw head / raw end，以及两个 governing setup event 地址。
  - raw range / artifact 的稳定地址、版本与必要 hash。
  - 当前没有 immutable tool-schema store，因此首版保存完整、可逆、content-addressed 的 inline
    `ToolDefinition` set snapshot；不伪造 tool schema address。
  - renderer / serializer / prompt / model / connection identity 与 fingerprint。
  - provider-neutral canonical `CompletionRequest` bytes 的 hash、attempt id、correlation id；该 hash
    不冒充 provider HTTP body hash。
- 引入绑定 head 的 `GoverningSetupCursor`，并实现 append 成功推进、setup 替换、CAS/branch 失配后失效
  与重解析规则。
- 增加 `completion-request-prepared` event kind / codec。
- reducer 将该 event 投影为 `RequestPrepared` / `AwaitingCompletion` execution phase；它对 rendered
  conversation context 与 governing setup 都是中性的。
- 正常 completion 成功后，`agent-action-produced.Header.Parent` 必须直接指向 prepared event，以因果边
  关联 request；body 不重复保存 Parent。import/manual action 走显式 unprepared append 入口。
- 在发送 completion 前提交该 event。
- 让 `ResolveGoverningSetup` 在 Parent 回溯中使用最近 checkpoint，并逐字段合并 checkpoint 之后的新
  setup。
- 记录 header visit count，证明 reopen 后只扫描 plan/manifest 之后的局部尾段。
- 保留首次、无 checkpoint 和 rewind 场景的纯 Parent scan fallback。

这一包先兑现本笔记的核心收益：程序正常运行时把内存中的 setup pointers 固化到 raw chain，重启后用
near-head checkpoint 恢复。Planner policy 可以先选择 full raw fallback，不必提前实现 CS-6。

实际落点：

- kind 8 `completion-request-prepared` 合并保存 minimal ContextPlan 与完整 v1 manifest；body 不复述
  frame `Header.Parent`。
- full-raw v1 的 `RawStartExclusive = null`，`RawRangeSha256` 覆盖
  `(RawStartExclusive, Header.Parent]`，使用带 domain tag、长度前缀、event address/Parent/kind/schema
  与 logical payload hash 的 canonical framing。
- setup ref 的 hash domain 固定为完整 logical SessionEvent envelope bytes；tool definitions 使用完整、
  可逆、content-addressed inline snapshot。
- connection identity 明确拆成 `ConnectionFingerprint` 与 `RequestAdapterFingerprint`，不保存秘密。
- manifest codec 严格拒绝 unknown/duplicate properties 与非法地址，并保证
  `Encode -> Decode -> Encode` byte exact。
- 每次 completion（含 tool-loop 续环）都在 provider 调用前提交 manifest；成功 action 直接以 prepared
  event 为 Parent。prepared 对 conversation context 中性。
- reopen 到 prepared 时投影为 `AwaitingCompletion`。CS-3A 的 `ResumeAsync` 明确 fail-fast，不使用当前
  config/head 重规划或盲目重发；CS-3C 再实现从 manifest 重建并继续。
- provider 明确返回 `Incomplete` / `Failed` 时写 kind 9 `completion-attempt-failed`，保存 attempt id、
  termination/reason/detail/errors，并投影为可替换 setup、可接收下一条 observation 的
  `TurnFailed`。transport exception/cancellation 没有已知 outcome，仍保留
  prepared/`AwaitingCompletion`。
- legacy/manual Action 走独立 kind 10 `imported-agent-action`；reopen 后不再把“缺少 prepared 的普通
  Action”猜成 import。live kind 5 Action 必须直接继承 prepared。
- create 时 cursor 已绑定；open 时 lazy；普通 append 推进 head，setup append 替换对应 pointer，任何
  observed-head/CAS 失配都使 cursor 失效。

### CS-3B：Tail Projection Contract

范围：

- 只支持无工具 completion。
- 定义 replay-safe / dependency-closed `RawStartExclusive`。
- materialize 一个 recap artifact，再读取必要的 raw suffix。
- 明确 boundary seed 与 current-head governing setup 的区别。
- 用 full replay 对照 setup 地址/值、boundary seed 和闭合 suffix fold 的对应状态。
- 对最终 artifact + suffix request，只要求 dependency-closed、确定性可重建且 provenance 可审计；有损
  recap 不与 full-raw request 声称逐字或结构等价。
- 对 mid-tool / dependency-open boundary fail-fast。

这一包证明“少读历史仍能确定性构造合法请求，并保持必要状态一致”，不新增完整 planner policy。

### CS-3C：Canonical Request Recovery

使用 CS-3A 已经完整落盘的 manifest：

- 从已提交 manifest 重建 request 的 reopen driver。
- request 前后、response 前后的 failpoint acceptance。

必须先提交 manifest，再发送 completion；reopen 后从 manifest 引用重建 request，不重新运行 planner。
CS-3A/B/C 可以在一个垂直切片内连续提交，但设计和测试断言应分开。任何可被正式写入 journal 的
`completion-request-prepared` 都必须从第一版起满足完整 schema；不能先写仅够 setup checkpoint、却无法
恢复 request 的 append-only 半成品。

### CS-4 以后

tool-loop tail recovery 需要正式处理：

- open action / observed results / pending operation。
- `ToolExecutionSequenceCheckpoint` 的可靠恢复。
- mid-turn boundary 与 request manifest 的关系。

不能把 CS-3 的无工具 seed 静默推广成通用 tool-loop reducer seed。

## 9. 验收矩阵

Governing setup：

- 无 manifest：回溯到 root setup，结果与当前 resolver 相同。
- 有 recent plan/manifest：结果相同，header visit 只覆盖局部尾段。
- checkpoint 后只更新 runtime：采用新 runtime + checkpoint prompt。
- checkpoint 后只更新 prompt：采用 checkpoint runtime + 新 prompt。
- 两者都更新：不读取旧 manifest payload即可完成。
- divergent branch / rewind：不可达 checkpoint 不复用。
- manifest setup refs kind/schema/payload hash 错误：reopen fail-fast；setup lineage/Parent binding
  在 manifest append 时由 exact-head cursor 强制。
- `GoverningSetupCursor.validForHead` 与 manifest 的 expected Parent 不同：拒绝写 manifest并重解析。
- 普通 append 保留两个 pointers，setup append 只替换对应 pointer；ref CAS/branch 失配使 cursor 失效。

Tail projection：

- 无工具、dependency-closed boundary：setup、seed 与闭合 raw suffix fold 的对应状态与 full replay
  一致。
- artifact + suffix request：确定性可重建、dependency-closed、provenance 可审计；不要求等价于
  full-raw prompt。
- tail 内有 setup change：最终 config/system prompt 与 full projection 相同。
- artifact anchor 非安全边界：选择更早 safe artifact 或 full-raw fallback，不从 mid-tool 强行
  reduce，也不默认注入 overlap。
- artifact 的 `SourceRawHead` setup refs 不误当 anchor seed。
- 删除 derived artifact：退化为 full/raw fallback，raw journal 仍可运行。

Request recovery：

- manifest 提交前崩溃：允许重新规划。
- manifest 提交后崩溃：只从已提交引用重建同一 canonical request。
- manifest 成为 head：`Project` 得到 `AwaitingCompletion`，`ResumeAsync` 以明确的 CS-3A
  fail-fast 拒绝重规划/重发，而不是因未知 kind 失败。
- prepared event 不改变 rendered conversation context 或 governing setup。
- completion 产生的 `agent-action-produced.Header.Parent` 必须是 prepared event；无 prepared parent 的
  import/manual action只能走显式入口并落为 `imported-agent-action`。
- provider 已明确返回 non-success：known outcome durable，reopen 为 `TurnFailed`；transport
  exception/cancellation：仍为 `AwaitingCompletion`，不伪造 outcome。
- 当前 config、renderer 或 planner 升级：不改变旧 manifest 的恢复结果。

## 10. 暂不采用的方案

- 把 full `SessionProjection.Context` 缓存回来：它无界且正是 tail-only 试图避免物化的冷历史。
- 每个 raw event 重复携带 setup pointers：污染 event body，增加双真源。
- 把尚未提交的未来 ContextPlan 当作首次 setup locator：此时 event 还不存在；但已提交的最近
  ContextPlan/manifest 正是重启恢复应使用的 checkpoint。
- 只 seed runtime config：遗漏 system prompt、session marker 与 execution checkpoint。
- 机械令 `RawStartExclusive = artifact.AnchorRawEvent`：coverage anchor 不等于 dependency boundary。
- 现在实现完整 CS-6 Context Planner：CS-3 只需要最小 plan/manifest 与确定性恢复合同。
- 立即新增 dedicated config ref 或通用 nearest-kind index：先让 raw manifest checkpoint 经真实负载验证。

## 11. 一句话决议

下一步实现最小 CS-3 是正确的；但它的主目标是 **tail request construction + persisted request recovery**，
不是给 setup resolver 造索引。

正常运行时把内存中的两个 governing setup 地址写入每次 completion 前提交的
`ContextPlan` / canonical request manifest；重启后从 head 扫描局部尾段，命中最近 checkpoint 后各一次
定址读取 setup payload。它不能替代首次 fallback，也不能把 artifact coverage anchor 冒充
dependency-closed reducer boundary。
