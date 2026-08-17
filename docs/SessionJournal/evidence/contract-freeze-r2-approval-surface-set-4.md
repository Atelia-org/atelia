# SessionJournal Contract Freeze R2 — additive surface set 4 approval

状态：**approval complete；unified gates与independent review complete；annotated surface set 4 tag anchored**  
production/test source：`881afb39af511567b8bb900c5db103426791ab95`  
candidate appendix commit：`2fa9808bfac0d8836da490548e9b3c98c38f2395`  
promotion docs / unified gate candidate：`494d215ad2d3504b6e997b8ea2bf13fb3e50270c`  
approval tag：`session-journal-contract-r2-approved-surfaces-v4`  
tag object：`76dcdc7010f5899fbd4238757cc387a2de140b13`  
dereferenced target：`0dac57a9e32ae5d0367394404524404689dfa4ef`  
记录日期：2026-08-18

本文只记录用户在immutable surface sets 1、2、3之上新增批准的HistoryLoad calibration report V2窄surface。
它不移动、重释或续期v1、v2、v3 tags；前三个tags继续只认证各自原始targets。本文也不把candidate
implementation的全部shape、全部CLI行为或historical calibration values升级为承诺。

## 1. Approved additive surface

Surface set 4只新增下列producer-only Tier C operational report contract：

1. `recap-grid timeline history-load inspect --report-json`的成功V2 report是JSON object，`schema` exact为
   `atelia.session-journal.recap-history-load-calibration.v2`；decoded root恰有11个case-sensitive top-level names：
   `schema`、`estimatorId`、`branchName`、`branchRefId`、`capturedHead`、`baseline`、`totals`、`byKind`、
   `unitDistributions`、`units`、`boundaries`。
2. 前六个字段是string；`totals`与`unitDistributions`是object；`byKind`、`units`与`boundaries`是array。
   各字段meaning以
   [HistoryLoad report V2 appendix §1](../current/contracts/history-load-report-v2.md#1-producerschema与exact-top-level-field-set)
   的exact table为准：它们分别标识schema、actual estimator、selected branch/ref、captured raw head、projection
   baseline，以及whole-window totals、per-kind aggregates、unit distributions、ordered units和replay-safe boundaries。
3. V2删除V1的`continuousWindowLoadDistributions`。Current producer只写V2，不dual-write该字段，也不提供V1/V2
   report reader或compatibility interpretation。
4. Command以read-only engine读取raw authority；成功report只是本次观察的operational receipt，不反向修改raw或任何
   derived owner state。提供`--report-json`时，exit 0只在report publication成功后返回。Publication失败不改变
   repository，因此可安全重新执行read-only inspect；重跑必须使用新report的`capturedHead`作为fresh witness，不能
   盲用旧head、counts或bytes。

批准的是上述decoded top-level field set、JSON types、meanings、V1字段删除和read-only publication/retry semantics。
它不批准report为raw、Timeline、cadence或provider authority。

## 2. Explicit non-promises

Surface set 4明确不批准：

- nested objects或array elements的exact key set、field type、member order、percentile member language或未来nested
  evolution policy；appendix §2只是current implementation/candidate导航；
- bounded work、memory、最终encoded bytes、online latency、pagination、cursor、truncation或stable oversize result/exit；
- JSON property order、whitespace、escaping、terminal newline、canonical bytes或byte identity；
- complete CLI input/path accepted language、stdout/stderr、diagnostic逐字文本、filesystem exception taxonomy或完整
  failure/exit matrix；
- current operator data、historical fixture counts、R/B建议、provider token equivalence、real-provider或deployment
  readiness；
- 其他Other reports、non-Store CLI details、public .NET API、raw/companion wire、physical repository bytes、generic
  report envelope、V1 reader或compatibility layer。

2026-07-31的
[`history-load-galatea-calibration.md`](history-load-galatea-calibration.md)继续是V1 single-fixture historical evidence；
surface set 4不修改、重释或认证其中的历史values，也不把它升级为current deployment evidence。

## 3. Evidence与verification boundary

- `881afb39`是V2 producer与owning contract tests的source pin；`2fa9808b`是consumer gate与candidate appendix pin。
- `494d215a`只准备promotion docs，不修改production/tests，并作为本轮exact clean unified gate candidate；以下结果不是
  surface set 3或HISTORY-LOAD-REPORT-A1旧counts的复制。
- public inventory与legacy rebuild为**NotRun / 本次无需**：本promotion没有.NET API、raw wire或rebuild semantics
  delta。Ignored operator state、real provider与deployment为**NotRun**，不因批准一个content-free report contract而
  自动成为gate。
- independent pre-tag review已通过，确认批准范围未扩大、historical V1 evidence未重释且v1/v2/v3 tags未移动。

### 3.1 Unified gate ledger at `494d215a`

| Gate | Result |
|:--|:--|
| `dotnet test tests/SessionJournal.Cli.Tests/SessionJournal.Cli.Tests.csproj --no-restore -m:1 -nr:false` | 113 passed / 0 failed / 0 skipped；test duration 1m41s；command elapsed 140.36s |
| `dotnet test Atelia.sln --no-restore -m:1 -nr:false` | 38 projects / 4,694 passed / 0 failed / 0 skipped；command elapsed 1,282.48s |
| `dotnet build Atelia.sln --no-restore -m:1 -nr:false` | 0 warnings / 0 errors；MSBuild 16.93s；command elapsed 17.32s |
| production HTTP Node contract suite | 1 passed / 0 failed / 0 skipped；command elapsed 0.76s |
| production SSE Node contract suite | 1 passed / 0 failed / 0 skipped；command elapsed 0.34s |
| scoped docs checker | 18 files / 0 diagnostics |
| candidate diff / status / tag preflight | diff clean；worktree clean；v4 tag absent；immutable v1/v2/v3 targets仍为`6378cebb` / `c4c6dd16` / `adf547e2` |
| public inventory / disposable legacy rebuild | NotRun / 本次无需；无.NET API、raw或rebuild semantics delta |
| ignored operator state / provider / deployment | NotRun；不属于本promotion gate |

所有命令均使用首次选择的正确path/arguments通过，没有retry或命令calibration。随后independent review通过，且
annotated v4 tag已锚定包含本gate ledger的`0dac57a9`。

## 4. Tag closure record

1. exact clean promotion HEAD `494d215a`上的unified gates已按§3.1完成；
2. independent reviewer已核对本addendum、current contract、appendix、routers、active plan与runbook没有扩大批准范围，且
   v1/v2/v3 tags及historical V1 evidence均未改变；
3. annotated tag已exact创建为`session-journal-contract-r2-approved-surfaces-v4`；tag object为
   `76dcdc7010f5899fbd4238757cc387a2de140b13`，dereferenced target为包含final gate ledger的
   `0dac57a9e32ae5d0367394404524404689dfa4ef`；
4. tag message pin production/test source `881afb39`、candidate appendix `2fa9808b`、promotion/gate ledger、approved exact
   top-level/read-only scope、§2 non-promises与immutable prior tags；
5. v1/v2/v3 dereferenced targets仍为`6378cebbde4cf150ecb4d8de5699ef1f77ce4f0b`、
   `c4c6dd1698c7460fbf8ff3563d7800203f3202e0`与`adf547e2a2319fd3009a7015a4289ab875af43f7`。

本post-tag status commit只记录已经发生的closure；v4 tag继续指向`0dac57a9`，不会因本commit或未来docs commit而
反向移动、续期product/provider/deployment evidence或扩大§1批准范围。
