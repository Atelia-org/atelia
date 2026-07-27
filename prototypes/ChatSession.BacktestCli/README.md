# ChatSession.BacktestCli

`ChatSession.BacktestCli` 是离线检查和回放 ChatSession 历史的实验 CLI。它同时支持：

- 读取 `ChatSessionEngine` 导出的 legacy upgrade JSON，检查数据或运行无副作用 backtest。
- 把 legacy export 导入 `SessionJournal` raw repo。
- 从 `SessionJournal` addressed replay 运行 MemoryMaintainer，并把成功结果写成可重建的
  Derived Recap Artifact。

CLI 不启动真实聊天服务。当前 LLM maintainer 统一采用单次完整 Rewrite，不暴露工具、不运行
tool-loop。已归档的 Recording / Compression / two-stage Text Edit Agent 实验可通过 tag
`memory-maintainer-agentic-experiment-v1` 查阅；重构决策见
`docs/Galatea/memory-maintainer-slimming-refactor.md`。

## Legacy 输入命令

### export-legacy-upgrade

读取旧版 `ChatSession` repo，通过 `ChatSessionLegacyUpgradeExporter` 导出用于迁移的
legacy upgrade JSON。该命令不会修改输入 repo；默认输出格式化 JSON，使用 `--compact` 可输出紧凑 JSON。
输出必须位于输入 repo 外，且通过同目录临时文件 atomic replace 发布；输入、输出路径链不能包含
symlink/reparse point。

```bash
dotnet run --project prototypes/ChatSession.BacktestCli -- export-legacy-upgrade \
  --input prototypes/FamilyChat.Server/.atelia/family-chat/sessions/<session-repo> \
  --output gitignore/migrations/<session-name>.json
```

参数：

- `--input <repo-dir>`：旧版 `ChatSession` repo。
- `--output <json>`：迁移 JSON 输出路径；父目录会自动创建，已有文件会被覆盖。
- `--branch <name>`：要导出的 branch，默认 `main`。
- `--compact`：关闭格式化缩进。

在 legacy-inferred 历史中，`model-turn` 表示“保持既有 message prefix 并追加
`appendedMessages`”，允许一次追加 `1..N` 条消息；旧版 repo 可能分别持久化 observation 和 action，
不能假设它一定是恰好两条的完整回合。

### inspect

检查 legacy export 的 schema、branch、事件数量和 message kind 分布。

```bash
dotnet run --project prototypes/ChatSession.BacktestCli -- inspect \
  --input prototypes/Galatea/.atelia/galatea/sessions/cyber-copy-upgraded/chat-session-legacy-upgrade-export.json
```

### replay-pattern-count

运行不调用 LLM 的轻量规则分析器，用于验证 legacy replay cursor、阈值触发和 JSONL 输出。

```bash
dotnet run --project prototypes/ChatSession.BacktestCli -- replay-pattern-count \
  --input prototypes/Galatea/.atelia/galatea/sessions/cyber-copy-upgraded/chat-session-legacy-upgrade-export.json \
  --threshold-tokens 24000 \
  --output gitignore/backtest/pattern-count.jsonl \
  --report-md gitignore/backtest/pattern-count.md
```

参数：

- `--input <path>`：legacy export JSON。
- `--output <jsonl>`：逐 epoch 写入的 JSONL 结果。
- `--report-md <path>`：可选，写出最终 Markdown 摘要。
- `--threshold-tokens <n>`：估算 token 数达到阈值后才触发分析，默认 `24000`。
- `--respect-original-compaction`：按 export 中原始 compaction 事件回放；默认忽略。

### replay-rolling-summary

从 legacy export 运行 LLM maintainer backtest。它使用 synthetic sliding prefix，但不写
SessionJournal Derived Recap Artifact。

```bash
dotnet run --project prototypes/ChatSession.BacktestCli -- replay-rolling-summary \
  --input prototypes/Galatea/.atelia/galatea/sessions/cyber-copy-upgraded/chat-session-legacy-upgrade-export.json \
  --threshold-tokens 24000 \
  --connections prototypes/Galatea/.atelia/galatea/connections.json \
  --connection dsv4p \
  --output gitignore/backtest/legacy-world-understanding/result.jsonl \
  --call-log-dir gitignore/backtest/legacy-world-understanding/calls \
  --max-epochs 1 \
  --preset world-understanding-rewrite
```

## SessionJournal 工作流

### import-session-journal

把 legacy upgrade export 导入新的 `SessionJournal` repo。导入只写 raw facts：
`initial-state` 成为初始化 setup/session 链，observation/action 成为对应 raw events，
legacy `compaction` / `recap` 作为可重建 derived 信息跳过。

