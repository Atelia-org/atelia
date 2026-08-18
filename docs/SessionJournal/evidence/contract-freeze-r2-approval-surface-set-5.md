# SessionJournal Contract Freeze R2 — additive surface set 5 approval

状态：**user approval recorded；promotion docs candidate；unified gates、independent promotion review与annotated tag Pending**  
production/test source：`4e1e80e6875a3a963bd90c3845250da261548730`  
candidate appendix/docs commit：`6ed308f0268d8e337753252aad0d2ad4f5039eb8`  
candidate independent final review：PASS  
authorized tag：`session-journal-contract-r2-approved-surfaces-v5`（尚未创建）  
记录日期：2026-08-18

本文只记录用户在immutable surface sets 1至4之上新增批准的Cadence `set-reserve` command-local receipt与recovery
boundary。Surface set 5完整继承既有批准与non-promises，但不替换、移动、重释或续期v1/v2/v3/v4 tags；它也不把
Cadence owner、durable wire或共享CLI printer提升为新承诺。

## 1. Approved additive surface

Surface set 5只新增下列Tier C operational command contract：

1. 在既有RecapGrid CLI outer report内，exact `command`为`cadence.set-reserve`；本批准只覆盖下表command-local
   `status/detail/exit`，不新增generic outer envelope承诺。

   | Status | Exit | Exact decoded `detail` |
   |:--|--:|:--|
   | `updated` | 0 | `{head,minimumRecentHistoryLoad}` |
   | `unchanged` | 0 | `{head,minimumRecentHistoryLoad}` |
   | `stale` | 2 | `null` |
   | `absent` | 2 | `null` |
   | `busy` | 2 | `null` |
   | `disposed` | 2 | `null` |
   | `platform-unsupported` | 2 | `null` |
   | `unsupported-schema` | 2 | `{version}` |
   | `invalid` | 2 | `{code}` |
   | `commit-indeterminate` | 2 | `{expectedHead,intendedHead,minimumRecentHistoryLoad}` |

2. Success detail的R是Int64；head恰有string `refId`、Int64 integer `generation`与string `domainDigest`。
   `updated`表达CAS返回的updated snapshot，`unchanged`表达expected-head snapshot已具有requested R且不为相同policy
   重写Cadence。Unsupported `version`是integer；invalid `code`是string。
3. `set-reserve`只修改R并保留B及其余Cadence policy。若R+B不能由current policy表示，CAS前exact返回exit 2、
   `invalid`与code-only `CadenceReserveRangeInvalid`，不写Cadence。其他invalid也只投影code，不暴露owner
   message/path/exception；本批准不关闭整个invalid code namespace。
4. Minimal receipt不是Cadence authority。stdout loss、success receipt loss或`commit-indeterminate`后，第一步必须fresh
   `cadence inspect`完整head与policy，command不得自动retry。对于indeterminate receipt：
   - current head exact等于`intendedHead`且R匹配，desired visible，停止；
   - current head exact等于`expectedHead`，change未visible；operator人工确认完整policy与intent后，才可same-expected、
     same-R exact retry；
   - 其他head、intended-head/R mismatch或inspect不可用/invalid/unsupported均视为conflict/unproven并停止。
5. 已提交的updated request若用old expected head重试，会`stale`且不产生第二次mutation；这只是fail-closed CAS
   recovery fact，不是automatic retry许可。完全丢失stdout且没有可信`intendedHead`时，仅R相等不能证明mutation ownership。

Exact field meanings与operator wording见
[Cadence receipt appendix](../current/contracts/cadence-set-reserve-receipt.md)。批准的是上述decoded command-local
ledger、privacy与inspect-based recovery rule，不是receipt、raw或Cadence之间的transaction authority。

## 2. Explicit non-promises

Surface set 5明确不批准：

- generic RecapGrid CLI envelope/printer的新语义、其他non-Store command或syntax/confirmation/argument detail；
- Cadence durable V1 wire、canonical sidecar bytes、owner open/read/CAS contract、migration/reprovision或full inspect wire；
- Cadence owner public result hierarchy、`CommitIndeterminate.Observed`、exceptions、source/binary compatibility或
  arbitrary CLR exports；
- 除`CadenceReserveRangeInvalid`外的complete/future `invalid.code`集合，或code作为recovery authority；
- JSON property order、whitespace、escaping、terminal newline、canonical bytes或byte identity；
- complete CLI input/path accepted language、stdout/stderr、human diagnostic、stack trace或filesystem exception taxonomy；
- receipt/raw、receipt/Cadence或stdout/filesystem atomicity、rollback、exactly-once、automatic retry或receipt-as-commit-record；
- current operator data、ignored state、provider behavior、deployment readiness、physical storage bytes或其他candidate。

## 3. Evidence与Pending boundary

- `4e1e80e6`是production与owning contract tests的source pin；`6ed308f0`是candidate appendix与first-party consumer docs
  pin；candidate independent final review已PASS。
- 本promotion只修改docs，不修改production、tests、operator state或tag。
- 本轮unified CLI/solution/build/Node/docs gates尚未运行或登记；promotion docs independent review尚未完成。不得复制
  surface set 4或candidate implementation package的counts作为本轮结果。
- Public inventory、disposable rebuild、ignored operator state、provider与deployment均NotRun；是否需要前两项由tag前
  delta review裁决，后三项不因批准一个provider-free CLI receipt而自动成为gate。

因此当前状态只是**user-authorized promotion candidate**，不是tagged/anchored completion。

## 4. Immutable anchors与tag-before checklist

Immutable prior dereferenced targets继续为：

- v1 `6378cebbde4cf150ecb4d8de5699ef1f77ce4f0b`；
- v2 `c4c6dd1698c7460fbf8ff3563d7800203f3202e0`；
- v3 `adf547e2a2319fd3009a7015a4289ab875af43f7`；
- v4 `0dac57a9e32ae5d0367394404524404689dfa4ef`。

创建annotated tag前必须：

1. 在exact clean promotion HEAD上完成并记录本轮选择的unified gates；
2. 由独立reviewer核对本addendum、current appendix/contract、routers、active plan与CLI guide没有扩大§1；
3. 确认v1至v4 targets未移动，`session-journal-contract-r2-approved-surfaces-v5`仍不存在，并记录reviewed gate ledger；
4. annotated tag message同时pin production/test `4e1e80e6`、candidate docs `6ed308f0`、promotion/gate ledger、§1 exact
   scope与§2 non-promises；
5. tag创建后另做post-tag status docs commit；不得移动tag吸收post-tag文档。

