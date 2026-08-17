# SessionJournal Contract Freeze R2 — additive surface set 3 approval

状态：**user approval recorded；promotion docs commit pending；unified gates Pending；annotated tag authorized / pending**  
production source：`da3aa27af56add07bc70229120c522b8d24c99ba`  
contract test evidence：`8a54e613f7c1a92bab3a4dd0806aad19411c41b1`  
candidate appendix commit：`60904628f90f203cb59eebfb5bcc8438e33aebaa`  
authorized tag：`session-journal-contract-r2-approved-surfaces-v3`（尚未创建）  
记录日期：2026-08-17

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
- production/test commits已形成candidate evidence，但本promotion draft只修改docs。Tag前必须在exact code/test source
  `8a54e613`加docs-only promotion HEAD上完成本轮统一验证；所有本轮gate当前均为**Pending**，不得抄用surface-set-2
  或其他historical green counts；
- provider/deployment、ignored operator state不读取、不运行，也不从未来tag前gates推导。Public inventory与disposable
  rebuild不因本docs promotion自动续期；若tag前review判定无需，必须明确记录NotRun/理由，而不能借用旧结果。

## 4. Tag-before checklist

1. containing promotion docs commit必须先产生；当前commit ID为Pending，待提交后记录；
2. exact source/test baseline `8a54e613` + docs-only promotion HEAD上的SessionJournal CLI owner suite、full solution
   test/build、scoped docs checker、link/diff/status checks与independent scope review必须全部完成；当前结果均Pending；
3. tag前review必须明确记录HTTP/SSE Node、inventory、disposable rebuild及provider/deployment为何Run或NotRun；不得将
   surface-set-1/2 counts复制为本次运行；
4. annotated tag必须exact命名为`session-journal-contract-r2-approved-surfaces-v3`并指向包含final gate ledger、已通过
   independent review的promotion docs commit；
5. tag message必须pin production source `da3aa27a`、contract test evidence `8a54e613`、candidate appendix
   `60904628`、§1 exact scope、§2 non-promises，以及immutable v1/v2 tags不移动；
6. 创建tag前再次确认同名tag不存在、worktree无本包遗漏并核对target；创建后另行记录exact tag object/target。

在以上checklist完成前，surface set 3的用户授权已经记录，但unified gates与annotated tag必须保持Pending；任何文档
都不得声称该tag已经创建或用授权反向认证surface-set-2 tag之后的整个repository HEAD。
