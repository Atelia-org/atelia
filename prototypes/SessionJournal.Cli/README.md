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
recap-grid control create|inspect|verify|export|put-family|put-definition|put-recipe|compose-full-recipe|provision-built-in|activate|promote|backup|restore|reinitialize ...
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

所有 branch mutation 都要求与 selected SessionJournal branch 相同的
`--confirm-ref`。`init` 显式按 Timeline、Cadence、Control、Grid 四域创建，且
`--minimum-recent-history-load`是必需输入；其他命令不自动
创建。Family、Definition、Recipe 输入必须是 formal canonical bytes；
`provision-built-in` 只接受 code-owned exact asset ID。Control admission 是独立 strict
canonical 文件，不能从 payload 自授权。

`recap-grid scaffold` 是 provider-free、create-only 的operator bootstrap：对一个
code-owned built-in asset，把operator显式给出的permissions、logical-column prefixes、
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

`run-online-turn` 是唯一正式 online CLI。Prepared 按 frozen identity exact bind；
启动时strict config/connections已经冻结；Started/Refuse早于本次current connection
selection/client、route与derived owner。报告使用
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
