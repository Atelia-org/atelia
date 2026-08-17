# SessionJournal Contract Freeze R2 — approval review

状态：AP closure、unified renewal gates与independent docs review **complete**；user approval recorded；surface set 1 anchored  
source candidate：`cd966fc7fddfa6acbda6f80431cf9b588177d969`  
approval anchor：`session-journal-contract-r2-approved-surfaces-v1`  
review docs baseline：`fa9701dfd8f7be47f292625063bca4c75c9fb255`  
记录日期：2026-08-17

## 1. 目标与边界

本文承接[R5 candidate evidence](contract-freeze-r2-r5-candidate.md)，但AP closure包含两个最窄production修复，
因此`a77ed16c`的final gates不能自动认证新的`cd966fc7`。本文以current source重新完成统一candidate gates，
但不把`ready for approval`改写成既成stable/frozen声明。目标是把逐Tier批准拆成可审查的兼容承诺，
关闭用户批准前仍存在的小型docs/tests/producer-reader缺口。

本review不读取ignored operator config、不启动Galatea Host、不构造provider、不调用模型。deployment readiness、
real-provider content quality、physical SQLite/RBF bytes与assembly binary ABI均不属于本review的批准对象。

## 2. 初始独立审计裁决

| Surface | 初始裁决 | 最小后续动作 |
|:--|:--|:--|
| Tier A logical raw/recovery wire | Conditional Go | 锁住retired raw ID `12`，并补齐剩余unique body shape的independent literal goldens |
| Tier B blanket approval | Defer | 改为逐owner/current-generation裁决；先精确列slot、identifier与operator action |
| Store SQLite V2 logical wire | Defer | 现有fingerprint与validator足以实现/验证，但批准前仍缺独立exact logical-schema appendix；不冻结SQLite physical bytes |
| Rewriter five protocol axes | Partial Go | 只批准五个exact identifier与pre-route/pre-dispatch mismatch，不顺带冻结provider输出实现 |
| Completion connections V1 | Go | exact numeric V1、bounds、source rules、fingerprint/secret boundary与no-dual-reader |
| Galatea HTTP/SSE V1 | Go | tracked server/browser协议；operator config与real provider只阻塞deployment，不阻塞wire批准 |
| Route manifest / AgentControl profile | Conditional Go | 补齐field/count/byte/numeric max/max+1与strict mutation gates |
| Galatea root config | Split | version/direct-cut/path policy可批准；完整field language在ledger/count gate闭合后批准 |
| RecapGrid CLI | Split | outer envelope、16 MiB fallback与Store四命令ledger可批准；其余command detail/status继续Defer |
| Tier D named source roles | Conditional Go | 把自然语言大类改成named roles/construction exceptions，并建立真正nonfriend compile fixtures |

## 3. 本批 closure 工作包

### AP-A — raw registry与literal evidence

- 将numeric ID `11`、`12`都登记为retired且不得复用；
- 为`AgentActionProduced`/`ImportedAgentAction`、`ToolExecutionStarted`、`ToolResultObserved`与
  `CompletionAttemptFailed`补test-owned exact UTF-8 literal；
- 不改变production codec、event ID/body version、accepted language或physical EventJournal/RBF layout。

实现commit：

- `e9b966e7`：retired ID `11/12`与action/tool/failure independent literals；
- `02be1510`：RuntimeConfig V2、relaxed escaping与failure explicit-null tail。

focused与SessionJournal full分别通过；最终SessionJournal suite为461/461。独立review finding已由tail关闭。

### AP-C1 — Route/Profile accepted language

- Route manifest锁identifier UTF-8、route count、concurrency、timeout与maximum output token边界；
- AgentControl profile锁profile id、canonical profile/admission bytes、registry count与id/runtime uniqueness；
- 以tests锁定边界，并只引入下列两项最窄producer/reader修复；不增加schema framework、compatibility reader或production option。

实现commit：

