# SessionJournal Contract Freeze R2 — R0 current inventory

状态：R0 complete；R1 candidate review pending  
Inventory baseline：`380df30fc069d2dfbc3c71fe1923e0442389ecd8`  
调查日期：2026-08-16

本文是
[Contract Freeze R2 计划](../work/active/session-journal-contract-freeze-r2.md)
的 fresh current inventory。它记录 current HEAD 的 public construction surface、raw/durable wire 与
operational wire 边界，并给出 R1 draft candidate ledger。本文不是 API/wire freeze 声明，也没有批准或实施
任何 candidate。

## 1. 结论

R0 得到三个不同结论：

1. **public construction authority 有真实收口机会。** G assembly 中一组 Manager/Getter/Online
   progress/evidence 类型只有 owning production module 构造，但 public positional record constructor、
   `init` 和 record clone 允许外部 caller 伪造同形状态。它们应进入最高优先级 R1，而不是只做
   `public -> internal` 的机械改动。
2. **current durable companion wire 没有可直接删除的字段。** path identity、canonical bytes、indexed
   columns、digest、head、generation、schema 与 physical counters 中看似重复的值，目前分别承担
   scope binding、query/FK、corruption、CAS/ABA、recovery 或 operator-action proof。删除会扩大 accepted
   language 或削弱诊断。
3. **operational wire 存在更明确的稳定性债务。** `connections.json` 有三套已漂移的 reader；Galatea
   HTTP/SSE 与 root config 没有显式版本边界；同一个 `recap-grid` CLI root 有两套 report envelope。
   这些 finding 应在 freeze/production promotion 前进入 R1；若最终 Adopt，按本计划比较 direct hard cut，
   不把当前偶然形状直接冻结下来。

raw SessionJournal event/recovery wire 与 historical snapshot 的 ID/version 映射一致，本轮没有新的高杠杆
冗余 finding，继续默认 `Retain`。

## 2. 复核边界与方法

- baseline 开始、三个独立调查结束时，`git status --short` 均为空；主线程综合前再次确认 clean。
- SDK：`.NET 10.0.110`。
- 平台：`Linux 6.18.33.2-microsoft-standard-WSL2 x86_64`。
- R0-A 在 baseline 的 isolated `git archive` 与独立 artifacts 中 build S/T/O/C/G/H，生成两次
  byte-identical metadata inventory，并以 compiled IL 扫描 primary production consumer 的 constructor、
  setter 与 record clone 调用；test/reflection/serializer blocker 另做 source search。
- R0-B/R0-C 只读 current code/tests/docs；没有读取真实部署 repo、ignored `.atelia` secret/config，
  没有运行 durable mutation、crash、HTTP 或 provider test。
- 本文只保存 exact commit、口径、count/hash 与 semantic finding；run-local JSONL 位于临时 isolated
  workspace，不是 current contract authority。未来 diff 必须从本 baseline commit 按同一口径重新生成。

product 与 tool revision 刻意分开：product DLL 必须从 baseline `380df30f` 的 isolated archive build；runner
使用包含本文的 evidence revision 所 tracked 的 inventory tool。tool identity 是：

- `Program.cs` SHA-256：`8a4f233dc5a4a2c5a8cd94a5611907b653f7583a54cd2ee6dcf53a967e44b6f7`；
- `.csproj` SHA-256：`4659c618b6dba7701e58431487c416c01bb65d466be3c2b49e5a14bb855aafa4`。

可重复命令（`<project.csproj>`、`<project-name>`、`<assembly-name>` 按下表 mapping替换）：

