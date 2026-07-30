# SessionJournal Repo-owned `RecapPlannerConfig` 设计

> **状态**：Target Design / Implementation Guidance
> **日期**：2026-07-30
> **实施状态**：尚未实现；current CLI仍使用 hardcoded `RecapPlannerConfig`
> **相关类型**：
> `Atelia.SessionJournal.DerivedRecap.Planner.RecapPlannerConfig`
> **相关设计**：
> [Event-addressed Derived Recap V4](event-addressed-derived-recap-v4-target-design.md)、
> [Derived Recap Cadence](derived-recap-cadence-target-design.md)、
> [EADR V4 实现与替换计划](event-addressed-derived-recap-v4-implementation-plan.md)

## 0. 结论

每个 SessionJournal repository 使用一个 repo-wide 配置文件：

```text
<session-repo>/config/recap-planner-config.json
```

文件名与主要消费者类型 `RecapPlannerConfig` 直接对应。它是 repo-owned operator intent：

- 不进入 raw EventJournal；
- 不放入可删除、可 reset 的 `derived/recap`；
- 不由 DerivedRecap Store 解释或保存；
- 不包含 connection、model endpoint、API key或 prompt正文；
- 对一次命令只加载一次，解析为 immutable resolved snapshot；
- 新 planning与确实需要建立新 Building 的 online lifecycle使用同一个 snapshot同时构造
  Planner、policy和 active roster；
- Building建立后，frozen manifest仍是 Resume authority，配置更新不得重新规划旧 Building。

V1 cadence使用最终进入 Context 的 `SessionHistoryPlanningUnit` count，并把 raw event counts仅作为
resource/safety limits。未来 backend-model-neutral information estimator单独升 schema，不在 V1
预留 opaque JSON或 tokenizer-specific字段。

## 1. Authority 边界

四种 authority 必须分开：

| Authority | 负责什么 | 不负责什么 |
|---|---|---|
| raw SessionJournal | session facts、Parent lineage、governing setup、`NthPrevious` | concrete Recap policy、Maintainer roster |
| `recap-planner-config.json` | 未来 planning intent、active profile roster、planning ceilings | 已冻结 Building 的 route、Published membership |
| Building manifest | exact admission、block bindings、source、route、prior、content limits | 后续新 set 的调度策略 |
| Published publication | strict ordinal membership与 exact materialization | Planner配置 |

配置文件丢失或损坏不会令 raw repo或已 Published Recap变成不合法，也不妨碍：

- raw `validate`；
- `recap inspect`；
- healthy Published materialization；
- Prepared/Started exact request recovery。

需要新 planning时必须得到合法配置，不得 fallback到编译期默认值。Resume/Restore则不读取 active
config；它们只要求 frozen plan、Host capability与稳定 protocol hard caps。Online发现 exact
Building时也走相同 Resume路径，不得先要求 active config。

### 1.1 为什么不放入 `RuntimeConfigSetup`

`RuntimeConfigSetup` 是 raw、branch-local、event-addressed的 completion runtime fact。目前
`derivedContext.nthPrevious`只描述 online request选择哪个 strict Recap ordinal。

具体 planning policy、profile catalog和调用预算属于 Planner/Host。把它们写入 raw core会迫使
`Atelia.SessionJournal`理解 concrete Planner/Maintainers vocabulary，并使普通调参改变 raw
request/setup commitments。V1不这样做。

若未来确实需要沿 raw timeline切换维护配置，只在 raw中增加 neutral、versioned
`MaintenanceProfileId`，由它引用 repo config中的命名 profile；不得把完整 Planner JSON复制进 raw。

### 1.2 为什么不放入 DerivedRecap Store

`derived/recap/v4` 是可删除、可 rebuild的 sidecar。Store reset或 operator清理 derived data不能删除
重建所需的配置输入。`store.json` 也不应接收 Planner DTO或保存 opaque policy bytes。

