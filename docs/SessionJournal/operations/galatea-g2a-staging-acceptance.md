# Galatea RecapGrid repeatable staging acceptance

> **状态**：Active procedure / WP-08 formal CLI shape
> **验证边界**：本文是runbook，不是Passed evidence。WP-08没有运行真实LLM或真实cyber repository。

## 1. Goal and trust zones

从一份exact legacy export创建新的SessionJournal staging，显式provision Timeline/Cadence/Control/Grid，再从
disposable clones运行deterministic或real-provider Galatea canary。raw events与selected `RefId` lineage是会话
authority；Timeline/Cadence/Control/Grid是独立companion authorities。

每轮使用全新的run root：

```text
gitignore/galatea-grid-acceptance/<run-id>/
  staging-repo/              # provider canary前raw只读
  acceptance-clones/
    deterministic-<id>/      # 可写raw，用完丢弃
    real-<id>/               # 可写raw，用完丢弃
  reports/                   # bounded content-free reports
  call-logs/                 # 可能含正文，限制访问
  host-config/               # strict acceptance-only config
```

source、run root、staging、clones与Galatea configured `sessionDir`必须canonical disjoint，所有existing
ancestors拒绝symlink/reparse。任何clone都不得提升为production repository。

禁止：

- 原地修改source或live Galatea repo；
- `--force`覆盖已有输出；
- 自动create/provision/activate或从payload自授权；
- route wildcard/default fallback；
- normal path读取、修复或删除old `derived/recap` roots；
- 把secret、request/response正文、recap正文或content digest写入汇总报告。

## 2. Import and raw baseline

```bash
dotnet run --project prototypes/SessionJournal.Cli -- \
  import-legacy-json --input "$source_export" --output "$staging_repo" \
  --report-json "$reports/import.json"

dotnet run --project prototypes/SessionJournal.Cli -- \
  validate --input "$staging_repo" --branch main \
  --report-json "$reports/validate-imported.json"

dotnet run --project prototypes/SessionJournal.Cli -- \
  recap-grid timeline history-load inspect \
  --input "$staging_repo" --branch main \
  --report-json "$reports/history-load.json"
```

记录source length/SHA-256、import report、selected RefId与raw head。对raw repository做sorted
`relative-path + length + SHA-256` inventory；在provider canary前的所有derived-only步骤后必须exact相同。
`ref_id`与`raw_head`分别从validation report的`.branchRefId`与`.head`读取；import report不包含RefId，
不得从旧repo或历史run复制。`history-load.json`的`.capturedHead`必须等于本轮`raw_head`。

## 3. Explicit formal provisioning

准备strict bounded inputs：

- Timeline/Cadence initial policy：partition algorithm、HistoryLoad estimator、target load、minimum recent load、
  max raw events、max rendered bytes；本runbook目标值为B=60,000、R=24,000；
- Control admission：permissions、allowed families/capabilities/carriers/prefixes与budgets；
- canonical Family/Definition values，或code-owned exact built-in asset id；
- exact route manifest与acceptance-only strict Completion connections；`semanticModelId`必须显式，含`null`；
- Galatea `recapGrid` config：deferred route path、bounded profile files、exact current profile id。

先用provider-free scaffold创建三份strict canonical配置，并从bounded JSON report读取两个ordered definition digests：

