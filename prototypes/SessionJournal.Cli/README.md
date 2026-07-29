# SessionJournal.Cli

`SessionJournal.Cli` 是 `SessionJournal` 的离线开发与迁移工具，也是
`SessionJournal.Maintainers` 的 composition root。它依赖 SessionJournal contracts、
concrete maintainer profiles 和 Completion provider，但不依赖旧 `ChatSession`
程序集。

旧 `ChatSession` repo 的读取和导出由相邻的
[`ChatSession.LegacyExportCli`](../ChatSession.LegacyExportCli/README.md)
负责；两个工具只通过版本化 JSON schema
`atelia.chat-session.legacy-upgrade-export.v1` 交换数据。

## import-legacy-json

把 `ChatSession.LegacyExportCli export-json` 生成的 JSON 导入新的
`SessionJournal` repo：

```bash
dotnet run --project prototypes/SessionJournal.Cli -- import-legacy-json \
  --input gitignore/migrations/<session>.json \
  --output gitignore/session-journal/<session> \
  --report-md gitignore/session-journal/<session>-import.md
```

导入只保留 raw facts：initial setup、observation、agent action 和 system
prompt setup。旧 compaction/recap 是可重建的 derived 信息，因此只计数并跳过。
未知 event（包括 `revert-turn`）会 fail fast，不会猜测 SessionJournal 的
Parent/ref 语义。

当前 importer 接受普通 observation/action 历史与 system-prompt update。带
tool-call/tool-results 的旧回合会在触碰 `--force` 目标前 fail fast：
SessionJournal 的 tool execution 需要 started/result/checkpoint/correlation
等 raw 事实，不能把旧 transcript 直接伪装成新 execution wire。

目标目录必须是不存在或为空的目录。`--force` 会先在目标的同级 staging
repository 完整导入并 reopen 验证，再替换精确目标；发布失败时会尝试恢复旧目录。
验证不再调用 full `Project()`：importer 从 source message 独立计算版本化 semantic
history commitment，再用 `SessionJournal.Offline` report 与 read-only exact
branch/ref/head、lineage、governing setup API 检查 target。验收同时覆盖完整
event-kind/count histogram、Idle boundary、最终 config/prompt hash、source-vs-target
semantic commitment，以及每条 legacy mapping 的 raw address/kind/顺序；staging 与
发布后的 repository 都执行同一检查。
input/output/report 的路径链都拒绝 symlink/reparse point，且 output 不得包含
input、report 不得覆盖 input 或位于 output repo 内。报告通过同目录临时文件
atomic publish；报告只记录 setup identity、hash/codec、counts 和 mapping，不复制
明文 system prompt 或 observation/action 内容。

## run-memory-maintainer

对一个已经由 `DerivedArtifactEpochPlanner` 持久化的 exact epoch 运行一个
rewrite maintainer。runner 不重新遍历整段历史、不按 role 自行 threshold/split，
也不推进 epoch 或 ArtifactSet pointer；它只物化该 epoch 的
`(sourceStartExclusive, sourceEndInclusive]`，并输出 JSON report、Completion call
log 与 append-only `derived/memory/v2/artifacts/` candidate：

```bash
dotnet run --project prototypes/SessionJournal.Cli -- run-memory-maintainer \
  --input gitignore/session-journal/<session> \
  --branch main \
  --connections prototypes/Galatea/.atelia/galatea/connections.json \
  --connection dsv4p \
  --profile autobiographical-rewrite \
  --epoch dae_<...> \
  --candidate-id prompt-tuning-a \
  --attempt-id attempt-1 \
  --output gitignore/backtest/<run>/result.json \
  --call-log-dir gitignore/backtest/<run>/calls
```

可用 profile：

| profile | role | target |
| --- | --- | --- |
| `autobiographical-rewrite` | `autobiography` | `action/roleplay.first-person-autobiography` |
| `world-understanding-rewrite` | `world-understanding` | `observation/roleplay.world-understanding` |

可用覆盖参数为 `--system-prompt <path>` 与 `--prompt <path>`；覆盖只改变
prompt/producer fingerprint 和 candidate identity，不改变 durable epoch。输出与
call-log 目录必须位于 input repo 外，且 `--output` 与 `--call-log-dir` 不得相同或
互为 ancestor/descendant；这些冲突在创建 Completion client、目录或调用 LLM 前拒绝。
report 使用同目录临时文件 atomic publish。

genesis epoch 使用显式 empty `MemoryPack`。non-genesis 从 epoch 的 exact input set
恢复全部 role blocks；若 input set 尚无当前 role（例如 topology 新增 maintainer），
该 role 的 old block 显式为空，但其他 blocks 仍作为 ContextHeader 输入。同一
role/epoch 可保存多个 alternative candidates；只有后续
`publish-derived-artifact-set` 才会让某个 candidate 进入可选择 set。