```bash
CF_R0_ROOT=$(mktemp -d /tmp/atelia-cf-r0.XXXXXX)
mkdir -p "$CF_R0_ROOT/product-src"
git archive 380df30fc069d2dfbc3c71fe1923e0442389ecd8 \
  | tar -x -C "$CF_R0_ROOT/product-src"
sha256sum scripts/SessionJournal.RecapGrid.ApiInventory/Program.cs \
  scripts/SessionJournal.RecapGrid.ApiInventory/SessionJournal.RecapGrid.ApiInventory.csproj
dotnet build scripts/SessionJournal.RecapGrid.ApiInventory/SessionJournal.RecapGrid.ApiInventory.csproj \
  -m:1 -nr:false --artifacts-path "$CF_R0_ROOT/tool"
dotnet build "$CF_R0_ROOT/product-src/<project.csproj>" -m:1 -nr:false \
  --artifacts-path "$CF_R0_ROOT/product"
dotnet "$CF_R0_ROOT/tool/bin/SessionJournal.RecapGrid.ApiInventory/debug/Atelia.SessionJournal.RecapGrid.ApiInventory.dll" \
  --assembly "$CF_R0_ROOT/product/bin/<project-name>/debug/<assembly-name>.dll" \
  "$CF_R0_ROOT/<alias>.jsonl" "$CF_R0_ROOT/<alias>.construction.jsonl"
sha256sum "$CF_R0_ROOT/<alias>.jsonl" \
  "$CF_R0_ROOT/<alias>.construction.jsonl"
```

工具在单次进程内生成两遍并要求 byte-identical；默认无 `--assembly` 的旧调用仍清点 G。isolated build
按上述命令把 restore/build outputs放进 temp artifacts。每个目标 DLL 再独立读取，避免共享 repo `bin/obj`
与 dependency shadow copy。

综合 tail-fix 后，tracked tool 在独立 artifacts 中 build 为 0 warnings / 0 errors；对 baseline 六个 DLL 重跑
同时复现下列 API 与 construction hash，且 legacy default G output 与 generic `--assembly` G output
byte-identical。

### 2.1 Public inventory 口径

`typeCount` 是 effective-public type（public top-level；public/protected/protected-internal nested 且 enclosing
chain 同样可见）。`memberCount` 是这些 type 上 declared-only、public/protected/protected-internal 的
constructor/method/field/property/event API row；property/event 各算一个 logical row，不把 accessor 重复算成
method。该口径刻意包含继承者可见面，因此不能与只数 `public` member、`GetMembers()` 或 accessor 的临时
reflection 数字混用。

| Alias | Project | `project-name` / `assembly-name` |
|:--:|:--|:--|
| S | `prototypes/SessionJournal/SessionJournal.csproj` | `SessionJournal` / `Atelia.SessionJournal` |
| T | `prototypes/SessionJournal.HistoryTimeline/SessionJournal.HistoryTimeline.csproj` | `SessionJournal.HistoryTimeline` / `Atelia.SessionJournal.HistoryTimeline` |
| O | `prototypes/SessionJournal.HistoryTimeline.O200k/SessionJournal.HistoryTimeline.O200k.csproj` | `SessionJournal.HistoryTimeline.O200k` / `Atelia.SessionJournal.HistoryTimeline.O200k` |
| C | `prototypes/SessionJournal.RecapGrid.Cadence/SessionJournal.RecapGrid.Cadence.csproj` | `SessionJournal.RecapGrid.Cadence` / `Atelia.SessionJournal.RecapGrid.Cadence` |
| G | `prototypes/SessionJournal.RecapGrid/SessionJournal.RecapGrid.csproj` | `SessionJournal.RecapGrid` / `Atelia.SessionJournal.RecapGrid` |
| H | `prototypes/SessionJournal.RecapGrid.Hosting/SessionJournal.RecapGrid.Hosting.csproj` | `SessionJournal.RecapGrid.Hosting` / `Atelia.SessionJournal.RecapGrid.Hosting` |

## 3. Public surface 与 construction authority

### 3.1 Exact baseline

| Assembly | Alias | Types | API rows | SHA-256 |
|:--|:--:|--:|--:|:--|
| `Atelia.SessionJournal` | S | 148 | 1,339 | `64363409b9af31c04648ee6d464b3527029acc0e272c09502f1e4c8df0910e03` |
| `Atelia.SessionJournal.HistoryTimeline` | T | 229 | 2,609 | `2bfaeb65329529117e840b56d5a03d8eae088c88250d44a6fa68303185de8d7c` |
| `Atelia.SessionJournal.HistoryTimeline.O200k` | O | 1 | 4 | `35b5c4c62b37807b8d8211bcbe40177a6d97f76559a5f677ad02edb67e57465a` |
| `Atelia.SessionJournal.RecapGrid.Cadence` | C | 76 | 827 | `f0ab6567b5c8f3e4013107e93311ed94c4683151530c9e5b85f019b5ea7f274a` |
| `Atelia.SessionJournal.RecapGrid` | G | 415 | 4,429 | `1e0aecbc5b653e552c49a91acc488f4e7fe698bf763fb869e3660a4a391e2bd3` |
| `Atelia.SessionJournal.RecapGrid.Hosting` | H | 22 | 228 | `cf94abecee3363875339b0e6e1bc52b8e17e2fc7057fb66136c5a313c5e59404` |

