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
| `SessionJournal` | raw replay、selected Parent lineage、setup authority、bounded planning/audit、neutral context lifecycle |
| `SessionJournal.HistoryTimeline` | immutable timeline rows、selected-path head、policy、branch reconcile、owner-bound build reads |
| `SessionJournal.RecapGrid.Cadence` | per-Ref R/expected Timeline policy、reserve-aware seal authority、strict CAS/no-create reader |
| `SessionJournal.RecapGrid.Abstractions` | Family/Definition/Recipe、projection、cell、row-view 与 fulfilled-key canonical contracts |
| `SessionJournal.RecapGrid.Control` | registered families/definitions/recipes、active recipe、operation receipts、whole-head CAS |
| `SessionJournal.RecapGrid.Store` | cells、row views、fulfilled entries、store identity、verification/reset |
| `SessionJournal.RecapGrid.Manager` | recipe closure wavefront、exact row-build derivation、progress、fulfillment proof |
| `SessionJournal.RecapGrid.Runtime` | provider-neutral batch executor、route/lane scheduling、strict output protocol |
| `SessionJournal.RecapGrid.Getter` | exact active+head fulfilled resolution、NthPrevious、context contribution materialization |
| `SessionJournal.RecapGrid.AgentControl` | strict `recap_grid.control` tool、built-in asset catalog、operation replay |
| `SessionJournal.RecapGrid.Hosting` | strict completion/route composition、single connection owner、runtime lifetime |
| `SessionJournal.RecapGrid.Online` | Timeline reconcile/seal、readiness、lazy build 与 composite lifecycle |
| CLI / Galatea | operator surface、application phase gate、provider and UI composition |

## Key code and focused evidence

| Concern | Owner / tests |
|---|---|
| bounded raw history and lifecycle audit | `SessionJournal`, `SessionJournal.Tests` |
| durable Timeline and branch reconcile | `HistoryTimeline`, `HistoryTimeline.Tests` |
| durable cadence and recent reserve | `RecapGrid.Cadence`, `RecapGrid.Cadence.Tests` |
| canonical Grid values | `RecapGrid.Abstractions`, `RecapGrid.Abstractions.Tests` |
| Control state and receipts | `RecapGrid.Control`, `RecapGrid.Control.Tests` |
| SQLite artifact Store | `RecapGrid.Store`, `RecapGrid.Store.Tests` |
| wavefront build/progress | `RecapGrid.Manager`, `RecapGrid.Manager.Tests` |
| provider-neutral runtime | `RecapGrid.Runtime`, `RecapGrid.Runtime.Tests` |
| pure-read context selection | `RecapGrid.Getter`, `RecapGrid.Getter.Tests` |
| online lifecycle | `RecapGrid.Online`, `RecapGrid.Online.Tests` |
| formal CLI / Galatea composition | `SessionJournal.Cli.Tests`, `Galatea.Server.Tests` |
| dependency and retired-owner absence | `SessionJournal.RecapGrid.WalkingSkeleton.Tests` |

## Authority and recovery rules

- Selection and materialization always bind exact repository, `RefId`, Timeline whole head,
  Control whole head and Store identity; no latest/global scan or cross-handle fallback is an authority source.
- A missing active recipe or an empty Timeline is a raw-only state and does not open the Grid Store or provider.
- A non-empty active recipe with missing current fulfillment is `Unfulfilled`; Online may invoke Manager only at
  an allowed lifecycle boundary.
- Timeline writers must enter a Cadence-owned reserve-aware seal operation. Getter validates exact Cadence and
  Timeline policy, then selects the latest healthy R-eligible fulfillment; healthy bootstrap shortage is a
  distinct `ReserveBootstrapRawOnly` state rather than `Unfulfilled` fallback.
- Frozen Prepared/Started recovery binds the frozen completion/tool identity before current configuration;
  Prepared performs no derived open and Started refuses before connection construction.
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
