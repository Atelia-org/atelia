# SessionJournal 语义保持型 Contract Normalization 审阅报告

状态：N0–N5 closed；normalization accepted  
Execution baseline：`cd804c39cf96499167c80e5d046fb21e4d3b8c7d`  
Implementation candidate：`49ebb4634e5b4136032db983dd92a9a4560b33eb`  
Implementation range：`cd804c39..49ebb463`（baseline exclusive）  
Gate-tooling commit：`81a1fa24058fc4b5405e14a5fc504844ce5fd345`（tests/runbook only）

本文是
[`session-journal-semantic-preserving-contract-normalization-review-plan.md`](session-journal-semantic-preserving-contract-normalization-review-plan.md)
的综合结论。run-specific inventory、盲审报告与 plan lock 位于
`gitignore/session-journal/reviews/2026-08-03-contract-normalization/`，不作为 contract authority。

## 1. Decision summary

本轮删除了重复 public entry path、caller 可构造的 authority-bearing result 状态和重复 registry key
概念，并关闭两个 Host fail-closed gaps。没有改变 raw/recovery authority、Prepared reconstruction、Planner
config wire、DerivedRecap filesystem wire、exact-head mutation、bounds、atomic publication或恢复语义。

已实施的收口：

1. `SessionJournalEngine` 只保留无 runtime 的 public `Create/Open` 与唯一 `UseRuntime` 绑定路径；
2. 任意 `EventAddress` raw payload byte escape 从 public 收回 internal；
3. 九个 Store authority-bearing inspection/success 类型只能由 Store 以完整、非空、匹配的 authority 构造；
4. Planner catalog 直接接收 implementations，删除 registration wrapper 和 caller-supplied duplicate key；
5. Galatea 在 config/Host 构造期拒绝 duplicate normalized lexical `SessionDir`；
6. Galatea 对 `BeyondPrefix` 使用稳定 typed failure，保持 bounded stop且不 fallback；
7. Planner、Maintainers、Galatea README 与当前 public API、exhaustive mapping和 lazy session access 对齐。

没有找到值得移动 A/B/C durable wire 的高杠杆等价化方案。Prepared 与 Store 中看似重复的字段承担
dependency closure、path binding、commitment、corruption detection或recovery proof，因此予以保留。

## 2. Candidate ledger

| Candidate | Decision | 结论 |
|---|---|---|
| N-A-01 single runtime binding path | **Adopt** | 删除三个 runtime-bearing `Create/Open` overload；保留无-runtime factory + `UseRuntime`。 |
| N-A operation-scoped runtime | **Prototype / Defer** | 会扩大 Core 私有执行链与 composition-root blast radius；当前无 safety finding 支撑实施。 |
| N-A-02 arbitrary raw payload escape | **Adopt** | `ReadPayloadBytes(EventAddress)` public→internal；supported readers继续走projection、ReadView或Offline checked scan。 |
| N-A-03 Engine/ReadView bounded aliases | **Retain-intentional / Defer** | Engine服务owning Host/Offline；ReadView是Derived capability boundary，不是双authority。 |
| N-A-04 owning read-only type | **Prototype** | 尚未证明能以更小surface同时覆盖Offline audit、projection、Prepared inspection与view lifetime。 |
| N-A-05 same-path repository replacement | **Prototype** | 旧opaque proof在同路径repo replacement下的风险尚未复现，不直接增加incarnation wire。 |
| N-B-01 Prepared setup versions/model assertion | **Retain-intentional** | 属于fail-closed proof redundancy；删除需Prepared v6 direct cut且降低自描述与drift detection。 |
| N-B-02 merge/delete Prepared proof fields | **Reject-not-equivalent** | raw range、exact inputs、setup hash、origin/execution、tool/runtime/target各承担不同proof。 |
| N-B-03 shared `JsonWriterOptions` | **Defer** | 低杠杆实现去重，不减少contract concept。 |
| N-C-01 Store-issued authority results | **Adopt** | 九个inspection/success类型改为Store-only construction，authority public get-only且从出生完整。 |
| N-C-02 publication positional normalization | **Retain-intentional** | `RefId`/anchor/block identity/target负责path、slot与envelope自认证，冲突必须fail closed。 |
| N-C-03 catch-up/block wire normalization | **Prototype / Defer** | 会改变checkpoint indexing/transitive hashes或削弱standalone diagnostic decode。 |
| N-D-01 registration wrappers/duplicate keys | **Adopt** | catalog直接冻结construction-time implementation ID与对象，删除两个wrapper类型。 |
| N-D-02 Planner operation environment | **Reject / Defer** | 只把capability snapshot与active source装箱，没有删除两个不同角色。 |
| N-D-03 provenance/hard-cap/baseline variants | **Retain / Prototype** | document decode、environment resolution、code caps与operator limits保持分层。 |
| N-E-01 duplicate normalized session dirs | **Adopt** | 一个normalized OS-aware lexical path只能属于一个user。 |
| N-E-02 typed `BeyondPrefix` | **Adopt** | 稳定`GalateaTurnException`，保持bounded stop；不扩界、不full scan，不触发normalizer/provider/maintainer/log或Recap Store write。governing setup已对齐的focused fixture还证明raw/Store tree不变。 |
| N-E-03 README/API drift | **Adopt** | samples统一为Preparer→exhaustive mapping→PreparedExecutor并修正文档。 |
| N-E-04 repository preparation overload | **Prototype / Defer** | 保留custom source seam后会新增public entry path，未证明能净减少concept。 |
| N-E Host phase/reinspection/projection | **Retain-intentional** | 分别承担Host policy、verification、secret construction或race defense。 |