```bash
dotnet run --project prototypes/ChatSession.BacktestCli -- import-session-journal \
  --input prototypes/Galatea/.atelia/galatea/sessions/cyber-copy-upgraded/chat-session-legacy-upgrade-export.json \
  --output gitignore/session-journal/cyber-copy-upgraded \
  --report-md gitignore/session-journal/cyber-copy-upgraded-import.md
```

参数：

- `--input <path>`：legacy export JSON。
- `--output <repo-dir>`：新 `SessionJournal` repo。目录已存在且非空时默认失败。
- `--force`：删除并重建整个目标 repo；只应用于已经确认可替换的输出路径。
- `--report-md <path>`：可选，写出 legacy ordinal 到新 `EventAddress` 的映射。

这是明确的 **legacy export → current SessionJournal wire** 离线迁移：命令创建一个新 repo，并记录
legacy ordinal 到新 `EventAddress` 的映射。它不会、也不能原地改写已有 SessionJournal 的 immutable
raw events。若未来要迁移旧版 SessionJournal wire，必须提供对应旧 codec、写入另一个新 repo，并重新
生成所有 address-sensitive derived artifacts；当前命令不会猜测或兼容任意未知旧 wire。

### validate-session-journal

对当前 main Parent chain 做完整、严格、只读的 offline validation：

```bash
dotnet run --project prototypes/ChatSession.BacktestCli -- validate-session-journal \
  --input gitignore/session-journal/cyber-copy-upgraded \
  --report-json gitignore/backtest/session-journal-validation.json
```

validator 会逐 event 严格 decode、计算 logical payload bytes，以 full reducer 对照 exact-head tail
resolver，检查 governing setup、`PreparedRequestCount`，并对每个 Prepared v2 执行 exact coherent
request reconstruction；同时检查最后一个 raw `ArtifactSetCommitted` 及其 exact sidecar members。
当前 wire 只有 coherent artifact-tail Prepared v2，不再读取早期 full-raw / explicit Prepared。
readiness 为：

- `active-coherent`：存在可用的 durable active ArtifactSet。
- `needs-artifact-set-checkpoint`：尚无 activation，或 activation 指向的 exact sidecar member 已缺失/
  不可用。

`--report-json` 可选，但必须位于输入 repo 外；命令不会写 raw、derived index 或 EventJournal
forward-plan cache。底层 events、ref-op-log、live ref objects 均以严格只读方式打开；需要 recovery
的 active tail 只报错、不截断。validator 会逐历史 activation/setup/Prepared 完整验证，因此成本是
有意的 O(raw inventory)，不用于 online recovery。raw/wire 不兼容、Parent/correlation/checkpoint
等不变量破坏时命令以非零状态失败，而不是输出一份貌似可用的报告。

### checkpoint-artifact-set-session-journal

在已生成至少两个 common-anchor exact artifacts 后，显式提交一次 durable active-set activation：

```bash
dotnet run --project prototypes/ChatSession.BacktestCli -- \
  checkpoint-artifact-set-session-journal \
  --input gitignore/session-journal/cyber-copy-upgraded \
  --member autobiography=<autobiography-artifact-id> \
  --member world-understanding=<world-understanding-artifact-id>
```

`--member <role>=<artifact-id>` 可重复，role 和 artifact id 都必须唯一。命令先运行上述只读
validation，再验证 exact artifact 的 common anchor、setup、current Parent lineage、target
contribution hash，并以 exact-head CAS **只追加一条** `ArtifactSetCommitted`。旧 event/manifest 和
derived files 均不改写；缺 member、duplicate role/id、anchor/setup/lineage 不一致或当前 head 不是合法
idle boundary 时不 append。

### replay-rolling-summary-session-journal

从 `SessionJournal` raw repo 的 addressed history replay 运行 maintainer。成功 completion 会先写入
`derived/recaps/v1/`，store 成功后 runner 才提交本 epoch 的 `MemoryPack` 和 sliding prefix。

```bash
dotnet run --project prototypes/ChatSession.BacktestCli -- replay-rolling-summary-session-journal \
  --input gitignore/session-journal/cyber-copy-upgraded \
  --threshold-tokens 24000 \
  --connections prototypes/Galatea/.atelia/galatea/connections.json \
  --connection dsv4p \
  --output gitignore/backtest/session-journal-autobiographical/result.jsonl \
  --call-log-dir gitignore/backtest/session-journal-autobiographical/calls \
  --max-epochs 1 \
  --preset autobiographical-rewrite
```

两条 LLM replay 命令共享这些参数：

