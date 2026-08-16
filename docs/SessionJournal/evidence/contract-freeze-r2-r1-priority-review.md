# SessionJournal Contract Freeze R2 — R1 priority candidate review

状态：Focused R1 review complete；R2 plan lock / implementation pending  
调查基线：`677e94c9d931cfabc8137a24bf5163b3b494331f`  
调查日期：2026-08-16

本文深入复核 [Contract Freeze R2 计划](../work/active/session-journal-contract-freeze-r2.md)
优先级最高的 `CF-A-01`、`CF-D-01` 与较小的 `CF-D-04`。调查只读：开始/结束 worktree clean，
没有修改 production/tests，没有运行 build/test，也不构成 R4 candidate 验证。

## 1. 结论

1. **`CF-A-01` 的 API 表面收口成立，但不是 security authority hardening。** 七个候选都是
   output-only value/evidence；没有 public production input 接受它们。应拆成 Manager、Getter、Online
   原子包，只删除无用 public argument constructor / mutation accessor，保留 public getters、record value
   semantics 与 CLI JSON。不得为了封死 clone、全部 result variants 或 `default(T)` 引入 capability class。
2. **`CF-D-01` 是本轮最高杠杆的真实化简。** `connections.json` 当前有 Completion、Hosting、Galatea
   三套 parser truth，accepted language 已漂移。推荐把唯一 strict bounded byte language 收回现有
   `Atelia.Completion` owner，hard cut 到顶层数值 `v:1`；file acquisition/path safety 继续 owner-local。
   不新增 assembly、generic parser options、dual reader 或 superset config DTO。
3. **`CF-D-04` 是可独立提交的小切口。** Store 四个 CLI 命令只需复用现有
   `{schema,command,status,detail}` + 16 MiB printer，删除 Store 私有 envelope。typed result、status/detail
   mapping 与 command-specific payload 不应被抽成 generic union/framework。
4. 三包都验证了同一复杂性门槛：若实现开始要求新 shared assembly、typestate hierarchy、兼容 reader、
   generic result algebra 或 cursor-aware通用 printer，说明已经越过最小 seam，应停止并回到 owner-local
   direct cut。

## 2. 方法与边界

- simplifier 与 robustness/authority 两个独立视角逐包复核；
- source scan 覆盖 declarations、production/test consumers、serializer、reflection/DI/Activator、CLI writer、
  config bootstrap、docs/scripts；
- wire 包建立 current accepted-language / status-detail ledger，再裁决 target language；
- 只读检查 ignored Galatea operator manifest 的**形状**，未输出 ID、endpoint、env locator 或 secret value；
- 未读取真实 provider secret，未修改 ignored config，未盘点/改变 durable Prepared 状态。

本文的 `Adopt` 是 R1 recommendation。只有 active plan 的 R2 preflight、blast radius、red/green gates 与
rollback boundary 锁定后，才能进入 R3。

## 3. CF-A-01 — output construction surface

### 3.1 共同事实与裁决边界

候选 declarations 位于
[`ManagerContracts`](../../../prototypes/SessionJournal.RecapGrid/Manager/ManagerContracts.cs)、
[`GetterContracts`](../../../prototypes/SessionJournal.RecapGrid/Getter/GetterContracts.cs) 与
[`OnlineContracts`](../../../prototypes/SessionJournal.RecapGrid/Online/OnlineContracts.cs)。当前 positional record
暴露 public primary constructor、public `init`、`Deconstruct` 与 record copy/`with`；metrics struct 还永远
允许 `default(T)`。

consumer scan 的结论：

- 七个类型都没有 public production input/sink；伪造实例不能进入 G durable state machine 或 recovery；
- CLI 会 runtime serialize Manager result、Getter evidence/provenance 与 Online evidence：
  [`RecapGridBuildCommands`](../../../prototypes/SessionJournal.Cli/RecapGridBuildCommands.cs) 和
  [`RecapGridOnlineTurnCommand`](../../../prototypes/SessionJournal.Cli/RecapGridOnlineTurnCommand.cs)；
- 未发现仓内 deserialize、Activator、DI、reflection construction、generic constraint 或 positional
  deconstruction consumer；
