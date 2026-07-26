# SessionJournal Configuration Access Notes

> 状态：CS-3A + CS-3B + CS-3C + CS-3D0 + CS-3D1 + CS-3D2 Implemented
> 日期：2026-07-26
> 相关文档：[SessionJournal 主干设计基线](session-journal-trunk-design.md)、
> [Tail-only Execution Recovery Design](tail-execution-recovery-design.md)、
> [ChatSession 事件源与长期上下文架构路线图](../ChatSession/event-sourced-session-architecture-roadmap.md)

## 1. 结论

CS-5-lite 完成后，主线已进入 roadmap 的 **最小 CS-3：可恢复 Completion**。CS-3A 已落地 minimal
`ContextPlan`、canonical request manifest 与 governing setup checkpoint；CS-3B 又落地了由调用方指定
exact artifact 的 dependency-closed tail context projection；CS-3C 进一步让 prepare/reopen 共用同一个
canonical request reconstructor，并以显式的新 attempt 恢复 outcome 不确定的 Prepared。

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

同时要注意：显式调用 `Project()` / `ReplayHistory()` 以及非 CS-3B phase 的通用 execution recovery，
当前仍通过 `ReadChronologicalChain` 解码完整 raw chain。CS-3B 只为
`explicit-artifact-tail + ObservationAccepted + no tools` 建立窄 fast path：

- `SendAsync` 沿最近的 setup run 找到 idle predecessor，并对 bootstrap、live/imported terminal
  Action 或 failed attempt 做有界局部因果证明，再以 CAS append observation。
- observation-head `ResumeAsync` 验证该 observation 的 direct predecessor 通过同一 idle-boundary
  合同；它不会仅凭链头 kind 猜测 reducer 状态。
- 随后的 request preparation 从 exact observation address、governing setup、artifact 与 raw suffix
  构造，不调用 `Project()`。

这里采用与 setup checkpoint 相同的 **validated-writer trust model**：局部证明信任更早 prefix 已经由
SessionJournal 的受控 writer / artifact provenance 校验，不试图把任意低层伪造 raw chain 重新做一次
full-history reducer validation。若未来允许不可信 raw import 直接进入 fast path，必须先做完整验真，或
增加 suffix-local execution DFA / 更强 checkpoint，不能继续把 bounded proof 当作全链等价证明。

因此 CS-3B 收掉了该无工具 observation request path 的 full-history context materialization；CS-3C 又
让 `explicit-artifact-tail` 的 Prepared reopen 直接从链头进入 manifest-only reconstruction，不调用
`Project()`。这仍不能被描述成“整个 SessionJournal 已经 O(tail) reopen”：full-raw reconstruction 与
其他 execution phase 仍可能完整 replay，后续 execution checkpoint/recovery 工作还要继续缩短重启路径。

下一阶段已收束为 [CS-3D Tail-only Execution Recovery](tail-execution-recovery-design.md)：在线恢复只
重建最小 execution state，不构造完整 conversation；需要调用 LLM 时，再由 coherent recap/artifact
set（rolling 第一人称自传、world-understanding 等）与 dependency-closed raw suffix 构造 bounded
request context。完整 `Project()` 只保留为显式审计/迁移 API 与 reference oracle。

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

### 7.2 CS-3B 的 dependency-closed 无工具 tail

CS-3B 将边界收窄为：

- runtime 只接收调用方指定的 exact `ArtifactId`；engine 不读取 `latest` index，也不在不可用时偷偷改选
  另一个 artifact。未配置 tail projection 时走 full raw；显式指定的 artifact 无效时 fail-fast。
- `DerivedRecapStore` 读取时重算包含完整 `MemoryPack` 的 artifact identity，并核对 deterministic
  `ArtifactId`（含合法 collision suffix）；不能只验证 target block content 后就让被篡改的非 target
  system/action carrier 沿用旧 id。
- `RawStartExclusive` 等于该 artifact 的 `AnchorRawEvent`，且必须是当前 observation head 的严格祖先；
  suffix 必须非空，从而保留本次 observation。