```bash
mkdir -p "$operator_dir"
galatea_connections="prototypes/Galatea/.atelia/galatea/connections.json"
recap_connection_id="opus4-6"
partition_algorithm="atelia.history-timeline.partition.first-replay-safe-at-target.v1"
estimator="atelia.history-load.o200k-base.history-unit-v1"
minimum_recent=24000
target_load=60000
# Timeline partition的单次raw range上限；不要与recent-reserve operation跨页审计预算262144混淆。
max_raw=65536
max_rendered=1048576
bootstrap_row_cap=1
projected_call_cap=2
admission="$operator_dir/recap-grid-admission.json"
profile="$operator_dir/recap-grid-agent-control-profile.json"
route_manifest="$operator_dir/recap-grid-routes.json"
strict_connections="$operator_dir/recap-grid-connections.json"
recipe_file="$operator_dir/galatea-rolling-full-recipe.json"

# 当前Galatea connections文件含legacy routing字段，且省略strict reader要求的
# baseAddress/completionSurfaceId；不能直接交给recap-grid build。只投影本轮Opus连接，
# 不复制literal apiKey。所有output必须事先不存在。
( set -o noclobber; umask 077; jq -e --arg id "$recap_connection_id" '
    [.connections[] | select(.id == $id)]
    | if length != 1 then error("exact recap connection absent or duplicate")
      else .[0] end
    | select(.kind == "anthropic")
    | {
        defaultConnectionId: .id,
        connections: [{
          id, kind, modelId,
          completionSurfaceId: "anthropic",
          baseAddress: (.baseAddress // ""),
          baseAddressEnv, apiKeyEnv,
          maxTokens, reasoningEffort, anthropicPromptCacheTtl
        } | with_entries(select(.value != null))]
      }
  ' "$galatea_connections" >"$strict_connections" )

base_address_env="$(jq -er '.connections[0].baseAddressEnv' "$strict_connections")"
api_key_env="$(jq -er '.connections[0].apiKeyEnv' "$strict_connections")"
[[ -n "${!base_address_env:-}" && -n "${!api_key_env:-}" ]] || {
  echo "selected connection environment is unavailable" >&2; exit 1;
}

scaffold_report="$(dotnet run --project prototypes/SessionJournal.Cli -- \
  recap-grid scaffold \
  --asset galatea-rolling-rewrite-zh-cn-v1 \
  --profile-id galatea-rolling-v1 \
  --connection-id "$recap_connection_id" \
  --permission create --permission register-family \
  --permission register-definition --permission register-recipe \
  --permission activate --permission promote \
  --logical-column-prefix world-understanding \
  --logical-column-prefix autobiography \
  --max-bootstrap-rows "$bootstrap_row_cap" \
  --max-projected-calls "$projected_call_cap" \
  --max-concurrency 1 --dispatch-timeout-ms 900000 \
  --max-output-tokens 32768 \
  --admission-output "$admission" --profile-output "$profile" \
  --route-output "$route_manifest")"
world_definition="$(jq -er '.detail.definitions[] | select(.logicalColumnId == "world-understanding") | .digest' <<<"$scaffold_report")"
autobiography_definition="$(jq -er '.detail.definitions[] | select(.logicalColumnId == "autobiography") | .digest' <<<"$scaffold_report")"
```

scaffold为create-only；任一输出已存在时必须先由operator核对并选择新的空输出路径，不得覆盖。
首轮canary使用`max-concurrency=1`，使world-first调用先写shared Anthropic prefix cache、autobiography调用再
读取；并发2不能验证本轮shared-prefix reuse目标。对当前固定export，已校准HistoryLoad为116,458；
在B=60,000/R=24,000下预期`bootstrap_row_cap=1`、`projected_call_cap=2`。这两个值是Control
registration admission与本轮外部call cap，不是Timeline/Grid lifetime cap；任何实测偏差都必须No-Go重算，不能放大为1000000。

先取得selected RefId，再显式创建四域：

```bash
dotnet run --project prototypes/SessionJournal.Cli -- \
  recap-grid init --input "$staging_repo" --branch main \
  --confirm-ref "$ref_id" --admission "$admission" \
  --partition-algorithm "$partition_algorithm" \
  --history-load-estimator "$estimator" \
  --minimum-recent-history-load "$minimum_recent" \
  --target-history-load "$target_load" \
  --max-raw-events "$max_raw" \
  --max-rendered-bytes "$max_rendered"
```

先完成Timeline sync并证明exact selected rows；必须在compose Full recipe之前执行，否则空Timeline会把
`BootstrapThroughRowId`固定为null，之后补出的Timeline rows不属于该recipe bootstrap：

```bash
sync_report="$(dotnet run --project prototypes/SessionJournal.Cli -- \
  recap-grid timeline sync \
  --input "$staging_repo" --branch main --confirm-ref "$ref_id" \
  --max-rows "$((bootstrap_row_cap + 1))")"
jq -e --argjson expected "$bootstrap_row_cap" '
  .status == "synchronized" and .detail.committed == $expected
' <<<"$sync_report" >/dev/null
```

若返回`row-limit`、committed数量不符或不是terminal synchronized，停止；不得提高limit后继续。
随后显式登记code-owned asset，并按world-first顺序组成、登记初始Full recipe：