- `d4a20d3f`：Route manifest full bounds，并修正connection id按strict source UTF-8而不是JSON escaping长度计数；
- `af5fc7aa`：AgentControl profile/registry bounds与strict mutation gates；
- `5e458238`：移除Route-only control-character language分叉；Control Admission producer在64 KiB前闭合、
  string Ordinal排序与numeric排序显式化；
- `d8517bac`：owner canonical 65,536/65,537 exact boundary与defensive byte/source ownership tail。

Route保留Completion/Galatea现有的nonblank + strict UTF-8 128-byte connection-id language；outer manifest仍有
独立1 MiB canonical cap。Control Admission现在保证public producer不会生成owner decoder拒绝的bytes。

### AP-D1 — named-role source fixtures

- 新增无`InternalsVisibleTo`的SessionJournal public-surface project；
- 显式登记并编译external candidate/lifecycle、estimator、executor、completion route/invoker/telemetry与Host
  composition角色；
- 显式列出external implementer必须合法构造的result/outcome，owner-issued output则只承诺读取；
- 这些fixture是role-level source compatibility oracle，不是901 types / 9,419 rows或binary ABI allowlist。

实现commit：

- `9ba6c943`：新增nonfriend SessionJournal project并登记S/T/G/H named roles；
- `c50356c0`：移除owner-issued output construction误承诺，补external source/lifecycle、executor、runtime
  resolver/invoker/telemetry的合法output/input shape；
- `cd966fc7`：补external lifecycle `RawHistoryAuthorized`结果。

独立review最终PASS。fixture不承诺`SessionPendingToolBoundaryResult`、telemetry event或selected-lineage snapshot的
external construction，也不把Completion assembly全部export升级为Tier D承诺。

## 4. Production delta与candidate renewal

相对R5 source `a77ed16c`，本轮production只有：

1. Route connection id从JSON-escaped byte length改为strict source UTF-8 byte length，并保持与Completion/Galatea
   accepted language一致；
2. Control Admission缓存owned canonical bytes、在producer侧执行inclusive 64 KiB cap，并把decoder collection
   order固定为Ordinal string / numeric integer。

没有public type/member、event kind/body version、durable schema version、HTTP/SSE grammar或CLI envelope变化。
但commit identity已经改变，因此必须重新运行solution、Node、inventory与provider-free disposable rebuild；
旧R5结果只能作baseline，不能冒充新source的final gate。

### 4.1 Unified code gates

exact source `cd966fc7fddfa6acbda6f80431cf9b588177d969`已完成：

| Gate | Result |
|:--|:--|
| `dotnet test Atelia.sln --no-restore -m:1 -nr:false` | 38 projects；4,677 passed / 0 failed / 0 skipped |
| PublicSurface projects | 12 projects；40/40；新增S nonfriend 3/3 |
| `dotnet build Atelia.sln --no-restore -m:1 -nr:false` | 0 warnings / 0 errors；20.06 s |
| HTTP production JS Node suite | 1/1 |
| SSE production JS Node suite | 1/1 |

测试开始/结束HEAD一致；未读取ignored config、未启动Host/provider。

### 4.2 Current isolated inventory

inventory root为`/tmp/atelia-ap-inventory.HC3k5g/`，SDK `.NET 10.0.110`，Linux WSL2 x86_64。tool
`Program.cs` / `.csproj` SHA-256仍为
`8a4f233dc5a4a2c5a8cd94a5611907b653f7583a54cd2ee6dcf53a967e44b6f7` /
`4659c618b6dba7701e58431487c416c01bb65d466be3c2b49e5a14bb855aafa4`；tool与S/T/O/C/G/H
分别isolated restore/build 0W/0E。每次tool内部double-generation与每assembly run1/run2外层比较均byte-identical。

current totals为901 effective-public types / 9,419 logical API rows / 2,123 construction lines；六assembly的
counts与API/construction SHA均与R5 source `a77ed16c`逐项byte-identical，delta为0。新增nonfriend
`SessionJournal.PublicSurface.Tests`不在product artifacts或inventory中。

### 4.3 Renewal disposable rebuild