- 一次 authoritative Parent walk 必须证明
  `currentHead -> ... -> artifact.SourceRawHead -> ... -> artifact.AnchorRawEvent`，并要求
  `AnchorRawEvent == SourceEndInclusive`。物理地址排序、latest index 与 artifact 自报 lineage 都不能替代
  这项证明。
- suffix 不得从 tool execution/result 中间开始。本版接受 setup、`session-created`、observation、
  `completion-attempt-failed` 与无 tool call 的 terminal Action 作为 exclusive boundary；带 tool calls
  的 Action、`tool-execution-started`、`tool-result-observed` 与
  `completion-request-prepared` anchor 保守 fail-fast。
- 不默认把 raw start 向前跨过 artifact anchor。有损 recap + overlap 会把已经被摘要吸收的 raw 内容
  再次注入 request；只有未来 planner/renderer 明确建模“可控重复”并有对应语义测试时才允许。
- seed 必须明确表达 session 已初始化，以及 boundary as-of 的两个 setup。
- current-head governing setup 与 boundary seed 是两个概念：若 tail 内可能出现 setup change，
  generic fold 应以 boundary as-of setup 为 seed，再让 tail events 更新它；不能把未来的 head setup
  注入更早边界。

实现采用专用 `SessionTailContextProjection`，不扩展通用 `SessionReducer` seed。原因是 CS-3B 只承诺
context 与 governing setup fold；它没有闭合 `ToolExecutionSequenceCheckpoint`、active correlation、
pending attempt 等 execution state。把这个 context seed 伪装成完整 reducer seed 会产生错误恢复承诺。

artifact 的 `MemoryPack` 先 materialize 为 `SessionContextHeader`，再由版本化 renderer 展开成真正的
provider-facing request：

1. 非空 system fragment 经 `Trim()` 后以固定字节 `\n\n` 追加到 governing system prompt；不使用
   平台相关的 `Environment.NewLine`。
2. 非空 observation fragment 展开为 `ObservationMessage`。
3. 非空 action fragment 展开为 text-only `ActionMessage`。
4. 展开后的普通 messages 才与 raw suffix messages 拼接；`SessionContextHeader` 本身绝不进入
   `CompletionRequest`，canonical request codec 也继续拒绝它。

这套精确规则由独立 renderer id/fingerprint 固定，不能复用 full-raw renderer identity。

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

CS-3B 没有实现“自动换用更早 artifact”的 planner。调用方若希望 fallback，必须在提交 manifest 前明确
选择 full raw 或另一个 exact artifact；一旦 `completion-request-prepared` 已提交，恢复不得重新规划。

### 7.4 可删除 artifact 与 prepared request 恢复

`DerivedRecapStore` 的 artifacts 是可删除、可重建的 sidecar；而 raw
`completion-request-prepared` 必须足够封闭，使 CS-3C 能恢复同一个 request。只在 manifest 中保存
`ArtifactId + hash` 会让“删除 derived store”破坏已提交 request，这是不合法的。

因此 `explicit-artifact-tail` manifest 的单个 artifact input 同时保存：

- exact artifact id / kind，用于 selection provenance；
- materialized header 的三段 exact string snapshot；
- 对该 snapshot 使用 domain tag 与 32-bit big-endian 长度前缀计算的 SHA-256。

snapshot 有明确大小上限。artifact 只参与 manifest 提交前的选择和校验；manifest 提交后，即使 derived
artifact 被删除，CS-3C 仍可从 raw manifest 内联 snapshot、raw suffix、setup refs 与 renderer identity
重建同一 provider-neutral request。这里内联的是有界 recap header，不是复制整份 rendered request。

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
- 正常首次 completion 成功后，`agent-action-produced.Header.Parent` 直接指向 source Prepared；CS-3C
  restart 后则指向当前 Restarted。两者都以因果边绑定 active attempt，body 不重复保存 Parent。
  import/manual action 走显式 unprepared append 入口。
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
- reopen 到 prepared 时投影为 `AwaitingCompletion`。CS-3A 阶段的 `ResumeAsync` 先 fail-fast；CS-3C
  已替换为 committed-manifest reconstruction + 显式 recovery policy，不使用当前 config/head
  重新规划。
