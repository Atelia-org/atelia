# Galatea RecapGrid repeatable staging acceptance

> **状态**：Active procedure / current formal CLI shape
> **验证边界**：本文是可重复runbook，不把procedure本身冒充Passed evidence。WP-08当时没有运行真实LLM或actual cyber repo；
> C2D在2026-08-15已按本流程完成，exact record见
> [`C2 Galatea rolling maintainers`](../work/active/derived-recap-grid-c2-galatea-rolling-maintainers.md)。

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
  reports/                   # operational metadata；offline/history-load reports explicitly unbounded
  call-logs/                 # 可能含正文，限制访问
  host-config/               # strict acceptance-only config
```

source、run root、staging、clones与Galatea configured `sessionDir`必须canonical disjoint，所有existing
ancestors拒绝symlink/reparse。任何clone都不得提升为production repository。

禁止：

- 原地修改source或live Galatea repo；
- `--force`覆盖已有输出；
- 在本operator staging流程中自动create/provision/activate或从payload自授权；Galatea V2对完全missing user path的
  unpublished first-turn structural bootstrap是独立current product contract，不授权本runbook改写existing/live repo；
- route wildcard/default fallback；
- normal path读取、修复或删除old `derived/recap` roots；
- 把secret、request/response正文或recap正文写入汇总报告；已有hash/address/path仍按operational metadata限制访问。

## 2. Import and raw baseline

```bash
require_offline_validation_v3_idle() {
  local report_path="$1"
  jq -e '
    .schema == "atelia.session-journal.offline-validation.v3"
    and (keys == [
      "agentActionCount",
      "branchName",
      "branchRefId",
      "eventCount",
      "eventKindCounts",
      "executionPhase",
      "head",
      "headKind",
      "historyContributionCount",
      "historySemanticCommitmentCodecId",
      "historySemanticCommitmentSha256",
      "importedAgentActionCount",
      "logicalPayloadBytes",
      "observationCount",
      "preparedRequestCount",
      "repositoryPath",
      "runtimeConfig",
      "runtimeConfigSetup",
      "scanDiagnostics",
      "schema",
      "systemPromptSetup",
      "systemPromptUtf8Sha256",
      "systemPromptUtf8Sha256CodecId",
      "toolExecutionSequenceCheckpoint",
      "toolResultHistoryCount"
    ])
    and ([
      .schema,
      .repositoryPath,
      .branchName,
      .branchRefId,
      .head,
      .executionPhase,
      .headKind,
      .runtimeConfigSetup,
      .systemPromptSetup,
      .systemPromptUtf8Sha256CodecId,
      .systemPromptUtf8Sha256,
      .historySemanticCommitmentCodecId,
      .historySemanticCommitmentSha256
    ] | all(.[]; type == "string"))
    and ([
      .eventCount,
      .logicalPayloadBytes,
      .toolExecutionSequenceCheckpoint,
      .preparedRequestCount,
      .observationCount,
      .agentActionCount,
      .importedAgentActionCount,
      .toolResultHistoryCount,
      .historyContributionCount
    ] | all(.[]; type == "number" and . == floor))
    and (.branchName == "main")
    and (.executionPhase == "idle")
    and (.runtimeConfig | type == "object")
    and (.eventKindCounts | type == "array")
    and (.scanDiagnostics | type == "object")
  ' "$report_path" >/dev/null
}

dotnet run --project prototypes/SessionJournal.Cli -- \
  import-legacy-json --input "$source_export" --output "$staging_repo" \
  --report-json "$reports/import.json"

validate_imported="$reports/validate-imported.json"
if [[ -e "$validate_imported" ]]; then
  echo "offline validation report must be absent for this run" >&2
  exit 1
fi
if ! dotnet run --project prototypes/SessionJournal.Cli -- \
  validate --input "$staging_repo" --branch main \
  --report-json "$validate_imported"; then
  echo "offline validation failed; do not consume an old report" >&2
  exit 1
fi
if ! require_offline_validation_v3_idle "$validate_imported"; then
  echo "offline validation report is not exact idle V3; stop before reading raw authority witnesses" >&2
  exit 1
fi

ref_id="$(jq -er '.branchRefId | select(type == "string")' \
  "$validate_imported")"
raw_head="$(jq -er '.head | select(type == "string")' \
  "$validate_imported")"

history_load_report="$reports/history-load.json"
dotnet run --project prototypes/SessionJournal.Cli -- \
  recap-grid timeline history-load inspect \
  --input "$staging_repo" --branch main \
  --report-json "$history_load_report"