## 3. Before/after authority graph

Before：

```text
Host-owned SessionRuntime ──> Create/Open(runtime) ─┐
                         └──> UseRuntime ───────────┴──> Engine._runtime

external caller ──> ReadPayloadBytes(any address) ──> raw bytes
external caller ──> public Store result ctor/init ──> forgeable/incomplete result shape
caller key + implementation.Id ──> registration wrapper ──> Planner catalog
UserId ──> per-user Lazy Engine ──> SessionDir (duplicate owner可延迟到首次open才冲突)
```

After：

```text
Host ──> Create/Open ──> inspect/compose ──> UseRuntime ──> exact-head Send/Resume
reader ──> projection / engine-bound ReadView / Offline checked scan ──> raw authority
arbitrary raw-byte escape ──> internal only

Store ──> complete inspection/success + matching non-null opaque authority
     └──> locked exact-state validation ──> mutation ──> refreshed authority

implementation list ──> construction-time Ordinal ID snapshot ──> Planner lookup
                                                        └──> current ID drift typed reject

normalized SessionDir ──> unique user owner ──> one Lazy Engine/TurnLock
preparation closed union ──> exhaustive mapping ──> BeyondPrefix remains bounded stop
```

After graph没有新增authority owner。Core仍拥有raw写入/codec/recovery validation；Host仍拥有runtime
composition；Store仍拥有DerivedRecap membership与mutation authority；Planner仍只冻结execution plan；
Maintainers仍只产生由Store authority接收的concrete output。

## 4. Before/after public support map

| Role | Before | After | Semantic delta |
|---|---|---|---|
| Online Host | 无-runtime factory、三个runtime factory、`UseRuntime` | 无-runtime `Create/Open` + 唯一`UseRuntime` | 0；phase/recovery/exact-head规则不变 |
| Offline/audit | `OpenReadOnly`、checked audit、Engine bounded reads | 相同 | 0；仍fail closed且不repair |
| Derived consumer | lifetime-bound `SessionJournalReadView` | 相同 | 0；Derived仍拿不到writable Engine |
| Migration | owned import writer与checked readers | 相同 | 0 |
| Raw bytes | public arbitrary-address escape | internal only | unsupported surface收窄；raw wire不变 |
| Store consumer | 可构造/`init` authority-bearing result | 只能读取Store-issued closed result | 合法能力保留，非法状态移除 |
| Planner composition | caller key + wrapper + implementation | implementation lists direct | custom injection保留；config/result不变 |
| CLI | 既有phase/resource routing | 相同；tests改走normalized runtime path | 0 production delta |
| Galatea | duplicate path延迟失败；`BeyondPrefix`落generic unknown | 构造期reject；typed bounded stop | fail-closed contract变严格 |
| White-box tests | 旧factory形状 | test-only `SessionJournalTestRuntime.Attach` | helper不属于public contract，失败会dispose Engine |

