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
```

记录source length/SHA-256、import report、selected RefId与raw head。对raw repository做sorted
`relative-path + length + SHA-256` inventory；在provider canary前的所有derived-only步骤后必须exact相同。

## 3. Explicit formal provisioning

准备strict bounded inputs：

- Timeline/Cadence initial policy：partition algorithm、HistoryLoad estimator、target load、minimum recent load、
  max raw events、max rendered bytes；本runbook目标值为B=60,000、R=24,000；
- Control admission：permissions、allowed families/capabilities/carriers/prefixes与budgets；
- canonical Family/Definition values，或code-owned exact built-in asset id；
- exact route manifest与Completion connections；`semanticModelId`必须显式，含`null`；
- Galatea `recapGrid` config：deferred route path、bounded profile files、exact current profile id。

先用provider-free scaffold创建三份strict canonical配置，并从bounded JSON report读取两个ordered definition digests：

```bash
mkdir -p "$operator_dir"
admission="$operator_dir/recap-grid-admission.json"
profile="$operator_dir/recap-grid-agent-control-profile.json"
route_manifest="$operator_dir/recap-grid-routes.json"
recipe_file="$operator_dir/galatea-rolling-full-recipe.json"
timeline_head_file="$operator_dir/timeline-head.json"

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
  --max-bootstrap-rows 1000000 --max-projected-calls 1000000 \
  --max-concurrency 2 --dispatch-timeout-ms 900000 \
  --max-output-tokens 32768 \
  --admission-output "$admission" --profile-output "$profile" \
  --route-output "$route_manifest")"
world_definition="$(jq -er '.detail.definitions[] | select(.logicalColumnId == "world-understanding") | .digest' <<<"$scaffold_report")"
autobiography_definition="$(jq -er '.detail.definitions[] | select(.logicalColumnId == "autobiography") | .digest' <<<"$scaffold_report")"
```

scaffold为create-only；任一输出已存在时必须先由operator核对并选择新的空输出路径，不得覆盖。

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

direct activation必须使用put-recipe之后fresh读取的whole Control/Timeline authority；CLI的`timeline export`报告提供exact
`headCanonicalBase64`，不得手写canonical Timeline head：

```bash
control_report="$(dotnet run --project prototypes/SessionJournal.Cli -- \
  recap-grid control inspect --input "$staging_repo" --branch main)"
timeline_report="$(dotnet run --project prototypes/SessionJournal.Cli -- \
  recap-grid timeline export --input "$staging_repo" --branch main --max-rows 1)"
printf '%s' "$(jq -er '.detail.headCanonicalBase64' <<<"$timeline_report")" \
  | base64 --decode >"$timeline_head_file"

control_instance="$(jq -er '.detail.head.instanceId.value' <<<"$control_report")"
control_timeline="$(jq -er '.detail.head.timelineId.value' <<<"$control_report")"
control_generation="$(jq -er '.detail.head.generation' <<<"$control_report")"
control_state="$(jq -er '.detail.head.stateDigest.value' <<<"$control_report")"
control_active="$(jq -r '.detail.head.activeRecipeDigest.value // "none"' <<<"$control_report")"

dotnet run --project prototypes/SessionJournal.Cli -- \
  recap-grid control activate \
  --input "$staging_repo" --branch main --confirm-ref "$ref_id" \
  --admission "$admission" --recipe "$recipe_digest" \
  --confirm-instance "$control_instance" \
  --confirm-timeline "$control_timeline" \
  --confirm-generation "$control_generation" \
  --confirm-state "$control_state" --confirm-active "$control_active" \
  --confirm-timeline-head "$timeline_head_file"
```

所有mutation都提交fresh exact confirmation；Busy/Stale/Unsupported/Indeterminate不自动retry。

## 4. Provider-free gates

在打开provider factory前依次运行：

```bash
recap-grid timeline sync ...
recap-grid timeline inspect --input "$staging_repo"
recap-grid timeline verify --input "$staging_repo"
recap-grid cadence inspect --input "$staging_repo" --branch main
recap-grid control inspect --input "$staging_repo"
recap-grid control verify --input "$staging_repo"
recap-grid inspect --input "$staging_repo"
recap-grid verify --input "$staging_repo"
recap-grid progress --input "$staging_repo" ...
```

这些命令必须no-provider；inspect/verify/progress必须read-only/no-create。若Timeline sync需要offline audit，CLI只能
使用本operation的一次性bounded snapshot；raw drift返回typed failure且不retry。

用`recap-grid legacy-root inspect`记录old seven-slot manifest。normal provisioning/sync/build/materialize与Grid reset
前后legacy bytes必须不变；archive/delete只能在单独operator run中提供fresh exact source/archive witnesses。

## 5. Build, restart and materialization

在staging或disposable deterministic clone运行：

```bash
recap-grid build --input "$repo" ...
recap-grid progress --input "$repo" ...
recap-grid materialize --input "$repo" --nth-previous 0 ...
```

至少覆盖一次bounded partial build：首operation提交部分cells/views后dispose所有handles；fresh reopen只dispatch
missing work，完成后再次reopen为zero-call。任何阶段都不得创建old rebuild spool。

candidate recipe build不自动activate。promotion必须同进程pure-read检查current head-through assignment、fulfillment与exact proof，
再用whole Control/Timeline heads执行operation-aware CAS。partial/stale/missing不得activate。

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