Store只需继续验证 manifest/block/publication；最多在 operation report中携带外部提供的 config
fingerprint，不把它升级为 Store authority。

## 2. 路径与命名

Canonical path：

```text
config/recap-planner-config.json
```

Canonical schema：

```text
atelia.session-journal.recap-planner-config.v1
```

建议代码命名：

```text
RecapPlannerConfig                 // 已解析的 Planner runtime config
RecapCadenceConfig                 // recent reserve + rolling interval
RecapPlannerConfigDocument         // persisted JSON DTO
RecapCadenceConfigDocument         // persisted cadence DTO
RecapPlannerConfigCodec            // strict decode + canonical encode
RecapPlannerConfigLoader           // repo path、安全读取、snapshot
RecapPlannerConfigResolveResult    // Host侧 policy/profile resolution
ResolvedRecapPlannerComposition    // Host侧 config + policy + profiles
```

assembly边界：

- `SessionJournal.DerivedRecap.Planner`：Document、codec、relative path、loader、typed load result、
  policy ids与 protocol hard caps；
- `SessionJournal.DerivedRecap.Maintainers`：profile name / MaintainerId到 concrete descriptor/factory的
  capability catalog；
- `SessionJournal.Cli`：拥有 Host-side resolve result，把 config snapshot、policy、profile metadata、
  connection与 logging client组合为一次 command/lifecycle；
- raw SessionJournal与DerivedRecap Store均不引用 config document。

不在文件名中加入 `v1`；wire version由 `schema` 决定，稳定发现路径不随 schema升级变化。

不选其他位置：

- repo root平铺文件：可用，但未来 repo-level controls容易堆积；
- `.atelia/`：Session repo常已位于外层 `.atelia/<app>/sessions/<id>`，再次嵌套含义重复；
- `derived/recap/`：会把 rebuild input放进 disposable output tree；
- branch name目录：branch name可变，不是 durable identity。

V1是 repo-wide。同一 EventJournal repo的所有 RefId共享配置。确有 per-ref需求后再设计
canonical `RefId` override与 precedence；V1不预埋 nullable override或自由字典。

## 3. V1 JSON

```json
{
  "schema": "atelia.session-journal.recap-planner-config.v1",
  "planningPolicy": "bounded-maintain-all-v1",
  "cadence": {
    "minimumRecentHistoryUnitCount": 20,
    "recapBuildIntervalUnitCount": 24
  },
  "catalog": [
    {
      "maintainerProfile": "world-understanding-rewrite",
      "maxContentUtf8Bytes": 32768
    },
    {
      "maintainerProfile": "autobiographical-rewrite",
      "maxContentUtf8Bytes": 32768
    }
  ],
  "limits": {
    "maxRawGrowthEventCount": 512,
    "maxRouteEndpointsPerBlock": 4,
    "maxMaintainerCallsPerBuild": 8,
    "maxRawEventsPerStep": 64,
    "maxRawEventsPerBuild": 512
  }
}
```

### 3.1 字段语义

`planningPolicy`
: versioned Host/Planner registry key。V1只接受 `bounded-maintain-all-v1`，对应
  `BoundedMaintainAllRecapPlanningPolicy`。未知 id fail-fast，不反射加载任意类型。

`cadence`
: 映射 `RecapCadenceConfig`。`minimumRecentHistoryUnitCount`是每次 Published后必须留在
  admission之后的 minimum recent reserve；`recapBuildIntervalUnitCount`是 reserve之外至少新增
  多少 HistoryUnits才允许下一次 build。精确公式、replay-safe admission与 delayed catch-up见
  [Derived Recap Cadence](derived-recap-cadence-target-design.md)。

`catalog`
: 有序 active profile数组。顺序是新 manifest的 canonical block顺序，也是最终 context
  contribution顺序。至少一个元素；不得重复 profile、resolved `RecapBlockId`或 resolved Target。

