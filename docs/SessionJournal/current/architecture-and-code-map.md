# SessionJournal current architecture and code map

状态：WP-08 formal source cutover Complete，independent closure Closed。源码、strict codecs 与final tests是当前事实。

## Mental model

```text
raw EventJournal events + selected RefId Parent lineage  (authority)
                         |
                 SessionJournalEngine
                         |
              HistoryTimeline ledger
                         |
       Cadence + Control recipe graph + RecapGrid Store
                         |
          Manager / Runtime / Getter / Online
                         |
                  CLI / Galatea
```

raw events 是 append-only 事实源。HistoryTimeline、Cadence、Control 和 RecapGrid Store 都是有明确
identity、head fence 与重建边界的 companion state；它们不回写 raw history。

## Ownership

| Assembly | Owns |
|---|---|
| `SessionJournal` | typed raw input、selected Parent lineage、setup authority、bounded planning/audit、neutral context lifecycle 与请求时 host projection 接缝 |
| `SessionJournal.HistoryTimeline` | 单一 HistoryRowId、schema 3 rows、selected-path head、policy、branch reconcile、owner-bound build reads 与显式 UpgradeSchemaV2 |
| `SessionJournal.HistoryTimeline.O200k` | fixed o200k estimator、renderer 与 tokenizer adapter |
| `SessionJournal.RecapGrid.Cadence` | per-Ref R/expected Timeline policy、reserve-aware seal authority、strict CAS/no-create reader |
| `SessionJournal.RecapGrid` | 以 namespace/source module 分隔的 规则 canonical contracts、普通结果模型、Control、Store、Manager、Runtime、Getter、Online 与 AgentControl；source-module 依赖由 RG0001/RG0002 守门 |
| `SessionJournal.RecapGrid.Hosting` | strict completion/route composition、single connection owner、runtime lifetime |
| CLI / Galatea | operator surface、application phase gate、provider and UI composition |

## Key code and focused evidence

| Concern | Owner / tests |
|---|---|
| bounded raw history and lifecycle audit | `SessionJournal`, `SessionJournal.Tests` |
| durable Timeline and branch reconcile | `HistoryTimeline`, `HistoryTimeline.Tests` |
| fixed o200k history-load estimation | `HistoryTimeline.O200k`, `HistoryTimeline.Tests` |
| durable cadence and recent reserve | `RecapGrid.Cadence`, `RecapGrid.Cadence.Tests` |
| Grid rules、Slot 与普通结果 ID | `RecapGrid/Abstractions`, `RecapGrid.Abstractions.Tests` |
| Control state and receipts | `RecapGrid/Control`, `RecapGrid.Control.Tests` |
| SQLite artifact Store | `RecapGrid/Store`, `RecapGrid.Store.Tests` |
| wavefront build/progress | `RecapGrid/Manager`, `RecapGrid.Manager.Tests` |
| provider-neutral runtime | `RecapGrid/Runtime`, `RecapGrid.Runtime.Tests` |
| pure-read context selection | `RecapGrid/Getter`, `RecapGrid.Getter.Tests` |
| online lifecycle | `RecapGrid/Online`, `RecapGrid.Online.Tests` |
| formal CLI / Galatea composition | `SessionJournal.Cli.Tests`, `Galatea.Server.Tests` |
| dependency and retired-owner absence | `SessionJournal.RecapGrid.WalkingSkeleton.Tests` |

新请求使用 [CompletionRequestPrepared v9](contracts/completion-request-prepared-v9.md)：Observation/Setup v2
保存 `SessionInputContent`，Prepared 保存语义计划，Started v2 保存本次 canonical request v2 的长度与摘要。
新计划不保存渲染文本；核心通过 host 提供的 `ISessionInputProjector` 在发送前投影 structured 输入。
查询、setup 比较、undo、exact append proof、重开与审计不需要 projector。

[历史 v7/v8](contracts/completion-request-prepared-v7.md)继续按旧 recipe 重建 exact request，恢复不调用新输入
projector；原 Observation/Setup v1 读成明确的 Text。独立 v5 decoder/verifier 保留 append-only history 与
Action-address provenance，但不能生成可发送的请求。旧 tag 的验证证据不自动覆盖这些新格式或全域接入。

## Authority and recovery rules

- Selection and materialization always bind exact repository, `RefId`, Timeline whole head,
  Control whole head and Store identity; no latest/global scan or cross-handle fallback is an authority source.
- A missing active recipe or an empty Timeline is a raw-only state and does not open the Grid Store or provider.
- A non-empty active recipe with missing current fulfillment is `Unfulfilled`; Online may invoke Manager only at
  an allowed lifecycle boundary.
- Timeline writers must enter a Cadence-owned reserve-aware seal operation. Getter validates exact Cadence and
  Timeline policy, then selects the latest healthy R-eligible fulfillment; healthy bootstrap shortage is a
  distinct `ReserveBootstrapRawOnly` state rather than `Unfulfilled` fallback.
- v9 及历史 v7/v8 Prepared/Started recovery 都按已保存的 connection/protocol/tool identity 恢复，不重新选 recap。
  v9 先纯验证语义计划，再按 uncertain policy 决定是否可投影/发送；换 renderer 不产生重试权限。
  历史 v7/v8 继续 exact 校验。Host 在 Prepared recovery 不打开 derived owners，在 Started Refuse 时不构造连接。
  Historical v5
  Prepared/Started is commitment-verified and then fails closed before a frozen requirement, client binding,
  provider call, or journal write.
- Timeline/Control/Store failures remain typed Busy/Stale/Invalid/Unsupported/Indeterminate outcomes. Hosts do
  not message-map exceptions or blindly retry.
- Old `derived/recap` generations are inert legacy data. The formal legacy-root operator is the only path that
  inventories, archives or confirms their deletion; normal Grid operation never reads them.

## Current boundaries

- WP-08 source implementation and independent closure are complete. C2D later completed a separately bounded
  real-provider canary and local actual cyber repository activation; it did not rewrite WP-08 source evidence.
- A fresh no-local checkout ran for the containing source candidate. It does not replace either independent source
  closure or the later machine-local C2D activation evidence.
- Operator provisioning/composition/activation is explicit. There is no implicit autobiographical or
  world-understanding default roster.
- The formal CLI surface is `recap-grid ...` plus top-level `run-online-turn`; Galatea owns one
  `RecapGridCompletionHost` and one formal RecapGrid composition.
- Provider cache/economic claims require a real authenticated canary and cannot be inferred from deterministic
  tests. C2D has such evidence for the current V3/Opus route; future provider, route or prompt revisions need fresh evidence.

See [concepts](derived-recap/concepts.md), [durable target](derived-recap/durable-target.md),
[HistoryLoad](derived-recap/history-load.md), and [host integration](host-integration/derived-recap-host-integration.md).