G 的 bytes/hash 与已保存的 M2 inventory 一致；其他 assembly 是本轮新基线。construction surface 另计：

| Alias | visible ctors | visible `init` | visible `set` | record clone | Construction SHA-256 |
|:--:|--:|--:|--:|--:|:--|
| S | 84 | 217 | 0 | 78 | `8cbf9ba4f803260bb1acc117b9fbe00f1613655c53cd9ab30176838606753d59` |
| T | 195 | 177 | 0 | 197 | `600069a7091da9017b167dc843d1ec212dc023cbf6864d65d597d24fa09c58c0` |
| O | 1 | 0 | 0 | 0 | `c80c0478817fc961faf7c994c7e6a0ec6c1f0cc7de853bfd0e37de2877580d52` |
| C | 66 | 52 | 0 | 64 | `f886260ad094bde84848c634fe710917766cd0aedadbce486e35edc8026119a3` |
| G | 342 | 338 | 0 | 308 | `d70ea2ecc31d74853314966da7e7821d7128199c32c638b26521f89b411be6a2` |
| H | 16 | 21 | 0 | 15 | `bfd51fb2eb4ca10c0f12bdee73fa2063b3bd3a011ec4d2086edd6ed3dce21f9a` |

这张表不是“都应收窄”的清单。caller input/spec、external implementer result 与 ordinary immutable value
需要 public construction；只有 owner-issued output/authority/proof 才适用 construction cut。

### 3.2 Provisional support map

| Owner | 明确支持角色 | 默认不冻结的形状 |
|:--|:--|:--|
| S | Host create/open/runtime binding、read-only/offline/derived read、migration、typed recovery/result/input | test hooks、first-party diagnostics、任意 raw escape、owner-issued内部 body |
| T | timeline create/open/read/coordinator、policy/partition input、maintenance/operator result、estimator seam | SQLite/storage helper、test persistence hooks、owner-local proof construction |
| O | O200k estimator implementation与稳定 estimator ID | tokenizer/renderer implementation details |
| C | cadence policy/head、factory/coordinator/maintenance result | durable syscall/codec helper、owner-issued state construction |
| G | Abstractions input/value、Control/Store/Manager/Getter/Online/AgentControl/Runtime 的 supported owner APIs | first-party cross-module mechanics、diagnostics、caller-forgeable owner outputs |
| H | route/config composition boundary 与 first-party Host factory | duplicated syntax reader、lazy registry implementation details |

`public` 只表示 CLR visibility，不自动等于 stable support promise。R1 必须把具体 symbol 归入 support role，不能按
namespace 或 record 名称批量收窄。

### 3.3 CF-A owner-issued construction candidates

compiled production construction graph 只发现 G 内 owning module 构造下列 readable outputs；Galatea、Online
等 caller 读取它们，但没有 production external construction、serializer、reflection、Activator 或 DI blocker：

- Manager：`RecapGridBuildProgressMetrics`、`RecapGridBuildProgressAuthority`、
  `RecapGridMissingAssignmentProgress`、`RecapGridRecipeRowWork`；
- Getter：`RecapGridContextProvenance`、`RecapGridReserveBootstrapEvidence`；
- Online：`RecapGridOnlineMaintenanceEvidence`。

当前问题不只是 public constructor：positional record 同时暴露 public `init` 与 clone，Manager/Online 自身确实
使用 `with` 更新。最小可行 cut 必须同时处理 constructor、copy/clone、`init` 和 enclosing result variant；只把
constructor 改 internal 仍可由 caller `with` 伪造新值。

R1 还必须处理两个细节：

- `RecapGridBuildProgressMetrics` 是 value type，`default(T)` 永远可构造；若目标是不可伪造 authority，需证明
  class conversion 的收益足以承担 type/ABI delta。若它只是 readable telemetry，则可只收窄显式 construction，
  不宣称 cryptographic unforgeability。
- existing white-box tests 可能直接构造 Online evidence；G 已向对应 owner tests 授予 IVT，不应因此保留 public
  construction，也不应新增 production IVT。

