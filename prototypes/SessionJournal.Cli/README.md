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
recap-grid timeline create|sync|inspect|verify|export|backup|restore|abandon|upgrade-schema-v2 ...
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

`recap-grid build --connections` 读取 Completion V3 catalog，可直接使用 Galatea 的 `connections.json`，
不要求或虚构 `defaultConnectionId`，不猜测格式或回退 V2。`--routes` 使用 `RecapGridRouteManifest.ParseJson`：
允许换行、缩进、属性顺序；缺失、重复、未知字段与无效值仍拒绝。route 按
`(FamilyDigest, RuntimeProtocolId, SemanticModelId?)` exact 匹配，显式 `null` 也没有 fallback。
Family/Definition/Recipe、admission 等持久 canonical 输入继续使用各自严格 codec。

`run-online-turn`、`llm-smoke` 等其他 CLI connections 入口保留各自 V2 合同：根有 integer `"v": 2`、
1..256 项 `connections` 与 exact `defaultConnectionId`，可带 `selectableConnectionIds/bindings`，不接受
caller-selected output cap。它们不会因为 build 支持 V3 而自动改读 Galatea catalog；V1 也没有兼容 reader。
`build` 与 V2 入口均执行原有 catalog 配置验证，包括 `baseAddressEnv/apiKeyEnv` 解析；
缺少这些变量仍可在没有 missing work 时拒绝配置。

默认 CLI factory 支持 `openai-codex-responses`，沿 Completion 共用 subscription 装配读取：

- 必需 `ATELIA_CODEX_SUBSCRIPTION_ACCOUNT_FINGERPRINT`，使用既有已 provision 的值；
- 可选 `ATELIA_CODEX_SUBSCRIPTION_ORIGINATOR`，CLI 默认 `session-journal-cli`；
- 可选 `ATELIA_CODEX_SUBSCRIPTION_AUTH_FILE`，若设置须为绝对路径；否则使用 Codex CLI 默认 auth file。

CLI 仅在首次实际创建 Codex client 时读取 subscription 环境，认证读取沿既有 credential provider。
无 missing work 或零调用预算重开不为此读取 subscription 环境/认证文件、也不构造 client；这不等于跳过普通
catalog 的环境变量验证。非 Codex 使用普通 factory，测试或宿主的显式 factory 注入保持。Galatea 仍保留原启动时
subscription 配置验证行为，默认 originator 仍是 `galatea`。

`progress` 是不构造 provider 的纯读入口；`promote` 在同一进程用 `--max-new-calls 0` 重证
head-through proof 后执行 Promotion CAS；build 本身不 activate。`materialize` 只走 Getter strict `--nth-previous`。
`build --call-log-dir <dir>` 显式启用现有 Completion call log；目录只在实际构造 recap client 时 materialize。
无 missing work 或 route 未命中时，不会仅因该选项创建日志目录；日志写入继续 best-effort，不改变 provider outcome。

### 构建与即时诊断

下面复用已配置的 route 和 V3 catalog，不新增 profile 或默认连接。先关闭该 repository 的其他 owner，
使用已核验的 branch/ref；`<配置目录>` 可取 Galatea `config.json` 所在目录，route 路径以其中 `routeManifestPath` 为准。
`build` 不接收 profile：当前构建读取已经注册的规则，已有 profile 仍用于各自的 bootstrap/历史工具恢复入口。

```bash
dotnet run --no-build --project prototypes/SessionJournal.Cli -- recap-grid progress \
  --input '<session-repository>' --branch '<branch>' --live \
  --max-recipe-row-steps 100 --max-new-calls 4 --max-elapsed-ms 600000

dotnet run --no-build --project prototypes/SessionJournal.Cli -- recap-grid build \
  --input '<session-repository>' --branch '<branch>' --confirm-ref '<refId>' --live \
  --routes '<配置目录>/recap-grid-routes.json' --connections '<配置目录>/connections.json' \
  --max-recipe-row-steps 100 --max-new-calls 4 --max-elapsed-ms 600000 \
  > build-result.json 2> build-progress.log
```

