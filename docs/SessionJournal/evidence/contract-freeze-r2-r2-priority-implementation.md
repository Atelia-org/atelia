# SessionJournal Contract Freeze R2 — priority implementation evidence

状态：Priority R2/R3 code complete；R4 code gates passed；CF-D-01 operator migration complete  
实现基线：`5ca08be9efeb98859a345cfcab95bcad9cfe25d7`  
代码候选：`58d8ae0656959570b5a48b4d4527c621759fc03b`；test-only tail `87079eaa14681e83b7d2db584b3b5bf59dd99ab5`  
日期：2026-08-16

本文记录 [active plan](../work/active/session-journal-contract-freeze-r2.md) 中优先级最高的
`CF-A-01`、`CF-D-01` 和 `CF-D-04` 的 plan lock、实现、独立复核与动态证据。它只认证本文列出的
candidate；不把全部 public API、durable wire、HTTP/SSE 或 operator deployment 宣称为 frozen。

## 1. 结果摘要

| Candidate | 裁决与结果 | Commit |
|:--|:--|:--|
| `CF-D-04` | `Adopt`；Store CLI 删除独立 schema，统一到现有 outer envelope；因可执行大小反例把 export page canonical cap 从 4 MiB hard-cut 到 2 MiB | `f37c1d77`、`b77a4c67` |
| `CF-A-01-G` | `Adopt`；两类 Getter evidence 只保留 public read/value surface | `46c44cd0`、`3e530f95` |
| `CF-A-01-M` | `Adopt`；三类 Manager progress records 与 result Metrics mutation 收口；metrics struct保持 | `e011a8b9` |
| `CF-A-01-O` | `Adopt`；Online evidence只保留三项 owner 所需的 assembly-internal init | `dba72974` |
| `CF-A-01-ResultFamilies` | `Reject-overreach`；不继续改写9个family/76个variants | 本文裁决，无代码 |
| `CF-D-01` | `Adopt`；Completion-owned strict numeric `v:1` 成为唯一 connections byte language；H/G旧grammar删除 | `58d8ae06`、`87079eaa` |

三类实现都遵守复杂性停止线：没有新增 shared assembly、generic parser options、DTO hierarchy、result union、
dual reader、compatibility shim 或 cursor-aware printer。独立 reviewer 对 production design 均为 PASS。

## 2. CF-A-01 — construction API hygiene

### 2.1 Exact cut

- Getter：`RecapGridContextProvenance`、`RecapGridReserveBootstrapEvidence`；
- Manager：`RecapGridBuildProgressAuthority`、`RecapGridMissingAssignmentProgress`、
  `RecapGridRecipeRowWork`，以及 `RecapGridBuildProgressResult.Metrics`；
- Online：`RecapGridOnlineMaintenanceEvidence`；仅 `NextRecipeRow`、`NextAuthority`、
  `ContinuationKind` 保留 `internal init`，其余属性 get-only。

六个 positional reference records 改为 explicit sealed records：public type、getter声明顺序、类型、record
equality/hash/`ToString`/clone 与 CLI JSON保持；public argument constructor、public positional
`Deconstruct` 和 public mutation accessors删除。`RecapGridBuildProgressMetrics` 仍是 public readonly record
struct；`default(T)` 与 `Disposed` 全零语义保持。

这是 external API hygiene，不是 capability/security boundary：G 内所有 source owner仍在同一程序集；空
`with { }` clone仍合法；result没有 production public sink。

### 2.2 Inventory与非 friend gate