- G 的 `internal` 是 assembly boundary，不是 Manager/Getter/Online source-folder capability boundary；
- enclosing result variants仍是 public positional records。因此本包只能宣称 external API hygiene，不能宣称
  “结果不可伪造”或 security authorization。

### 3.2 原子候选

| ID | Symbols / minimum cut | R1 recommendation | 复杂性裁决 |
|:--|:--|:--|:--|
| `CF-A-01-M` | `RecapGridBuildProgressAuthority`、`RecapGridMissingAssignmentProgress`、`RecapGridRecipeRowWork` 改 non-positional sealed record + internal ctor + public get-only；`RecapGridBuildProgressResult.Metrics` 改 `get; internal init;` | `Adopt`，S/M | 保持字段名/声明顺序/value equality；不封闭全部 result variants |
| `CF-A-01-Metrics` | `RecapGridBuildProgressMetrics` 仍是 readonly record struct | `Retain-intentional` | 它是 telemetry；`Disposed` 合法携带全零，`default(T)` 无法消除。改 class会引入 null、allocation 与 ABI/JSON delta |
| `CF-A-01-G` | `RecapGridContextProvenance`、`RecapGridReserveBootstrapEvidence` 改 non-positional sealed record + internal ctor + public get-only | `Adopt`，S | Getter tests依赖 value equality；不得转普通 identity class |
| `CF-A-01-O` | `RecapGridOnlineMaintenanceEvidence` internal ctor；`NextRecipeRow`、`NextAuthority`、`ContinuationKind` 为 `get; internal init;`，其余 get-only | `Prototype -> Adopt after exact gate`，M | owner现有 `with` 会修改这三项；不能只保留 `ContinuationKind` setter |
| `CF-A-01-ResultFamilies` | 封闭 Manager/Getter/Online 全部 public result variant construction/copy | `Defer/Reject-overreach` | output没有信任入口；重写整个结果代数不能带来相应 capability gain |

`CF-A-01-Metrics` 不并入 Manager reference-record 包。Manager 另一个 build result 的 metrics init surface也不是
本候选的 authority proof；不得用局部改动宣称所有 Manager metrics 已封闭。

### 3.3 R2/R3 gates

- non-friend compile-negative：`new`、object initializer、mutating `with`、`Deconstruct`；
- positive consumer：从真实 result pattern-match并读取原 public getters；
- reflection：type仍 public，无 public argument ctor/setter，getter名称/类型不变；
- 保留 record equality/hash/ToString；`default(RecapGridBuildProgressMetrics)` 继续是全零合法值；
- CLI exact JSON锁 property名称、大小写、顺序；不存在 reader不等于可以忽略 writer ABI；
- G inventory、Manager/Getter/Online/PublicSurface、CLI、Galatea、AgentControl、Walking/RG focused gates；
- 不新增 production IVT。

## 4. CF-D-01 — one `connections.json` language

### 4.1 Current drift

| Fact | Completion `LoadFile` | Hosting manifest | Galatea load path |
|:--|:--|:--|:--|
| parser | Web STJ + normalize | independent `JsonDocument` allow-list | independent token scanner + Web STJ + normalize |
| document / count bound | none | 1 MiB / 4096 | 1 MiB / 256 |
| unknown/case/duplicate | tolerant/last-win path存在 | exact reject | exact property switch；case-insensitive duplicate reject |
| `defaultConnectionId` | missing/blank => first item | optional；normalize defaults | optional；normalize defaults |
| `completionSurfaceId` | missing/blank => kind-derived | property required | missing/blank可 kind-derived |
| endpoint source | env-only可用；inline+env时env wins | `baseAddress` property required，可空+env | env-only可用；inline+env时env wins |
| enum | stable string converter，case-insensitive | exact lowercase string | token层string/number，converter最终拒numeric、接受mixed-case |
| `maxTokens` | 0/negative可过 normalize | positive Int32 only | 0/negative可过 normalize |
| per-field UTF-8 bound | none | 128 / 4 KiB / 64 KiB | none |

owners：