`maintainerProfile`
: `RecapMaintainerProfileCatalog` 的稳定 profile name，例如
  `world-understanding-rewrite`。Host由 profile唯一解析：
  `RecapBlockId + Target + MaintainerId + concrete factory`。

`maxContentUtf8Bytes`
: operator为该 block设置的最终 content ceiling；仍受 neutral
  `SessionContextContributionContract`上限约束。

`limits`
: raw traversal、route与 provider call的五个 planning safety ceilings。它们不参与正常 cadence
  trigger。`maxRawGrowthEventCount`统计 raw Parent-lineage events，包含 API failed/retry，仅作为
  pathological growth backpressure；它只统计 cadence baseline之后的 exact raw range，不统计
  bootstrap setup prefix或 lagging block cursor到 baseline的旧区间。

配置不重复声明 profile已经决定的 `RecapBlockId`、carrier/block key和 `MaintainerId`，避免同一个
binding出现两份可冲突 authority。

### 3.2 Strict codec

V1 codec要求：

- 所有字段 required；
- exact property set，unknown/duplicate property拒绝；
- enum/id采用 ordinal、case-sensitive匹配；
- catalog保持输入顺序；
- 文件有明确的 bounded byte limit；
- 数值先做 JSON range检查，再调用 `RecapCadenceConfig`与 `RecapPlannerConfig` constructors执行
  领域与 checked-sum校验；
- V1要求
  `checked(MinimumRecentHistoryUnitCount + RecapBuildIntervalUnitCount)
  <= MaxRawGrowthEventCount`，保证 cadence在 raw safety gate前可达；
- 不支持 comments、trailing commas、environment interpolation或 extension bag；
- canonical encode固定 property order与 escaping；
- `ConfigSha256`由 normalized canonical document bytes计算，文件内不保存 self-hash。

`ConfigSha256`只标识 operator config document，不声称标识 prompt bytes或 concrete provider。
operation report可另外记录 resolved profile prompt fingerprints，但不得把 prompt正文写入
content-free report。

## 4. Resolution：一个 snapshot、两个消费者

当前 CLI分别调用 `CreateConfig()` 与 `CreateMaintainers()`，后者再次创建 catalog。Repo-owned
cutover必须删除这条潜在 drift：

```text
open config file once
  -> strict RecapPlannerConfigDocument
  -> RecapPlannerConfigLoadResult.Available
  -> resolve planningPolicy
  -> load Host capability metadata independently
  -> resolve ordered active profiles against capabilities
  -> build exact RecapBlockCatalogEntry[]
  -> new RecapPlannerConfig(...)
  -> ResolvedRecapPlannerComposition
```

一次新-planning command/lifecycle只持有一个 immutable
`ResolvedRecapPlannerComposition`。Planner config、policy、active profile bindings、safe report
view与config hash都从它投影；任何消费者不得自行重建默认 catalog。

connection选择仍来自外部 `connections.json`。同一 resolved profile可使用本次 operator选择的不同
connection/model；connection identity与 secret不写入 repo config。

### 4.1 Active roster 与 capability registry

需要区分：

- active roster：配置的 `catalog`，只决定新 Building包含哪些 blocks；
- capability registry：当前 Host能为哪些 exact `MaintainerId + Target`提供实现，用于执行 frozen
  Building或 Restore旧 Published component。

配置删除一个 active profile不能重新解释既有 manifest，也不等于 Host必须立刻失去修复旧 set的能力。
Host应通过独立 capability metadata catalog按 frozen manifest的 exact MaintainerId解析所有仍受支持的
built-in profile；只在确定实际 execution actions后创建所需 logging client。

V1不设计 active roster的在线演化。文件可表达初始 roster；一旦已有 Published，后续新 planning
要求 active catalog与 latest Published frozen catalog保持 exact ordered equality。比较字段为：

```text
RecapBlockId + Target + MaxContentUtf8Bytes
```

