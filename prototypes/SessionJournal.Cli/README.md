# SessionJournal.Cli

## 离线 branch 回退

`rewind-branch` 是低层 raw lineage repair，不是 Galatea 的 completed-turn Undo。
必须先停止所有 repository owner（包括 headless Galatea），备份并确认没有打开的文件；
EventJournal 的 CAS 是 single-driver 的 instance-local 保证，不能在运行中的服务旁修改 ref。

```bash
# 默认纯只读预览；steps 默认 1，计数单位是 Parent 事件，不是 turn 或 reflog。
dotnet run --project prototypes/SessionJournal.Cli -- rewind-branch \
  --input <repo-dir> --branch main --steps 5

# 逐字使用预览的 refId、beforeHead、targetHead，显式接受外部副作用无法撤销。
dotnet run --project prototypes/SessionJournal.Cli -- rewind-branch \
  --input <repo-dir> --branch main --steps 5 --apply \
  --confirm-ref <refId> --expected-head <beforeHead> --confirm-target <targetHead> \
  --accept-external-effects

dotnet run --project prototypes/SessionJournal.Cli -- validate --input <repo-dir> --branch main
```

JSON 报告只含地址、numeric event kind 和操作元数据，不输出会话载荷。默认只读且 no-create；
apply 禁止 event/ref-op/ref-object 的 open-time tail repair，核对 exact Ref/head/target，
checked-read 所跨过的 Parent suffix 与目标后，仅执行一次 `MoveRef`。不允许跨过 root 到 null。
旧 raw events、其他 branch 和所有 sidecar 不变，reflog 追加移动记录，保留原 head 供恢复。

**该命令不保证目标是合法的 SessionJournal execution boundary**，不自动选择 Idle、不解码
Prepared、不绑定 provider、不重试 uncertain completion。应先在副本上 `validate`，再执行真实回退；
apply 后重新 `validate` 并 reopen owner，derived owner 仍需按新 selected lineage reconcile。
退一条 Started 往往仍停在 Started/Prepared，需要按预览明确选择本次未完成 turn 之前的边界。
回退会使后缀（包括 Observation）离开 selected lineage，但不能撤销 provider/tool、delegation、
CharacterMemory/receipt 等已产生的外部副作用；不要据此推断旧调用从未发生或自动重发消息。

退出码：0 为 preview/applied，1 为参数错误，2 为操作不可用或提交结果不确定。
遇到 `rewind-indeterminate` 或 stdout 丢失，先重新读取 head/reflog，不能自动重试。
这里没有提供任意 retarget/恢复命令；如需恢复旧 head，可在停服后使用已备份的数据或
EventJournal `MoveRef`，并重新验证。不要手工覆盖 `.rbf` 文件。

## RecapGrid operator surface

正式 RecapGrid operator surface：

```text
recap-grid inspect|verify|export|reset ...
recap-grid scaffold ...
recap-grid init ...
recap-grid timeline create|sync|inspect|verify|export|backup|restore|abandon ...
recap-grid timeline history-load inspect ...
recap-grid cadence inspect ...
recap-grid cadence set-reserve --confirm-ref <ref> --expected-generation <generation> --expected-domain-digest <sha256> --minimum-recent-history-load <R> ...
recap-grid control create|inspect|verify|export|put-family|put-definition|put-recipe|compose-full-recipe|provision-asset|activate|promote|backup|restore|reinitialize ...
recap-grid build|progress|materialize ...
recap-grid legacy-root inspect|archive|delete ...
run-online-turn ...
```

`recap-grid` 是唯一 Grid operator root。Store maintenance 与 Timeline、Control、
build/readiness/materialization 共用正式 owner contracts；旧 `recap` 命令和旧 recap
product 已移除。`timeline history-load inspect` 是 provider-free 的只读校准工具。