```bash
dotnet run --project prototypes/SessionJournal.Cli -- \
  recap-grid control provision-asset \
  --input "$staging_repo" --branch main --confirm-ref "$ref_id" \
  --admission "$admission" \
  --asset galatea-rolling-rewrite-zh-cn-v1

compose_report="$(dotnet run --project prototypes/SessionJournal.Cli -- \
  recap-grid control compose-full-recipe \
  --input "$staging_repo" --branch main \
  --definition "$world_definition" \
  --definition "$autobiography_definition" \
  --output "$recipe_file")"
recipe_digest="$(jq -er '.detail.recipeDigest' <<<"$compose_report")"

dotnet run --project prototypes/SessionJournal.Cli -- \
  recap-grid control put-recipe \
  --input "$staging_repo" --branch main --confirm-ref "$ref_id" \
  --admission "$admission" --recipe "$recipe_file"
```

`control inspect`的`Head`及其record字段使用实际PascalCase JSON；以下fresh读取只作authority evidence，
不要使用旧文档中的`.detail.head.instanceId.value`：

```bash
control_report="$(dotnet run --project prototypes/SessionJournal.Cli -- \
  recap-grid control inspect --input "$staging_repo" --branch main)"
control_instance="$(jq -er '.detail.Head.InstanceId.Value' <<<"$control_report")"
control_timeline="$(jq -er '.detail.Head.TimelineId.Value' <<<"$control_report")"
control_generation="$(jq -er '.detail.Head.Generation' <<<"$control_report")"
control_state="$(jq -er '.detail.Head.StateDigest.Value' <<<"$control_report")"
control_active="$(jq -r '.detail.Head.ActiveRecipeDigest.Value // "none"' <<<"$control_report")"
[[ "$control_active" == none ]]
```

初始Full recipe不得direct activate；先以explicit candidate完成build与zero-call promotion。所有mutation都提交fresh
exact confirmation；Busy/Stale/Unsupported/Indeterminate不自动retry。

## 4. Provider-free gates

在打开provider factory前依次运行：

```bash
dotnet run --project prototypes/SessionJournal.Cli -- recap-grid timeline inspect --input "$staging_repo"
dotnet run --project prototypes/SessionJournal.Cli -- recap-grid timeline verify --input "$staging_repo"
dotnet run --project prototypes/SessionJournal.Cli -- recap-grid cadence inspect --input "$staging_repo" --branch main
dotnet run --project prototypes/SessionJournal.Cli -- recap-grid control inspect --input "$staging_repo"
dotnet run --project prototypes/SessionJournal.Cli -- recap-grid control verify --input "$staging_repo"
dotnet run --project prototypes/SessionJournal.Cli -- recap-grid inspect --input "$staging_repo"
dotnet run --project prototypes/SessionJournal.Cli -- recap-grid verify --input "$staging_repo"
```

这些命令必须no-provider；inspect/verify/progress必须read-only/no-create。若Timeline sync需要offline audit，CLI只能
使用本operation的一次性bounded snapshot；raw drift返回typed failure且不retry。

用`recap-grid legacy-root inspect`记录old seven-slot manifest。normal provisioning/sync/build/materialize与Grid reset
前后legacy bytes必须不变；archive/delete只能在单独operator run中提供fresh exact source/archive witnesses。

## 5. Build, restart and materialization

对explicit candidate先pure-read计算外部调用预算。`Frontier.PendingRecipeRows`是尚待推进的row数，
`OrderedMissing`是下一row需要实际调用的cells；每次build仍以operator确认的`max-new-calls`为硬上限：

