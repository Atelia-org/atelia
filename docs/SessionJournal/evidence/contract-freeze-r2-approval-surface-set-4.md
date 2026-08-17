# SessionJournal Contract Freeze R2 — additive surface set 4 approval

状态：**user approval recorded；promotion docs candidate；unified gates与independent review Pending；annotated tag authorized / Pending**  
production/test source：`881afb39af511567b8bb900c5db103426791ab95`  
candidate appendix commit：`2fa9808bfac0d8836da490548e9b3c98c38f2395`  
authorized tag：`session-journal-contract-r2-approved-surfaces-v4`（尚未创建）  
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

## 3. Evidence与Pending boundary

- `881afb39`是V2 producer与owning contract tests的source pin；`2fa9808b`是consumer gate与candidate appendix pin。
- 本commit只准备promotion docs，不修改production、tests、operator state或tag。
- 本轮unified solution/CLI/Node/docs gates尚未运行或登记；independent pre-tag review尚未完成。历史surface set 3或
  HISTORY-LOAD-REPORT-A1的counts不能复制为本次gate结果。
- public inventory、legacy rebuild、ignored operator state、real provider与deployment均NotRun；是否需要前两项由tag前
  delta review裁决，后四项不因批准一个content-free report contract而自动成为gate。

因此当前状态只是**user-authorized promotion candidate**，不是tagged/anchored completion。

## 4. Tag-before checklist

创建annotated tag前必须：

1. 在exact clean promotion HEAD上完成并记录本轮选择的unified gates与结果；
2. 由独立reviewer核对本addendum、current contract、appendix、routers、active plan与runbook没有扩大批准范围，且
   v1/v2/v3 tags及historical V1 evidence均未改变；
3. 确认`session-journal-contract-r2-approved-surfaces-v4`仍不存在，记录promotion docs commit与tag target；
4. annotated tag message同时pin production/test source `881afb39`、candidate appendix `2fa9808b`、promotion/gate ledger、
   approved exact top-level/read-only scope和§2 non-promises；
5. tag创建后另做post-tag status docs commit；不得移动tag来吸收post-tag文档。

