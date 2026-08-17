# HistoryLoad calibration report V2 approved top-level contract

状态：**surface set 4 exact top-level/read-only scope user-approved；unified gates、review与tag Pending**  
production/test source：`881afb39af511567b8bb900c5db103426791ab95`  
approval boundary：不属于immutable v3 tag；authorized v4 tag尚未创建

本文定义`recap-grid timeline history-load inspect --report-json` current producer输出的V2 machine-readable
calibration report。Surface set 4只批准§1的exact top-level field set/types/meanings、V1字段删除与§4的read-only
publication/retry semantics；它不是raw、Timeline、cadence或provider authority。其余实现细节仍是candidate事实。

## 1. Producer、schema与exact top-level field set

唯一production producer是
[`RecapHistoryLoadCommands`](../../../../prototypes/SessionJournal.Cli/RecapHistoryLoadCommands.cs)，field-set/type与
read-only oracle是
[`ProgramRecapHistoryLoadCommandTests`](../../../../tests/SessionJournal.Cli.Tests/ProgramRecapHistoryLoadCommandTests.cs)。

Root必须是JSON object，`schema`必须exact等于
`atelia.session-journal.recap-history-load-calibration.v2`，且必须恰有下列11个decoded property names；missing、extra、
wrong-case或wrong-type均不属于V2：

| Field | JSON type | Current exact meaning |
|:--|:--|:--|
| `schema` | string | exact V2 identifier |
| `estimatorId` | string | 本次measurement实际使用的`IHistoryUnitLoadEstimator.Id` |
| `branchName` | string | read-only engine本次open的exact branch name |
| `branchRefId` | string | selected branch的current RefId hex text |
| `capturedHead` | string | full planning window捕获的exact observed raw head；使用SessionJournal `EventAddress` text codec |
| `baseline` | string | `HistoryLoadProjection.BaselineAddress`；totals、units与boundaries相对该address计算 |
| `totals` | object | baseline-relative raw/unit/boundary counts，以及whole-window HistoryLoad与rendered UTF-8 bytes |
| `byKind` | array | 按`HistoryMessageKind`排列的HistoryUnit count/load/rendered-byte aggregates |
| `unitDistributions` | object | 单个HistoryUnit的HistoryLoad与rendered-byte nearest-rank distributions |
| `units` | array | zero-based ordered full-window HistoryUnits与每unit source range/load/rendered bytes |
| `boundaries` | array | ordered replay-safe raw boundaries及其baseline-relative absorbed unit/load progress |

V2删除V1的`continuousWindowLoadDistributions`；producer不dual-write该field，也不提供V1/V2 report reader。

## 2. Nested current shape与semantic invariants

- `totals`恰由`rawEvents`、`historyUnits`、`replaySafeBoundaries`（Int32），`historyLoad`（Int64）与
  `renderedUtf8Bytes`（Int32）组成。`rawEvents`是planning window的baseline-relative raw address count，不是physical
  repository file/frame count；`historyLoad`不是provider/model token usage；
- `byKind[]` element包含`kind` string、`historyUnits` Int32、`historyLoad` Int64与`renderedUtf8Bytes` Int64；
- `unitDistributions`包含`historyLoad`与`renderedUtf8Bytes`两个distribution objects。每个object包含exact method
  `nearest-rank`、Int32 `count`，以及nullable Int64 `min/p50/p75/p90/p95/p99/max`；空input时percentile fields为null；
- `units[]` element包含Int32 `ordinal`、string `kind`、EventAddress strings `sourceStartInclusive` /
  `sourceEndInclusive`、Int64 `load`与Int32 `renderedUtf8Bytes`。Ordinals从0开始并服从window order；
- `boundaries[]` element包含EventAddress string `address`、Int32 `completedHistoryUnitCountSinceBaseline`与Int64
  `absorbedHistoryLoadSinceBaseline`。最后一个boundary只在current measurement实际产生时表达whole suffix progress；
  consumer不得凭数组非空假设某个固定unit/raw比例。