- shared DTO、normalizer、registry、fingerprint、factory 已在
  [`CompletionConnections`](../../../prototypes/Completion/CompletionConnections.cs)；
- H strict grammar 在
  [`CompletionConnectionsManifest`](../../../prototypes/SessionJournal.RecapGrid.Hosting/CompletionConnectionsManifest.cs)；
- Galatea grammar与 guarded file acquisition 混在
  [`GalateaStrictConfigReader`](../../../prototypes/Galatea/GalateaStrictConfigReader.cs)，load/bootstrap 在
  [`GalateaServices`](../../../prototypes/Galatea/GalateaServices.cs)。

路径策略不是共同 language：Galatea 的 Linux no-follow/regular-file read、CLI 的 lexical reparse/bounded read、
Completion 的 ordinary file wrapper有不同 deployment authority，必须保留 owner-local。

### 4.2 R1 target language

唯一 byte entry 归 `Atelia.Completion`，接收 `ReadOnlySpan<byte>`；内部保持四段：

1. strict bounded syntax DTO/locals；
2. non-secret semantic validation；
3. environment endpoint/secret resolution；
4. defensive freeze，然后由 existing registry/factory lazy materialize client。

target document：

```json
{
  "v": 1,
  "connections": [{
    "id": "main",
    "kind": "test",
    "modelId": "model",
    "completionSurfaceId": "test-v1",
    "baseAddress": "https://example.invalid/"
  }],
  "defaultConnectionId": "main"
}
```

锁定规则：

- `v`、nonempty `connections`、`defaultConnectionId` required；`v` 必须是 integer token `1`；
- exact camelCase；unknown、duplicate、case variant、BOM、invalid UTF-8、comment、trailing comma/data拒绝；
  普通 trailing whitespace与任意 property order允许；
- 每项 required `id/kind/modelId/completionSurfaceId`，全为 bounded nonblank string；default必须 Ordinal命中；
- wire syntax上 `baseAddress/baseAddressEnv` exactly-one，`apiKey/apiKeyEnv` at-most-one；string一旦出现必须
  nonblank；不再 silent env override inline；
- `maxTokens` absent/null或positive Int32；reasoning/TTL absent仍使用 `provider-default`，显式值必须 exact
  lowercase stable name；
- 1 MiB document、depth 8、1..256 connections、identifier/env locator 128 UTF-8 bytes、endpoint 4 KiB、
  inline/resolved secret 64 KiB；env-resolved value必须重新过 cap；
- `kind`/`completionSurfaceId` 仍是开放 bounded string；custom `ICompletionClientFactory` 是合法 seam。
  built-in kind/URI/surface membership继续在 lazy default factory验证；
- `BaseAddress` 是 non-secret metadata。Default factory应单独研究拒绝 URI userinfo/credentials；不得把
  secret写入 URI、fingerprint、log或error。

source exclusivity **只能在 syntax representation 上判断**。env-only wire resolve后，runtime record会有
resolved `BaseAddress/ApiKey` 与 locator同时存在；不得在 resolved `CompletionConnectionConfig` 上重查
exactly-one/at-most-one，也不得让 programmatic config继承 wire-only invariant。

### 4.3 Owner与方案裁决

| 方案 | R1 recommendation | 理由 |
|:--|:--|:--|
| Completion-owned single byte language | `Adopt-with-corrections` | existing semantic owner；H/G已引用Completion；删除三truth，无新依赖反向 |
| numeric `v:1` direct hard cut | `Adopt` | 文件名/loader已判别document type，schema string是重复ID；future version可 typed reject |
| new shared contract assembly | `Reject-overdesign` | 新owner/DTO/bridge/refs却没有第二domain |
| H-owned reader | `Reject` | 令通用Completion/Galatea反向依赖RecapGrid |
| three readers + shared tests/options | `Reject` | 参数化或校准drift，不删除language authority |
| eager built-in kind/URI/surface validation | `Reject-not-equivalent` | 会破坏 custom factory与lazy failure timing |
| compatibility reader/automatic secret rewrite | `Reject` | 保留第二truth，且自动改写含secret文件不安全 |