示例的 4 次调用和 10 分钟是显式预算，不承诺足以完成任意历史。已有准备好的 CLI build 才使用 `--no-build`；
直接运行 CLI DLL 也可避免构建日志混入 stdout。不重定向 stderr 时，可直接观察即时进度。

stderr 进度统一以 `[recap-build]` 开头，按工作身份关联并发调用：

| 事件 | 含义 |
|---|---|
| `request-start` | 进入本地 Runtime invoker 调用边界；不是服务器已接收请求的证明 |
| `waiting` | 每 30 秒提示尚未结束的工作与等待耗时，不额外请求模型或重复扫描 Store |
| `request-end` | Runtime 调用已结束/结算；尚不表示 cell 或 row 已提交 |
| `row-committed` | Manager 取得当前行的已提交结果 |
| `row-existing` | Manager 读取并沿用已有行结果 |
| `summary` | 本次构建汇总；仍以 stdout 的最终业务结果解释完成或失败 |

进度是 best-effort 操作信息；观察者输出异常不改写模型结果、Store 写入或取消/结果不确定语义。
`NewCalls == 0` 或空 telemetry 本身不能证明从未发生外部调用，executor 异常时计数可能尚未汇总。

进入 build 的业务结果输出路径后，stdout 只输出一份最终 JSON；stderr 不作为该 JSON 的一部分。
派生结果的 `Code/Detail`、`Failures`、预算种类、`Proof/Receipt`、实际 stale head 与 settlement 信息保留。
客户端构造与 route 加载失败保留受限长度的具体异常消息及底层原因，不只打印异常类型。
若完整 telemetry evidence 会使报告超过字节上限，优先省略 evidence，保留原业务 status/result，并输出
`evidenceOmitted/evidenceOmittedEventCount/evidenceDroppedEventCount`。参数或配置解析在进入业务结果前失败时，
仍按 CLI 的错误规则在 stderr 报错，不伪造一个模型调用结果。

Hosting的provider-free exact route inspection只报告configured connection/model/limits，
不会构造 provider client；Runtime 的 start/settled telemetry 记录所用连接与本地调用状态，
不能仅凭 start、event 数量或空 evidence 判断网络已发送/未发送。这些字段是bounded operational evidence，不进入durable
Family、Definition、Recipe 或 CellSlot。Cell/Row 使用 Store 分配的普通结果 ID。
Runtime 日志直接携带 Slot、StoreIdentity 与必要前驱 ID，不再记录 EvaluationKey/PriorProjection digest。

Grid Store 当前为 SQLite schema v4，物理槽位仍是 `derived/recap-grid/v1/grid.sqlite`。
同 `CellSlot(recipe, history row, column)` 保留首个结果；不同 recipe 不自动共享同正文缓存，
Overlay 通过原 CellId/Slot 显式复用。SQL 列与成员关系是唯一持久数据，导出 JSON 只是临时投影：
输出使用 `jsonBase64/fulfilledRowResultId`，selection 使用 `rowResultId`；cursor wire v2 拒绝旧 v1；fulfilled through 使用 HistoryRowId，不能把旧 descriptor digest 当同长 RowId。
Getter provenance 为 `priorSourceAligned` 与行/cell/member/实际正文 UTF-8 bytes 计数，合法 Overlay 的
来源不同不拒绝正文。具体模型见 [Store v4 说明](../../docs/SessionJournal/current/contracts/recap-grid-store-sqlite-v4.md)。

普通打开旧 Store schema 返回 Unsupported，不自动迁移、Reset 或调用模型。全部重构完成后才统一清旧 Recap
并重建：先在旧库仍可读时正常收敛相关 pending promotion 与 Recipes 非空 registration，再停服备份、
在隔离副本完成所需 Timeline 升级，最后用已有显式离线 Reset 初始化新库。
Timeline/Cadence、Control 与 Journal/Prepared 保留；本代码切片不执行真实数据处置。

