# 已有 SessionJournal 的 RecapGrid 显式升级

状态：**Current operator runbook**  
范围：将一个已验证的、raw-only 或已完成 first-turn structural bootstrap 但尚未完整启用 RecapGrid 的 SessionJournal，显式升级为一个有已构建 active recipe 的 session。本文不授权修改任意 live state；所有命令中的占位符必须在停服后的实际盘点中重新取得。

本流程的正式命令语义以 [SessionJournal.Cli operator 指南](../../prototypes/SessionJournal.Cli/README.md) 为准。完整测试/浏览器验收的共通要求见 [Galatea E2E 指南](e2e-testing.md)。

## 1. 两种流程不可混淆

| 情形 | Galatea 正常 bootstrap 做什么 | 是否有 provider effect | 后续动作 |
|:--|:--|:--|:--|
| 新账号、create-if-missing，且 final session path 不存在 | 在 private staging 中创建 raw 三个 setup event、Cadence、empty Timeline、Control、Store、按该 user names 展开的 V6 asset、empty-Timeline full recipe 与 active recipe；全部验证并关闭 handle 后原子发布 | 没有 | 它已具备完整 RecapGrid 结构；没有历史行时首轮 context 仍是 raw-only，后续由正式 Online pass 按需维护 |
| 已有 raw-only 或受支持的 partial session | 不做任何 repair、adopt、Store 创建或 provider 调用 | 没有 | 只有 operator 可以按本流程创建 Store、登记 asset/recipe、构建和提升 |

登录本身不创建 session；首次需要 session 的 authenticated GetSessionAsync() 才可触发第一行的 create-if-missing bootstrap。它绝不修补已有 path。不要把“可 raw-only 聊天”误称为“RecapGrid 已启用”。

## 2. Stop 条件、备份与只读盘点

只对以下受支持的起点使用本文：selected branch 的 validate 为 Idle；Cadence、Timeline、Control 均可 strict verify；Control 的 family、definition、recipe 为空且没有 active recipe；Grid Store 为 absent。已有 Store、非空 Control、active recipe、Prepared、Started、未知 dispatch、schema failure、Busy，或任何结果不明，均是 **stop**，不是重跑 init 的理由。

1. 停止 Galatea 和一切可能持有该 session 的 CLI/测试进程；检查实际配置路径、实际 session path 和打开文件。lock 文件存在本身不等于 owner 已停止。
2. 对本次会触及的完整状态范围做 create-new、可打开、带 hash 的备份；至少覆盖 root config、connections、profile/route/admission artifacts、目标 SessionJournal 及其 derived/control sidecars。不要把范围较小的机器脚本说成完整备份。
3. 在停服状态记录 validate 的 head/event count/phase，并运行下列只读检查。输出保存在备份之外、受限的 operator 目录；不把 session content、credential 或 opaque token 复制进 tracked 文档。

~~~bash
cli='dotnet run --no-build --project prototypes/SessionJournal.Cli/SessionJournal.Cli.csproj --'
repo='<session-repository>'
branch='main'

$cli validate --input "$repo" --branch "$branch"
$cli recap-grid cadence inspect --input "$repo" --branch "$branch"
$cli recap-grid timeline inspect --input "$repo" --branch "$branch"
$cli recap-grid timeline verify --input "$repo" --branch "$branch"
$cli recap-grid control inspect --input "$repo" --branch "$branch"
$cli recap-grid control verify --input "$repo" --branch "$branch"
$cli recap-grid inspect --input "$repo"
$cli recap-grid verify --input "$repo"
$cli recap-grid timeline history-load inspect --input "$repo" --branch "$branch"
~~~

从 cadence inspect 读取 selected branch 的 exact refId 与 policy。不要从旧报告、别的 branch 或当前进程内缓存猜测；每个后续 mutation 都带这个 --confirm-ref。若角色或玩家名需要变更，必须在 asset provision **之前**于停服配置中完成，并重新严格加载配置；已 active 的 recipe 不支持藉此流程改名。

## 3. 先生成有界 operator artifact

scaffold 是 provider-free、create-only。它同时生成 admission、Agent Control profile 和 route manifest；每个输出必须是原先不存在的、互不嵌套的文件。对已有 session，最小必须使用新 admission；若还把新 profile/route 写入 config.json，必须保留所有历史 profile 以支持 frozen recovery，严格校验配置，并在后续重启中让 host 重新读取。

在开始前，根据盘点的 selected path 和计划的受限 sync，明确批准：