renewal run root为`/tmp/atelia-ap-renewal.cbuszS/`；machine summary是
`reports/ap-renewal-summary.json`（SHA-256
`f6b1af821862aae4fcfc2bfa22b81070e56c34e8ad0b1d0476353a437c2bbf71`）。执行时固定source
`cd966fc7fddfa6acbda6f80431cf9b588177d969`与current Debug CLI DLL SHA-256
`8e250fc683dbdc599b64a025ef475da20500144ad447d85b35365e6c80c4c377`；批准的legacy export仍是
1,281,881 bytes / SHA-256 `b71822a27003e8d9f9b9c0ff956ca7c268267aba72221be89df154ed7d4751f3`。

- 两次fresh import及两次offline validation均成功；normalized import与normalized validation分别exact相等；
- final raw facts仍为148 events / 474,498 logical payload bytes / 71 Observation / 71 Action / 142 history
  contributions / Idle / Prepared 0；derived前后3个raw files exact不变；
- scaffold、四owner init、Timeline sync、asset provision、recipe compose/put、owner inspect/verify均成功；
- main scaffold的route id是exact 128 strict UTF-8 bytes，包含non-ASCII、LF与backslash，并贯穿production
  create/encode/decode/write/read/decode；普通Admission为353 bytes、profile为548 bytes，不能冒充64 KiB边界；
- 三项scaffold source assets在init/sync/provision workflow前后exact；compose加入recipe后，四项operator assets
  到put/owner/repeat/final gates结束仍byte-exact不变；
- repeat-init与standalone Timeline create都返回ready/already-exists，各自13-file repository snapshot前后exact；
- 每个product CLI都在`bwrap --unshare-net`内执行；actual Galatea root在namespace内由tmpfs遮蔽，只有已批准
  legacy export单文件映射到中性输入路径；21个product stderr合计0 bytes，provider/call-log artifacts为0。

隔离harness有两次只读mount-target pre-product失败；另一次product import在`Guid.NewGuid()`阶段因缺`/dev`
失败，但repository尚未创建，stderr独立保留。补齐device mount后的final-gate 21个product stderr均为0；四条成功
import/validate之后还修正了一条旧事实断言，没有覆盖或重跑已成功的imports。完整calibration保存在run root。
current rebuild的product semantic failure为0。
该证据不含provider factory counter，所以只承诺禁网、命令集不含online/materialize及artifact扫描为0；不把它
夸大成真实provider integration。

## 5. 用户批准结果

用户于2026-08-17批准主线程推荐的精确surface set与annotated tag。批准范围是：

- Tier A logical raw/recovery wire，Frozen R2；不含physical RBF bytes；
- Rewriter五个exact protocol axes，Frozen R2 sub-surface；不扩展到provider renderer/output实现；
- Completion connections V1、Route manifest V1、AgentControl profile V1、Galatea HTTP/SSE V1；
- RecapGrid CLI outer envelope与Store `inspect/verify/export/reset` ledger；
- Tier D §2.3 exact named-role source compatibility；不含candidate navigation categories、blanket exported surface或binary ABI。

用户接受继续Defer：blanket Tier B、History/Cadence/Control损坏或旧代状态的统一operator action、完整root-config
field language、所有independent reports的完整field/status language、非Store CLI command的全部detail/status、
Store SQLite V2 exact logical schema appendix、五个Rewriter轴之外的provider renderer/protocol，以及任何physical
SQLite/RBF determinism。Rewriter五个exact protocol轴已在本次作为Tier B独立sub-surface批准，但不应借此把
整个Tier B标成frozen。

annotated tag `session-journal-contract-r2-approved-surfaces-v1`指向包含本批准记录的promotion docs commit；tag
message固定validated product source `cd966fc7`、批准范围及non-promises。该tag不得移动；未来变更使用新tag/version。

初始approval docs commit为`3575bf30`；三位independent reviewer发现的事实与承诺范围问题由`8585d889`关闭，
三位tail re-review均PASS。scoped docs checker为18 files / 0 diagnostics；all-tracked仅保留11条既有archive
missing-target diagnostics。