## 5. Wire fact-ownership matrix

| Tier | Durable facts | Owner/writer | Reader/validator | Intentional proof redundancy | 本轮delta |
|---|---|---|---|---|---|
| A raw/recovery | raw envelope、event kind/body、selected Parent lineage、Prepared v5 proofs | Core Engine与owned import/create paths | strict codec、Prepared reconstructor、Offline audit | setup body version、model assertion、payload/range/input hashes、origin/execution assertions冲突时fail closed | **0 bytes；0 reader-language delta** |
| B Planner config | config v2 policy/estimator IDs、limits、catalog order | canonical codec与operator active config | strict codec + resolver/catalog | decode/environment resolution继续分层；unknown ID、duplicate、drift、hard-cap overflow拒绝 | **0 config bytes；0 routing delta** |
| C DerivedRecap | store v4、manifest v6、frozen input v5、block v4、publication v6、checkpoint | Store membership/publication；Planner frozen plan；Maintainer content | Store codec、path/lineage/setup/plan/hash/state-token validators | outer identity、embedded plan、block target及payload/plan/envelope hashes承担path/slot/corruption proof | **0 filesystem bytes；0 schema/recovery delta** |

这些关系不是两个可独立writer的双真源。冲突规则仍是fail closed，不能替换为implicit default、array-position
inference或tolerant reconstruction。

## 6. Illegal states removed or rejected earlier

| 非法状态 | Before | After |
|---|---|---|
| 多个factory path写同一runtime state | public可选 | 旧overload不存在；只有`UseRuntime` |
| external任意address raw-byte read | public可调用 | public surface不可见 |
| caller构造缺authority或转抄authority的Store result | 类型可表示 | constructor/internal issuance收口；authority get-only、非空 |
| registry alias key与implementation `Id`分离 | wrapper允许表达后再检查 | wrapper删除；catalog直接冻结implementation ID |
| null/blank/duplicate implementation ID | 检查分散 | catalog construction统一拒绝 |
| implementation drift后按新IDfallback | 依赖resolver防守 | frozen old-key lookup后typed mismatch，无fallback |
| 两个user以`.`/`..`或comparer-equivalent path共享SessionDir | config可通过 | loader与Host construction fail closed，且无repo/client/log/maintainer副作用 |
| `BeyondPrefix`落generic unknown分支 | 可发生 | typed `recap-beyond-prefix`，保持bounded stop |
| docs诱导实例化internal executor或漏match variant | 可发生 | samples只用current public API与exhaustive mapping |

这里的“不可表示”只指public contract/composition boundary；不声称恶意filesystem writer不能制造损坏。

## 7. Commit map and independent tail reviews

| Commit | Package | Closure |
|---|---|---|
| `8bdb5799` | WP-A | 收窄public factory/raw-byte surface |
| `9d6402d6` | WP-A tail | 删除残余internal factory shim，95个白盒调用走唯一runtime path |
| `a3ead9fc` | WP-C | 九个Store-issued result类型收口 |
| `ee3dbc58` | WP-C tail | 删除raw-token seam与post-write补读；success直接返回refreshed authority |
| `96f1ee14` | WP-D | 删除registration wrappers/duplicate keys |
| `9a5225a1` | WP-D tail | reflection锁定旧类型不存在与catalog唯一constructor |
| `63253a1f` | cross-package test | Galatea测试迁移到normalized runtime path |
| `4e4873ed` | WP-E | duplicate SessionDir、typed BeyondPrefix、tests/docs |
| `19011132` | WP-E docs tail | 精确说明SSE首访lazy-open行为 |
| `49ebb463` | cross-package test | CLI测试迁移到normalized runtime path |
| `81a1fa24` | N4 gate tooling | create-only current-v6 scripted staging provisioner、path-safety tests与runbook |