- `--input <path>`：legacy 命令接受 export JSON；SessionJournal 命令接受 repo 目录。
- `--output <jsonl>`：逐 epoch replay record。
- `--connections <path>` / `--connection <id>`：Completion connection 配置和可选连接 ID。
- `--call-log-dir <dir>`：每次 LLM 调用的请求/响应日志目录。
- `--threshold-tokens <n>`：达到阈值后触发 maintainer，默认 `24000`。
- `--max-epochs <n>`：本次最多产生多少个 epoch；真实 LLM 验收建议先用 `1`。
- `--preset <name>`：`autobiographical-rewrite` 或 `world-understanding-rewrite`。
- `--system-prompt <path>` / `--prompt <path>`：可选，覆盖 preset 的 system/user prompt。

对 SessionJournal 命令，`--output` 和 `--call-log-dir` 必须位于输入 repo 外。JSONL 先写到同目录
临时文件，replay 正常结束后才替换最终路径；preflight/configuration 失败不会截断已有报告。为避免
路径 alias 绕过 repo 边界，`--input`、`--output`、`--call-log-dir` 的路径链不能包含
symlink/reparse point。

当前可用 preset：

| preset | target | 用途 |
| --- | --- | --- |
| `autobiographical-rewrite` | `Action / roleplay.first-person-autobiography` | 将新经历融入第一人称自传并完整重写。 |
| `world-understanding-rewrite` | `Observation / roleplay.world-understanding` | 维护事实档案和世界认知地图。 |

> **当前限制**：SessionJournal replay 的 `--threshold-tokens` 是 maintainer-runner-local 参数。
> 每个 preset 独立累计 active history，达到阈值后再用
> `HistoryWindowSplitPolicy.FindHalfContextSplitPoint()` 计算自己的 `splitIndex`。两个命令使用完全相同
> raw input、estimator、threshold 和 replay 起点时通常得到同一 anchor，但系统没有持久化的 shared
> partition identity；这不能作为 coherent ArtifactSet 的长期同步合同。

输出 JSONL schema 为 `atelia.chat-session.memory-maintainer-backtest.v2`。除调用、target、split 和状态信息外，
SessionJournal 成功 record 还包含：

- `sourceRawHead`、`sourceStartInclusive`、`sourceEndInclusive`：本次 replay snapshot 与实际吸收范围。
- `artifactId`、`artifactPath`、`anchorRawEvent`、`previousArtifact`：实际落盘 artifact 的链接。
- `callLogPaths`：本 epoch 实际写出的 Completion call log。

### Full replay 与重新生成

当前 SessionJournal 命令是从 raw history 起点、空 `MemoryPack` 开始的 full replay。因此目标
`profile + target` lineage 必须为空；已有 artifact 时命令会在首次 LLM 调用前失败。它尚不支持从
latest artifact materialize 后只 replay anchor 之后的 tail。

两个正式 preset 的 profile/target lineage 不同，因此可以在同一个新 repo 上各运行一次；第二个
preset 的 root artifact 不会把第一个 preset 的 artifact 设为 `previousArtifact`。但同一 preset
不能在未清理其既有 lineage 时直接重复 full replay。

### Shared coverage epoch 的目标形态（尚未实现）

日常 SessionJournal 中，history 如何滑出 recent context 与“注册了几个 MemoryMaintainer”无关。同一
`coherenceGroup`（例如 core-memory 中的 autobiography + world-understanding）应先由 Derived
Artifact Epoch Planner 生成一个 immutable shared plan，再让所有 maintainer 消费：

```text
planner config
  minimumRecentTokens
  epochTriggerTokens
  tokenEstimatorId
  dependencyBoundaryPolicyId
  topologyVersion

epoch plan
  epochId
  previousEpoch
  plannedAtRawHead
  exact sourceStartExclusive/sourceEndInclusive
  common anchor + setup refs
  config fingerprint
```

config 只定义未来怎样切；epoch ledger 固定历史实际上怎样切了。它们属于独立 DerivedMemory
repository，不写入 raw SessionJournal。概念上的 repo 布局可以是：

```text
derived/memory/v1/
  planner-config.json          # current config pointer
  planner-configs/<hash>.json  # immutable snapshots
  epochs/<epoch-id>.json       # immutable actual partitions
  artifacts/...
  sets/...
  indexes/...
```

第一版触发规则是：先保留至少 `minimumRecentTokens` 的最新 dependency-closed suffix；更旧且可安全滑出
的 prefix 达到 `epochTriggerTokens` 时，先 durable 写一个 epoch plan，再并行运行该 group 的 required
maintainers。split 必须对齐 replay-safe/dependency-safe boundary，所以各 epoch 大小可以不同；同步
要求是共享 exact epochId/range，而不是每块大小相同。

prompt-tuning 不应再通过修改 `--threshold-tokens` 让单个 maintainer 重新切 history。目标 CLI 会把
planning 与 producer execution 分开，下面只是意图示意，不是当前可用命令：

```text
plan-derived-artifact-epochs --input <repo> --config <config>
run-memory-maintainer --input <repo> --epoch <epoch-id> --preset <name> [prompt overrides]
run-memory-maintainers --input <repo> --epoch <epoch-id>               # online parallel group
publish-derived-artifact-set --input <repo> --epoch <epoch-id> --candidate ...
```