- bootstrap_rows：recipe 要覆盖的最大 selected rows；sync 后实际数超过它即停止；
- projected_calls：该 asset 为每 row 两列，通常为 bootstrap_rows 乘以 2；
- sync_row_cap：本次允许 Timeline 新提交的最大 rows；它不是放宽 budget 的借口；
- recap_connection_id、maximum_concurrency、dispatch_timeout_ms：来自已核验的 exact route policy，而不是 main-agent default。

下面的 operator-dir 是受限、空的 operator 输出目录。scaffold report 中的两个 digest 是后续 compose 的唯一输入；不要从名字、heading 或旧文件猜 digest。

~~~bash
config_dir='<Galatea-config-directory>'
operator_dir='<empty-create-new-operator-directory>'
user_id='<user-id>'
bootstrap_rows='<approved-positive-row-bound>'
projected_calls='<approved-positive-call-bound>'
sync_row_cap='<approved-positive-sync-row-bound>'
recap_connection_id='<exact-recap-connection-id>'
maximum_concurrency='<approved-positive-concurrency>'
dispatch_timeout_ms='<approved-positive-timeout-ms>'

character_name="$(jq -er --arg user "$user_id" \
  '.users[] | select(.userId == $user) | .characterName' "$config_dir/config.json")"
player_name="$(jq -er --arg user "$user_id" \
  '.users[] | select(.userId == $user) | .playerName' "$config_dir/config.json")"

scaffold_report="$($cli recap-grid scaffold \
  --asset galatea-rolling-rewrite-zh-cn-v6 \
  --character-name "$character_name" --player-name "$player_name" \
  --profile-id '<new-profile-id>' \
  --connection-id "$recap_connection_id" \
  --permission create --permission register-family \
  --permission register-definition --permission register-recipe \
  --permission activate --permission promote \
  --logical-column-prefix world-understanding \
  --logical-column-prefix autobiography \
  --max-bootstrap-rows "$bootstrap_rows" \
  --max-projected-calls "$projected_calls" \
  --max-concurrency "$maximum_concurrency" \
  --dispatch-timeout-ms "$dispatch_timeout_ms" \
  --admission-output "$operator_dir/admission.json" \
  --profile-output "$operator_dir/profile.json" \
  --route-output "$operator_dir/routes.json")"

admission="$operator_dir/admission.json"
route_manifest="$operator_dir/routes.json"
world_definition="$(jq -er '.detail.definitions[]
  | select(.logicalColumnId == "world-understanding") | .digest' <<<"$scaffold_report")"
autobiography_definition="$(jq -er '.detail.definitions[]
  | select(.logicalColumnId == "autobiography") | .digest' <<<"$scaffold_report")"
~~~

scaffold、timeline history-load inspect、progress 和所有 inspect/verify command 都不构造 Recap provider。它们也不证明未来 build 没有调用 provider。

## 4. 离线 Store、Timeline、Control 和 recipe

从刚才的 cadence report 取 exact policy value；以下 ID 是当前 CLI 所支持的唯一 partition/estimator token。init 在这个受支持起点会重验已有 Cadence/Timeline/Control，并只创建缺失 Store；它不是通用 repair/migration 命令。任一 failed、busy、invalid、unsupported、commit-indeterminate 或 policy 不一致都立即停止并重新盘点。

~~~bash
ref_id='<fresh-ref-id-from-cadence-inspect>'
minimum_recent_history_load='<fresh-cadence-minimumRecentHistoryLoad>'
target_history_load='<fresh-cadence-targetHistoryLoad>'
max_raw_events='<fresh-cadence-maxRawEvents>'
max_rendered_bytes='<fresh-cadence-maxRenderedBytes>'

$cli recap-grid init --input "$repo" --branch "$branch" \
  --confirm-ref "$ref_id" --admission "$admission" \
  --partition-algorithm atelia.history-timeline.partition.first-replay-safe-at-target.v1 \
  --history-load-estimator atelia.history-load.o200k-base.history-unit-v1 \
  --minimum-recent-history-load "$minimum_recent_history_load" \
  --target-history-load "$target_history_load" \
  --max-raw-events "$max_raw_events" \
  --max-rendered-bytes "$max_rendered_bytes"

$cli recap-grid timeline sync --input "$repo" --branch "$branch" \
  --confirm-ref "$ref_id" --max-rows "$sync_row_cap"
$cli recap-grid timeline verify --input "$repo" --branch "$branch"
$cli recap-grid control verify --input "$repo" --branch "$branch"
$cli recap-grid verify --input "$repo"
~~~

timeline sync may do a bounded offline selected-lineage audit and can mutate Timeline. It must end in its terminal success report without row-limit; then re-inspect the selected path. If its actual row count exceeds bootstrap_rows, stop before registering a recipe and generate a newly bounded admission. Never increase a bound after seeing a failure merely to continue.