- cadence、五个 planning limits与 policy调整可以影响后续新 plan；
- roster增删、重排、改变 resolved block/target或修改 per-profile content ceiling一律返回
  `CatalogMigrationRequired`，即使技术上能从 latest Published读取 subset也不例外；
- 仅改变 `maintainerProfile`，且新 profile仍解析为相同 `RecapBlockId + Target`，是合法的未来
  producer切换；下一次 `Maintain`在新 manifest冻结新的 `MaintainerId`，`Inherit`不伪造 producer；
- operator要做 breaking roster切换，必须显式设计迁移，或明确 reset/rebuild并承担完整 raw
  bootstrap预算。

没有 latest Published的空 Store可用当前 catalog fresh bootstrap。已有 Building时根本不加载
active catalog，而是继续 frozen Resume。gate只读取 latest frozen blocks，不追溯 source chain，
因为 `InheritRecapBlockPlan`没有 `MaintainerId`。codec合法不代表 roster evolution语义合法。

## 5. 加载与生效路径

### 5.1 通用读取

loader接受 SessionJournal repo root，内部解析 canonical descendant：

```text
<repo>/config/recap-planner-config.json
```

它必须：

- 拒绝 repo/config/file路径链上的 symlink/reparse point；
- 要求 regular file；
- bounded read；
- 从同一个 opened handle读取完整 bytes；
- decode、normalize并完成 document-level领域校验后才返回 snapshot；
- 不创建目录、文件、Store或 call-log；
- 对 missing、I/O、oversize或 invalid document返回 typed unavailable。

config使用同目录 temporary file + flush/close + atomic rename发布。一次 operation打开旧文件或新文件，
不会在中途切换；加载完成后的替换只影响下一次 operation。不需要 config ETag double-read或持续热加载。

建议 typed load result：

```text
RecapPlannerConfigLoadResult
  Available(document, canonicalBytes, configSha256)
  Missing(path)
  Invalid(defects)
  Unavailable(reason)
```

`Invalid` defect至少区分 `UnsupportedSchema / Malformed / SizeLimitExceeded /
DuplicateProfileName / InvalidLimit / UnsafePath`。配置替换不产生 `Retryable`；
raw-head/Building CAS race继续使用现有 retryable语义。

Loader不解析 concrete policy或 Maintainer profile，避免 Planner assembly反向依赖
Maintainers。Host/CLI随后调用独立 resolver：

```text
RecapPlannerConfigResolveResult
  Resolved(ResolvedRecapPlannerComposition)
  Invalid(UnknownPolicy / UnknownProfile / DuplicateResolvedBinding)
  Unavailable(CatalogMigrationRequired / CapabilityUnavailable)
```

`CatalogMigrationRequired`需要读取 latest Published frozen catalog，属于 planning readiness，不属于
file decode defect。

### 5.2 CLI phase matrix

| 操作 | 是否加载 | 生效方式 |
|---|---|---|
| `recap planner-config init` | 否 | create-new写入 canonical default document |
| `recap planner-config inspect` | 是 | 只验证并输出 content-free normalized view/hash |
| `recap create/reset` | 否 | Store lifecycle不拥有 Planner config |
| `recap inspect` | 否 | Store/manifest/publication结构检查 |
| `recap abandon-building` | 否 | exact Store quarantine |
| `recap run` | 条件读取 | 有 Building时跳过 active config并按 manifest resume；无 Building才读取并规划 |
| `recap resume` | 否 | exact Building manifest + Host capability + protocol hard caps |
| `recap restore` | 否 | exact Published plan + Host capability + protocol hard caps |
| raw `validate` / import | 否 | raw工具保持 Recap-neutral |
| online SendNewTurn / CompleteObservation | 条件读取 | exact Building存在则 frozen Resume；否则同一 snapshot驱动新 planning |
| online Prepared / Started recovery | 否 | exact request recovery继续 config/Store zero-touch |