每个Ref的cadence由repo-owned canonical sidecar持有。`cadence inspect` pure-read、
no-create且不构造provider；`cadence set-reserve`只允许CAS更新
`MinimumRecentHistoryLoad`，要求exact Ref、generation与domain digest，并原样保留
partition algorithm、estimator、`TargetHistoryLoad`和segment caps。它不能修改B来绕过
Timeline policy matching；Busy、Stale与CommitIndeterminate均返回typed report且不自动retry。
Exact command-local ledger与恢复矩阵见
[Cadence set-reserve approved receipt contract](../../docs/SessionJournal/current/contracts/cadence-set-reserve-receipt.md)：stdout丢失或
commit-indeterminate后必须fresh `cadence inspect`完整head/policy，不能从receipt absence推断未提交，也不能自动retry。
该appendix形成于immutable surface-set-4 tag之后；surface set 5 exact narrow scope现由immutable v5 tag object
`e1100017`锚定到reviewed ledger `89d61ba2`。Post-tag docs不移动tag、不续期证据或扩大scope；对`845539c5`与actual v5 tag的
independent review已PASS。

所有 branch mutation 都要求与 selected SessionJournal branch 相同的
`--confirm-ref`。`init` 显式按 Timeline、Cadence、Control、Grid 四域创建，且
`--minimum-recent-history-load`是必需输入；其他命令不自动
创建。Family、Definition、Recipe 输入必须是 formal canonical bytes；
`provision-asset` 只接受CLI compile-time closed operator catalog中的code-owned exact
asset ID；Galatea operator asset不会进入AgentControl built-in catalog或其implementation
fingerprint。Control admission 是独立 strict
canonical 文件，不能从 payload 自授权。

当前Galatea selector已hard-cut为`galatea-rolling-rewrite-zh-cn-v6`。它在`scaffold`与
`control provision-asset`都要求exactly-one `--character-name <name>`与`--player-name <name>`；两个参数先经
共享`GalateaCharacterName` / `GalateaPlayerName`验证，再在Family/Definition bundle构造前展开member
prompt，角色名另外进入topic与semantic heading。其他operator asset携带任一选项会被拒绝；
unknown、missing或invalid输入均在
打开repo、写output或构造provider之前fail closed。Family、logical columns、carrier与
`BlockKey`不随名字改变；使用`Galatea` + `刘世超`时四个canonical bundle digests与旧V5完全相同。
scaffold与provision必须使用同一对名字。同一Control instance中再次用不同角色名或玩家名provision会复用
现有V6 operation key并得到`operation-conflict`；现阶段不承诺existing-session rename，也不把
character/player name或command digest加入receipt/runtime identity。

`recap-grid scaffold` 是 provider-free、create-only 的operator bootstrap：对一个
code-owned operator asset，把operator显式给出的permissions、logical-column prefixes、
Control budgets和route execution limits组合成三份strict canonical文件——Control admission、
AgentControl profile、Hosting route manifest。family/capability/carrier只来自code-owned
registration bundle；三个output必须pairwise distinct且全部不存在，任一existing时零写。
命令会在每次写前与写后调用正式`DecodeCanonical`做exact self-check，并报告bounded
length/SHA-256/runtime identity。built-in capability的semantic model为null时必须省略
`--semantic-model-id`，wire中仍是explicit null；不存在wildcard/default fallback。生成后可把
admission交给`init`，profile/route路径交给Galatea strict config。

`build` 与 Fresh/NewRequest online 只在 lazy dispatch boundary 读取 strict route manifest
和 Completion connections；route 按
`(FamilyDigest, RuntimeProtocolId, SemanticModelId?)` exact 匹配，显式 `null` 也不
fallback。`progress` 不构造 provider；`promote` 在同一进程用
`--max-new-calls 0` 重证 head-through proof 后才执行 Promotion CAS；build 本身永不
activate。`materialize` 只走 Getter strict `--nth-previous`。
`build --call-log-dir <dir>`是显式opt-in；日志目录只在lazy dispatch实际构造recap
client时materialize，每个实际provider call通过现有Completion call-log V10 seam写入一份日志。
未传时行为不变；provider-free、无missing work或exact route未命中的路径不会仅因该选项创建日志目录。日志写入失败仍沿用
`LoggingCompletionClient`的best-effort合同，不改变provider outcome。