独立review关闭的tail findings：

- WP-A：首个commit仍保留三个internal runtime factory shim；`9d6402d6`彻底删除并reviewer PASS；
- WP-C：internal raw-token seam会在durable replace后补读并可能“写成功后抛错”；`ee3dbc58`删除seam，
  允许Unavailable component合法empty state token并保持Building规则不变，reviewer PASS；
- WP-D：缺少永久public-shape guard；`9a5225a1`补reflection fixture，reviewer PASS；
- WP-E：README把SSE“不消费Engine”写得过宽；`19011132`区分成功订阅与endpoint首访，reviewer PASS。
- N4：首次显式staging执行正确拒绝了已direct-cut的publication v4旧fixture，暴露当前
  v6 fixture provisioning缺口。`81a1fa24`增加scripted、create-only、Linux
  `renameat2(RENAME_NOREPLACE)`发布路径；原子directory ownership、root-aware overlap、reparse no-follow、
  publish race与zero-side-effect ordering findings均由focused tests关闭，最终reviewer PASS。

implemented scope内没有遗留open P0–P3 finding；未证实候选保留为Prototype，不伪写成已修复。

## 8. Validation matrix

环境：Ubuntu/WSL2，ext4，.NET SDK `10.0.110`；所有build/test严格串行，使用`-m:1 -nr:false`。

| Local gate | Passed | Skipped | Failed |
|---|---:|---:|---:|
| `SessionJournal.Tests` | 394 | 0 | 0 |
| `SessionJournal.Offline.Tests` | 6 | 0 | 0 |
| `SessionJournal.DerivedRecap.Store.Tests` | 194 | 0 | 0 |
| `SessionJournal.DerivedRecap.Planner.Tests` | 261 | 0 | 0 |
| `SessionJournal.DerivedRecap.Maintainers.Tests` | 28 | 0 | 0 |
| `SessionJournal.Cli.Tests` | 98 | 1 real-data opt-in | 0 |
| `Galatea.Server.Tests` | 63 | 4 staging opt-in | 0 |
| Explicit real-data acceptance | 1 | 0 | 0 |

Gate-tooling实施前，implementation candidate `49ebb463`的独立local Release solution build为0 warnings、0
errors；七套default合计1044 passed、5 expected skips、0 failed，explicit real-data acceptance另计1 passed。

真实数据fixture是1,281,881-byte legacy export，SHA-256
`b71822a27003e8d9f9b9c0ff956ca7c268267aba72221be89df154ed7d4751f3`。acceptance使用scripted
Completion clients，在disposable import上完成recap failed-run/resume、损坏final missing-only restore、online
turn与Prepared recovery；`sourceUnchanged=true`、`rawUnchangedThroughRestore=true`、原148-event raw prefix在最终
157-event lineage中保持。它不是real-provider或Galatea staging run。

Gate-tooling commit `81a1fa24`的提交后验证：Release solution build 0 warnings、0 errors；
`SessionJournal.Cli.Tests` 116 passed、1 expected real-data opt-in skip、0 failed；同一real-data Fact带
opt-in output运行1 passed，原子发布current-v6 scripted fixture；`Galatea.Server.Tests`带该fixture全套
70 passed、0 skipped、0 failed。CLI safety 18项与Galatea clone safety 3项已包含在上述全套计数中。

该scripted fixture由当前strict writer生成，production CLI复验为148 events、`Idle`、strict latest
`Selected`、2 contributions、0 defects、publication schema v6。固定source SHA-256与受保护的旧v4
base全树hash在provision前后不变；当前fixture在Host tests前后全树SHA-256均为
`3d150c647ca82106407dca2d939fde1e15de5bbafb6cefc7f51ba6122d7e83fd`。

