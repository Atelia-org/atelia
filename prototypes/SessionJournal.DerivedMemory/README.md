# SessionJournal.DerivedMemory

可替换的 SessionJournal derived-memory 子系统。它单向引用
`Atelia.SessionJournal` 的 neutral candidate contracts，负责：

- `derived/memory/v1/artifacts/` epoch-bound append-only candidate persistence；
- `derived/memory/v1/` coherent ArtifactSet、exact-previous CAS 和 latest pointer；
- shared `DerivedArtifactEpochPlanner` 的 immutable config lineage、epoch ledger 与
  rebuildable current/latest indexes；
- deterministic multi-role orchestration transaction、immutable role settlement、
  durable finalization intent、missing-role resume 与 required-role closure；
- 把已发布的 exact set 投影为 bounded two-phase `ICoherentContextCandidateSource`；
- 通过 `DerivedMemoryOnlineLifecycleCoordinator` 在 safe raw boundary 组合 shared epoch
  planning、pending-first maintenance、ArtifactSet publication 与显式 backpressure。

DM-6 candidate store 不再维护 role-local latest pointer。`DerivedMemoryArtifactStore`
只接受 v2 exact-epoch identity，并允许同一 role/epoch 的 prompt/model tuning 结果
append-only 共存；只有 ArtifactSet publication 才决定 candidate 是否可选择。旧
`derived/recaps/v1/`、latest-by-profile 与 linear recap CAS 已直接退役。

DM-8 provider 支持 `Latest`、`NthPrevious` 与 `Budgeted`：discovery 阶段只返回
content-free descriptors，materialization 才读取 exact member text。`Latest` 最多发现 1 个，
`NthPrevious(n)` 最多发现 `n + 1`，`Budgeted` 受 core request 的 candidate bound 限制。
ordinal 不是 cost；raw suffix 与 total canonical request budget 由 SessionJournal core 用共享
estimator 和 raw authority window 计算。

边界约束：

- raw SessionJournal 不引用 artifact/set id；
- online planner 由 composition root 传入已有 `SessionJournalEngine`；offline repository
  validation/latest-epoch pointer rebuild 在未显式传入 engine 时可短暂打开它来证明 raw
  authority，本项目仍不直接依赖 `EventJournal`；
- composition root 在发布前通过 SessionJournal 的 strict anchor helper 取得
  setup address/schema/payload hash；
- provider 返回的 raw-facing assertions 仍由 SessionJournal authoritative validator 复核；
- Prepared 已保存进入 provider request 的 exact snapshots，故 Prepared 后删除整个
  `derived/` 仍可恢复。

真实空 lineage 通过 strict `EmptyLineage` 状态进入 bounded bootstrap；missing/stale latest
pointer 会先 rebuild，不能伪装为空。bootstrap 不创建空 artifact，而由 Prepared v5 的零个
`ExactContextInputs` 固化 exact request。首个真实 ArtifactSet 发布后 bootstrap 自动失效。

DM-5 planner 在任何 maintainer/LLM 执行前，只通过 SessionJournal 暴露的
`ReadHistoryPlanningWindow()` 读取 bounded、dependency-closed suffix。config key 是
`lineageKey + coherenceGroup`，但 v1 只接受 current `main` lineage，尚不伪称支持 arbitrary
branch token；config snapshot 与 epoch 都是 deterministic、append-only
identity，mutable pointer 只作为可重建 index。genesis 明确使用 empty-memory-pack policy；
非 genesis epoch 必须绑定一个真实、self-validating 的 coherent ArtifactSet，且其
`CommonAnchor` 必须等于 previous epoch 的 `SourceEndInclusive`。planner 不运行 maintainer、
不发布 set、也不写 raw event。

raw scan/candidate computation 不持有 derived repository write lock；所有 planning 终态在
短锁内重读 current config/latest pointer 后线性化。strict repository validation 与
latest-epoch pointer rebuild 还会使用 core header-only snapshot 与 batched planning seeds
验证 epoch raw interval/current-main membership；随后按 exact historical head 增量重放每个
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
与 `derived/memory/v1/settlements/<transaction>/` 下的 durable success settlement 分层；
required role 失败时保留已成功 partial candidates/settlements，但绝不发布半套 set。
transaction/job identity 包含 policy、topology、完整 role provisioning 与
candidate/attempt；改变 job 会创建新 transaction，重跑同一 job 只补缺失 role。
required roles 闭合后先写 immutable finalization intent，冻结 exact included
settlements、omitted optional roles、expected previous 与 expected set id。reopen
遇到 intent 时不再运行任何 role：expected set 缺失就续 publish，已存在则 exact
验证 latest；latest 为该 set 或其同 exact-key 后代时 short-circuit，missing pointer
通过 unique-tip rebuild 恢复且不回退 descendant，divergent pointer fail-fast。即使
latest 已继续推进也不会误重开已完成 transaction。ArtifactSet v2
只能从 exact durable transaction/finalization 发布，并在 CAS 前复核 current raw lineage
authority。

`SessionJournal.Cli` composition root 提供 exact-epoch single-maintainer tuning、
multi-role orchestration run/resume、ArtifactSet publish/list/validate/rebuild，以及
planner configure/plan/list 和 `run-online-turn` 命令；本程序集仍不反向依赖 CLI。

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

global validation 会把每个 artifact 与 durable epoch/config 和 exact input set
dependency closure 交叉验证，并按 unique raw end 缓存复核 `AnchorSetups`。未被任何
ArtifactSet 选择、但仍有完整 epoch closure 的 alternative candidate 是合法 orphan；
缺 epoch 或 dependency snapshot 漂移的 detached artifact 非法。单 role runner 不执行
这项 repository-wide audit，因此无关 malformed candidate 不阻断独立 prompt tuning。