需要 config 的路径必须在创建 completion client、call-log、调用 LLM或 append新 Observation之前完成
readiness、load与resolve。missing/invalid config不得留下 raw或provider副作用。

### 5.3 Online order

```text
open raw engine
  -> inspect durable phase
  -> Prepared/Started: skip Store and config, exact recovery
  -> new-request phase:
       read-only Store readiness
       prepare independent capability metadata
       exact Building exists:
         resolve frozen capabilities
         resume with protocol hard caps
       no Building:
         load one repo config snapshot
         resolve policy + active roster
         compare latest Published exact frozen catalog
       resolve connection/client
       compose lifecycle + candidate source
       append/prepare/dispatch
```

不得为了判断 phase而先读取 config。删除整个 `config/` 与 `derived/recap/v4`后，Prepared exact reopen
仍必须成功。

## 6. 配置更新与 Frozen Plan

配置只支配尚未冻结的未来 plan：

```text
no Building
  -> current config decides new plan

Building exists
  -> manifest decides exact plan
  -> current config cannot replan, reorder, add/drop block or换 source/route

Published exists
  -> publication decides membership/materialization
  -> current config cannot改变 ordinal
```

### 6.1 Stable protocol hard caps

repo config中的 limits是新 planning ceilings。loader还必须验证它们不超过代码/协议定义的稳定 hard
caps。hard caps只防止单次读取、route、call或content的资源失控，不是 operator policy，也不从 active
config热更新。

V1 hard caps由 Planner assembly中的 `RecapProtocolHardCaps`唯一声明。五项 raw/route/call
初值与 R3 production config一致，content/catalog复用既有 contribution contract：

```text
MaxRawGrowthEventCount = 512
MaxRouteEndpointsPerBlock = 4
MaxMaintainerCallsPerBuild = 8
MaxRawEventsPerStep = 64
MaxRawEventsPerBuild = 512
MaxContentUtf8Bytes = SessionContextContributionContract.MaxContributionUtf8Bytes
MaxCatalogEntries = SessionContextContributionContract.MaxContributionCount
```

repo config可以取更小值，不能超过这些值。以后放宽 hard cap是显式 protocol/code review，不通过
repo热配置完成。

只要 Building通过安装 gate，它的 frozen route就已满足当时及当前 schema的 protocol hard caps。
Resume/Restore因此无需再加载 operator config，也不会因 operator调低未来 planning limit而卡死。

若未来 hard caps本身需要按 plan变化，应把紧凑的 execution limits冻结进 manifest并升级 schema；
不得重新借用“当前 active config”裁决旧 plan。

### 6.2 Resume

Resume完全不加载 active config。它只使用：

- frozen manifest/block bindings与route；
- Host对 frozen `MaintainerId + Target` 的 capability；
- stable protocol hard caps。

若 Host已不支持所需 profile，返回 typed `MaintainerUnavailable`。若 frozen manifest违反当前 schema
hard caps，返回 structural/protocol defect。不得自动 abandon、reset或按新配置 replan。

operator可以：

1. 恢复所需 Host capability并 resume；
2. 显式 `abandon-building` 后用当前 active config重新 `run`。

不把整个 `ConfigSha256`写入 manifest作为恢复锁。manifest已冻结 correctness-relevant plan；仅修改
trigger等与 Resume无关的字段不应阻止恢复。

### 6.3 Restore

Restore同样不加载 active config。它按 exact publication/manifest确定所需 Maintainer capability，
只受stable protocol hard caps约束。capability缺失时 exact slot保持 not-ready，不 fallback邻居、
不改变 ordinal。

### 6.4 必需的 authority 拆分

当前 `RecapPlannerConfig`同时包含 scheduling和execution字段。实现 repo config时必须把 internal
projection明确拆成：

```text
RecapPlanningInputs
  = active catalog + RecapCadenceConfig + policy

RecapPlanningLimits
  = repo-owned raw-growth/route/call/raw-step planning ceilings

RecapProtocolHardCaps
  = code/schema-owned absolute safety bounds
```

