# SessionJournal current architecture and code map

状态：current discovery map。源码、strict codecs与focused tests仍是最终事实。

## Mental model

```text
raw EventJournal events + selected RefId Parent lineage  (authority)
                         |
                 SessionJournalEngine
                         |
          bounded online planning / explicit paged audit
                         |
            DerivedRecap v8 shared epoch Store
                         |
             context candidate materialization
```

raw events是append-only事实源。`derived/recap/v8`是可删除、可重建sidecar；
`derived/recap/rebuild/v1`只是content-free execution aid。

## Ownership

| Assembly | Owns |
|---|---|
| SessionJournal | raw replay、selected Parent lineage、setup authority、bounded planning windows、paged audit/forward cursor、Send/Resume/context contracts |
| DerivedRecap.Abstractions | Maintainer epoch input、Updated/Keep、registry contract |
| DerivedRecap.Store | v8 canonical artifacts、atomic Building/final/publication、repair authority、selection/materialization、rebuild spool |
| DerivedRecap.Planner | NoBuild/Build policy、serial complete-roster kernel、multi-epoch campaign、explicit rebuild consumer、v3 config |
| DerivedRecap.Maintainers | family/member definitions、shared prompt/tool shape、structured output |
| DerivedRecap.Runtime | connection lane、family runtime group、bound maintainer |
| CLI / Galatea | Host composition、provider connection、operator surface |

## Key code and tests

| Concern | Owner | Focused tests |
|---|---|---|
| bounded raw history | `SessionHistoryPlanning.cs` | `SessionBoundedLineageTests.cs` |
| paged selected-lineage audit | `SessionJournalEngine.SelectedLineageAudit.cs` | `SessionSelectedLineageAuditTests.cs` |
| v8 wire | `DerivedRecapV8Contracts.cs`, `DerivedRecapV8Codec.cs` | `DerivedRecapV8CodecCandidateTests.cs` |
| v8 Store/recovery | `DerivedRecapEpochStore.cs` | `DerivedRecapEpochStoreCandidateTests.cs` |
| context candidate | `DerivedRecapContextCandidateSource.cs` | `DerivedRecapEpochStoreCandidateTests.cs` |
| rebuild spool | `DerivedRecapRebuildSpoolStore.cs` | `DerivedRecapRebuildSpoolTests.cs` |
| serial roster kernel | `DerivedRecapSerialEpochKernel.cs` | `DerivedRecapSerialEpochKernelTests.cs` |
| online/multi-epoch campaign | `DerivedRecapEpochCampaignExecutor.cs` | `DerivedRecapEpochCampaignExecutorTests.cs` |
| explicit rebuild execution | `DerivedRecapExplicitRebuildExecutor.cs` | `DerivedRecapExplicitRebuildExecutorTests.cs` |
| online lifecycle | `DerivedRecapOnlineLifecycleCoordinator.cs` | `DerivedRecapOnlineLifecycleCoordinatorTests.cs` |
| strict config v3 | `RecapEpochConfigDocument.cs` | `RecapEpochConfigCodecTests.cs` |
| Host CLI | `RecapExecutionCommands.cs`, `OnlineTurnCommand.cs` | `ProgramRecapV8CommandTests.cs`, architecture boundary tests |
| Galatea Host | `GalateaRecapComposition.cs` | Galatea server tests |

## Current flow

1. Host captures exact raw head and opens v8 Store with code-owned recovery caps.
2. Campaign first selects Building. Frozen snapshot validation replays exact Start→Admission raw commitment and governing setups.
3. Serial kernel pre-resolves every pending roster binding before the first call; healthy finals are skipped.
4. Updated writes new content；Keep copies the matching structured prior block；first-cycle Keep rejects。
5. complete roster publishes atomically with expected raw-head fence。
6. Only when no frozen recovery remains does the executor load active v3 and measure HistoryLoad。
7. `NoBuild` makes zero calls. `Build` freezes one shared slab and invokes the entire ordered roster。
8. One online operation may publish multiple contiguous epochs. Budget exhaustion after progress is MoreWorkPending, not Ready。
9. bounded online authority/growth failure is FullRebuildRequired and does not scan/spool。
10. explicit rebuild seals a paged raw audit, optionally resets v8, then consumes efficient bounded forward ranges through the same Store/kernel path。

## Recovery and authority rules

- Building install后prompt input self-contained；Resume/Restore不live-read previous publication。
- active config missing/invalid不阻止先完成frozen recovery；新planning才需要它。
- final/publication repair authority只来自missing或完整bounded captured damage；I/O、permission、oversize fail closed。
- publication ManifestWitness只修复missing/canonical-damaged envelope；path/manifest identity conflict不可降级。
- context candidate descriptor绑定exact publication、setup和completion raw head；strict ordinal不跳slot。
- spool seal不是raw authority本身；使用前仍需当前engine/read-view验证captured RefId/head/provenance。

## Current non-goals

- R4之前不做parallel/family cache scheduling；当前kernel故意serial。
- dynamic topology onboarding、不同member频率和retrieval working memory不在当前production机制内。
- R6 real-provider/staging acceptance尚未重跑；deterministic green不能表述为external acceptance。

详细规则见[concepts](derived-recap/concepts.md)、[durable target](derived-recap/durable-target.md)、
[planner config](derived-recap/planner-config.md)，以及Store/Planner/Maintainers各README。