### 4.4 Actual operator shape（content-free audit）

只读检查 `prototypes/Galatea/.atelia/galatea/connections.json` 的 keys/计数：

- 5 connections，低于 target 256 cap；
- root已有 explicit `defaultConnectionId`，每项已有 explicit `completionSurfaceId`；
- root没有 `v`；
- 每项当前同时存在空 `baseAddress` property与nonblank `baseAddressEnv` locator；API key使用env locator。

因此 target cut不需要改变 catalog size、default或surface值；operator migration是：增加 `"v":1`，删除空
`baseAddress` properties，保留 env locators。审计只记录 presence/nonblank shape；没有输出或记录具体 ID、
endpoint、locator 或 secret，也没有读取 locator指向的环境变量值。

### 4.5 R2 blockers与gates

- 先盘点未完成 Prepared/Started：若保留现有 `defaultConnectionId` 值，且各 connection normalized
  fingerprint不变，则增加 `v`、删除空 `baseAddress` 不改变 Prepared exact binding；若现存 mixed-case kind
  需要改值，必须先用旧binary settle/terminate，不能增加fallback；
- Galatea bootstrap writer、staging jq、Completion两个CLI入口、RecapGrid CLI两个H入口、H/G integration
  必须与reader atomic cut；bootstrap不得生成新reader拒绝的旧文件；
- old no-v manifest在全部production入口以明确manual-migration diagnostic拒绝；不自动改写含secret文件；
- 一份 mutation corpus覆盖version、unknown/duplicate/case/null、source exclusivity、default/surface、enum、
  `maxTokens`、UTF-8/depth/document/count/field/post-env bounds；
- failure前 factory call count为0；custom factory仍可接收开放 kind/surface；default factory仍lazy reject；
- explicit V1与旧有效 normalized config的 non-secret fingerprint相同；secret不进入 fingerprint/log/error；
- H/G source gate不再存在 connections property allow-list，Completion不保留第二STJ reader；
- Galatea no-follow/FIFO/symlink/bounded file tests继续成立；bootstrap v1 round-trip；
- 256/257 locked。实际 operator catalog已证明 `<256`，但部署前仍需重复content-free shape preflight。

Galatea bootstrap的 file owner/mode仍依赖 process umask，不在本 candidate 解决；不得宣称 secret at-rest
protection已经冻结。

## 5. CF-D-04 — Store CLI outer envelope

### 5.1 Current / target

current common printer在
[`RecapGridCommands`](../../../prototypes/SessionJournal.Cli/RecapGridCommands.cs) 输出：

```json
{"schema":"atelia.session-journal.recap-grid-cli.v1","command":"...","status":"...","detail":null}
```

并对 serialized payload施加 16 MiB cap。Store在
[`RecapGridStoreCommands`](../../../prototypes/SessionJournal.Cli/RecapGridStoreCommands.cs) 另有
`atelia.session-journal.recap-grid-store-cli.v1` writer，缺少 `command` 与 cap。

最小 cut：common `Print` 仅开放到同程序集，Store四命令传入
`inspect|verify|export|reset` 后复用；`reset --prepare`仍是 `command:"reset"` + `status:"prepared"`。
删除Store private writer与旧schema，不改typed results，不建command DTO/generic result/serializer framework。

### 5.2 Stable detail ledger

| Command/status | Exit | Retained machine fields / diagnostic boundary |
|:--|--:|:--|
| `inspect/available` | 0 | `instanceId,schemaVersion,databaseBytes` + four counts；SQLite version/source/options retained但属于diagnostic |
| `verify/healthy` | 0 | identity/schema + four counts |
| `verify/unhealthy` | 2 | `Incomplete` machine fact；`Errors` diagnostic text |
| `export/page` | 0 | items全部字段、opaque `nextCursor`、`Incomplete`；canonical content/byte count/digest均保留 |
| `reset/prepared` | 0 | exact physical `length,sha256` witness |
| `reset/reset` | 0 | new `instanceId,schemaVersion` |
| `reset/stale-confirmation` | 2 | `actualLength,actualSha256` |
| `reset/commit-indeterminate` | 2 | `intendedInstanceId,observedInstanceId`；operator先inspect，不盲重试 |
| common failures | current mapping | `SchemaVersion`、`Code/Detail`、`Slot`、`Name`按现有status保留；absent仍exit 0，busy/platform/invalid等仍exit 2 |

