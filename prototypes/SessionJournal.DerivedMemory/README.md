# SessionJournal.DerivedMemory

可替换的 SessionJournal derived-memory 子系统。它单向引用
`Atelia.SessionJournal` 的 neutral candidate contracts，负责：

- `derived/memory/v2/artifacts/` epoch-bound append-only candidate persistence；
- `derived/memory/v2/` coherent ArtifactSet、exact-previous CAS 和 latest pointer；
- shared `DerivedArtifactEpochPlanner` 的 immutable config lineage、epoch ledger 与
  rebuildable current/latest indexes；
- deterministic multi-role orchestration transaction、immutable role settlement、
  durable finalization intent、missing-role resume 与 required-role closure；
- 把已发布的 exact set 投影为 exact two-phase `ICoherentContextCandidateSource`；
- 通过 `DerivedMemoryOnlineLifecycleCoordinator` 在 safe raw boundary 组合 shared epoch
  planning、pending-first maintenance、ArtifactSet publication 与显式 backpressure。

DM-6 candidate store 不再维护 role-local latest pointer。`DerivedMemoryArtifactStore`
只接受 v2 exact-epoch identity，并允许同一 role/epoch 的 prompt/model tuning 结果
append-only 共存；只有 ArtifactSet publication 才决定 candidate 是否可选择。旧
`derived/recaps/v1/`、latest-by-profile 与 linear recap CAS 已直接退役。

DM-8 provider 只支持精确的 `NthPrevious(n)`；`n = 0` 就是 latest。selection 阶段沿
`PreviousSetId` 严格走到第 n 个 set，只返回一个 content-free descriptor，materialization
才读取 exact member text。非空 lineage 太短返回 `OrdinalUnavailable`；中间 set 缺失、
损坏或形成 cycle 都 fail-fast，不跳过、不重编号，也不转入 bootstrap。
latest pointer 缺失时 selection 只从 immutable sets 证明 unique tip，不修复 pointer；
持久 rebuild 只能走带 Engine raw-authority gate 的 maintenance/ops API。ordinal 来自
governing `RuntimeConfigSetup` v2 的 `derivedContext.nthPrevious`，不由 provider request
budget 或 host runtime flag 决定。唯一 request-size guard 是
`MaximumCanonicalRequestBytes`：它测量 SessionJournal canonical request JSON 的精确 UTF-8
byte length，超限时拒绝，不会自动改选另一个 set；该值不是 provider tokenizer 或 context
window。

边界约束：

- raw SessionJournal 不引用 artifact/set id；
- branch-local planner、publication、rebuild 与 validation 由 composition root 先按 branch name
  打开 `SessionJournalEngine`，再通过 `DerivedMemoryRepository.Bind(engine)` 获得不能自由伪造的
  exact `RefId` scope；无 engine 的 global validation 枚举所有 active refs 并逐 ref 证明 raw
  authority；
- composition root 在发布前通过 SessionJournal 的 strict anchor helper 取得
  setup address/schema/payload hash；
- provider 返回的 raw-facing assertions 仍由 SessionJournal authoritative validator 复核；
- Prepared 已保存进入 provider request 的 exact snapshots，故 Prepared 后删除整个
  `derived/` 仍可恢复。

真实空 lineage 只有在 raw branch 仍是 native fresh-genesis topology 时才能 bootstrap：
`SessionCreated` 后只能有 setup updates，或再有恰一个 active first `ObservationAccepted`。
`SessionCreated.origin=legacy-import`、任一 Prepared/action/import/tool/attempt/failure/history
fact 都拒绝 bootstrap。missing latest pointer 必须先通过 immutable sets 的 unique-tip discovery
证明，不能伪装为空。bootstrap 不创建空 artifact，而由 Prepared v5 的零个
`ExactContextInputs` 固化 exact request。一旦 raw ancestry 写入 Prepared，即使 derived lineage
后来被删空也不会再次 bootstrap；反之，未被 Prepared 使用的 set 删除后仍可按 fresh topology
bootstrap。

DM-5 planner 在任何 maintainer/LLM 执行前，只通过 SessionJournal 暴露的
`ReadHistoryPlanningWindow()` 读取 bounded、dependency-closed suffix。config key 是
`BranchRefId + coherenceGroup`；branch name 只在 Engine Open 时作为 selector，durable
config/epoch/set/latest/orchestration identity 一律保存 canonical lowercase `RefId`。config snapshot
与 epoch 都是 deterministic、append-only
identity，mutable pointer 只作为可重建 index。genesis 明确使用 empty-memory-pack policy；
非 genesis epoch 必须绑定一个真实、self-validating 的 coherent ArtifactSet，且其
`CommonAnchor` 必须等于 previous epoch 的 `SourceEndInclusive`。planner 不运行 maintainer、
不发布 set、也不写 raw event。

raw scan/candidate computation 不持有 derived repository write lock；所有 planning 终态在
短锁内重读 current config/latest pointer 后线性化。strict repository validation 与
latest-epoch pointer rebuild 还会使用 core header-only snapshot 与 batched planning seeds
验证 epoch raw interval/selected-ref membership；随后按 exact historical head 增量重放每个
window，并用该 epoch immutable config 重算 dependency-safe boundary 与 token cost。genesis
必须从 SessionCreated 开始，multi-tool 中间 boundary、rewind/divergent epoch、wrong setup/cost
即使 derived JSON/hash 自洽，也不能成为 current latest。batch seed 只解 setup payload，避免
多 epoch legacy stable-root setup 验证退化成 E 次全链回溯；整个路径不调用 `Project()`。