`RecapGridContextSelection` 已采用 internal constructor + public getters，可作为形状参考，但不能未经 review
机械套用到全部 result family。

## 4. Raw event/recovery wire

current code 对 raw payload 使用 exact `{v,body}` envelope；unknown kind/version/field、duplicate property、
wrong type 与 retired ID 都 fail closed。

| Kind | ID | Body version |
|:--|--:|--:|
| `RuntimeConfigSetup` | 1 | 2 |
| `SystemPromptSetup` | 2 | 1 |
| `SessionCreated` | 3 | 2 |
| `ObservationAccepted` | 4 | 1 |
| `AgentActionProduced` | 5 | 1 |
| `ToolExecutionStarted` | 6 | 1 |
| `ToolResultObserved` | 7 | 1 |
| `CompletionRequestPrepared` | 8 | 5 |
| `CompletionAttemptFailed` | 9 | 2 |
| `ImportedAgentAction` | 10 | 1 |
| `CompletionAttemptStarted` | 13 | 1 |

ID 11 retired。Prepared v5 的 origin、execution、raw range、exact context inputs、governing setups、parameters、
tool/runtime、recipe、target 与 commitment 仍由 strict codec、reconstructor、recovery 与 audit consumer 使用。
`CompletionAttemptStarted` 的 empty body 仍是 uncertain external dispatch 的 durable phase proof；
`ImportedAgentAction` 与 ordinary action body 相同但 lineage/origin semantics 不同。本轮没有 field deletion candidate。

## 5. Durable companion wire matrix

| Artifact | Layout / version | Strict reader、recovery与 proof | R0 decision |
|:--|:--|:--|:--|
| History locator | `derived/history-timeline/v2/refs/<ref>/locator.json`；JSON v1 | 1..4096 bytes；exact canonical；path Ref、timeline与 generation 绑定 active DB/ABA；invalid/non-v1 不 fallback | `Retain-intentional` |
| History ledger | sibling timeline SQLite；app id `0x41544854`，Schema V2 | exact pragma/schema；scope/head hash、canonical policy/row、indexed locator、selected path/Merkle；unsupported version typed；backup/restore/reprovision | `Retain-intentional` |
| Cadence | `control/recap-grid/v1/.../cadence.json`；`atelia.session-journal.recap-grid.cadence.v1` | 2..4096 bytes、exact order/canonical、path Ref、generation、domain digest；fd-relative publish；post-publish indeterminate | `Retain-intentional` |
| Control | layout v1 `control.json`；content `schemaVersion=2` | 2..32 MiB；exact JSON；whole head/state digest、canonical closure、bootstrap与 receipts；atomic replace；backup/restore/reinitialize | fields `Retain`；reader classification `Prototype` |
| Store | `derived/recap-grid/v1/grid.sqlite`；app id `0x41544752`，Schema V2 | exact pragma/schema；canonical payload + columns/FK/index/counts；transactional writes；reset physical witness；post-publish indeterminate | `Retain-intentional` |
| Rewriter | IDs durable in Control Family/Definition；runtime/output v3，input/prior/history v1 | five exact protocol axes；provider output是 Completion block shape，不是单一 JSON envelope；mismatch pre-dispatch reject | `Reject` merge/delete IDs |

History/Store 的 `metadata.schema_version` 与 `PRAGMA user_version` 是 verification redundancy。当前一方为 2、
另一方错误的 DB 会被拒绝；删除 metadata check 会扩大 accepted language，修改 exact schema 则需要新 SQLite
version 与 reprovision，因此不属于低成本化简。

Control 的 concrete finding 不删除字段：当前先把全部 bytes 反序列化成 V2 DTO，再检查 `schemaVersion`，真实
future shape 可能先因 unknown/missing V2 property 被归为 `Invalid`。R1 可研究 bounded discriminator-first probe：
future version 仍拒绝，但稳定归为 typed `UnsupportedSchema`，使 operator 选择 upgrade/reprovision 而非 corruption
repair。它改变 operator action，尚未 `Adopt`。

### 5.1 Freeze evidence gaps