这些是current V2 producer的nested decoded shape与meaning，但**不属于surface set 4批准范围**；本批准不冻结nested
key set/type、record declaration order、serializer byte output、精确percentile member language或任意历史fixture的
counts/distribution values。未来nested evolution必须另立candidate与consumer review，不能把v4 tag解释为预先批准。

## 3. Full-window、unbounded与offline boundary

Producer直接调用无参数的full `SessionJournalEngine.ReadHistoryPlanningWindow()`，再对整个window执行
`HistoryLoadProjector.Measure`。`units` materialize每个HistoryUnit，`boundaries` materialize每个replay-safe boundary；
report work、memory与最终JSON size会随selected lineage增长。

Current command没有raw/unit/boundary page、cursor、work budget、final encoded JSON byte cap或bounded fallback，也没有稳定的
`Oversize` result/exit semantics。`--report-json`走通用atomic JSON writer，但writer不会在serialization前证明final size。
因此本report只适合作为显式offline operator/calibration action，不能直接当成request-path、online readiness或bounded
service contract。未来若需要online/bounded consumer，应另立分页/摘要/oversize contract，而不是假装V2已经bounded或
只在文档里补一个任意数字。

## 4. Read-only、success与failure recovery

Command以`SessionJournalEngine.OpenReadOnly`打开selected branch，在engine scope内读取/measure full planning window；关闭
engine后才选择性publish report。Current tests锁定repository files/bytes不变、不创建`derived`或`config`、Completion
client construction与provider calls均为0。

提供`--report-json`时，exit 0只在report成功写出后返回。Report publication failure不会修改raw repository或任何derived
owner state，也没有需要rollback的repo mutation；operator可以安全重新执行read-only inspect。重跑必须把新report的
`capturedHead`当作本次witness：若selected branch在两次运行之间前进，不能期待旧bytes、counts或head继续相等。

Report path位于repository内会被拒绝；complete path/input accepted language、filesystem exception taxonomy与diagnostic
文本不属于本candidate。Atomic leaf replacement也不等于cross-filesystem durability、create-only或hostile-directory
writer defense。

## 5. Consumer、privacy与explicit non-promises

仓内没有production V2 report reader，也没有compat parser或dual writer。First-party activation runbook必须先验证exact
schema、decoded exact 11-field set与top-level types，之后才能读取`capturedHead`并与本轮raw head比较；不得对unknown、
missing或wrong-type做best-effort fallback。

Report不包含message/tool/prompt正文、Completion connection、call log或provider response，但包含branch/RefId、raw event
addresses、per-unit kinds、source ranges、load与rendered-byte measurements。这些是content-free operational metadata，
仍可能泄露会话长度、结构与变化，不应自动视为可公开。

本contract不承诺：

- V1 compatibility、V1 continuous-window fields、unknown-field tolerance或generic Other-report envelope；
- JSON property order、whitespace、escaping、terminal newline、canonical bytes或byte identity；
- final byte cap、bounded work/memory、pagination、truncation、oversize status/exit或online latency；
- complete CLI input/path language、stdout/stderr、diagnostic逐字文本或failure type/exit的完整分类；
- 历史fixture counts、R/B建议、provider token equivalence、provider/deployment readiness或current operator state；
- report作为raw/Timeline/cadence authority，或任何未列入本文的future field。

2026-07-31的
[`history-load-galatea-calibration.md`](../../evidence/history-load-galatea-calibration.md)是V1 single-fixture历史证据，
保持原样且不认证V2 current output。本文形成于immutable surface-set-3 tag之后；用户已明确批准
[surface set 4 addendum](../../evidence/contract-freeze-r2-approval-surface-set-4.md)圈定的窄scope，但unified gates、
independent review与authorized v4 tag仍Pending，且该批准不反向移动或扩大v3 tag。