所有 detail property name、mixed camel/Pascal casing、声明顺序保持。字段规范化属于另一项 wire cut，不能顺手
并入 envelope consolidation。

### 5.3 Cap delta与gates

准确兼容表述是：**报告不超过16 MiB时**，原 Store status/detail/exit 保持；超过cap时收窄为
`status:"limit-exceeded"`、`detail:{limit:"RecapGridReportUtf8Bytes"}`、exit 2。cap计算serialized JSON bytes，
不含 `WriteLine` newline，exact cap accepted，cap+1 fallback。

Store export当前最多128 items / 4 MiB canonical bytes；Base64加metadata仍低于16 MiB，因此正常 page不会丢
cursor。R2必须把“最大合法 CLI page < report cap”变成 executable relation gate；若未来失效，应降低Store page
bound，不给通用printer添加cursor-aware special case。

其他 gates：

- 四命令 success/absent exact whole-envelope golden，锁 root `schema,command,status,detail` 顺序；
- prepared、stale、indeterminate、unhealthy、invalid与两页export exact fixture；第一page cursor可用于第二page；
- old Store schema不再由production/tests/scripts产生；无dual emit/alias；
- syntax/confirmation error继续 stderr-only、exit 1、stdout无JSON；
- provider-zero、read-only/no-create、reset witness/domain isolation现有 gates保持。

`CF-D-04` 推荐 `Adopt`。若实现需要新的 DTO hierarchy、result union或serializer options，说明方案已偏离
outer-envelope direct cut，应停止扩张。

## 6. Focused candidate ledger

| ID | R1 recommendation | R2前置 |
|:--|:--|:--|
| `CF-A-01-M` | `Adopt` | nonfriend mutation/CLI JSON/value semantics gates |
| `CF-A-01-Metrics` | `Retain-intentional` | 不再以不可伪造authority重开 |
| `CF-A-01-G` | `Adopt` | nonfriend mutation/CLI JSON/value semantics gates |
| `CF-A-01-O` | `Prototype -> Adopt` | 锁三项internal init与Online CLI JSON |
| `CF-A-01-ResultFamilies` | `Defer/Reject-overreach` | 只有出现真实trusted input才重开 |
| `CF-D-01` | `Adopt-with-corrections` | Prepared/operator shape preflight；atomic reader/writer/consumer cut |
| `CF-D-01-BaseAddress` | `Prototype` | 声明non-secret invariant；default factory拒URI credentials/userinfo |
| `CF-D-01-SecretFileMode` | `Defer security work` | Galatea bootstrap 0600/owner policy，不与JSON parser framework混合 |
| `CF-D-04` | `Adopt` | exact envelope/status ledger/cap/export relation gates |

## 7. 后续路线调整

建议把下一阶段从“大类顺序”改成按风险/独立性推进：

1. **先锁并实施 `CF-D-04`**：最小 wire cut，快速删除一个正式-looking schema；
2. **再做 `CF-A-01-G`、`CF-A-01-M`**；Metrics保持，Online在exact CLI gate后单独实施；
3. **`CF-D-01` 先做 operator/Prepared preflight，再一次性 atomic cut**；不要边迁移边保留旧reader；
4. `CF-D-02` 继续先拆 HTTP core 与 SSE event，不冻结 anonymous/framework默认；
5. `CF-D-03` root config version仍独立，不把 users/routes/secrets 合并进 connections superset；
6. `CF-C-01/02` 继续是 durable classification/golden evidence，字段删除保持关闭。

调整理由：`CF-D-04` 与两个 CF-A reference-record包已是低歧义 direct cut；`CF-D-01` 虽收益最高，
但跨四类入口、bootstrap与Prepared recovery，需要先完成部署preflight。HTTP/SSE仍是大umbrella，不能因为本轮
connections成功统一就推广为“所有 operational JSON 共用一个 framework”。