- History locator/head 缺完整 literal canonical fixture；
- Control whole state 缺完整 literal golden；
- Store expected schema 与 writer 同源于 `SchemaV2.sql`，缺独立 fingerprint/fixture；
- Rewriter prior 已有 empty/one-cell literal golden；work-tail 仍缺完整 rendered-message literal fixture；
- normal open 与 maintenance full verify 的验证范围需要在 current wire 文档中显式分开。

这些是 freeze evidence debt，不证明 current reader 宽松，也不是删除 proof 的理由。

### 5.2 Owning source 与 existing gates

| Artifact | Writer / reader / validator | Existing golden / recovery evidence |
|:--|:--|:--|
| raw events | [`SessionEventCodec`](../../../prototypes/SessionJournal/SessionEventCodec.cs)、[`SessionRequestManifestCodec`](../../../prototypes/SessionJournal/SessionRequestManifestCodec.cs) | [`SessionEventBodySchemaVersionTests`](../../../tests/SessionJournal.Tests/SessionEventBodySchemaVersionTests.cs)、[`SessionEventCodecStrictnessTests`](../../../tests/SessionJournal.Tests/SessionEventCodecStrictnessTests.cs)、[`SessionRequestManifestCodecTests`](../../../tests/SessionJournal.Tests/SessionRequestManifestCodecTests.cs) |
| History locator/ledger | [`HistoryTimelineCanonicalCodec`](../../../prototypes/SessionJournal.HistoryTimeline/HistoryTimelineCanonicalCodec.cs)、[`SqliteHistoryTimelineLedger`](../../../prototypes/SessionJournal.HistoryTimeline/SqliteHistoryTimelineLedger.cs) | [`HistoryTimelineContractAndCodecTests`](../../../tests/SessionJournal.HistoryTimeline.Tests/HistoryTimelineContractAndCodecTests.cs)、[`HistoryTimelineDurableLedgerTests`](../../../tests/SessionJournal.HistoryTimeline.Tests/HistoryTimelineDurableLedgerTests.cs)、[`HistoryTimelineCrashRecoveryTests`](../../../tests/SessionJournal.HistoryTimeline.Tests/HistoryTimelineCrashRecoveryTests.cs) |
| Cadence | [`CadenceCanonicalCodec`](../../../prototypes/SessionJournal.RecapGrid.Cadence/CadenceCanonicalCodec.cs)、[`CadenceDurability`](../../../prototypes/SessionJournal.RecapGrid.Cadence/CadenceDurability.cs) | [`CadenceTests`](../../../tests/SessionJournal.RecapGrid.Cadence.Tests/CadenceTests.cs) literal/schema/settlement/path gates |
| Control | [`ControlState`](../../../prototypes/SessionJournal.RecapGrid/Control/ControlState.cs)、[`ControlDurability`](../../../prototypes/SessionJournal.RecapGrid/Control/ControlDurability.cs)、[`ControlMaintenance`](../../../prototypes/SessionJournal.RecapGrid/Control/ControlMaintenance.cs) | [`ControlVerticalTests`](../../../tests/SessionJournal.RecapGrid.Control.Tests/ControlVerticalTests.cs)、[`ControlSettlementTests`](../../../tests/SessionJournal.RecapGrid.Control.Tests/ControlSettlementTests.cs)、[`ControlCrashRecoveryTests`](../../../tests/SessionJournal.RecapGrid.Control.Tests/ControlCrashRecoveryTests.cs) |
| Store | [`SchemaV2.sql`](../../../prototypes/SessionJournal.RecapGrid/Store/SchemaV2.sql)、[`SqliteRecapGridStore`](../../../prototypes/SessionJournal.RecapGrid/Store/SqliteRecapGridStore.cs)、[`StoreMaintenance`](../../../prototypes/SessionJournal.RecapGrid/Store/StoreMaintenance.cs) | [`StoreAuthorityRegressionTests`](../../../tests/SessionJournal.RecapGrid.Store.Tests/StoreAuthorityRegressionTests.cs)、[`StoreMaintenanceAndFailureTests`](../../../tests/SessionJournal.RecapGrid.Store.Tests/StoreMaintenanceAndFailureTests.cs)、[`StoreCrashRecoveryTests`](../../../tests/SessionJournal.RecapGrid.Store.Tests/StoreCrashRecoveryTests.cs) |
| Rewriter | [`RecapRewriterProtocolV3`](../../../prototypes/SessionJournal.RecapGrid/Abstractions/RecapRewriterProtocolV3.cs)、[`RuntimeRenderer`](../../../prototypes/SessionJournal.RecapGrid/Runtime/RuntimeRenderer.cs)、[`RuntimePreflight`](../../../prototypes/SessionJournal.RecapGrid/Runtime/RuntimePreflight.cs)、[`RuntimeParser`](../../../prototypes/SessionJournal.RecapGrid/Runtime/RuntimeParser.cs) | [`CanonicalContractTests`](../../../tests/SessionJournal.RecapGrid.Abstractions.Tests/CanonicalContractTests.cs)、[`RuntimeRenderingAndSchedulingTests`](../../../tests/SessionJournal.RecapGrid.Runtime.Tests/RuntimeRenderingAndSchedulingTests.cs)、[`RecapCompletionRuntimeTests`](../../../tests/SessionJournal.RecapGrid.Runtime.Tests/RecapCompletionRuntimeTests.cs) |