if ! jq -e '
  .schema == "atelia.session-journal.recap-history-load-calibration.v2"
  and (keys == [
    "baseline",
    "boundaries",
    "branchName",
    "branchRefId",
    "byKind",
    "capturedHead",
    "estimatorId",
    "schema",
    "totals",
    "unitDistributions",
    "units"
  ])
  and (.baseline | type == "string")
  and (.boundaries | type == "array")
  and (.branchName | type == "string")
  and (.branchRefId | type == "string")
  and (.byKind | type == "array")
  and (.capturedHead | type == "string")
  and (.estimatorId | type == "string")
  and (.schema | type == "string")
  and (.totals | type == "object")
  and (.unitDistributions | type == "object")
  and (.units | type == "array")
' "$history_load_report" >/dev/null; then
  echo "history-load report is not exact V2; stop before reading capturedHead" >&2
  exit 1
fi

history_load_head="$(jq -er '.capturedHead' "$history_load_report")"
if [[ "$history_load_head" != "$raw_head" ]]; then
  echo "history-load capturedHead does not match validated raw head" >&2
  exit 1
fi
```

记录source length/SHA-256、import report、selected RefId与raw head。对raw repository做sorted
`relative-path + length + SHA-256` inventory；在provider canary前的所有derived-only步骤后必须exact相同。
`ref_id`与`raw_head`分别从validation report的`.branchRefId`与`.head`读取；import report不包含RefId，
不得从旧repo或历史run复制。读取任何raw witness前必须先通过同一exact V3/25-field/root-type/Idle gate；该gate不验证
nested property order或serializer bytes。Offline validation是full selected-lineage audit，work、memory、cumulative payload与
final JSON都没有production cap；它包含absolute path、model/surface、addresses、hashes与counts，不是content-free。
若validation失败或publication不确定，使用fresh absent output重新执行read-only validation，不能消费existing stale report。
Exact producer/report边界见[Offline validation report V3 approved contract](../current/contracts/offline-validation-report-v3.md)；
用户已批准[surface set 6 addendum](../evidence/contract-freeze-r2-approval-surface-set-6.md)精确圈定的scope；fresh gates/rebuild
与final pre-tag review已完成，annotated v6 tag object `acc73dab`已锚定reviewed ledger `14b570cb`。它不属于immutable
surface set 5；runbook存在、contract approval或rebuild PASS都不证明current operator acceptance Passed。对post-tag review
object `bbfd7823`与actual tag的independent review已PASS；本tail不移动tag、不续期证据或扩大scope。

只有上面的exact V2/11-field/type gate通过后才能读取HistoryLoad `capturedHead`；
`history_load_head`必须等于本轮`raw_head`。History-load report是full-window unbounded offline report，没有final byte cap或
stable oversize结果；若report write失败，raw repo不变，可重新执行inspect并用fresh `capturedHead`复核，不要消费partial/stale output。
Exact top-level/read-only contract见[HistoryLoad report V2](../current/contracts/history-load-report-v2.md)；该窄scope已获
surface set 4用户批准、通过unified gates/review并由immutable v4 tag锚定。Runbook消费约束不把nested
shape、resource bounds或current operator state顺带升级为批准承诺。

## 3. Explicit formal provisioning

准备strict bounded inputs：

- Timeline/Cadence initial policy：partition algorithm、HistoryLoad estimator、target load、minimum recent load、
  max raw events、max rendered bytes；本runbook目标值与Galatea missing-session bootstrap一致，但其唯一code authority是
  `prototypes/Galatea/GalateaSessionRepositoryProvisioner.cs`中的`GalateaFirstTurnBootstrapPolicy`，本文数字只服务本轮
  operator acceptance，不构成第二份runtime policy source；
- Control admission：permissions、allowed families/capabilities/carriers/prefixes与budgets；
- canonical Family/Definition values，或code-owned exact built-in asset id；
- exact route manifest与acceptance-only strict Completion connections；`semanticModelId`必须显式，含`null`；
- Galatea `recapGrid` config：deferred route path、bounded profile files、exact current profile id。

先用provider-free scaffold创建三份strict canonical配置，并从bounded JSON report读取两个ordered definition digests：

```bash
mkdir -p "$operator_dir"
galatea_connections="prototypes/Galatea/.atelia/galatea/connections.json"
recap_source_connection_id="opus4-6"
recap_connection_id="opus4-6-recap"
partition_algorithm="atelia.history-timeline.partition.first-replay-safe-at-target.v1"
estimator="atelia.history-load.o200k-base.history-unit-v1"
minimum_recent=24000
target_load=60000
# Timeline partition的单次raw range上限；不要与recent-reserve operation跨页审计预算262144混淆。
max_raw=65536
max_rendered=1048576
bootstrap_row_cap=1
projected_call_cap=2
character_name="Galatea"
player_name="刘世超"
admission="$operator_dir/recap-grid-admission.json"
profile="$operator_dir/recap-grid-agent-control-profile.json"
route_manifest="$operator_dir/recap-grid-routes.json"
strict_connections="$operator_dir/recap-grid-connections.json"
recipe_file="$operator_dir/galatea-rolling-full-recipe.json"