artifact schema v2 是直接切换：旧 `derived/recaps/v1/` 与
latest-by-profile index 已退役，不做 silent compatibility。derived 数据可删除并由
planner/runner/publisher 重建；raw events 仍是唯一 correctness source。

## run-derived-memory-orchestration

这是 DM-7 的日常 maintenance transaction 入口。命令对一个 exact epoch 固定一次
immutable input/history snapshot，按 role provisioning 并行运行所有尚未结算的
maintainer；每个成功 role 先持久化 artifact，再写 immutable settlement。required
roles 全部结算后，才以 exact transaction 原子发布一个 ArtifactSet。失败、取消或进程
重启不会发布半套 set；required 闭合后先写 immutable finalization intent，冻结
included settlements 与 omitted optional roles，再发布 exact set。重跑相同 job 在 intent
前只执行缺失 role；intent 后只补 publish/验证 exact set，旧 latest set 保持可用。

```bash
dotnet run --project prototypes/SessionJournal.Cli -- \
  run-derived-memory-orchestration \
  --input gitignore/session-journal/<session> \
  --branch main \
  --epoch dae_<...> \
  --role required:autobiographical-rewrite:produce \
  --role required:world-understanding-rewrite:produce \
  --policy-id roleplay-memory \
  --policy-fingerprint roleplay-memory-v1 \
  --candidate-prefix daily \
  --attempt-id attempt-1 \
  --connections prototypes/Galatea/.atelia/galatea/connections.json \
  --connection dsv4p \
  --output gitignore/backtest/<run>/orchestration.json \
  --call-log-dir gitignore/backtest/<run>/calls
```

role mode 有三种：

- `produce`：调用对应 concrete maintainer，需要 Completion connection；
- `identity`：显式写一个 current-epoch identity artifact；旧 role 缺失时从空 block
  开始，不创建 Completion client；
- `select-existing:<artifact-id>`：选择一个 exact alternative candidate；transaction
  会校验 artifact 的 epoch、role、target、producer、prompt/model、candidate/attempt
  全部 identity，而不只校验 id。它也不创建 Completion client。

仅当至少一个 role 为 `produce` 时才要求 `--connections`，并创建 call-log 目录。
transaction id 由 exact epoch、policy/topology 与完整 provisioning/job identity
确定；prompt/model、candidate/attempt 或 mode 变化会创建新 transaction，不会偷用旧
settlement。

两个 maintainer 命令都在读取 connections/prompt、创建 output/call-log、构造
Completion client 或调用 LLM 前完成统一 path preflight：input repo、connections、
system prompt、user prompt 都是 readonly inputs；output file 与 call-log directory
不得与任一 readonly path 相同、互为 ancestor/descendant，所有路径链也拒绝
symlink/reparse point。orchestration 的 policy/role provisioning 纯结构检查同样早于
任何 writable side effect。

## run-online-turn

这是 DM-8 的最小 online composition/acceptance 入口。命令把 planner、pending-first
maintenance、coherent candidate provider 和 SessionJournal engine 组合起来：在 Observation
append 前先做 fresh-bootstrap 与 request-size preflight，再执行 lifecycle；append 后按新 exact
head 重新维护/选择，
最后提交 Prepared v5 并调用 agent Completion。

```bash
dotnet run --project prototypes/SessionJournal.Cli -- \
  run-online-turn \
  --input gitignore/session-journal/<session> \
  --branch main \
  --message "continue" \
  --role required:autobiographical-rewrite:produce \
  --role required:world-understanding-rewrite:produce \
  --policy-id roleplay-memory \
  --policy-fingerprint roleplay-memory-v1 \
  --coherence-group memory-pack \
  --connections prototypes/Galatea/.atelia/galatea/connections.json \
  --connection dsv4p \
  --maximum-canonical-request-bytes 262144 \
  --uncertain-recovery refuse \
  --output gitignore/backtest/<run>/online-turn.json \
  --call-log-dir gitignore/backtest/<run>/calls
```

candidate ordinal 不再是 `run-online-turn` runtime flag；它由 selected branch 上 governing
`RuntimeConfigSetup` v2 的 `derivedContext.nthPrevious` 唯一决定，`0` 表示 latest。
`--maximum-canonical-request-bytes` 是可选的 final request guard；它测量 Prepared commitment
所用 canonical request JSON 的精确 UTF-8 byte length，不是 provider/model token count 或
context-window 保证，也不参与 candidate selection/fallback。strict empty-lineage bootstrap
由 native fresh-genesis raw topology 启用，不再有独立 bootstrap budget，也不会创建伪 artifact。
当前 CLI
便利入口只接受 `produce` roles；generic lifecycle coordinator 本身不依赖 Maintainers catalog
或 Completion connection，长期 host 可注入其他 exact role executions。