## 6. Operational wire matrix

| Surface | Current fact | Authority / support classification | R0 decision |
|:--|:--|:--|:--|
| Route manifest | canonical numeric `v:1`；1 MiB/4096；exact order、unknown/missing/duplicate/noncanonical reject | operator routing policy，无 secret，不是 durable semantic identity | `Retain-intentional` |
| Completion connections | 同一 `connections.json` 被 Completion、Hosting、Galatea 三套 reader 解释；无 discriminator；required/default/cap 已漂移 | runtime client construction，含 secret/secret locator | highest-priority `Prototype` |
| AgentControl profile | canonical `v:1`；profile/admission bytes bounded并参与 durable runtime fingerprint | operator profile + durable implementation identity input | `Retain-intentional` |
| Galatea root config | README 称 V1，但 bytes 无 discriminator；strict unknown/duplicate/cap/path checks | user/server/secret/fresh composition config | add explicit version `Prototype` |
| RecapGrid CLI JSON | 通常为 `{schema,command,status,detail}` + 16 MiB cap；Store 四命令另用无 command/无 cap envelope | plausible machine contract；detail混有 workflow fact与诊断 | unify Store `Prototype` |
| Other JSON reports | offline validation v2、legacy import v1、desired setup v1、history-load v1、legacy-root v2 | versioned operator artifact | `Retain` |
| Human stdout/stderr/help | 无 schema，line-oriented | diagnostic | explicitly not frozen |
| Galatea HTTP | eight `/api/*` endpoints由 tracked browser JS 逐字段消费；无 version/closed error contract | real first-party network wire | establish v1 boundary `Prototype` |
| Galatea SSE | event-name discriminator，payload为 `object`/anonymous shape；error至少两形 | real first-party stream wire | closed v1 events `Prototype` |

### 6.1 `connections.json` drift

这是 R0 最明确的 duplicate authority：

- Hosting reader要求 `completionSurfaceId` 与 `baseAddress` property 出现，cap 为4096 entries，并做
  strict bounded unknown/duplicate/missing validation；
- Completion loader 可从 kind 补 `completionSurfaceId`、从首项补 `defaultConnectionId`，由 env 解析
  `baseAddress`，且缺少同等 byte/count/unknown gate；
- Galatea 自有 token validator、256-entry cap 与 envelope，再调用 shared normalization。

同一 manifest 可因入口不同而被接受或拒绝。R1 应先锁唯一 syntax language，再比较 Completion owner、H owner
或更窄 shared seam；strict syntax decode 与 env/secret resolution 必须保持两层。resolved `ApiKey` 不得进入
report、fingerprint 或 durable identity。若 R2 Adopt，优先比较显式 version direct cut；按本计划不保留 dual
reader/fallback。

### 6.2 Galatea API/SSE boundary

current browser 使用 `/api/me`、recent/current/start/resume/pop/stop 与 per-turn SSE。HTTP product code 至少
显式产生 `{error}`、`{code,error}`、`{code,error,phase,head}` 与 DTO-embedded error；malformed binding 可能
另由 framework 产生 ProblemDetails，但当前没有 exact fixture，尚未确认。SSE
`meta/reasoning-delta/text-delta/done/error` payload 没有 closed DTO。

R1 应比较 `/api/v1` path、统一 envelope version或原地 hard cut，并分别研究 closed request/response DTO、
machine error code 与 SSE event union；不得直接把 anonymous payload 和 framework default 冻结成 v1。

