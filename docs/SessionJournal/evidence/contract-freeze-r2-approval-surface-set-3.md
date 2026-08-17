# SessionJournal Contract Freeze R2 — additive surface set 3 approval

状态：**user approval recorded；unified gates complete；tag-ready candidate；annotated tag authorized / pending**  
production source：`da3aa27af56add07bc70229120c522b8d24c99ba`  
contract test evidence：`8a54e613f7c1a92bab3a4dd0806aad19411c41b1`  
candidate appendix commit：`60904628f90f203cb59eebfb5bcc8438e33aebaa`  
promotion docs commit：`cb8ba5581c456fdc264005ce3d7a3eedda198430`  
authorized tag：`session-journal-contract-r2-approved-surfaces-v3`（尚未创建）  
记录日期：2026-08-18

本文只记录用户在immutable surface sets 1与2之后明确批准的一个additive surface：Desired Setup reconciliation
report V2。Surface set 3完整继承既有批准与non-promises，但不替换、移动或重新解释v1/v2 tags；特别是immutable
v2 tag继续只认证其原始approval ledger target `c4c6dd16`及当时的两个surface。

## 1. Approved additive surface

| Tier / surface | Exact approved scope | Upgrade / failure policy |
|:--|:--|:--|
| Tier C — [Desired Setup reconciliation report V2](../current/contracts/desired-setup-reconciliation-report-v2.md) | exact schema `atelia.session-journal.desired-setup-reconciliation.v2`；decoded exact 10-field set、JSON types与field meanings；`beforeHead`/`afterHead`、two changed flags与final governing setup invariants；lowercase SHA-256 of exact final prompt UTF-8；exit 0 report-publication boundary；producer-only strict V2 consumer rule与privacy field inventory | raw reconcile先于report publication，因此exit 1、missing/invalid/stale receipt不证明无raw mutation；重新inspect current exact head、Idle与governing setup，再以observed head幂等reconcile；breaking field/type/meaning change必须使用新schema与独立promotion，不提供V1/dual reader/writer |

该批准是一个narrow operational receipt contract，不把report提升为raw、governing setup、recovery或transaction
authority。Raw/report之间没有atomic transaction承诺；批准的是已记录的ordering与fail-closed operator action。

## 2. Explicit non-promises

Surface set 3明确不批准：

- offline validation、legacy import、history-load、legacy-root或任何其他`Other reports`的field/status/exit language；
- non-Store CLI commands的完整detail/status，或任意CLI通用result/report envelope；
- `reconcile-desired-setup`的完整command accepted language、connections/prompt inputs、`--system-prompt-file`的
  decode/BOM/newline/trim semantics、report path/filename或其他argument behavior；
- JSON property order、whitespace、indentation、escaping、terminal newline、canonical bytes或byte identity；
- stdout/stderr、human summary、exception/diagnostic逐字文本、exit 1细分类或stack trace；
- raw mutation与report publication之间的atomicity、rollback、exactly-once或report-as-commit-record semantics；
- create-only writer contract、file permissions/ownership、cross-filesystem durability或hostile same-directory writer defense；
- prompt text、provider request/content、endpoint/secret、real-provider behavior、deployment readiness或ignored operator state；
- blanket CLR public API、physical RBF/SQLite bytes、surface sets 1/2既有non-promises，或任何未在§1精确列出的surface。

Runbook的create-only report path是stale-receipt排除用的operator precondition；current production writer可overwrite是
implementation fact，不被本approval扩张成通用CLI accepted-language或filesystem durability promise。

## 3. Evidence与verification boundary

- production writer与ordering owner是
  [`DesiredSetupReconciliationCommand`](../../../prototypes/SessionJournal.Cli/DesiredSetupReconciliationCommand.cs)；
  exact field/type/meaning oracle是
  [`ProgramDesiredSetupReconciliationCommandTests`](../../../tests/SessionJournal.Cli.Tests/ProgramDesiredSetupReconciliationCommandTests.cs)；
- [V2 appendix](../current/contracts/desired-setup-reconciliation-report-v2.md)与
  [activation runbook](../operations/galatea-g2a-staging-acceptance.md#9-actual-activation-after-a-passed-disposable-candidate)
  记录consumer gate与failure recovery；它们不建立第二个executable parser或raw authority；
- production/test commits已形成candidate evidence，本promotion只修改docs。统一验证已在包含candidate appendix与
  promotion draft的exact clean HEAD `cb8ba558`上完成；以下结果只认证该candidate，不抄用surface-set-2或其他
  historical green counts；
- provider/deployment、ignored operator state不读取、不运行，也不从未来tag前gates推导。Public inventory与disposable
  rebuild为**NotRun / 本次无需**：本包没有.NET API、raw wire或rebuild semantics delta，旧结果也没有被续期。

### 3.1 Unified gate ledger at `cb8ba558`

| Gate | Result |
|:--|:--|
| `dotnet test tests/SessionJournal.Cli.Tests/SessionJournal.Cli.Tests.csproj --no-restore -m:1 -nr:false` | 113 passed / 0 failed / 0 skipped；test duration 1m26s；command elapsed 142.77s |
| `dotnet test Atelia.sln --no-restore -m:1 -nr:false` | 38 projects / 4,694 passed / 0 failed / 0 skipped；command elapsed 1,560.98s |
| `dotnet build Atelia.sln --no-restore -m:1 -nr:false` | 0 warnings / 0 errors；MSBuild 19.29s；command elapsed 19.66s |
| production HTTP Node contract suite | 1 passed / 0 failed / 0 skipped |
| production SSE Node contract suite | 1 passed / 0 failed / 0 skipped |
| scoped docs checker | 18 files / 0 diagnostics |
| candidate diff / status / tag preflight | diff clean；worktree clean；v3 tag absent；immutable v1/v2 targets仍为`6378cebb` / `c4c6dd16` |
| public inventory / disposable legacy rebuild | NotRun / 本次无需；无.NET API、raw或rebuild delta |
| ignored operator state / provider / deployment | NotRun；不属于本promotion gate |

HTTP与SSE均在首次命令中使用correct production test paths并通过，没有file-not-found、retry或其他命令calibration。

## 4. Tag-before checklist

1. containing promotion docs commit `cb8ba558`已产生；本gate ledger tail的commit ID待本次docs提交后记录；
2. exact clean promotion HEAD `cb8ba558`上的SessionJournal CLI owner suite、full solution test/build、HTTP/SSE Node、
   scoped docs checker、diff/status/tag preflight已按§3.1通过；
3. inventory、disposable rebuild、ignored operator state、provider/deployment的NotRun/无需边界已按§3.1明确记录，未复制
   surface-set-1/2 counts；
4. annotated tag必须exact命名为`session-journal-contract-r2-approved-surfaces-v3`并指向包含final gate ledger、已通过
   independent review的promotion docs commit；
5. tag message必须pin production source `da3aa27a`、contract test evidence `8a54e613`、candidate appendix
   `60904628`、§1 exact scope、§2 non-promises，以及immutable v1/v2 tags不移动；
6. 当前candidate已完成统一gates、达到independent pre-tag review入口；review仍须针对包含本ledger的docs commit PASS。
   创建tag前再次确认同名tag不存在、worktree无本包遗漏并核对target；创建后另行记录exact tag object/target。

Surface set 3现为gate-complete、tag-ready candidate；independent review与annotated tag仍Pending。任何文档都不得声称
该tag已经创建，或用授权/green gates反向认证surface-set-2 tag之后的整个repository HEAD。