若 fresh-bootstrap、lifecycle/backpressure、candidate 或 canonical-byte preflight 失败，Observation 尚未 append，
raw head/event count 不变。Prepared/Started reopen 不再调用 maintainer/provider。输出 report
只含 head、phase、provider/API/model、agent text hash 与 error count；完整 request/action 只留在
显式 call-log 目录，不会写入 report。output、call-log、input repo 与 connections 的路径边界和
`run-derived-memory-orchestration` 一样在 client/目录/LLM side effect 前验证。

命令在 idle/failed boundary 使用 `--message` 调用 `SendAsync`；若打开时已处于
AwaitingAgentAction/Prepared/Started/tool continuation，则调用 `ResumeAsync`，不会重复 append
message。uncertain provider attempt 缺省 `--uncertain-recovery refuse`；operator 只有明确接受潜在
重复外部调用时，才可指定 `restart-new-attempt`。

## validate

严格、只读验证 SessionJournal：

```bash
dotnet run --project prototypes/SessionJournal.Cli -- validate \
  --input gitignore/session-journal/<session> \
  --branch main \
  --report-json gitignore/session-journal-validation.json
```

`--branch` 选择一个 existing active branch，省略时默认 `main`。命令调用
`Atelia.SessionJournal.Offline`：在 exact captured head 上检查完整 raw chain、所有 historical
Prepared commitments、forward operational legality，并与 tail execution state / governing
setup 做 differential。report 只含最终 phase/head-kind/sequence checkpoint、setup
address/runtime config、system-prompt UTF-8 hash、counts、版本化 semantic history
commitment 和 scan diagnostics；它不输出完整 execution state、明文 system prompt、
tool raw arguments、operation/correlation id，也不物化 LLM context。semantic commitment
复用 canonical request 的 history-value 语义并排除 raw address/execution metadata。report
必须在 repo 外；validator 不修复或截断 raw/refs。

## DerivedMemory ArtifactSet 运维命令

以下命令只操作可重建的 `derived/` 子系统。它们不会向 raw SessionJournal
追加 event。除上述 orchestration 的 `produce` mode 外，运维命令不会创建 Completion
client。所有命令都拒绝未知 option；标量 option 重复出现也会 fail fast。
`--report-json` 必须位于 input repo 外，并通过同目录临时文件 atomic publish。

role/target 使用 `role=carrier/block-key`，其中 carrier 只能是 `system`、
`observation` 或 `action`。block key 可以继续包含 `/`。member 使用
`role=artifact-id`。通常可写 `--key value`；若合法 value 本身以 `--` 开头，
使用 conventional inline form `--key=--value`（例如
`--required-role=--role=observation/--block`），避免与下一个 option 混淆。

### publish-derived-artifact-set

从已经 durability-settled 的 exact orchestration transaction 发布 immutable coherent
set。这个命令是低层运维入口；日常路径应使用
`run-derived-memory-orchestration`：

```bash
dotnet run --project prototypes/SessionJournal.Cli -- \
  publish-derived-artifact-set \
  --input gitignore/session-journal/<session> \
  --branch main \
  --transaction dmt_<...> \
  --member autobiography=<artifact-id> \
  --member world-understanding=<artifact-id> \
  --report-json gitignore/reports/publish-set.json
```

CLI 从 durable transaction 得到 policy、exact epoch/input set 与 role provisioning，
并要求 members 与 immutable settlements 完全相等。previous-set CAS 固定为
`epoch.inputSetId`，不能由参数伪造；CLI 从 members 的唯一 common anchor 读取
raw-authoritative governing setup address/schema/payload hash。发布只写
`derived/memory/v2/sets` 与 latest pointer；raw SessionJournal 不写任何 derived-set
definition/activation event。

### list-derived-artifact-sets

```bash
dotnet run --project prototypes/SessionJournal.Cli -- \
  list-derived-artifact-sets \
  --input gitignore/session-journal/<session> \
  --report-json gitignore/reports/derived-inventory.json
```

inventory 严格验证每个 set/pointer 的 self identity 与 exact member artifact，
并按 exact key/id 稳定排序。它有意保留 missing/stale pointer、fork/cycle 供诊断；
这些 topology 问题不会让 list 失败。

### validate-derived-memory

```bash
dotnet run --project prototypes/SessionJournal.Cli -- \
  validate-derived-memory \
  --input gitignore/session-journal/<session> \
  --report-json gitignore/reports/derived-validation.json
```