- provider 明确返回 `Incomplete` / `Failed`，或 host 在收到 success response 后发现结果违反已经提交的
  request policy 时，写 kind 9 `completion-attempt-failed`，保存 attempt id、
  termination/reason/detail/errors，并投影为可替换 setup、可接收下一条 observation 的
  `TurnFailed`。当前 `explicit-artifact-tail` 收到 tool calls 时使用保留 reason
  `atelia.host.unsupported-tool-call`。transport exception/cancellation 没有已知 outcome，仍保留
  prepared/`AwaitingCompletion`。
- legacy/manual Action 走独立 kind 10 `imported-agent-action`；reopen 后不再把“缺少 prepared 的普通
  Action”猜成 import。live kind 5 Action 必须直接继承当前 active attempt（source Prepared 或
  Restarted）。
- create 时 cursor 已绑定；open 时 lazy；普通 append 推进 head，setup append 替换对应 pointer，任何
  observed-head/CAS 失配都使 cursor 失效。

### CS-3B：Tail Projection Contract（已实施）

实际落点：

- `SessionRuntime.TailProjection` 接收 exact artifact id；不新增 latest/ranking policy。
- derived store 读取时重算完整 artifact identity，使 exact id 覆盖 renderer 实际消费的整个
  `MemoryPack`，而不只是 target block。
- 新增严格的 `explicit-artifact-tail` plan/renderer identities；full-raw bytes 与旧 identity 保持不变。
- manifest 内联 exact materialized header snapshot 及其 canonical hash，derived artifact 删除不影响已
  prepared request 的恢复合同。
- 验证 current head、artifact source head 与 anchor 的 Parent ancestry/order；只读取并 hash
  `(AnchorRawEvent, current observation]`。
- 以 `ResolveGoverningSetup(anchor)` 取得 boundary-as-of seed，让 suffix setup events 更新，并与 exact
  current-head governing setup 地址和值对照。
- 专用 projector 严格 fold observation/action/tool start/result 的 context dependencies；不冒充完整
  execution reducer。
- 固定展开 artifact header，再构造最终 provider-facing `CompletionRequest` 与 canonical commitment。
- 只支持 observation boundary、空 tool definitions；mid-tool / dependency-open anchor fail-fast。
- `SendAsync` 与 observation-head `ResumeAsync` 使用 bounded recent-idle validator 进入 tail fast path；
  validator 不只检查 kind，还证明 bootstrap setup chain、live Action 的
  `Observation -> Prepared -> Action`、failed attempt 的 attempt binding，以及 imported Action 的
  observation parent。CS-3C 另允许由 validated full-raw writer 产生的
  `ToolResult -> Prepared[/Restarted] -> terminal Action/Failure` 作为下一次 tail Send 的近头闭合
  边界；它只信任已提交 manifest 中较远的 observation correlation，不把任意 imported ToolResult
  当作可从中间启动 tail projection 的入口。测试以 full-projection invocation delta 证明成功路径不调用
  `Project()`。
- provider 即使返回 tool calls，也不会留下伪装成 unknown outcome 的 prepared：engine 先提交带
  `atelia.host.unsupported-tool-call` reason 的 known `completion-attempt-failed`，再抛
  `SessionJournalTurnAbortedException`；reopen/投影为 `TurnFailed`，可接受下一条 observation。
- 对最终 artifact + suffix request，只要求 dependency-closed、确定性可重建且 provenance 可审计；有损
  recap 不与 full-raw request 声称逐字或结构等价。

这一包证明“只读取明确 raw suffix 仍能确定性构造合法 request context，并保持 setup/context fold
一致”，不新增完整 planner policy，也不声称通用 execution `Project()` 已经 tail-only。

### CS-3C：Canonical Request Recovery（已实施）

实际落点：

- 新增唯一的 `SessionPreparedRequestReconstructor`。prepare 前以
  `manifest + authoritative raw end` 重建，reopen 以 source Prepared 的 `Header.Parent` 为 raw end；
  两者最终都比较 exact canonical bytes/length/SHA-256，不再保留只检查 artifact prefix 的旁路。
- full-raw 重读并验证 `[root, raw end]`，复用完整 `SessionReducer`；explicit-artifact-tail 只读取
  `(RawStartExclusive, raw end]`，使用 manifest 内联 snapshot + 同一个 suffix fold，不打开
  `DerivedRecapStore`。