使用 [R0 evidence §2](contract-freeze-r2-r0.md#2-复核边界与方法) 记录的 exact command、SDK
`10.0.110`、tool revision与双生成口径，仅替换product checkout，在隔离目录中重复生成：

| Baseline | Exported types | Logical members | Hash |
|:--|--:|--:|:--|
| R0 `380df30f` | 415 | 4,429 | `1e0aec...bd3` |
| candidate `58d8ae06` | 415 | 4,417 | `efde4a41f2c0f6cc8d77a441083d8a04d9fcb20f830be164b2b5fe15b6625452` |

净变化仅为目标六个 public argument constructor 与六个 public `Deconstruct` 消失。row diff 中另有41个
property row由 public getter+setter替换为 public getter-only；public property/getter名称、类型和logical
count不变，没有非目标 row 漂移。

隔离的非 friend consumer证实：目标实例的属性读取与property pattern编译成功；外部 argument `new`、
mutating `with`、positional deconstruction均编译失败。永久 PublicSurface reflection gates另锁定无任意
public declared constructor、无 `Deconstruct`、exact getter与所需的 assembly-internal init modifier。

### 2.3 停止继续扩张

current M/G/O 有9个 result base families、76个 nested variants。全部改写会要求大规模 CLI/value-semantics
gates，却仍不能增加 authority protection；而 `IRecapCellBatchExecutor.ExecuteAsync` 的 external implementer
确实需要构造 `RecapCellBatchExecutionResult` 与 `RecapCellExecutionOutcome`。因此完整 result-family 收口从
`Defer` 提升为 `Reject-overreach`：除非出现具体 public trusted sink 或单一小 family 有独立 support-role
理由，不再为降低 member count 重开。

## 3. CF-D-04 — one CLI outer envelope

Store `inspect|verify|export|reset` 已删除私有
`atelia.session-journal.recap-grid-store-cli.v1` writer，统一复用
`atelia.session-journal.recap-grid-cli.v1` 的 `{schema,command,status,detail}` 与16 MiB fail-closed cap。
`reset --prepare` 仍以 `command:"reset"`、`status:"prepared"` 表达；typed result、status/detail/exit mapping
没有泛化。

R1 的“4 MiB canonical page必低于16 MiB report”推论被 executable counterexample推翻：默认
`System.Text.Json` encoder会把Base64中的 `+` 写成 `\u002B`；合法 UTF-8 U+9FFE 的Base64为高密度
`6b++`，近4 MiB canonical page的outer report可超过16 MiB并丢失cursor。最小修复不是增加 printer特殊
状态，而是把 public `RecapGridStoreLimits.MaximumPageBytes` pre-release hard-cut到2 MiB；items cap仍为128。

永久 gates 覆盖：

- exact/cap+1 common printer；四个command exact root envelope与旧schema缺席；
- production `PrintExportPage` 的4 MiB对抗fixture必须得到 `limit-exceeded`；
- 2 MiB对抗fixture必须保留128 items、`nextCursor` 与 `Incomplete`，且report小于16 MiB；
- real Store 129-item分页的第一页cursor可读取第二页；
- PublicSurface exact锁定 `MaximumPageItems=128`、`MaximumPageBytes=2*1024*1024`。

该关系测试锁定当前默认 encoder 的已知最坏模式，不宣称穷举证明任意未来 serializer；以后改变 encoder、
item metadata或page bound时必须重跑 composed-output relation gate。

## 4. CF-D-01 — Completion-owned connections V1

### 4.1 Plan lock与operator preflight

content-free preflight确认 current operator manifest有5项，target cap为256；default、surface、kind、tokens与
source locator形状除两点外都满足target：root缺少numeric `v:1`，每项同时保留空 `baseAddress` 与非空
`baseAddressEnv`。迁移只需增加 `v` 并删除空 inline endpoint，不改变ID、default、surface或locator；在
service env values不变的前提下，迁移本身不改变normalized fingerprint。

current config实际引用的repository经旧binary只读验证为Idle，active main heads没有 pending
`CompletionRequestPrepared` / `CompletionAttemptStarted`。另一个未被current config引用的dormant repository
存在既有 commitment mismatch；它不是本candidate迁移或修复对象。

审计没有输出ID、endpoint、env locator或secret，没有调用provider，也没有修改真实repository。

### 4.2 Implemented language

唯一 byte decoder现在由
[`CompletionConnectionConfigLoader`](../../../prototypes/Completion/CompletionConnections.cs) 拥有，strict
parser位于internal/non-public
[`CompletionConnectionsManifestV1Reader`](../../../prototypes/Completion/CompletionConnectionsManifestV1Reader.cs)。
Completion file load、两个RecapGrid CLI ingress、Galatea guarded load全部进入该decoder；Hosting只保留
internal programmatic `Freeze`。旧H/G grammar、DTO、public limits/decoder已删除。

target language锁定：

- numeric raw token `"v":1` required；no-v/其他version fail closed，无compatibility reader；
- exact camelCase，unknown/duplicate/case/BOM/invalid UTF-8/comment/trailing data拒绝；
- 1..256 connections；required default/id/kind/model/surface；wire endpoint exactly-one、API key at-most-one；
- positive plain Int32 tokens、exact reasoning/TTL spelling与TTL provider restriction；
- 1 MiB document、depth 8、identifier/env 128 UTF-8 bytes、endpoint 4 KiB、secret 64 KiB，env resolved value
  再过cap；
- kind/surface仍是开放string，built-in factory validation与client materialization继续lazy；
- wire-only source exclusivity只在private syntax shape判断；programmatic config仍保留env precedence与既有
  default/freeze semantics。

Galatea继续拥有Linux no-follow/regular-file acquisition，CLI/Completion继续拥有各自file wrapper；只统一
语言，不伪造相同的path/threat contract。bootstrap用purpose-built V1 writer并在写入前由shared decoder
self-check；staging writer也只生成exactly-one endpoint source。

### 4.3 Gates与部署边界

mutation corpus覆盖version、unknown/duplicate/case/source、default/surface、enum/token、UTF-8、depth、
document/count/field/env-resolved caps；failure前provider call count与相关write均为0。新增迁移等价gate直接
比较旧等价 programmatic shape与V1 env-only shape的normalized non-secret fields/fingerprint；独立depth gate
必须由 `JsonException` inner证明命中MaxDepth，而不是顺带被unknown property拒绝。

2026-08-16后续operator cutover已完成：ignored live manifest在停服、Idle/Prepared=0和content-free shape
preflight后增加 `v:1`、删除5个空 `baseAddress`；实际service env resolved caps、Completion decoder与Galatea
full config loader均provider-free通过。semantic normalized SHA保持。独立reviewer PASS；完整证据见
[HTTP/SSE plan lock §2](contract-freeze-r2-http-sse-plan-lock.md#2-cf-d-01-operator-cutover-evidence)。

manifest仍是operator state而非tracked code；rollback必须停服并让code与manifest成对执行。本轮没有保留旧
secret-bearing文件副本，因此可逆迁移恢复语义，但不是byte-exact旧文件restore。

## 5. 动态验证

各原子提交已分别运行其owner regular/PublicSurface、CLI exact wire、Galatea、WalkingSkeleton/RG focused
矩阵；独立reviewer对CF-A三包、CF-D-04与CF-D-01均为PASS。组合候选运行：

```text
dotnet test Atelia.sln --no-restore -m:1 -nr:false
```

在 `58d8ae06` exit 0；随后 `87079eaa` 另运行两个focused gates 2/2 与Completion full 530/530。
`python3 scripts/check_session_journal_docs.py` 为18 files / 0 diagnostics；`--all-tracked` 仍只有11条既有
archive missing-target diagnostics；cached diff check通过。

## 6. 后续路线调整

1. 下一项仍是 `CF-D-02`，但先完成bounded recent projection与SSE replay/channel bound决策，再分别实施HTTP
   core DTO/error language与SSE event language；建立explicit version、closed event/error DTO和first-party
   browser exact fixtures，不做“通用 operational JSON”framework。
2. `CF-D-03` root config继续独立：users/password/routes/runtime policy的authority与secret生命周期不同，
   不因connections parser成功就合并成superset config。
3. broad `CF-B` 降到HTTP/SSE之后；CF-A证明 output-only constructor cut很快进入收益递减，不能为inventory
   count重写合法 external implementer contracts。
4. `CF-C` 继续做companion wire goldens/classification，不重开已Retain的head/digest/schema/index proof删除。
5. 新增统一规则：任何 outer wire bound都必须对**最终encoded bytes**做composed relation test；不得只用内层
   canonical/payload cap做纸面推导。
6. CF-D-01 的成功只证明“同一个accepted language应有一个semantic owner”，不证明需要shared parser
   framework；后续一旦出现generic options、compatibility mode或跨authority DTO，立即停止并重新论证。

R5仍然Pending：完成CF-D-02/03与support-role/wire inventory closure前，不声明这些tier
整体stable/frozen。