```bash
progress_before="$(dotnet run --project prototypes/SessionJournal.Cli -- \
  recap-grid progress --input "$staging_repo" --branch main \
  --recipe "$recipe_digest" \
  --max-recipe-row-steps "$bootstrap_row_cap" \
  --max-new-calls "$projected_call_cap" --max-elapsed-ms 60000)"
jq -e --argjson rows "$bootstrap_row_cap" --argjson calls "$projected_call_cap" '
  .status == "frontier"
  and .detail.PendingRecipeRows == $rows
  and (.detail.OrderedMissing | length) == $calls
  and .detail.Metrics.MissingAssignments == $calls
' <<<"$progress_before" >/dev/null

build_report="$(dotnet run --project prototypes/SessionJournal.Cli -- \
  recap-grid build --input "$staging_repo" --branch main \
  --confirm-ref "$ref_id" --recipe "$recipe_digest" \
  --max-recipe-row-steps "$bootstrap_row_cap" \
  --max-new-calls "$projected_call_cap" --max-elapsed-ms 1800000 \
  --routes "$route_manifest" --connections "$strict_connections" \
  --call-log-dir "$call_logs/recap-build")"
jq -e '.status == "fulfilled"' <<<"$build_report" >/dev/null

progress_after="$(dotnet run --project prototypes/SessionJournal.Cli -- \
  recap-grid progress --input "$staging_repo" --branch main \
  --recipe "$recipe_digest" \
  --max-recipe-row-steps "$bootstrap_row_cap" \
  --max-new-calls 0 --max-elapsed-ms 60000)"
jq -e '.status == "complete" and .detail.FulfillmentPresent == true' \
  <<<"$progress_after" >/dev/null

promote_report="$(dotnet run --project prototypes/SessionJournal.Cli -- \
  recap-grid control promote --input "$staging_repo" --branch main \
  --confirm-ref "$ref_id" --admission "$admission" \
  --recipe "$recipe_digest" \
  --max-recipe-row-steps "$bootstrap_row_cap" \
  --max-new-calls 0 --max-elapsed-ms 60000)"
jq -e '.status == "applied" or .status == "already-active"' \
  <<<"$promote_report" >/dev/null

dotnet run --project prototypes/SessionJournal.Cli -- \
  recap-grid materialize --input "$staging_repo" --branch main \
  --boundary "$raw_head" --nth-previous 0 --include-content \
  >"$call_logs/materialized-recap.json"
```

至少覆盖一次bounded partial build：首operation提交部分cells/views后dispose所有handles；fresh reopen只dispatch
missing work，完成后再次reopen为zero-call。任何阶段都不得创建old rebuild spool。

candidate recipe build不自动activate。promotion必须同进程pure-read检查current head-through assignment、fulfillment与exact proof，
再用whole Control/Timeline heads执行operation-aware CAS。partial/stale/missing不得activate。
`build_report`中的bounded telemetry必须exact记录2个settled events与`opus4-6`/`claude-opus-4-6`；
Completion call logs和带正文的materialization只放受限`call-logs/`，不得复制到content-free `reports/`。
首次build失败后不自动或循环retry：先读progress、已写logs与Store状态，再由operator决定是否只补missing cell。

## 6. Galatea/CLI host gates

对独立clone分别运行formal top-level CLI `run-online-turn`和Galatea HTTP service，至少验证：

- Fresh/NewRequest创建per-turn Online；empty/no-active走raw-only且zero Store/provider；
- Prepared exact frozen completion+tool bind，零Timeline/Control/Store open；
- 启动时strict config/connections已冻结；Started Refuse早于本次current connection
  selection/client、route与derived；
- ToolContinuation先frozen tool profile，再current completion，最后Online；
- ToolResult NewRequest保留raw tail并使用current profile；
- formal readiness只读；仅Unfulfilled时Manager InspectProgress；
- Timeline descriptors、Store fulfilled view与Getter contributions在同canonical fixture上exact等价；
- old v4-v8/rebuild/config sentinel bytes不变。

## 7. Real-provider canary

deterministic tests不能替代下列人工gate：

- TLS/auth/endpoint/model availability；
- real provider terminal tool protocol；
- provider-native cache write/read evidence与usage accounting；
- model quality、mystery-analysis coherence与经济性。

每个provider使用独立fresh clone与call-log目录。记录git identity、config/route digests、typed outcome与bounded usage；
不记录secret或正文。环境失败记`EnvironmentBlocked`，不能改写为Passed。

## 8. Result

只有import/raw baseline、formal provisioning、provider-free gates、restart/materialization、CLI/Galatea host gates与本轮要求的
real-provider gates全部通过，才把该run id标为`Passed`。否则精确记录`Failed`或`NotRun`。保留run root只用于审计，
cleanup也必须是显式、bounded、可核对的operator action。