- setup refs、raw range、reason/correlation、model/surface、tool snapshot、renderer/codec identity 与
  commitment 任一不一致，都在 journal mutation 和 provider call 前 fail-fast。
- 当前 runtime 只用于 dispatch compatibility：`CompletionTarget`、client name/API 与 visible tool
  definitions 必须和 manifest 精确匹配；request 的 model、prompt、max tokens、tools 与 context 始终取
  committed manifest/references，不能被当前 runtime 覆盖。
- 新增 kind 11 `completion-attempt-restarted`。source Prepared 始终是 canonical request 唯一真源；
  Restarted 的 Parent 指向前一个 active attempt，body 保存新 attempt id、被替代 id 与 source
  Prepared address。连续崩溃形成 `P -> R1 -> R2 ...`，每次新的 provider call 都有独立、可审计的
  attempt identity。
- `SessionRuntime.PreparedCompletionRecoveryPolicy` 默认 `RefuseUncertain`，保证 reopen 不自动增加 LLM
  调用或费用；该路径只验证近头 P/R attempt topology，不 materialize raw request。只有显式选择
  `RestartWithNewAttempt` 时，engine 才重建并验证 request、CAS 提交 Restarted、再调用 provider。
  当前 `ICompletionClient` 没有 provider lookup/idempotency 合同，因此不复用旧 attempt。
- Action / known failure 必须直接继承当前 active Prepared/Restarted；transport exception/cancellation
  仍留下该 attempt 的 uncertain `AwaitingCompletion`。下一次显式 restart 会再写一个新 attempt，不把
  新调用伪装成旧调用。
- CS-3D1 已在 manifest 同时固定 tool definitions 与 tool implementation/capability runtime identity；
  recovery 重发 exact full-raw request 后，只有当前 host identity 精确匹配才能进入 durable tool
  dispatch。artifact-tail 当前仍维持 no-tools 合同。
- tail Prepared 即使 sidecar artifact 已删除，也能仅凭内联 snapshot 恢复，且不调用 `Project()`。
- provider success envelope 的 invocation identity 与 committed target 不一致时，以
  `atelia.host.invalid-completion-invocation` 写 durable kind 9，而不留下伪 uncertain Prepared。

这里的 Restarted 解决的是“新调用不能冒充旧 attempt”的审计正确性，**不消除**无幂等 provider 的重复
费用或重复生成可能性；旧 attempt 可能已经在 provider 侧成功。未来 provider capability 接入后，可以
在 active attempt 上先 lookup / 使用原生 idempotency key；但首次 dispatch 也必须绑定 durable attempt
id，不能只给 reopen 临时加 lookup。当前显式 restart 还假定调用方独占该 branch 的 completion
driver；head CAS 能阻止两个结果同时接到同一 active attempt，却无法撤销已经并发发出的 provider
调用。跨进程 lease / single-flight 属于后续 capability。

### CS-3D1 / CS-3D2（已实施）与后续

CS-3D1 已把 last-issued tool sequence、Started reservation/result sequence、operation id 与 tool
runtime identity 变成近头 durable facts。CS-3D2 已新增独立
`SessionExecutionTailResolver`，按 exact Parent lineage 恢复 open action、observed results、pending
operation、attempt/correlation 与 checkpoint，不构造 Context，也不调用 full `Project()`。

Action 还新增 required `correlationId`：live 值继承 Prepared；import 值继承 Observation/settled
ToolResult completion boundary。这样 Action 本身就是 correlation + sequence 的 trust cut，连续 imported
tool continuation 不必追到最初 Observation。

后续仍需：

- CS-3D3 把 `ResumeAsync`、tool loop、setup/import boundary 等 online driver 全部切到 resolver。
- CS-3D4 让 tool continuation 的 request context 来自 coherent artifact set + dependency-closed
  suffix，而不是 full `SessionProjection.Context`。