### 6.3 CLI support boundary

Store `inspect/export/verify/reset` 当前旁路 unified RecapGrid report。R1 应比较将它们并入
`atelia.session-journal.recap-grid-cli.v1` 的 hard cut，包括补 `command` 与同一 16 MiB cap。`reset --prepare` 输出的
length/SHA witness、export cursor、head/digest 等是 machine workflow fact，必须逐 `command + status` 保留；
SQLite compile options、human `Detail` 与 scaffold verbose listing 可明确标为 diagnostic。

### 6.4 Compact accepted-language matrix

| Surface / reader | Writer | Required / optional-default | Unknown、case、duplicate、null | Bound | Failure / operator action |
|:--|:--|:--|:--|:--|:--|
| Completion shared `connections.json` | operator；Galatea bootstrap template仅是一个 first-party writer | nonempty connections；id/kind/model required；surface/default可补；base可由 env补 | Web STJ；unknown、case variant、duplicate没有 explicit reject gate；null/缺失再由 normalization处理 | `ReadAllText`，无 code-owned byte/count cap | CLI/Host startup failure；无 version/recovery |
| H strict connections | operator | connections、id/kind/model/surface/base properties required；default optional | exact property token；unknown/duplicate/wrong-null reject | 1 MiB；4096 entries；field byte caps | typed decode failure；无 fallback |
| Galatea connections | Galatea bootstrap template/operator | connections最终 nonempty；default optional；字段由 token + STJ + normalization三阶段决定 | token层 exact unknown/duplicate reject；numeric enum token最终仍由 converter reject | 1 MiB；256 entries | Host startup failure；无 fallback |
| Galatea root config | Galatea bootstrap template/operator | `users` 与 exact `recapGrid` 最终 required；listen/call-log/maintenance/prompt有 default/null policy | token层 exact unknown/duplicate reject；missing/default/null由 deserialization与后置 validation共同决定 | 1 MiB；256 users；profile files 1..256 | Host startup fail closed；无 version/recovery |
| Galatea HTTP request/response | Program/DTO/anonymous objects | current browser所需字段见 tracked JS；framework binder required/default尚未形成显式 contract | unknown/case/null/malformed policy缺 exact fixture；product error shape不统一 | 无本轮确认的 contract-level response cap | HTTP code/body依 endpoint；需要 R1 matrix |
| Galatea SSE | Host/Program | event name + `data`；各 payload字段由 JS约定 | event/payload closed-language、unknown policy均未显式定义 | 无本轮确认的 event byte cap | `done`/`error` terminal；error payload两形 |

Galatea strict file reader验证 existing ancestor no-reparse、leaf `O_NOFOLLOW`、regular-file 与 size/change；它不检查
Unix owner/mode。bootstrap `File.WriteAllText` 依赖 process umask，没有主动设置 private mode。因此当前只证明
bounded no-follow loading，不承诺 secret at-rest permission/ownership policy。

## 7. Draft candidate ledger

R0 不使用 `Adopt`；所有改变 API/wire 的条目都必须经过 R1 双视角 review 与 R2 plan lock。

