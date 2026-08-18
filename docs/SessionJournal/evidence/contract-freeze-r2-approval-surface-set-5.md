# SessionJournal Contract Freeze R2 — additive surface set 5 approval

状态：**surface set 5 anchored complete；post-tag docs independent review PASS**  
production/test source：`4e1e80e6875a3a963bd90c3845250da261548730`  
candidate appendix/docs commit：`6ed308f0268d8e337753252aad0d2ad4f5039eb8`  
candidate independent final review：PASS  
promotion docs / unified gate candidate：`aebc4040370029bedb1ed46e26423f079cbe59a9`  
approval anchor：`session-journal-contract-r2-approved-surfaces-v5`（tag object `e11000177af2877a9d7351dbb17d4bb6b591735e` → dereferenced target `89d61ba2c561d84eed235ee196b24d2016ecd3ff`）  
post-tag docs review：PASS（review object `845539c5b3dfe1a45295588cd1bdcf5d902c9e8e` + actual annotated tag）  
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

## 3. Evidence与anchored boundary

- `4e1e80e6`是production与owning contract tests的source pin；`6ed308f0`是candidate appendix与first-party consumer docs
  pin；candidate independent final review已PASS。
- `aebc4040`是promotion draft与本轮unified gate candidate；其independent draft review已PASS。Reviewed final gate
  ledger为`89d61ba2`，pre-tag review已关闭。
- 本promotion只修改docs，不修改production、tests、operator state或tag。

### 3.1 Unified gate ledger

下列结果均来自exact clean `aebc4040`，不是surface set 4或implementation package的旧counts：

| Gate | Result |
|:--|:--|
| `SessionJournal.Cli.Tests` full | 114 passed / 0 failed / 0 skipped |
| `dotnet test Atelia.sln --no-restore -m:1 -nr:false` | 38 projects / 4,695 passed / 0 failed / 0 skipped |
| `dotnet build Atelia.sln --no-restore -m:1 -nr:false` | 0 warnings / 0 errors；16.07s |
| Galatea production HTTP Node contract suite | 1 passed / 0 failed |
| Galatea production SSE Node contract suite | 1 passed / 0 failed |
| scoped SessionJournal docs checker | 18 checked / 0 diagnostics |
| diff、status与tag preflight | clean；v1-v4 dereferenced targets未移动；v5 tag不存在 |

第一次functions tool orchestration因脚本含`rm -f`而被安全层在`CreateProcess`前拒绝；它没有启动process、test或build，
也没有修改repository。去掉清理语句后，actual gate chain首次执行即全部PASS；这是command calibration，不是product failure。

Public inventory与legacy/disposable rebuild均**NotRun / 无需**：Cadence receipt change不扩public .NET API，也不修改
raw/derived rebuild semantics；owner PublicSurface tests已由solution test覆盖。Ignored operator state、provider与deployment均
**NotRun**，且不是本provider-free promotion的gate。

Annotated tag message已核对：它只累计继承immutable surface sets 1至4，并只新增§1 Cadence command-local boundary，
同时逐项保留§2 non-promises；tag object `e11000177af2877a9d7351dbb17d4bb6b591735e` dereference到reviewed ledger
`89d61ba2c561d84eed235ee196b24d2016ecd3ff`。因此surface set 5已经**anchored complete**；本post-tag status docs
commit不反向移动tag、不续期其证据，也不扩大approved scope。对`845539c5`与actual annotated tag的post-tag docs
independent review已PASS。

## 4. Immutable anchors与tag closure

Immutable prior dereferenced targets继续为：

- v1 `6378cebbde4cf150ecb4d8de5699ef1f77ce4f0b`；
- v2 `c4c6dd1698c7460fbf8ff3563d7800203f3202e0`；
- v3 `adf547e2a2319fd3009a7015a4289ab875af43f7`；
- v4 `0dac57a9e32ae5d0367394404524404689dfa4ef`。

Tag closure facts：

1. exact clean promotion HEAD `aebc4040`的unified gates与reviewed ledger `89d61ba2`均已记录；
2. final pre-tag review已关闭，tag message已核对为§1 exact scope与§2 non-promises；
3. annotated tag `session-journal-contract-r2-approved-surfaces-v5` object为`e11000177af2877a9d7351dbb17d4bb6b591735e`，
   dereferenced target为`89d61ba2c561d84eed235ee196b24d2016ecd3ff`；
4. v1至v4 targets仍为上列四个commit，未移动或由v5续期；
5. 本post-tag status docs commit位于anchor之后，不移动tag、不续期gate evidence、不扩大§1；对`845539c5`与actual
   annotated tag的独立review已PASS。