不能把 CS-3 的 context fold 静默推广成通用 execution reducer seed，也不能让 resolver 越过最近
Prepared/Action checkpoint 重验完整 autonomous loop。具体职责、事件协议和实施切片见
[Tail-only Execution Recovery Design](tail-execution-recovery-design.md)。

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
- manifest / Restarted 成为 head：`Project` 得到 `AwaitingCompletion`；默认 recovery policy
  `RefuseUncertain` 不调用 provider、不修改 journal。
- 显式 `RestartWithNewAttempt`：先提交新的 Restarted，再调用 provider；P/R 链上的 attempt id、
  replaces id、source address 与 Parent 必须全部一致。
- prepare 前重建的 canonical bytes 与实际 request byte exact；reopen 后同一 source Prepared 重建同一
  bytes，不运行 planner，不读取 derived artifact。
- Prepared / Restarted control event 不改变 rendered conversation context 或 governing setup。
- completion 产生的 `agent-action-produced.Header.Parent` 必须是当前 active Prepared/Restarted；无
  active attempt parent 的 import/manual action只能走显式入口并落为 `imported-agent-action`。
- provider 已明确返回 non-success：known outcome durable，reopen 为 `TurnFailed`；transport
  exception/cancellation：仍为 `AwaitingCompletion`，不伪造 outcome。
- 当前 config/max tokens/tail option 或 planner 升级：不改变旧 manifest 的恢复结果；dispatch
  connection/client/tool definitions 漂移则在新 attempt 提交前拒绝。
- 删除 derived artifact：已 committed explicit-artifact-tail request 仍可由内联 snapshot 恢复。
- recovered response 含 tool calls：当前 host tool runtime identity 必须与 manifest 精确一致；否则在
  新 Started 或外部 dispatch 前 fail-fast。

Tail execution recovery：

- exact-head 的 Empty/Setup/Created/Observation/P/R/Failure/Action/Started/Result state 与 full reducer
  oracle 一致。
- tool tail 按 Action 声明顺序 join；错 Parent/attempt/correlation/checkpoint/runtime identity、
  result-before-start、乱序 call 与 duplicate call id fail-fast。
- branch、rewind、divergent head 只消费各自真实 Parent lineage，不查询物理 latest。
- 1 turn 与 32 turns 冷前缀下，terminal Action 与新 Prepared 的 header/payload reads 相同；
  chronological-chain/full-projection reads 为 0。

## 10. 暂不采用的方案

- 把 full `SessionProjection.Context` 缓存回来：它无界且正是 tail-only 试图避免物化的冷历史。
- 每个 raw event 重复携带 setup pointers：污染 event body，增加双真源。
- 把尚未提交的未来 ContextPlan 当作首次 setup locator：此时 event 还不存在；但已提交的最近
  ContextPlan/manifest 正是重启恢复应使用的 checkpoint。
- 只 seed runtime config：遗漏 system prompt、session marker 与 execution checkpoint。
- 机械令 `RawStartExclusive = artifact.AnchorRawEvent`：coverage anchor 不等于 dependency boundary。
- 现在实现完整 CS-6 Context Planner：CS-3 只需要最小 plan/manifest 与确定性恢复合同。
- 立即新增 dedicated config ref 或通用 nearest-kind index：先让 raw manifest checkpoint 经真实负载验证。
- 在没有 provider lookup/idempotency 能力时直接复用旧 attempt id 重发：这会把新的物理调用伪装成旧
  attempt；CS-3C 改为显式 Restarted + 新 attempt id。

## 11. 一句话决议

CS-3A/B/C 已证明 **tail request construction + persisted request recovery** 的最小合同；
CS-3D1/D2 已证明 **durable operational checkpoint + pure tail execution projection**。下一步
CS-3D3 是让所有 online execution driver 使用该 resolver、完全退出 full `Project()`，但不要求审计
API 放弃完整历史。

正常运行时把内存中的两个 governing setup 地址写入每次 completion 前提交的
`ContextPlan` / canonical request manifest；重启后从 head 扫描局部尾段，命中最近 checkpoint 后各一次
定址读取 setup payload。它不能替代首次 fallback，也不能把 artifact coverage anchor 冒充
dependency-closed reducer boundary。完整 conversation 不是 execution recovery 的输入；真正需要调用
LLM 时，正常长会话由 coherent recap/artifact set + dependency-closed raw suffix 提供 bounded context。