| ID | Candidate | Semantic gain | Main risk / required proof | R0 decision |
|:--|:--|:--|:--|:--|
| CF-A-01 | 调查七个 G owner-issued progress/evidence type 的 ctor/init/copy 与 enclosing results | 减少 caller-forgeable owner output | external implementer/serializer scan；record struct default；positive read + negative construct/with fixture；R2前按 Manager/Getter/Online拆原子包 | `Prototype umbrella`, highest |
| CF-D-01 | 调查单一 strict bounded/versioned `connections.json` syntax boundary，syntax与secret resolution分层 | 若可行，删除三套 parser truth 与接受集合漂移 | 三 consumer mutation matrix；比较 owner/seam与discriminator；secret不泄漏；若hard cut则锁旧格式拒绝政策 | `Prototype`, highest |
| CF-D-02 | 调查 Galatea HTTP/SSE version与closed DTO/error/event边界 | 避免冻结 anonymous/framework偶然形状 | browser migration；binder unknown/null/case；exact success/error/SSE fixtures；R2前至少拆 HTTP core 与 SSE event 包 | `Prototype umbrella`, high |
| CF-D-03 | 调查 Galatea root config 的 discriminator/version boundary | 把隐式 hard cut变为可诊断 version policy | 比较 discriminator位置与cut政策；users/secrets不并入connections superset；bootstrap/missing/unsupported tests | `Prototype` |
| CF-D-04 | Store CLI 并入 unified RecapGrid envelope/cap | 删除第二 report schema/truth | per-command stable detail ledger；whole-envelope goldens | `Prototype`, bounded |
| CF-C-01 | Control discriminator-first typed Unsupported classification | future schema operator action稳定 | accepted language不变；V2 malformed仍 Invalid；crash/maintenance gates | `Prototype` |
| CF-C-02 | 增补 companion wire independent goldens/fingerprint | 建立 freeze-grade evidence | 不把 writer自身当唯一 oracle | evidence work |
| CF-E-01 | raw/Prepared field deletion | 当前没有新 redundancy evidence | replay/recovery proof与 accepted language | `Retain` |
| R-C-01 | 删除 SQLite metadata schema、path identity、digest/index列 | 少字段但少 corruption/scope/query proof | 会扩大 accepted language或要求新 schema/reprovision | `Retain/Reject` |
| R-C-02 | 合并 Rewriter 五个 protocol ID | 名称更少 | 会接受 mixed-version config并丢独立演进轴 | `Reject-not-equivalent` |

## 8. R1 建议顺序与 gates

1. **CF-A-01 construction authority umbrella**：逐 symbol 做 owner/non-owner construction graph；红态 compile fixture
   证明 external `new`、object initializer、copy/`with` 当前可用；绿态保留 public read/pattern match，且不新增
   production IVT。先裁决 value-type metrics 是否只属 telemetry，再拆成 Manager/Getter/Online 原子 candidate。
2. **CF-D-01 connections language**：同一个 literal/mutation corpus同时喂 Completion/Hosting/Galatea；先写
   accepted-language matrix，再裁决是否收口单一 syntax owner、是否加入 discriminator；若采用 hard cut，
   再锁旧格式拒绝与 operator migration policy。
3. **CF-D-04 bounded CLI envelope**：较小、可独立的 wire cut；锁定 Store 四命令的 command/status/detail
   machine fields、limit fallback 与 exit code。
4. **CF-D-02/03 Galatea wire**：先调查 tracked browser真正需要的最小 DTO、machine error与 SSE event payload，
   分拆 HTTP/SSE candidate 后再锁版本方案；config/connection不得把 secret/runtime/durable authority混合。
5. **CF-C-01 + CF-C-02 durable evidence**：先补 discriminator mutation与 independent goldens；字段删除继续
   Retain，除非出现新的双 writer 或可重建单一 authority proof。

每个实施 candidate 都必须是独立 semantic commit，并重新生成受影响 assembly public inventory或 exact wire
golden。wire cut 后旧 version必须显式拒绝或按 plan 声明 reprovision；不增加 silent migration、fallback、dual
reader 或 compatibility wrapper。

## 9. Residual uncertainty

1. source/compiled consumer scan不能证明没有仓外脚本；HTTP/SSE 与 CLI 因网络/机器输出形状继续按
   plausible external consumer处理。
2. 本轮没有读取真实 durable bytes或真实 `.atelia` config，避免把部署secret带入证据；current data的
   migration成本尚未量化。
3. ASP.NET binder 对 unknown/case/null/malformed body 的实际 response 尚无 exact fixture，不能从 framework
   默认推导 frozen contract。
4. companion wire 的 crash/reopen tests是现有 source evidence，本轮没有动态重跑；真实断电仍不是现有
   process-death evidence的承诺。
5. public support-role map是 R0 provisional classification；R1 仍需逐候选验证 external implementer、generic
   constraint、reflection、serialization、DI 与 test-only construction。
6. Galatea config/connections 的 secret file 当前没有 owner/mode policy；bootstrap mode依赖 umask。是否在 freeze
   前建立 explicit private-file contract 是 security/support decision，不是本轮已解决事实。

## 10. R0 decision

R0 已建立可复核 current baseline，足以进入 R1。当前不批准 production/test API 或 wire 改动：

> **R0 complete；优先研究 CF-A-01 与 CF-D-01，durable field deletion 与 raw wire changes 保持关闭。**