`run-online-turn` 是唯一正式 online CLI。Prepared 按 frozen identity exact bind；
启动时strict config/connections已经冻结；Started/Refuse早于本次current connection
selection/client、route与derived owner。Fresh/NewRequest不绑定current Agent Control
profile，也不向新的completion注入`recap_grid_control`；`--admission`只在恢复历史上
已经frozen的Prepared/ToolContinuation tool runtime时提供exact profile。报告使用
`atelia.session-journal.recap-grid-cli.v1`：syntax/confirmation 返回 1，typed
operational failure 返回 2，success/idempotent 返回 0；Busy、Stale、Unsupported、
Indeterminate 均不自动 retry。

Control 新写入文件为 schema v4；旧 v2/v3 按各自原格式验证后投影到同一 graph，bootstrap 只保留 RowId。
纯读、receipt 成功重放及 export/backup 保留原 Head/bytes，下一真实 mutation 才写 v4。receipt 的
operationKey、command/runtime/sequence 与首次生效坐标仍保留。Recipes 非空 registration 的新命令摘要改变，
旧 receipt 按新命令会 Conflict；family/definition-only 与 promotion 的命令不变，空 bundle 仍拒绝。
AgentControl 输出继续 schemaVersion 2/operationKey，Journal 旧工具结果不重渲染。最终 pending 收敛与回退边界见
[Timeline 单一行身份计划](../../docs/Galatea/timeline-row-identity-simplification-plan.md)。

`recap-grid legacy-root` 只处理固定七个旧 slot。`inspect` 产生 bounded opaque
manifest，并报告 canonical repository、selected branch、RefId 与 raw head；`archive`
和 `delete` 必须显式提供 `--branch --confirm-ref --confirm-raw-head`，在同一个
mutable SessionJournal owner 的 Idle 独占窗口内完成。archive 是 repository 外的
create-only V2 manifest，提交 branch/ref/raw authority；该 V2 operator 是 Linux-only，
在任何archive/delete写入前要求no-follow/fsync capability；`delete` 还要求 fresh source
witness 与已验证 archive witness。Busy、non-Idle、raw drift、v9、symlink/device 与未知
sibling均 fail closed；未知 sibling 一律不触碰。


## Timeline schema 2 离线升级

该操作保留已有 Timeline 分区，供停止的完整 repository 隔离副本使用；本轮代码实施不等于已操作真实数据。
不依赖 branch name 或 active locator，也不自动枚举其他 Timeline：每次明确指定一个 physical RefId/TimelineId。

```text
recap-grid timeline upgrade-schema-v2 --input <stopped-repository-copy> --ref <physicalRefId> --timeline <physicalTimelineId>
```

命令校验 input 路径和严格 hex ID，调用
`HistoryTimelineMaintenance.UpgradeSchemaV2(repositoryPath, refId, timelineId)`。它取得 Ref 独占锁，从 schema 2
分页转换所有行（包括非当前路径行）到 schema 3；descriptor 外层 wire 为 v2，原 RowId/body/domain v1 不变。
head/generation、policies、selected path/Merkle、guard 与 locator 原值保留；目标 verify 后发布并冷验。
健康 schema 3 再执行仅验证，不写文件或推进 head。Timeline 目录仍是 `derived/history-timeline/v2`。

报告 status 为 `upgraded`、`already-current`、`absent`、`busy`、`unsupported-schema`、`limit`、`invalid` 或
`publish-indeterminate`。不确定发布返回 exit 2；先重新核验目标状态，不自动重试或推断源库一定未替换。
该命令不是 Restore：旧备份先用匹配旧代码恢复到完整隔离副本，再调用同一升级；不改旧 backup manifest。
多个 Timeline 逐库发布不构成跨文件原子事务，需用的所有 scope 都升级并验证后才进入最终真实切换。

普通 Timeline reader 打开旧 schema 只返回 unsupported，不自动升级、重新分段或调用模型。Cadence/Recipe/
Journal 格式保持，Control 沿 codec 读取旧文件；Store 不转换，所有计划重构完成后才统一 Reset 与 LLM 重建。