同一 role 可以针对同一 epoch 独立重跑多个 prompt/model candidate；这些运行不能移动 epoch cursor。
最终 set 从每个 required role 中显式选一个同 epoch candidate，全部成功后才发布。若某 maintainer
判断内容无需变化，也应以显式 no-change/identity result 结算该 epoch，而不是拿上一 epoch artifact
冒充同步成员。

因此当前命令的 `--threshold-tokens` 将继续作为 legacy/backtest 实验入口，直至 shared epoch planner
落地；D6E 中两个 preset 得到共同 anchor 是一次有效验收结果，但不是 durable synchronization
机制。目标设计见
[`memory-maintainer-provisioning-planner-gap.md`](../../docs/SessionJournal/memory-maintainer-provisioning-planner-gap.md)
§5.4；具体实施顺序见
[`derived-memory-subsystem-implementation-plan.md`](../../docs/SessionJournal/derived-memory-subsystem-implementation-plan.md)。

### D6E 可审计迁移与 readiness 验收

正式验收应始终使用一个不存在的、带 run-id 的新 repo 和独立 evidence 目录，不覆盖已有实验 repo，
也不使用 `--force`：

```text
inspect legacy export
-> import-session-journal 到新 repo
-> validate：needs-artifact-set-checkpoint / PreparedRequestCount=0
-> autobiographical-rewrite --max-epochs 1
-> world-understanding-rewrite --max-epochs 1
-> 严格提取各自唯一 succeeded artifact id，并验证共同 anchor/setup
-> checkpoint 两个 exact members，一次且仅一次
-> validate：active-coherent / 两成员可用 / eventCount 恰好 +1
```

maintainer JSONL 与 Completion call log 应仅写入 `gitignore/backtest/<run-id>/`；源码文档只记录
artifact id、anchor、计数和 readiness，不复制请求/响应内容，也不读取或输出 connection secret。
checkpoint 失败后必须先重新 validate/inventory：若 event/head 未变，可以保留该 run 做 forensic
evidence；不要在无法确定是否 append 时重复 checkpoint。

2026-07-27 的 D6E 真实验收使用
`gitignore/session-journal/cyber-copy-d6e-20260727-061650`，对应 evidence 位于
`gitignore/backtest/session-journal-d6e-20260727-061650`。导入后为 148 events、
474439 logical payload bytes、`PreparedRequestCount=0`、`needs-artifact-set-checkpoint`；两个
`dsv4p` maintainer 各一次调用并产生共同 anchor 的 exact artifact，checkpoint 后为 149 events、
475915 logical payload bytes、两个成员可用且 `active-coherent`。

若要验证 derived artifact 可重建，只删除该 repo 的 derived recap store，保留 raw repo：

```bash
rm -rf gitignore/session-journal/cyber-copy-upgraded/derived/recaps/v1
```

然后用新的 `--output` / `--call-log-dir` 重新运行命令。不要用
`import-session-journal --force` 代替这一步；后者会删除并重建整个 repo。

## llm-smoke

发送一次最小 LLM 请求并写 call log，用于先检查 connection、API key 和 provider wrapper。

```bash
dotnet run --project prototypes/ChatSession.BacktestCli -- llm-smoke \
  --connections prototypes/Galatea/.atelia/galatea/connections.json \
  --connection dsv4p \
  --call-log-dir gitignore/backtest/llm-smoke-calls \
  --message "请用一句话回复：LLM smoke test ok。"
```

## 模块职责

`ChatSession.BacktestCli` 是薄 CLI 壳，主要依赖：

- `ChatSession`：legacy event source DTO、读取和投影，以及共享 history split policy。
- `SessionJournal`：raw journal replay、address/provenance、`MemoryPack`、maintainer/orchestrator substrate
  和 `DerivedRecapStore`。
- `ChatSession.Memory`：`AutobiographicalRewriteProfiles`、
  `WorldUnderstandingRewriteProfiles` 等内容层 profile。
- `Completion`：connection loader/registry、真实 provider client 和 `LoggingCompletionClient`。

以上是 current implementation dependency。目标形态中，`DerivedRecapStore`、shared epoch
planner/ledger、MemoryMaintainer orchestration 与 ArtifactSet publication 将迁入独立 DerivedMemory
程序集；CLI 作为 composition root 同时引用 SessionJournal contracts 和 concrete DerivedMemory
implementation。

推荐先导入 repo，再用 `--max-epochs 1` 分别审阅 JSONL、artifact 和 call log；确认输出后再提高 epoch
数量。真实 LLM 输出和日志应写到 `gitignore/backtest/...`，避免把大体积或敏感上下文放入源码 diff。