persisted JSON仍保持一个文件和一个 schema；hard caps不是第二份 operator configuration。拆 projection
是为了让 Resume/Restore从 active roster、trigger和可调 ceilings中彻底解耦。

必须相应改变 executor API：

```text
new-plan executor <- RecapPlanningInputs + RecapPlanningLimits
resume executor   <- frozen Building + capability registry + RecapProtocolHardCaps
restore executor  <- frozen Published plan + capability registry + RecapProtocolHardCaps
```

Resume不得再比较 `_config.Catalog`，Restore constructor不得再接收 `RecapPlannerConfig`。
frozen plan中的“limits exact”专指 per-block `MaxContentUtf8Bytes`与已经冻结的 actual route/window；
active `RecapCadenceConfig`与五个 operator planning ceilings不属于 frozen authority。

## 7. CLI 管理表面与 cutover

建议只增加：

```text
recap planner-config init
recap planner-config inspect
```

`init`
: create-new写入当前 canonical default；文件存在即拒绝，不提供 `--force`，不创建/reset Store。

`inspect`
: strict加载并输出 path、schema、config hash、policy、resolved ordered catalog和limits；不输出
prompt、connection或 secret。

V1不增加任意 `--planner-config <path>`、environment override或built-in fallback。否则 repo file、
CLI参数和默认常量会形成三重 precedence。

breaking cutover步骤：

1. 先实现 `RecapCadenceConfig`与 exact HistoryUnit cadence，删除 `RawGrowthTrigger`；
2. 实现 document/codec/loader与 path safety；
3. 实现 profile/policy resolution，形成 single composition snapshot；
4. 迁移 `recap run/resume/restore`；
5. 迁移 online new-request phases，保持 Prepared/Started zero-touch；
6. 删除 `RecapCliComposition.CreateConfig()` hardcoded authority与二次 `CreateCatalog()`；
7. 更新真实 repo，显式执行 `recap planner-config init`；
8. 不为缺失文件保留长期 compatibility fallback。

importer不自动创建配置。raw migration成功与 operator选择哪种 Recap policy是两个独立动作。

## 8. 未来 information estimator

V1的 cadence使用 `HistoryUnitCount`；`maxRawEventsPerStep/Build`继续作为结构性 hard ceilings。
两者都不是 token或信息量估算。

未来 estimator需求明确后升到 V2，采用受控 registry id和明确单位，例如：

```json
{
  "historyLoad": {
    "estimator": "some-versioned-estimator-id",
    "minimumRecentUnits": 100000,
    "recapBuildIntervalUnits": 120000
  }
}
```

具体字段名和单位届时再定。V1现在只冻结以下扩展原则：

- estimator实现由 Planner/Host registry拥有，raw core与Store不理解 tokenizer；
- config只保存 versioned estimator identity、明确单位与 cadence thresholds，不保存模型endpoint或
  任意插件参数；
- estimator直接测量 ordered HistoryUnit range；V2不预设 per-unit additivity或 prefix差分语义，
  absorbed range与 recent suffix range分别验证，以容纳 chat template、role marker与 separator
  overhead；
- raw event safety ceilings继续独立生效；
- estimator影响 admission/route时，Building至少冻结最终 route；是否还需冻结 estimator
  identity、fingerprint或诊断结果，由 V2 ADR依据可解释性与恢复需求决定；
- provider-specific exact tokenizer可以是某个 estimator实现，但不能成为基础 contract的默认语义。

## 9. 实施工作包

### C0：Cadence contracts + deterministic policy

- 按 [Derived Recap Cadence](derived-recap-cadence-target-design.md)实现
  `RecapCadenceConfig`；