严格、只读验证所有 artifact/epoch/transaction/settlement/set/pointer，以及每个 exact
key 的 canonical role snapshot、完整无环单 tip lineage 和
`latest pointer == tip`。每个 set 必须闭包到 exact transaction、epoch 与全部 durable
settlements/finalization；intent-before-set 是合法可恢复状态，已有 set 则必须等于
intent 的 exact expected set。validation report 同时计数 transactions、role
settlements 与 finalizations。未被 set 使用的
orphan artifact 合法，便于 prompt tuning 保存 alternatives。空 derived repo
也合法。DM-5 起 validation report schema 是
`atelia.session-journal.cli.derived-memory-validation.v3`，新增 planner config/current
与 epoch/latest counts；这是 pre-release direct cutover，不输出旧 v1 shape。该命令不
rebuild、不创建目录或 lock。

不带 `--branch` 时按 RefId 分组验证所有 active branches，并拒绝任何仍被 durable
derived records 引用但已 archive/non-active 的 ref；带 `--branch <name>` 时只验证该 Engine
lifetime 绑定的 exact ref。两种模式都使用 raw journal 的 strict read-only open；
malformed active tail 会失败但不会触发 recovery/truncation。

### rebuild-derived-artifact-set-latest

```bash
dotnet run --project prototypes/SessionJournal.Cli -- \
  rebuild-derived-artifact-set-latest \
  --input gitignore/session-journal/<session> \
  --branch main \
  --coherence-group roleplay.default \
  --policy-id roleplay-memory \
  --policy-fingerprint roleplay-memory-v1 \
  --required-role autobiography=action/roleplay.first-person-autobiography \
  --required-role world=observation/roleplay.world-understanding
```

该命令只重建一个 exact lineage/coherence/policy/role-snapshot key 的 latest
pointer。没有 matching set、missing predecessor、role drift、fork 或 cycle
都会 fail fast，不会猜测 tie-break。

## Shared DerivedArtifactEpochPlanner 命令

这些命令只规划 shared history coverage；不会调用 LLM、运行 maintainer、发布
ArtifactSet 或写 raw event。

所有 branch-local 命令先用 `--branch <name>` 打开 existing active branch，再从 Engine 绑定
stable `RefId`。branch name 只是人类 selector；durable config/epoch/set/latest/report identity
使用 canonical lowercase `branchRefId`。fork 与 archive 后同名重建都不会继承旧 ref 的
DerivedMemory lineage。

### configure-derived-artifact-planner

```bash
dotnet run --project prototypes/SessionJournal.Cli -- \
  configure-derived-artifact-planner \
  --input gitignore/session-journal/<session> \
  --branch main \
  --coherence-group roleplay.default \
  --topology-version roleplay-memory-v1 \
  --minimum-recent-tokens 24000 \
  --epoch-trigger-tokens 12000 \
  --scheduling-headroom-tokens 8000 \
  --hard-limit-tokens 64000 \
  --expected-current none
```

config 是 immutable lineage；更新时把 `--expected-current` 改为当前 `dpc_...` id。
相同 definition 的重试幂等，真实 cutover 只影响未来 epoch。
`hard-limit-tokens` 必须严格大于
`minimum-recent-tokens + epoch-trigger-tokens + scheduling-headroom-tokens`，
保证正常 trigger 在 backpressure 前可达。

### plan-derived-artifact-epoch

```bash
dotnet run --project prototypes/SessionJournal.Cli -- \
  plan-derived-artifact-epoch \
  --input gitignore/session-journal/<session> \
  --branch main \
  --coherence-group roleplay.default \
  --expected-previous none \
  --input-set none \
  --report-json gitignore/reports/plan-epoch.json
```

genesis 必须同时使用两个 `none`。后续规划必须同时给出 exact previous `dae_...`
与真实 input `das_...`；input set 必须属于同 lineage/coherence group，且 common
anchor 必须与 previous epoch 终点完全一致。每次调用最多发布一个 epoch；未达到
trigger 会报告 `BelowTrigger`，达到 hard limit 但没有合法 boundary 会显式
backpressure。

### list-derived-artifact-epochs

```bash
dotnet run --project prototypes/SessionJournal.Cli -- \
  list-derived-artifact-epochs \
  --input gitignore/session-journal/<session> \
  --report-json gitignore/reports/epoch-inventory.json
```

报告稳定排序且 content-free，只包含 config/epoch identity、raw addresses、cost 与
read diagnostics，不包含 conversation 或 derived block 文本。

## llm-smoke

发送一次最小 Completion 请求，用于验证 connection/provider/call-log：

```bash
dotnet run --project prototypes/SessionJournal.Cli -- llm-smoke \
  --connections prototypes/Galatea/.atelia/galatea/connections.json \
  --connection dsv4p \
  --call-log-dir gitignore/session-journal/llm-smoke-calls
```
