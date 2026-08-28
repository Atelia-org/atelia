# SessionJournal.Cli

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
`control provision-asset`都要求exactly-one `--character-name <name>`；该参数先经共享
`GalateaCharacterName`验证，再在Family/Definition bundle构造前展开member prompt、topic与
semantic heading。其他operator asset携带该选项会被拒绝；unknown、missing或invalid输入均在
打开repo、写output或构造provider之前fail closed。Family、logical columns、carrier与
`BlockKey`不随名字改变；使用`Galatea`时四个canonical bundle digests与旧V5完全相同。
scaffold与provision必须使用同一个名字。同一Control instance中再次用不同名字provision会复用
现有V6 operation key并得到`operation-conflict`；现阶段不承诺existing-session rename，也不把
character name或command digest加入receipt/runtime identity。

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
client时materialize，每个实际provider call通过现有Completion call-log V9 seam写入一份日志。
未传时行为不变；provider-free、无missing work或exact route未命中的路径不会仅因该选项创建日志目录。日志写入失败仍沿用
`LoggingCompletionClient`的best-effort合同，不改变provider outcome。

所有 CLI connections 入口共用 Completion-owned strict numeric V1 decoder：根必须显式
包含 integer `"v": 1`、1..256 项与 exact `defaultConnectionId`；还可携带通用
optional `selectableConnectionIds` 与 `bindings`。前者若存在，必须为 1..256 个
exact existing connection IDs，exact unique 且包含 default；后者若存在，必须为最多
256 项的 bounded key 到 exact existing connection ID 或 `null` 的映射。CLI 只消费 catalog/
default/explicit command route，不把这些 host metadata 解释为 CLI allowlist。No-v 文件不会
fallback；operator 应停服后人工增加版本，并把每项 endpoint source 收敛为
`baseAddress` / `baseAddressEnv` exactly-one，再与新 binary 一起发布。含扩展字段的
V1 需要当前 binary；旧 closed-root binary 会将它们拒绝为 unknown properties。

Hosting的provider-free exact route inspection只报告configured connection/model/limits，
不会构造provider client；只有settled runtime telemetry中的`ConnectionId`、model与provider
才是actual dispatch evidence。这些字段是bounded operational evidence，不进入durable
Family、Definition、Recipe、EvaluationKey、Cell或RowView identity。

`run-online-turn` 是唯一正式 online CLI。Prepared 按 frozen identity exact bind；
启动时strict config/connections已经冻结；Started/Refuse早于本次current connection
selection/client、route与derived owner。Fresh/NewRequest不绑定current Agent Control
profile，也不向新的completion注入`recap_grid_control`；`--admission`只在恢复历史上
已经frozen的Prepared/ToolContinuation tool runtime时提供exact profile。报告使用
`atelia.session-journal.recap-grid-cli.v1`：syntax/confirmation 返回 1，typed
operational failure 返回 2，success/idempotent 返回 0；Busy、Stale、Unsupported、
Indeterminate 均不自动 retry。

`recap-grid legacy-root` 只处理固定七个旧 slot。`inspect` 产生 bounded opaque
manifest，并报告 canonical repository、selected branch、RefId 与 raw head；`archive`
和 `delete` 必须显式提供 `--branch --confirm-ref --confirm-raw-head`，在同一个
mutable SessionJournal owner 的 Idle 独占窗口内完成。archive 是 repository 外的
create-only V2 manifest，提交 branch/ref/raw authority；该 V2 operator 是 Linux-only，
在任何archive/delete写入前要求no-follow/fsync capability；`delete` 还要求 fresh source
witness 与已验证 archive witness。Busy、non-Idle、raw drift、v9、symlink/device 与未知
sibling均 fail closed；未知 sibling 一律不触碰。
