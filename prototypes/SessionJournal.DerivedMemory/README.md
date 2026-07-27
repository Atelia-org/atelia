# SessionJournal.DerivedMemory

可替换的 SessionJournal derived-memory 子系统。它单向引用
`Atelia.SessionJournal` 的 neutral candidate contracts，负责：

- `derived/recaps/v1/` artifact persistence 与 rebuildable latest index；
- `derived/memory/v1/` coherent ArtifactSet、exact-previous CAS 和 latest pointer；
- 把已发布的 exact set 投影为 `ICoherentContextCandidateSource`。

DM-3B provider 只实现 `Latest`。`RawSuffixTokenBudget` 会验证形状，但在这一版只是
non-binding hint：provider 不搜索更早 set，也不保证 raw suffix 落入该预算。budgeted/NthPrevious
selection 属于后续版本。

边界约束：

- raw SessionJournal 不引用 artifact/set id；
- 本项目不打开 `SessionJournalEngine` 或 `EventJournal` 来证明 raw lineage；
- composition root 在发布前通过 SessionJournal 的 strict anchor helper 取得
  setup address/schema/payload hash；
- provider 返回的 raw-facing assertions 仍由 SessionJournal authoritative validator 复核；
- Prepared 已保存进入 provider request 的 exact snapshots，故 Prepared 后删除整个
  `derived/` 仍可恢复。

当前不包含 shared epoch planner 或 usage index；这些属于后续 DM-5+ 分片。DM-3C
已经由 `SessionJournal.Cli` composition root 提供 publish/list/validate/rebuild 命令，
本程序集仍不反向依赖 CLI。

Artifact 文件 strict read/write 上限为 8 MiB，ArtifactSet 为 1 MiB，latest pointer
为 64 KiB。strict read 上限都在 JSON deserialize 前按 file byte length 检查；writer
按 UTF-8 serialized byte count 使用同一 artifact 上限，并在创建 derived 目录、artifact
或 index 前 fail fast。8 MiB 是 derived-rebuildable v1 的直接 cutover，不为超限旧实验
artifact 增加 compatibility 分支；删除并重跑 maintainer 即可重建。

普通 `TryReadArtifactAsync` / latest-index rebuild 继续维持既有 tolerant 语义：未知字段、
重复副本等旧 sidecar 问题可被读取或跳过并重建。repository strict validation 才要求所有
artifact 文件满足 filename/schema/identity/8 MiB 上限。set JSON 持久化 canonical role
requirements，它们属于 set identity/hash；caller policy 的 role snapshot 必须 exact
match。这是尚未发布阶段对 v1 的直接 breaking 修正，不读取缺少 role snapshot 的旧实验 set。
