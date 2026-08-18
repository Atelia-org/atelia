# Cadence `set-reserve` approved receipt contract

状态：**surface set 5 exact command-local scope user-approved；unified gates complete / tag-ready；promotion draft review PASS；final ledger re-review与tag Pending**  
production/test source：`4e1e80e6875a3a963bd90c3845250da261548730`  
approval boundary：不属于immutable v4 tag；authorized v5 tag尚未创建  
记录日期：2026-08-18

本文定义`recap-grid cadence set-reserve` current machine receipt与operator recovery rule。它是
`atelia.session-journal.recap-grid-cli.v1` outer report中的command-local `status/detail/exit` candidate，只最小表达
CAS结果；它不是Cadence durable authority，也不新增generic CLI result/envelope承诺。

## 1. Exact candidate ledger

Outer `command`是`cadence.set-reserve`。Command-local ledger为：

| Status | Exit | Exact `detail` | Meaning |
|:--|--:|:--|:--|
| `updated` | 0 | `{head,minimumRecentHistoryLoad}` | CAS已返回updated snapshot；receipt中的exact head与R来自该snapshot |
| `unchanged` | 0 | `{head,minimumRecentHistoryLoad}` | expected-head snapshot已经具有requested R；没有为相同policy重写Cadence |
| `stale` | 2 | `null` | expected full head不再current，或CAS观察到head竞争 |
| `absent` | 2 | `null` | Cadence owner不存在 |
| `busy` | 2 | `null` | owner open/read/CAS暂不可用 |
| `disposed` | 2 | `null` | reader/coordinator已disposed |
| `platform-unsupported` | 2 | `null` | current platform不支持owner open |
| `unsupported-schema` | 2 | `{version}` | owner报告不支持的integer schema version |
| `invalid` | 2 | `{code}` | owner/candidate validation失败；只公开machine code |
| `commit-indeterminate` | 2 | `{expectedHead,intendedHead,minimumRecentHistoryLoad}` | publication outcome未知；receipt不猜测最终current head |

成功detail恰有`head`与Int64 `minimumRecentHistoryLoad`。每个head是现有Cadence head description：
`{refId,generation,domainDigest}`，其中RefId与digest是string、generation是Int64 integer；recovery比较必须exact比较
全部三项，不能只比较generation。Indeterminate detail恰有
两个这样的head与requested Int64 R；不包含owner result的`Observed`、`nextAction`、message或exception。

Syntax、confirmation与argument failure仍走既有exit 1边界，不属于本command-local typed ledger。

## 2. Mutation、range与privacy boundary

`set-reserve`以operator提供的exact Ref、generation与domain digest组成expected head，只修改
`MinimumRecentHistoryLoad`（R），并保留partition algorithm、HistoryLoad estimator、`TargetHistoryLoad`（B）与segment
caps。Cadence threshold的authority仍是R+B与replay-safe admission，而不是raw event count、turn count或provider/model
token count。

若requested R与保留的B相加不能由current policy表示，command在CAS前返回exit 2：
`status:"invalid"`、`detail:{"code":"CadenceReserveRangeInvalid"}`，不写Cadence。其他`invalid`也只投影owner machine
code；owner message、path、exception与可能含敏感信息的diagnostic不得进入detail。本candidate不冻结所有future invalid
code值，也不把code当成recovery authority。

Minimal success receipt没有完整policy或canonical bytes。`recap-grid cadence inspect`读取的full current snapshot——exact
head、完整policy与canonical representation——才是恢复时的Cadence authority；receipt不能替代fresh inspect。

## 3. Lost output与commit-indeterminate recovery

stdout丢失或收到`commit-indeterminate`后，第一步必须重新运行fresh `cadence inspect`；command不得自动retry。对于
indeterminate receipt，使用其exact `expectedHead`、`intendedHead`与requested R按下表裁决：

| Fresh inspect observation | Operator conclusion / action |
|:--|:--|
| current head exact等于`intendedHead`，且full policy的R等于requested R | desired value visible；停止，不retry |
| current head exact等于`expectedHead` | 本次change未visible；operator人工确认Ref、完整policy与intent后，才可用same expected head与same R exact retry |
| current head是其他值 | concurrent/conflicting transition；停止，不retry，不猜测ownership |
| head等于`intendedHead`但R不匹配，或inspect不可用/invalid/unsupported | 无法证明desired state；停止并人工处理 |

即使command曾返回exit 0但stdout丢失，也必须fresh inspect，不能靠进程exit、旧stdout文件或最小receipt cache猜测current
authority。若已提交的updated request用old expected head再次调用，current implementation会返回`stale`且不会产生第二次
mutation；这只是fail-closed CAS性质，不是自动retry许可。若stdout完全丢失且operator没有可信的`intendedHead`，仅看到
R相等也不足以证明该mutation属于本次调用。

Receipt publication与Cadence durable transition不是atomic transaction；本candidate也不声称receipt与raw repository
atomic。Missing/partial/stale stdout不能证明“没有mutation”，但也不能覆盖fresh inspect的current authority。

## 4. Explicit non-promises

本candidate不冻结：

- Cadence owner public result hierarchy、`CommitIndeterminate.Observed`、exceptions或source/binary compatibility；
- 所有future `invalid.code`值、human diagnostic、stdout/stderr逐字文本或stack trace；
- JSON property order、whitespace、escaping、terminal newline、canonical bytes或byte identity；
- complete CLI input/argument/path accepted language、syntax/confirmation detail或generic outer envelope的新增语义；
- Cadence durable V1 wire、canonical sidecar bytes、owner open/read/CAS contract或migration/reprovision policy；
- receipt/raw、receipt/Cadence或stdout/filesystem的atomicity、rollback、exactly-once或automatic retry；
- current operator state、ignored config、provider/deployment readiness或任何未列出的non-Store CLI status/detail。

本文形成于immutable surface-set-4 tag之后；用户已明确批准
[surface set 5 addendum](../../evidence/contract-freeze-r2-approval-surface-set-5.md)圈定的exact command-local scope，但
unified gates已在`aebc4040`通过，promotion draft independent review已PASS；包含最终gate ledger的commit仍须
independent pre-tag re-review，authorized v5 tag仍Pending。该批准不会反向扩大、移动或重释v4 tag。