# 首次部署时dedicated recap id尚不存在。live connections可能还包含retired routing字段；
# acceptance不复用whole file，而从exact main source connection投影provider/model/env/cache字段，
# 显式创建runtime-only recap id与low effort，不复制literal apiKey。所有output必须事先不存在。
( set -o noclobber; umask 077; jq -e \
  --arg source "$recap_source_connection_id" \
  --arg target "$recap_connection_id" '
    [.connections[] | select(.id == $source)]
    | if length != 1 then error("exact source connection absent or duplicate")
      else .[0] end
    | select(.kind == "anthropic")
    | . as $connection
    | {
        v: 1,
        defaultConnectionId: $target,
        connections: [
          ({
            id: $target,
            kind: $connection.kind,
            modelId: $connection.modelId,
            completionSurfaceId: ($connection.completionSurfaceId // "anthropic"),
            reasoningEffort: "low"
          }
          + (if (($connection.baseAddressEnv // "") | length) > 0 then
               {baseAddressEnv: $connection.baseAddressEnv}
             elif (($connection.baseAddress // "") | length) > 0 then
               {baseAddress: $connection.baseAddress}
             else error("selected connection has no endpoint source") end)
          + (if (($connection.apiKeyEnv // "") | length) > 0 then
               {apiKeyEnv: $connection.apiKeyEnv}
             else {} end)
          + (if $connection.maxTokens != null then
               {maxTokens: $connection.maxTokens}
             else {} end)
          + (if $connection.anthropicPromptCacheTtl != null then
               {anthropicPromptCacheTtl: $connection.anthropicPromptCacheTtl}
             else {} end))
        ]
      }
  ' "$galatea_connections" >"$strict_connections" )

base_address_env="$(jq -er '.connections[0].baseAddressEnv' "$strict_connections")"
api_key_env="$(jq -er '.connections[0].apiKeyEnv' "$strict_connections")"
[[ -n "${!base_address_env:-}" && -n "${!api_key_env:-}" ]] || {
  echo "selected connection environment is unavailable" >&2; exit 1;
}

scaffold_report="$(dotnet run --project prototypes/SessionJournal.Cli -- \
  recap-grid scaffold \
  --asset galatea-rolling-rewrite-zh-cn-v6 \
  --character-name "$character_name" \
  --player-name "$player_name" \
  --profile-id galatea-rolling-v6 \
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
  --asset galatea-rolling-rewrite-zh-cn-v6 \
  --character-name "$character_name" \
  --player-name "$player_name"

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
`build_report`中的bounded telemetry必须exact记录2个settled events与本轮`recap_connection_id`/`claude-opus-4-6`；
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
- V3 no-tools `FullReplacementText` Completion contract/provider projection与真实`end_turn` termination；
- provider-native cache write/read evidence与usage accounting；
- 两列连续性、source/role discipline与经济性。

每个provider使用独立fresh clone与call-log目录。记录git identity、config/route digests、typed outcome与bounded usage；
不记录secret或正文。环境失败记`EnvironmentBlocked`，不能改写为Passed。

## 8. Result

只有import/raw baseline、formal provisioning、provider-free gates、restart/materialization、CLI/Galatea host gates与本轮要求的
real-provider gates全部通过，才把该run id标为`Passed`。否则精确记录`Failed`或`NotRun`。保留run root只用于审计，
cleanup也必须是显式、bounded、可核对的operator action。

## 9. Actual activation after a passed disposable candidate

Passed disposable clone不能直接提升为actual repository。停服后必须再次从同一immutable export导入一个全新actual repo。先把
reconcile产生的新head提升为后续唯一raw authority witness：

```bash
# 复用§2在同一operator shell中定义的require_offline_validation_v3_idle。
pre_setup_head="$raw_head"
actual_connections="$host_config/candidate-connections.json"
main_connection_id="opus4-6"
system_prompt_file="prototypes/Galatea/.atelia/galatea/prompts/cyber.md"
setup_report="$reports/reconcile-actual-setup.json"
validate_after_setup="$reports/validate-actual-after-setup.json"

if [[ -e "$setup_report" || -e "$validate_after_setup" ]]; then
  echo "activation reports must be create-only for this run" >&2
  exit 1
fi

if ! dotnet run --project prototypes/SessionJournal.Cli -- \
  reconcile-desired-setup \
  --input "$actual_repo" --branch main \
  --expected-head "$pre_setup_head" \
  --connections "$actual_connections" \
  --connection "$main_connection_id" \
  --system-prompt-file "$system_prompt_file" \
  --report-json "$setup_report"; then
  echo "desired setup reconcile failed; re-inspect current raw authority before retry" >&2
  exit 1
fi

if ! jq -e '
  .schema == "atelia.session-journal.desired-setup-reconciliation.v2"
  and (keys == [
    "afterHead",
    "beforeHead",
    "branchName",
    "completionSurfaceId",
    "connectionId",
    "modelId",
    "runtimeConfigChanged",
    "schema",
    "systemPromptChanged",
    "systemPromptUtf8Sha256"
  ])
  and ([
    .schema,
    .branchName,
    .connectionId,
    .beforeHead,
    .afterHead,
    .modelId,
    .completionSurfaceId,
    .systemPromptUtf8Sha256
  ] | all(.[]; type == "string"))
  and (.runtimeConfigChanged | type == "boolean")
  and (.systemPromptChanged | type == "boolean")
' "$setup_report" >/dev/null; then
  echo "desired setup report is not exact V2; re-inspect current raw authority" >&2
  exit 1
fi

raw_head="$(jq -er '.afterHead' "$setup_report")"
if [[ "$raw_head" == "$pre_setup_head" ]]; then
  jq -e '
    .runtimeConfigChanged == false and .systemPromptChanged == false
  ' "$setup_report" >/dev/null
fi

if ! dotnet run --project prototypes/SessionJournal.Cli -- \
  validate --input "$actual_repo" --branch main \
  --report-json "$validate_after_setup"; then
  echo "post-setup validation failed; re-inspect current raw authority" >&2
  exit 1
fi
if ! require_offline_validation_v3_idle "$validate_after_setup"; then
  echo "post-setup validation report is not exact idle V3; re-inspect current raw authority" >&2
  exit 1
fi
if ! jq -e --arg head "$raw_head" --arg ref "$ref_id" '
  .head == $head and .branchRefId == $ref
' "$validate_after_setup" >/dev/null; then
  echo "post-setup validation head/ref mismatch; re-inspect current raw authority" >&2
  exit 1
fi
```

所有report/config output必须create-only且预先不存在。setup已exact时允许`afterHead == pre_setup_head`，但上面的gate要求report同时证明
两项均未改变。

`reconcile-desired-setup`先修改raw、再atomic publish report；production writer本身允许overwrite，create-only只是本
runbook用来排除stale receipt的operator precondition。若command exit 1、report缺失或V2 gate失败，不能据此推断raw
未变，也不得用旧`pre_setup_head`盲重试。必须重新只读inspect/validate current exact head、Idle boundary与governing
setup，再以observed exact head幂等执行同一desired intent；不要rollback或手工补raw setup。Exact report contract与
failure recovery见[Desired setup reconciliation report V2 approved contract](../current/contracts/desired-setup-reconciliation-report-v2.md)；
其surface set 3 exact narrow contract已由immutable v3 tag锚定；runbook存在与contract approval均不等于本轮activation已Passed。

随后按以下顺序执行：

1. 以更新后的`raw_head`重新执行四域init、Timeline sync、asset provision、world-first recipe compose/put、bounded build、zero-call
   repeat、verify、promotion与
   materialization；不得复制disposable clone的Grid/Control/Timeline文件；
2. 保留main-agent connection choices，给recap route使用独立connection id与runtime model/reasoning policy；model policy不进入durable identity；
3. 在仍停服时，用candidate config path先启动一次正式Host，证明strict config/connections可open且readiness exact/ready，再干净停机；
4. fresh核对live config/connections bytes/SHA-256仍等于preflight inventory，create-only保存两份pre-activation backups；安装两份candidate
   文件后再次核对bytes/SHA-256，并把含credential reference或本地登录信息的文件限制为owner-only访问。两文件切换期间不得启动Host；
5. 从正式live config再次启动Host，只登录并读取`/api/me`与`/api/recent-turns`；要求raw head、recipe、`freshness=exact`、`state=ready`，
   且不得发送canary
   用户消息；
6. 保留旧repo inert。首次新用户raw append前，可通过恢复两份pre-activation config回到旧repo；该动作不得删除或改写新repo。首次新用户
   append后，回退若会隐藏新经历则No-Go，必须改为停服forward-fix或显式raw-preserving迁移。
