# SessionJournal.Offline

`Atelia.SessionJournal.Offline` 是 raw SessionJournal 的显式离线审计 companion。
依赖方向固定为 `Offline -> SessionJournal + EventJournal`；online core 不引用本项目，
两者之间也没有 production `InternalsVisibleTo`。

`SessionJournalOfflineValidator.ValidateAsync(path, branch)` 打开指定 active branch 的
read-only Engine，消费 core `ScanCheckedAuditEvents()` 提供的 exact-head normalized
facts。scan 负责完整 Parent-chain/header/codec 检查及所有 historical Prepared commitment
重建；Offline 以独立 forward fold 检查 setup/session/order/correlation/attempt/tool identity
与 sequence legality，并与同一 captured head 的 tail execution state 和 governing setup
做 differential。

report 不包含完整 context、完整 `SessionExecutionState`、明文 system prompt 或 addressed
history。它只输出 exact branch/ref/head、最终 phase/head-kind/sequence checkpoint、setup
address 与 runtime config、system prompt 的 UTF-8 SHA-256、event-kind/history-contribution
counts、semantic history commitment 及 scan diagnostics。因此 tool raw arguments、
operation id、correlation id 等只参与内部 legality/differential 检查，不进入 report。

`historySemanticCommitmentSha256` 使用显式版本化的
`atelia.session-journal.history-semantic-commitment.v1`。Observation、Action 和按声明顺序
闭合的 ToolResults 复用 canonical request 的 history-value 写法，只承诺 LLM 可见的语义
内容；EventAddress、provider/invocation、correlation、checkpoint、operation id 和 runtime
identity 都不参与该 hash。地址或执行元数据不同但 history 语义相同的 branch 因而得到相同
commitment；语义内容或 contribution 顺序变化则得到不同 commitment。空 branch 合法；默认
branch 是 `main`。

P5-C 的 legacy importer 已从 source message 独立计算同一 public codec，再把期望值与
target Offline report 比较；因此 import 验收不是 target 自算自证。P5-D 已删除 core 的
public full projection/replay surface 与 production full reducer。Offline validator 和
importer 继续只依赖 checked audit scan、exact-head tail recovery 与 governing setup，
不会把 full history materialization 重新引入 online core。