登记 asset、以 world-first 顺序生成 Full recipe、再登记 recipe：

~~~bash
$cli recap-grid control provision-asset \
  --input "$repo" --branch "$branch" --confirm-ref "$ref_id" \
  --admission "$admission" --asset galatea-rolling-rewrite-zh-cn-v6 \
  --character-name "$character_name" --player-name "$player_name"

recipe_file="$operator_dir/full-recipe.json"
compose_report="$($cli recap-grid control compose-full-recipe \
  --input "$repo" --branch "$branch" \
  --definition "$world_definition" --definition "$autobiography_definition" \
  --output "$recipe_file")"
recipe_digest="$(jq -er '.detail.recipeDigest' <<<"$compose_report")"

$cli recap-grid control put-recipe \
  --input "$repo" --branch "$branch" --confirm-ref "$ref_id" \
  --admission "$admission" --recipe "$recipe_file"
$cli recap-grid control inspect --input "$repo" --branch "$branch"
~~~

此时 recipe 必须仍是 candidate（ActiveRecipeDigest 为 null）。不要 direct activate 一个尚未以 exact proof 构建完成的 recipe。

## 5. 唯一的外部 effect：有界 build，随后 promote

先用 candidate-only progress 读取缺口。operator 必须核对 frontier 的 selected rows、ordered missing assignments 和 metrics 都不超过刚批准的 bounds；不是预期结果就停止。

~~~bash
$cli recap-grid progress --input "$repo" --branch "$branch" \
  --recipe "$recipe_digest" \
  --max-recipe-row-steps "$bootstrap_rows" \
  --max-new-calls "$projected_calls" --max-elapsed-ms 60000
~~~

以下 build 才会构造 Recap completion client、向 provider 发出至多 projected_calls 个新调用并写入 Store。它不激活 recipe。call log 可能含敏感上下文，只放在受限的 operator 目录。

~~~bash
$cli recap-grid build --input "$repo" --branch "$branch" \
  --confirm-ref "$ref_id" --recipe "$recipe_digest" \
  --max-recipe-row-steps "$bootstrap_rows" \
  --max-new-calls "$projected_calls" --max-elapsed-ms '<approved-build-deadline-ms>' \
  --routes "$route_manifest" --connections "$config_dir/connections.json" \
  --call-log-dir "$operator_dir/call-logs"
~~~

网络中断、进程终止、executor-*、settlement-required、commit-indeterminate 或缺少最终报告都不能解释为“没有远程工作”。不要为取得 green result 自动重试 build。先保留首个报告/call log，再只读运行 progress、Store inspect/verify、Control/Timeline verify；是否仅补已确认 missing assignment 是一次新的 operator 决定。

build 明确 fulfilled 后，先以同一个 candidate 做零调用 proof 复查：

~~~bash
$cli recap-grid progress --input "$repo" --branch "$branch" \
  --recipe "$recipe_digest" \
  --max-recipe-row-steps "$bootstrap_rows" \
  --max-new-calls 0 --max-elapsed-ms 60000
~~~

只有 build 明确 fulfilled 且随后零调用 progress 显示完整 fulfillment/proof，才可 promote。promotion 自身以 --max-new-calls 0 在同一进程重验 head-through proof，再执行 Control CAS：

~~~bash
$cli recap-grid control promote --input "$repo" --branch "$branch" \
  --confirm-ref "$ref_id" --admission "$admission" --recipe "$recipe_digest" \
  --max-recipe-row-steps "$bootstrap_rows" \
  --max-new-calls 0 --max-elapsed-ms 60000
~~~

## 6. 冷重开与 first-fresh evidence

停服后 strict reopen，再次运行 validate、Cadence/Timeline/Control verify、Store verify，以及 candidate progress --max-new-calls 0。记录 active recipe digest、Store identity、Timeline/Control whole head 与 backup location；不要用旧日志或上次 shell 变量代替 fresh inspection。

如果第 3 节把 profile 或 route 写入 root config，或角色名改变，重启 Galatea 以重新加载 strict configuration。以该用户登录后，先查看 /api/v1/recent-turns：recapGridReadiness 应是 current exact candidate，且 contextHeader 在可 materialize 时含两个 Recap block。然后发送一条明确标为本次验收的最小 fresh message，确认其 SSE 是 done 而非 error、current 回到 Idle、回答可见，并冷重开再次读取同一 session。

这条 fresh message 是另一项 main-agent provider effect；它不是对 build 的重试，也不应用来掩盖 build 或 promote 的不确定结果。若要撤销验收输入，只能在确认最新真实用户卡片正是该输入后走页面 Undo；不得删除或重放既有剧情历史。