`DerivedMemoryMaintainerRunner` 只 lookup exact epoch/config/input set，并调用
core 的 durable setup-seed API 验证 `RawStartSetups` 的两个 exact payload 与 bounded
execution recovery，再用 `ReadHistoryPlanningWindowAt(sourceEnd, seed)` 物化 exact
range；不会为了一个 role run 扫描/验证全 repository，也不会从 epoch end 回扫 root。
window 直接从已解码 suffix/fold 回传 `EndSetups`。global validation 仍属于运维命令。input set 缺新 role
时以 empty old block 启动，同时把其他 role blocks 作为 PriorContext。artifact v2 明确
区分两组 typed setup provenance：`RawStartSetups` 必须等于 epoch 起点 fold seed，
`AnchorSetups` 来自 exact epoch 末端；ArtifactSet publication 只与后一组做 exact
address/schema/payload-hash coherence，下一 epoch 恢复输入时再把 set anchor 与其
`RawStartSetups` exact 对齐。

`DerivedMemoryOrchestrator` 对一个 exact epoch 只物化一次 immutable input/history
snapshot，并用 `Task.WhenAll` 并行执行尚未 settlement 的独立 roles。artifact persistence
与 `derived/memory/v2/settlements/<transaction>/` 下的 durable success settlement 分层；
required role 失败时保留已成功 partial candidates/settlements，但绝不发布半套 set。
transaction/job identity 包含 policy、topology、完整 role provisioning 与
candidate/attempt；改变 job 会创建新 transaction，重跑同一 job 只补缺失 role。
当前 composition 对每个 durable job 固定 provisioning，调用方必须复用同一
candidate/attempt identity 才能恢复同一 transaction；跨库 generation 的
JobFingerprint/TransactionId 与 CandidateId/AttemptId 合并留待独立 generation，不在当前
transaction/artifact/set id 下原地重解释。

required roles 闭合后先写 immutable finalization v2 intent。它只冻结 transaction id、
anchor setups、窄 `DerivedMemoryFinalizedRole` 列表（role/artifact/outcome）、omitted optional
roles 与 expected set id；epoch/job/policy/expected previous 均从 immutable transaction
联表取得，included role 不再重复 settlement 的 transaction id。v1 finalization 不兼容读取，
删除 derived 后可重建。reopen 遇到 intent 时不再运行任何 role：expected set 缺失就续
publish，已存在则 exact
验证 latest；latest 为该 set 或其同 exact-key 后代时 short-circuit，missing pointer
通过 unique-tip rebuild 恢复且不回退 descendant，divergent pointer fail-fast。即使
latest 已继续推进也不会误重开已完成 transaction。ArtifactSet v3
只能从 exact durable transaction/finalization 发布，并在 CAS 前复核 current raw lineage
authority。raw orchestration mutation 是 assembly-internal；外部 composition 通过
engine-bound `FinalizeAndPublishAsync` 按 Prepare → durable finalization → Publish 顺序完成闭包。

`SessionJournal.Cli` composition root 提供 exact-epoch single-maintainer tuning、
multi-role orchestration run/resume、ArtifactSet publish/list/validate/rebuild，以及
planner configure/plan/list 和 `run-online-turn` 命令；本程序集仍不反向依赖 CLI。

所有 branch-local CLI 命令必须使用 `--branch <name>`；`validate-derived-memory` 不带 branch
时验证全部 active refs，带 branch 时只验证该 exact ref。list 命令仍是 global inventory。

Artifact 文件 strict read/write 上限为 8 MiB，ArtifactSet 与 orchestration transaction
为 1 MiB，finalization 为 256 KiB，latest pointer 与 role settlement 为 64 KiB；
planner config、epoch、pointer
分别为 64 KiB、128 KiB、32 KiB。strict read
上限都在 JSON deserialize 前按 file byte length 检查；writer
按 UTF-8 serialized byte count 使用同一 artifact 上限，并在创建 derived 目录或 artifact
前 fail fast。8 MiB 是 derived-rebuildable v2 的直接 cutover，不为超限旧实验
artifact 增加 compatibility 分支；删除并重跑 maintainer 即可重建。

普通 `TryReadArtifactAsync` 对单个 malformed candidate 返回 unusable；repository strict
validation 要求所有 artifact 文件满足 filename/schema/identity/8 MiB 上限。set JSON 持久化 canonical role
requirements，它们属于 set identity/hash；caller policy 的 role snapshot 必须 exact
match。这是尚未发布阶段对 v1 的直接 breaking 修正，不读取缺少 role snapshot 的旧实验 set。

artifact id 是完整 canonical identity hash，不使用 collision suffix；exact retry
复用 durable existing，同路径若不是同一 strict identity 则视为 corruption/hash
collision 并 fail-fast。point reads 在 `File.Exists` 前检查完整路径链，dangling 或
external symlink 不能伪装成 missing。

global/exact validation 通过 `SessionJournalEngine.OpenReadOnly` 检查 raw authority，malformed
active tail 会 fail-fast 且不 recovery/truncate。validation 会把 finalization 与 immutable
transaction、durable settlements 和 artifacts 联表，重算 role closure、anchor-bound
candidate 与 expected set identity；随后把每个 artifact 与 durable epoch/config 和 exact input set
dependency closure 交叉验证，并按 unique raw end 缓存复核 `AnchorSetups`。未被任何
ArtifactSet 选择、但仍有完整 epoch closure 的 alternative candidate 是合法 orphan；
缺 epoch 或 dependency snapshot 漂移的 detached artifact 非法。单 role runner 不执行
这项 repository-wide audit，因此无关 malformed candidate 不阻断独立 prompt tuning。