### Fresh clone A

- Clone：`git clone --no-local`；独立临时目录；
- exact candidate：`49ebb4634e5b4136032db983dd92a9a4560b33eb`；
- restore/build：Release solution build 0 warnings、0 errors；
- seven suites：1044 passed、5 expected opt-in/staging skips、0 failed；
- explicit real-data acceptance：1 passed、0 skipped、0 failed；
- explicit scripted staging Host acceptance：4 passed、0 skipped、0 failed；
- status：**PASS**。

### Fresh clone B

- Clone：`git clone --no-local`；独立临时目录；
- exact candidate：`49ebb4634e5b4136032db983dd92a9a4560b33eb`；
- restore/build：Release solution build 0 warnings、0 errors；
- seven suites：1044 passed、5 expected opt-in/staging skips、0 failed；
- explicit real-data acceptance：1 passed、0 skipped、0 failed；
- explicit scripted staging Host acceptance：4 passed、0 skipped、0 failed；
- status：**PASS**。

两个exact-candidate fresh clone合计：七套default tests 2088 passed、10 expected skips、0 failed；
explicit real-data acceptance 2 passed、0 skipped、0 failed。两份real-data report都记录同一固定source hash、
`sourceUnchanged=true`、`rawUnchangedThroughRestore=true`与148→157 preserved raw prefix。显式scripted staging Host
acceptance另计8 passed、0 skipped、0 failed；两个clone都只写各test的private owned clone，fixture base hash不变。

## 9. Provider/staging boundary

本轮没有触及Completion request construction、canonical provider request bytes、adapter/route/model/tool binding、
prompt/body、runtime dispatch、Prepared durable identity或call-log authority contract。按计划只有触及provider request
construction时才需要真实provider，本轮调用预算为0，实际provider calls也是0。

Galatea default suite中的4个expected skips仅表示默认轮没有提供external fixture；随后两个exact-candidate
clone都使用由current writer安全生成的v6 fixture显式执行该4项，各自4/4通过。这是scripted
disposable-Host acceptance，不包含real-provider dispatch、`dsv4p` provider quality/availability或real Host canary。
上一候选`681fc02b`的real-provider与staging R4只作为历史baseline，不计入本候选aggregate。

## 10. Residual risks

1. provider exactly-once仍不是承诺；call log缺失不能证明未调用；
2. event append与selected-ref CAS不构成跨文件事务，失败时可留下unselected orphan；
3. `OpenReadOnly`不repair tail或执行full scrub；
4. Linux durability、directory fsync与hostile concurrent writer边界保持原定义；
5. bounded prefix/lineage/inventory不自动扩大为full scan；
6. DerivedRecap仍是rebuildable sidecar，raw+selected Parent lineage才是authority；
7. Store/Planner/Maintainers的active/frozen/execution ownership仍刻意分离；
8. D-level API是pre-release direct cut，仓外consumer需自行compile migration；
9. `SessionJournalTestRuntime.Attach`只是white-box helper，不属于support contract；
10. `SessionDir`规则证明normalized lexical uniqueness，不识别所有bind mount、hard-link或hostile alias；
11. `BeyondPrefix` zero-side-effect fixture先对齐governing setup；未对齐请求仍可先执行既有setup reconciliation；
12. same-path repository replacement/opaque proof lifetime尚未复现，不引入A-level incarnation wire。

## 11. Final decision

Adopt candidates已实现并完成独立review/tail loop；Retain/Reject redundancy均有明确proof或recovery职责；
Prototype/Defer候选未混入；A/B/C durable wire delta为0；local与real-data scripted acceptance为绿。

两个独立fresh clone已完成exact-candidate Release build、七套default tests、显式real-data acceptance
与显式scripted disposable-Host staging acceptance；所有required gates已闭环。最终裁决：

> **Contract normalization accepted; candidate `49ebb463` is Beta-supported within the boundaries recorded here.**