所有 CLI connections 入口共用 Completion-owned strict numeric V2 decoder：根必须显式
包含 integer `"v": 2`、1..256 项与 exact `defaultConnectionId`，并且有意不接受caller-selected
output cap；还可携带通用
optional `selectableConnectionIds` 与 `bindings`。前者若存在，必须为 1..256 个
exact existing connection IDs，exact unique 且包含 default；后者若存在，必须为最多
256 项的 bounded key 到 exact existing connection ID 或 `null` 的映射。CLI 只消费 catalog/
default/explicit command route，不把这些 host metadata 解释为 CLI allowlist。No-v 文件不会
fallback；operator 应停服后把每项 endpoint source 收敛为`baseAddress` / `baseAddressEnv`
exactly-one，再与V2 manifest及新binary一起发布。V1没有compatibility reader。

Hosting的provider-free exact route inspection只报告configured connection/model/limits，
不会构造provider client；只有settled runtime telemetry中的`ConnectionId`、model与provider
才是actual dispatch evidence。这些字段是bounded operational evidence，不进入durable
Family、Definition、Recipe 或 CellSlot。Cell/Row 使用 Store 分配的普通结果 ID。
Runtime 日志直接携带 Slot、StoreIdentity 与必要前驱 ID，不再记录 EvaluationKey/PriorProjection digest。

Grid Store 当前为 SQLite schema v3，物理槽位仍是 `derived/recap-grid/v1/grid.sqlite`。
同 `CellSlot(recipe, history row, column)` 保留首个结果；不同 recipe 不自动共享同正文缓存，
Overlay 通过原 CellId/Slot 显式复用。SQL 列与成员关系是唯一持久数据，导出 JSON 只是临时投影：
输出使用 `jsonBase64/fulfilledRowResultId`，selection 使用 `rowResultId`；旧 digest cursor 不兼容。
Getter provenance 为 `priorSourceAligned` 与行/cell/member/实际正文 UTF-8 bytes 计数，合法 Overlay 的
来源不同不拒绝正文。具体模型见 [Store v3 说明](../../docs/SessionJournal/current/contracts/recap-grid-store-sqlite-v3.md)。

普通打开旧 Store schema 返回 Unsupported，不自动迁移、Reset 或调用模型。全部重构完成后才统一清旧 Recap
并重建：先在旧库仍可读时正常收敛相关 pending promotion，再停服备份并用已有显式离线 Reset 初始化新库。
Timeline/Cadence、Control 与 Journal/Prepared 保留；本代码切片不执行真实数据处置。

`run-online-turn` 是唯一正式 online CLI。Prepared 按 frozen identity exact bind；
启动时strict config/connections已经冻结；Started/Refuse早于本次current connection
selection/client、route与derived owner。Fresh/NewRequest不绑定current Agent Control
profile，也不向新的completion注入`recap_grid_control`；`--admission`只在恢复历史上
已经frozen的Prepared/ToolContinuation tool runtime时提供exact profile。报告使用
`atelia.session-journal.recap-grid-cli.v1`：syntax/confirmation 返回 1，typed
operational failure 返回 2，success/idempotent 返回 0；Busy、Stale、Unsupported、
Indeterminate 均不自动 retry。

Control 新写入文件为 schema v3；旧 v2 按原格式校验后可直接读取和重放，纯读、
export/backup 和 receipt 重放不改 Head 或文件字节。正常持久 mutation 才写 v3，
无需先执行完旧 pending 或批量转换。AgentControl 输出 schemaVersion 2，用既有
`operationKey` 代替派生 `resultIdentity`；同 operation 仍须匹配 command/runtime/sequence，
receipt 与首次生效坐标在 restore/reinitialize 时保留。已写入 Journal 的旧 tool result
原文不重渲染。v3 写入后的二进制回退须配合匹配的数据快照，详见
[Control 回执简化](../../docs/Galatea/control-receipt-simplification-plan.md)。

`recap-grid legacy-root` 只处理固定七个旧 slot。`inspect` 产生 bounded opaque
manifest，并报告 canonical repository、selected branch、RefId 与 raw head；`archive`
和 `delete` 必须显式提供 `--branch --confirm-ref --confirm-raw-head`，在同一个
mutable SessionJournal owner 的 Idle 独占窗口内完成。archive 是 repository 外的
create-only V2 manifest，提交 branch/ref/raw authority；该 V2 operator 是 Linux-only，
在任何archive/delete写入前要求no-follow/fsync capability；`delete` 还要求 fresh source
witness 与已验证 archive witness。Busy、non-Idle、raw drift、v9、symlink/device 与未知
sibling均 fail closed；未知 sibling 一律不触碰。
