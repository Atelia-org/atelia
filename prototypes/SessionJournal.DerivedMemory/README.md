# SessionJournal.DerivedMemory

可替换的 SessionJournal derived-memory 子系统。它单向引用
`Atelia.SessionJournal` 的 neutral candidate contracts，负责：

- `derived/recaps/v1/` artifact persistence 与 rebuildable latest index；
- `derived/memory/v1/` coherent ArtifactSet、exact-previous CAS 和 latest pointer；
- shared `DerivedArtifactEpochPlanner` 的 immutable config lineage、epoch ledger 与
  rebuildable current/latest indexes；
- 把已发布的 exact set 投影为 `ICoherentContextCandidateSource`。

DM-3B provider 只实现 `Latest`。`RawSuffixTokenBudget` 会验证形状，但在这一版只是
non-binding hint：provider 不搜索更早 set，也不保证 raw suffix 落入该预算。budgeted/NthPrevious
selection 属于后续版本。

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

`SessionJournal.Cli` composition root 提供 ArtifactSet publish/list/validate/rebuild，以及
planner configure/plan/list 命令；本程序集仍不反向依赖 CLI。usage index 与 online
lifecycle 属于后续 DM-7/DM-8。

Artifact 文件 strict read/write 上限为 8 MiB，ArtifactSet 为 1 MiB，latest pointer
为 64 KiB；planner config、epoch、pointer 分别为 64 KiB、128 KiB、32 KiB。strict read
上限都在 JSON deserialize 前按 file byte length 检查；writer
按 UTF-8 serialized byte count 使用同一 artifact 上限，并在创建 derived 目录、artifact
或 index 前 fail fast。8 MiB 是 derived-rebuildable v1 的直接 cutover，不为超限旧实验
artifact 增加 compatibility 分支；删除并重跑 maintainer 即可重建。

普通 `TryReadArtifactAsync` / latest-index rebuild 继续维持既有 tolerant 语义：未知字段、
重复副本等旧 sidecar 问题可被读取或跳过并重建。repository strict validation 才要求所有
artifact 文件满足 filename/schema/identity/8 MiB 上限。set JSON 持久化 canonical role
requirements，它们属于 set identity/hash；caller policy 的 role snapshot 必须 exact
match。这是尚未发布阶段对 v1 的直接 breaking 修正，不读取缺少 role snapshot 的旧实验 set。