- 删除 `RawGrowthTrigger` scheduling authority；
- exact `HistoryUnitCount` trigger与 minimum recent admission；
- normalized baseline与 `R+B <= MaxRawGrowthEventCount`可达性校验；
- baseline等于 planning-window `StartExclusive`的 `L=0`分支与 typed invalid分支；
- API failed/retry zero-unit、bootstrap、dependency closure与 delayed catch-up focused tests；
- 本包先沿用 programmatic config，不提前接 repo file。

### C1：Document + single composition snapshot

- `RecapPlannerConfigDocument` strict codec/canonical bytes/hash；
- canonical repo path、bounded safe read与 atomic init；
- `planner-config init/inspect`；
- policy/profile registries；
- document → exact catalog → `RecapPlannerConfig`；
- wire loader与 Host resolver使用两个独立 typed result；
- config、policy、active roster只解析一次；
- capability metadata registry独立于 active roster；
- 实现具备上述固定初值的 `RecapProtocolHardCaps`；
- 将 new planning projections与 Resume/Restore输入 authority强制拆开；
- execution report增加 config schema/hash；
- 删除 hardcoded `ProductionConfig`和二次 `CreateCatalog()` authority。

### C2：CLI + Online cutover

- `run/resume/restore`加载矩阵；
- `run` existing-Building fast path与 frozen manifest authority；
- Resume/Restore API不再接收 active config，且不读取 active config；
- online phase-first、Building-first条件加载；
- online new planning使用 exact cadence，report分开记录 unit growth与 raw safety counts；
- latest Published catalog exact ordered equality gate；
- missing/invalid config的 zero-provider/zero-raw-side-effect tests；
- Prepared/Started删除 config/Store仍 exact recover。

### C3：Real repo acceptance

```text
import real export
  -> planner-config init
  -> recap create
  -> run / partial failure / resume
  -> Published inspect
  -> run again = NoBuild
  -> grow to R+B-1 units = NoBuild
  -> grow to R+B units + cadence-safe boundary = Published with recent >= R
  -> edit config atomically
  -> next command observes new hash
  -> existing Published ordinal/materialization unchanged
```

## 10. 验收矩阵

- filename/schema/type mapping golden；
- missing/unknown/duplicate/truncated/oversize config；
- unknown policy/profile；
- duplicate resolved block/target；
- latest Published后 add/remove/reorder/resolved-identity/content-ceiling变化均返回
  `CatalogMigrationRequired`，零 LLM、零 Building写入；
- 同一 block identity上的 profile切换只影响下一次 `Maintain`，且不递归追溯 Inherit producer；
- numeric boundary、`R+B` overflow及 `R+B <= MaxRawGrowthEventCount` cross-field validation；
- cadence `R=20/B=24` threshold、minimum reserve与 failed/retry zero-unit；
- catalog order canonical；
- symlink/reparse/file-vs-directory/ancestor escape；
- init create-new与 atomic publication crash points；
- one command只打开/resolve一次；
- report config hash来自实际 snapshot；
- Store create/reset/delete不触碰 config；
- raw import/validate不读取 config；
- config missing时，无 Building的 `run` 与 online new-planning在
  client、call-log、Observation前失败；
- Prepared/Started recovery不读取 config；
- config更新只影响下一次 operation；
- existing Building不按新 config replan；
- online existing Building在 config缺失/损坏时仍按 frozen manifest resume；
- active config丢失、损坏或调低 limits后仍可 resume/restore；
- frozen plan违反 protocol hard caps返回 structural/protocol defect；
- inactive但仍受支持的 profile可 Restore旧 Published component；
- config变化不改变 Published membership、ordinal或 materialized bytes；
- normal cadence、delayed multi-endpoint catch-up与真实 provider smoke。

## 11. Non-goals

- V1 tokenizer或 information estimator选型；
- provider/model/connection配置；
- prompt正文或任意 prompt override；
- per-ref/per-branch override；
- hot reload、watcher或后台 scheduler；
- config migration framework；
- Store schema变更；
- raw `RuntimeConfigSetup` schema变更；
- exactly-once provider调用。
