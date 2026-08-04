# SessionJournal DerivedMemory Next：实现与替换计划

> **状态**：Implementation Plan
> **日期**：2026-07-29
> **目标设计**：
> [DerivedMemory Next 目标设计](derived-memory-next-target-design.md)
> **临时项目名**：`SessionJournal.DerivedMemory.Next`
> **最终项目名**：`SessionJournal.DerivedMemory`
> **兼容策略**：无旧 generation 读取、迁移或双写

## 0. 计划目的

本计划指导后续 Coding Agent 在保留 current 实现作为只读参考和行为 oracle 的同时，从一张新的
领域模型开始实现 `derived/memory/v3`，最终一次性替换并删除 current
`prototypes/SessionJournal.DerivedMemory`。

实施不是把旧项目复制后改名。每个工作包都必须形成一个可 reopen、可验证的纵向闭环，并在进入下一包
前独立审阅。

首个 product profile 是 Galatea Role-Play Agent。实现必须先完成有界、可恢复的 core
autobiography/world-understanding memory，而不是把 V3 扩张成 vector/graph retrieval、动态
registry 或通用情境分类系统。

## 1. 总体策略

### 1.1 临时 side-by-side，最终 single implementation

实施期允许：

```text
prototypes/SessionJournal.DerivedMemory/       # current，只读参考
prototypes/SessionJournal.DerivedMemory.Next/  # 新实现

tests/SessionJournal.DerivedMemory.Tests/       # current tests
tests/SessionJournal.DerivedMemory.Next.Tests/  # 新 tests
```

最终必须：

1. composition root 全部切到 Next；
2. 删除 current production project；
3. 删除已迁移或失效的 current test project；
4. 把 Next 项目与 namespace 改回无版本正式名称；
5. solution、docs、CLI 中不残留 `.Next`、`DerivedMemory2` 或 implementation selector。

Git history 已保留旧源码，不在 active tree 建 `LegacyDerivedMemory` 兼容程序集。

### 1.2 旧实现使用规则

允许从旧实现摘抄：

- path/symlink guard；
- atomic file publication；
- strict JSON/size/hash helper；
- exact RefId binding；
- raw authority gate；
- bounded history planning；
- pointer rebuild；
- crash/corruption测试场景。

禁止：

- Next project reference current DerivedMemory；
- 复制旧 persisted DTO 后逐字段兼容；
- 读取 `derived/memory/v2`；
- 复用旧 hash domain/schema id；
- 为了迁移测试增加 old/new fallback；
- 让 CLI 同时选择两套 production backend。

### 1.3 每包协作循环

每个工作包采用固定循环：

```text
explorer/re-review
  -> package-local design lock
  -> implementation + focused tests
  -> independent reviewer
  -> tail fixes
  -> package commit
```

主线程维护跨包 invariants；subagent 只领取边界明确的包，不得自行扩大 public/wire surface。

## 2. 开工前 contract inventory

在创建新项目之前，先产出一份 package-local inventory，至少覆盖：

- SessionJournal public ports 与 Next 所需 raw capabilities；
- current CLI concrete callers；
- current 108 个 DerivedMemory tests 的行为分类；
- current storage safety primitives；
- current bootstrap、backpressure、NthPrevious、raw authority contracts；
- concrete Maintainer composition；
- Galatea `cyber.md` 中的 baseline memory categories，以及长日志里“结构化区块 + recent
  history + Hint + reabsorption”的代表性窗口；
- current planner 已保留 recent suffix、但 maintainer snapshot 只读取 absorbed interval 的证据；
- current policy/topology fingerprint 如何参与 set lineage；
- 哪些现有 public contracts 不应进入 Next public surface。

旧测试按三类登记：

| 分类 | 处理 |
|---|---|
| durable behavior / safety rule | 迁移到 Next tests |
| 有价值意图、旧实现表达 | 用新实体重写 |
| 旧 wire/helper/DTO implementation detail | 删除，不迁移 |

inventory 必须特别列出下列 crash cases 的现有证据位置：

- partial success reopen；
- finalization without set；
- set without latest pointer；
- divergent latest；
- artifact/settlement/finalization corruption；
- branch rewind/divergence；
- long cold prefix bounded reads；
- absorbed interval 与 retained recent lookahead；
- per-role content budget/long-run bounded forgetting；
- prompt/model/roster revision 后的 stable domain lineage；
- bootstrap pre-append 与 observation crash/reopen。

优先检查的 current 路径：

- `prototypes/SessionJournal/SessionContextCandidateContracts.cs`
- `prototypes/SessionJournal/SessionHistoryPlanning.cs`
- `prototypes/SessionJournal/SessionJournalEngine.cs`
- `prototypes/SessionJournal.DerivedMemory/`
- `tests/SessionJournal.DerivedMemory.Tests/`
- `prototypes/SessionJournal.Cli/DerivedMemoryCommands.cs`
- `prototypes/SessionJournal.Cli/Program.cs`
- `tests/SessionJournal.Cli.Tests/ProgramDerivedMemoryCommandTests.cs`
- `prototypes/SessionJournal.Maintainers/`
- `prototypes/Galatea/.atelia/galatea/prompts/cyber.md`
- `prototypes/Galatea/.atelia/galatea/sessions/cyber-copy-upgraded/chat-session-legacy-upgrade-export.md`
  （只按关键词抽样，不顺序通读整份日志）

2026-07-29 current baseline 是 DerivedMemory tests 108/108、CLI tests 71/71、solution build
0 warning/error。N0 应先在实际 checkout 重新取得 baseline；数字变化时记录原因，不机械追求旧计数。

## 3. 工作包总览

| 包 | 目标 | 首个 durable 闭环 |
|---|---|---|
| N0 | 锁定 Shape/Rule 与旧测试 inventory | 文档和 contract checklist |
| N1 | Next scaffold 与 storage kernel | v3 manifest 可 strict write/read |
| N2 | branch scope + shared epoch stream | exact branch 上可规划 absorbed + lookahead epoch |
| N3 | single-slot job/attempt/artifact/settlement | failed attempt 后新 attempt，job id 不变 |
| N4 | static core-domain orchestration | partial success reopen 只补 missing slot |
| N5 | finalization + domain ArtifactSet publication | policy/topology revision 不切断 lineage |
| N6 | static publication view + exact NthPrevious | select/materialize + raw revalidation |
| N7 | online lifecycle | pending-first/backpressure/bootstrap 闭环 |
| N8 | validation、rebuild、facade 与 CLI | Next-only ops/E2E |
| N9 | final cutover | 删除旧项目并恢复正式名称 |

严格串行推进 N0～N9。N3/N4 的 identity 模型未通过审阅前，不开始 set 或 online composition。

## 4. N0：设计锁与验收资产

### 目标

- 本目标设计与实现计划进入版本控制；
- 明确 epoch stream、coherence domain、stable memory slot、job、attempt、artifact、settlement、
  finalization、set 与 request-local view；
- 决定 attempt ordinal/identity canonical shape；
- 固定 Galatea core domain 的两个 slots/targets 和 per-role content budget；
- 固定 absorbed interval 与 retained recent lookahead contract；
- 完成旧 tests inventory。

### 必须回答

- `EpochStreamId`、`CoherenceDomainId`、`MemorySlotId` 分别稳定到什么范围？
- 哪些 RoleSpec 字段改变会创建新 job，但不切断 domain lineage？
- job-local `InputSetId` 如何 exact join domain set 与 epoch start anchor？
- uncertain attempt 如何产生 successor？
- `Succeeded/Failed/Canceled/Uncertain` 的 outcome 字段真值表是什么？
- artifact durable 但 outcome/settlement 缺失时如何发现？
- producer 怎样同时读取 absorbed history 和 bounded retained lookahead，又不把 lookahead 声称为
  已吸收 source？
- content budget 在 artifact/settlement 哪个边界强制？
- stable content-budget failure 如何通过 same epoch/domain/input atomic `JobReprovision` 解除
  pending-first，而不冒充同一 job retry？
- 如何验证 set lineage exact key 是 stable
  `BranchRefId + CoherenceDomainId`，并允许跨 policy/prompt/model/roster revision？
- `StaticPublicationView` 与 `NthPrevious` 如何保持两轴分离？
- 删除 v3 后 active provisioning 从哪个 authority 重建？
- public facade 的最小 contract 是什么？

### 验收

- 文档中不再出现未定义的 `candidate/attempt` 双义词；
- job identity 明确排除 physical attempt；
- policy fingerprint 不再 partition set lineage；
- `NthPrevious` 只表示时间深度；
- `VariantId`、`SelectExisting`、optional omission 和动态 registry 不进入首切；
- crash matrix 与 target acceptance 一致；
- Galatea fixture 的长期 markers 不得预埋于 system prompt，必须能归因到 derived contributions；
- reviewer 能仅凭文档画出 maintenance 状态机和 Galatea context consumption path。

## 5. N1：Next scaffold 与 storage kernel

### 变更

- 新建：
  - `prototypes/SessionJournal.DerivedMemory.Next/`
  - `tests/SessionJournal.DerivedMemory.Next.Tests/`
- Next production project 只引用 `SessionJournal.csproj`；
- 建立 `derived/memory/v3/manifest.json`；
- 实现最小 internal storage kernel：
  - safe descendant/path guard；
  - strict JSON options；
  - UTF-8 size guard；
  - domain-separated canonical hash；
  - same-directory atomic create/move；
  - short repository write lock；
  - immutable point-read collision validation。

### 不做

- 不建 epoch/job/artifact 空壳大全；
- 不引用 current DerivedMemory；
- 不接 CLI。

### 测试

- architecture dependency direction；
- manifest schema/generation；
- filename/hash mismatch；
- unknown JSON member；
- oversize read/write；
- symlink/reparse ancestors；
- atomic create collision；
- incomplete temporary file 不可见。

### 验收

一个全新 SessionJournal repo 可以创建、关闭、重开 v3 workspace；malformed v3 root strict fail，
不存在 v2 fallback。

## 6. N2：Branch scope 与 shared epoch stream

### 变更

- 实现 Engine-bound exact `RefId` scope；
- 移植 planner 的算法，不移植旧 wire；
- 建立 v3 PlannerConfig、Epoch 和 rebuildable pointers；
- 继续使用 SessionJournal bounded planning、setup seed 与 raw authority APIs；
- 把 current planning window 明确分成：
  - absorbed interval `(SourceStartExclusive, SourceEndInclusive]`；
  - retained recent lookahead `(SourceEndInclusive, PlannedAtRawHead]`；
- Epoch 只描述 shared raw clock，不保存 domain-specific InputSetId 或 role topology。

### 关键规则

- config key 是 `BranchRefId + EpochStreamId`；
- epoch id 由完整 immutable plan 计算；
- planner config 只固定 scheduling/boundary/lookahead policy，不固定 RoleSpecs、prompt/model 或 view；
- planner 不运行 maintainer；
- planner 不发布 set；
- scan/candidate compute 不持 write lock；
- publish 前短锁内重读 config/latest 并线性化；
- pointer 可删后从 immutable config/epoch rebuild。

### 测试

迁移 current epoch planner 中：

- deterministic config/epoch identity；
- competing plan CAS；
- trigger/hard-limit/backpressure；
- dependency-safe boundary；
- multi-tool boundary；
- absorbed/lookahead exact partition，无 gap/overlap；
- lookahead bounded cost 与 payload reads；
- `SourceEndInclusive < PlannedAtRawHead` 时两段 history 均可 exact 重建；
- non-genesis `PreviousEpochId` exact 同 branch/stream；
- current start == previous end，且 RawStartSetups 等于 previous end authoritative setups；
- cross-epoch gap、overlap、wrong-stream parent 与 off-lineage planned head 全部拒绝；
- branch/ref isolation；
- rewind/divergence；
- setup authority；
- long-history bounded reads；
- pointer rebuild/corruption。

### 验收

在 arbitrary selected branch 上可计划一个 exact epoch，关闭 repo 后重开仍能复现 absorbed interval、
retained lookahead、setup 与 hashes；不调用 producer，不创建 job/artifact/set。N2 允许独立保存
shared epoch facts；N7 online lifecycle 在 core input set 尚未发布时不得为 successor epoch 创建
maintenance job。

## 7. N3：Single-slot job、attempt、artifact 与 settlement

这是新 generation 最关键的 identity 包。

### 变更

- 实现唯一 `MaintenanceJobId`，删除双重 job fingerprint 概念；
- job 保存 EpochId、CoherenceDomainId、domain-local InputSetId、maintenance policy identity 与
  stable RoleSpecs；branch/raw interval 通过 epoch join，不复制 shadow fields；
- 实现 stable `MemorySlotId + Target` 和 `MaxContentUtf8Bytes`；
- 不实现 `VariantId`、`SelectExisting`、optional omission 或跨 job artifact adoption；
- repository 分配 slot-local monotonic attempt ordinal/id；
- attempt intent 在 producer 前 durable；
- 实现 attempt outcome 真值表；`Uncertain` 只能由 absent outcome 推导；
- 实现按 job/slot/attempt 分区、可在 outcome 缺失时定点发现的 v3 artifact；
- artifact exact 保存 absorbed 与 lookahead provenance，但 set anchor 只推进到 absorbed end；
- 实现 per-slot settlement CAS；
- 实现窄 `JobReprovision`/`ReprovisionJob`：
  - 一个 atomic fact 同时内嵌完整 replacement job 与 old→new edge；
  - replacement exact 使用 same epoch/domain/input 和 ordered slot/target roster；
  - 只允许 execution provisioning 改变，不允许 add/remove slot；
  - old job 未 finalization；
  - single active leaf、acyclic；
- 建立 single-slot runner，producer 调用不持锁。

### 强制测试

1. 同一 epoch/domain/input set + stable RoleSpec 得到同一 JobId；
2. 改 profile/prompt/model/budget 得到新 JobId，但 `MemorySlotId` 不变；
3. attempt-1 failed，attempt-2 success，JobId 不变；
4. caller API 不接受 arbitrary AttemptId；
5. artifact identity 绑定 origin job/slot/attempt/content/absorbed/lookahead provenance；
6. success artifact/outcome/settlement exact join；
7. settlement 后重新 Run 不调用 producer；
8. crash after intent：产生 successor attempt；
9. crash after artifact：发现 artifact 并补 outcome/settlement，不调用 producer；
10. producer 可以读取 retained lookahead，但 artifact/set source end 仍是 `SourceEndInclusive`；
11. exact UTF-8 content 超限时 attempt failure、无 settlement；
12. `Succeeded + null artifact`、`Failed/Canceled + artifact` 等非法 outcome shape 全部拒绝；
13. stable budget failure 后用 revised prompt/model/budget atomic reprovision；create 前 old
    仍 active，durable 后 pending-first 只选择 embedded replacement；
14. roster/target change、replacement mismatch、equivalent provisioning、cycle、double
    reprovision、finalized-old 全部拒绝；
15. reprovision 与 finalization race 只有一个 winner，两者同时存在 strict corruption；
16. same epoch/domain/input 下未由 reprovision 连接的 competing root jobs fail-fast；
17. settlement collision 或不同 artifact winner fail-fast；
18. malformed attempt/artifact/settlement/reprovision strict fail；
19. unrelated orphan artifact 不阻断 point run，但 global validation 可解释它。

### 验收

必须用 failpoint 证明：

```text
failed/new attempt changes AttemptId
but MaintenanceJobId remains stable
and settled slot would remain settled
```

N3 reviewer 未确认 identity、lookahead provenance、outcome truth table、content budget 与
reprovision terminality 前不得进入 multi-slot。

## 8. N4：Static core-domain multi-slot orchestration

### 变更

- provision 首个 `roleplay-core` domain：
  - `autobiography` / `roleplay.first-person-autobiography`；
  - `world-understanding` / `roleplay.world-understanding`；
- 一个 job 固定 canonical ordered RoleSpecs，全部 declared slots 都必须闭合；
- N4 先以 genesis empty MemoryPack 或测试注入的 validated prior snapshot 完成 multi-slot
  orchestration；真实 non-genesis InputSet reader/materializer 在 N5 闭合；
- 一次 Prepare 只物化一份 immutable input MemoryPack、absorbed history 与 retained lookahead；
- 尚未 settlement slots 可并行 producer；
- 每个 slot 独立 attempt/settlement；
- failures 返回窄 report，不泄漏 prompt/secret。

### 测试

- alpha success + zeta failure；
- crash/reopen 后 alpha producer 调用次数仍为 1，只运行 zeta 新 attempt；
- zeta attempt id 改变但 JobId 不变；
- cancellation 保留已成功 settlement；
- concurrent settlement single winner；
- different job provisioning 不偷用旧 settlement；
- shared epoch/input/absorbed/lookahead snapshot 在所有 slots 一致；
- duplicate `MemorySlotId` 或 duplicate target provisioning fail；
- 任一 slot content 超限不发布 domain set，但不丢失其他 slot settlement。

### 验收

任一 declared core slot 缺失时没有 finalization、set 或 latest pointer；partial
artifacts/settlements 可 durable 存在但 candidate provider 不可见。首切不实现动态
add/remove/pause/retire API。

## 9. N5：Finalization 与 ArtifactSet publication

### 变更

- 实现窄 finalization；
- finalization exact 覆盖 job 的全部 declared slots；
- finalization 后禁止新 attempt/settlement；
- 实现 domain-local v3 ArtifactSet identity/file；
- set members 只保存 canonical slot/artifact refs；branch/policy/RoleSpec/attempt/content metadata
  通过 dependency joins 取得；
- immutable set file + latest pointer CAS；
- set lineage key 固定为 stable `BranchRefId + CoherenceDomainId`；
- maintenance policy/prompt/model/roster revision 可跨相邻 set 改变，不切断 lineage；
- publication 前 exact raw authority gate。
- job-local non-genesis `InputSetId` 接通 input MemoryPack materialization，并 exact 要求
  domain、branch、epoch stream、common anchor 与 AnchorSetups 全部 join；
- 完整 InputSet dependency closure 必须在 producer 调用前验证；
- 同一 domain lineage 内 `MemorySlotId -> Target` 永久绑定，remove/re-add 仍执行该约束。

### 测试

- declared slot exact closure；
- included slot 必须 exact settlement；
- ExpectedSetId 重建；
- finalization file durable、set 缺失 reopen；
- reopen 不调用任何 producer；
- set durable、pointer 缺失 rebuild；
- latest 已是 expected set；
- latest 已是同 key 后代；
- divergent latest；
- missing parent/cycle；
- old latest 在 CAS 前仍可 select；
- partial settlement/finalization 不可 select；
- branch rewind 后 publication 拒绝；
- tampered job/settlement/artifact/finalization/set 全部 strict fail；
- P1 set → prompt/model revision P2 set → `NthPrevious(1)` 回到 P1；
- add/remove slot revision 仍沿同一 domain lineage，旧 set 保持可验证；
- policy fingerprint 不出现在 latest-pointer/lineage partition key；
- non-genesis job 从 published input set 恢复 MemoryPack，并要求 exact
  domain/branch/stream/start-anchor/setup join；
- wrong branch、same-address wrong setups、wrong epoch stream、detached set 全部在 producer 前拒绝；
- 后继 job 将既有 slot 改绑 Target、或 remove 后以同 slot/new target re-add，全部拒绝。

### 验收

使用 failpoint 覆盖：

```text
last settlement
  -> finalization
  -> set file
  -> latest pointer CAS
```

每个边界重启都必须得到唯一、相同的 ExpectedSetId，且 finalization 后 producer call count 为 0。

## 10. N6：Static publication view + exact NthPrevious provider

### 变更

- 实现 `ICoherentContextCandidateSource`；
- 实现首版 `roleplay-core/full` StaticPublicationView；
- view 先选 stable coherence domain，再对其 published lineage 应用 `NthPrevious`；
- two-phase select/materialize；
- materialization exact 读取 member artifacts；
- raw-facing descriptor 不泄漏 job/attempt/store identity。

### 测试

- `n=0` latest；
- exact nth ancestor，包括跨 policy/prompt/model/roster revision；
- lineage too short；
- true empty lineage；
- missing pointer unique-tip discovery；
- missing/corrupt/cycle parent fail-fast；
- content hash/provenance；
- anchor/setup mismatch由 SessionJournal validator拒绝；
- static view 只物化 core domain 全部 slots；
- unknown/duplicate domain、duplicate target fail；
- selection 阶段不读取 member content；
- 10k cold prefix 不增加 selected anchor 前 payload reads；
- no Budgeted/Latest fallback branch。

### 验收

provider 可以替换 current provider 完成真实 SessionJournal request context materialization；
`NthPrevious` 只表示时间深度；Prepared 仍只保存最终 exact snapshots，不保存 v3 id，也不在 reopen
时重跑 view selection。

## 11. N7：Online lifecycle

### 变更

- 实现 `ISessionMemoryLifecycleCoordinator`；
- pending-first：先恢复未完成 job/finalization，再考虑规划 successor epoch；
- reprovisioned job 视为 terminal，pending-first 只跟随 unique embedded replacement leaf；
- active core domain 尚无 `CommonAnchor == PreviousEpoch.SourceEndInclusive` 的 published input set
  时，不为 successor epoch 创建 job；
- shared planner backpressure；
- strict fresh bootstrap；
- online adapter 同时组合 lifecycle、static publication view 与 candidate provider；
- Engine callback 前后 exact raw head 验证。

### 测试

- fresh setup-only pre-append bootstrap；
- first observation crash/reopen bootstrap；
- imported/non-genesis/Prepared ancestry 拒绝 bootstrap；
- hard-limit planning；
- partial job backpressure；
- partial job reopen missing-only；
- `JobReprovision` atomic create 前后 crash/reopen；
- reprovision 后 old partial settlements 不被恢复，replacement 按自身 frozen provisioning 运行；
- finalization reopen publication-only；
- absorbed/lookahead snapshot 在 maintenance 前后 raw head 改变时拒绝；
- raw head 在 callback 中改变；
- provider/lifecycle unavailable；
- online completion 不 full replay；
- deletion of derived 不破坏 Prepared reopen。

### 验收

用真实 mock maintainer + mock completion client 完成：

```text
raw observation
  -> plan epoch
  -> core multi-slot maintenance
  -> publish domain set
  -> static view + NthPrevious
  -> prepare/call completion
  -> crash/reopen
```

另外使用**可归因**的 Galatea 代表性 fixture 验证一次多 epoch maintenance → reopen →
materialize：

- identity/relationship/plan/open-topic markers 只出现在 absorbed raw interval，不得预埋在
  system prompt、seed block 或 retained suffix；
- recent-scene marker 只出现在 retained/raw suffix；
- 先断言 autobiography/world-understanding artifacts 与 contributions 的 marker/provenance；
- 再断言 materialized request 中长期 markers 来自 contributions，recent marker 来自 raw suffix；
- deterministic fixture/recorded producer response 负责 transport 与归因 gate；
- real concrete maintainer/model 另做非 deterministic eval，检查 identity flattening、关系漂移和
  open-thread 遗忘，不把文学质量伪装成普通 unit test。

## 12. N8：Strict validation、ops facade 与 CLI

### 变更

- 完成 repository-wide strict validation；
- 实现 planner/latest index rebuild；
- 建立窄 public facade/report；
- facade 显式报告 epoch stream、coherence domain、stable memory slot、policy revision 与 static view；
- CLI composition切到 Next：
  - configure/plan/list；
  - run/resume maintenance；
  - inspect/reprovision failed frozen job；
  - list/validate/rebuild；
  - run-online-turn；
- CLI 删除 job-level `--attempt-id`；
- CLI 不直接构造 persisted DTO。

### 测试

- global/exact branch validation；
- all dependency joins；
- detached artifact解释；
- unexpected file、oversize、symlink；
- CLI restart uses same JobId and new role attempt；
- CLI partial failure/resume不重跑 settled role；
- CLI policy/prompt/model revision 不创建新 domain lineage；
- CLI content budget violation 不发布 set；
- CLI reprovision exact same epoch/domain/input/slot roster，报告 replacement 可能重跑的 settled slots；
- CLI finalization/set failpoints；
- Markdown/JSON report 不泄漏 prompt/content/secret；
- arbitrary `--branch`；
- real E2E repository。

### 验收

CLI 不引用 current DerivedMemory project；CLI tests 对 Next 完整通过。current CLI命令可直接 breaking
调整，不保留 old/new flags。

## 13. N9：最终切换与旧版删除

### 切换步骤

1. 确认 Next 的 target acceptance 全部通过；
2. solution/CLI/Host project references 只指向 Next；
3. 删除：
   - `prototypes/SessionJournal.DerivedMemory/`
   - 已迁移/失效的 `tests/SessionJournal.DerivedMemory.Tests/`
4. 将 Next project/tests 改回正式 basename、assembly、namespace；
5. 更新 `Atelia.sln` 与 architecture boundary tests；
6. 全仓更新 current docs/navigation；
7. 删除 active docs 中 old root/schema/current 行为叙事，历史 `done/` 文档保留 supersession；
8. 不复制或读取 `derived/memory/v2`；
9. 清理 temporary `.Next` 名称和 generation selector。

### 结构扫描

至少执行：

```text
rg "SessionJournal\.DerivedMemory\.Next|SessionJournal\.DerivedMemory2"
rg "derived/memory/v2|derived-memory-transaction\.v2"
rg "JobFingerprint|TransactionId|CandidateId|--attempt-id"
rg "VariantId|SelectExisting|OmittedOptional"
rg "PolicyFingerprint.*lineage|latest.*PolicyFingerprint"
rg "ProjectReference.*SessionJournal.DerivedMemory"
```

对 target 文档的明确非目标和历史 `docs/SessionJournal/done/` 中的匹配只做分类，不机械改写。

### 数据策略

- 无 migration；
- 无 compatibility read；
- 无双写；
- existing experimental derived data 直接删除；
- 从 raw SessionJournal 加 Agent Host/raw setup/其他 durable configuration authority 重新 provision
  并重建 v3；
- 已 Prepared request 使用 raw inline snapshots恢复，不依赖重建完成；
- 新 online request 在 v3 usable set 建立前显式 not-ready/backpressure。

### 最终验证

```bash
dotnet test tests/SessionJournal.Tests/SessionJournal.Tests.csproj \
  -m:1 -nr:false --no-restore

dotnet test tests/SessionJournal.DerivedMemory.Tests/SessionJournal.DerivedMemory.Tests.csproj \
  -m:1 -nr:false --no-restore

dotnet test tests/SessionJournal.Cli.Tests/SessionJournal.Cli.Tests.csproj \
  -m:1 -nr:false --no-restore

dotnet test tests/SessionJournal.Offline.Tests/SessionJournal.Offline.Tests.csproj \
  -m:1 -nr:false --no-restore

dotnet build Atelia.sln -m:1 -nr:false --no-restore
git diff --check
git status --short
```

## 14. 跨包不变量

所有实现包始终遵守：

- raw facts authoritative；
- derived 可删可重建；
- exact selected branch/ref；
- stable epoch stream 上的 shared immutable epoch；
- absorbed interval 与 retained recent lookahead exact 分离；
- stable `MemorySlotId` 不随 prompt/model/policy revision 改变；
- physical attempt 不属于 stable job identity；
- settled slot 不重跑；
- reprovision 只用于显式修改不可完成 frozen execution provisioning；same job recovery 不得借此逃避
  settlement reuse；
- finalization 后不运行 producer；
- domain slot closure 前不发布；
- old set until atomic publication；
- set lineage key 只使用 stable branch + coherence domain，policy/topology revision 不切断 lineage；
- `NthPrevious` 只表示时间深度；
- request projection 不改变 durable publication；
- content budget 在 settlement 前强制，遗忘不靠破坏 immutable history；
- outcome status/field truth table strict；
- Prepared exact reopen 不打开 DerivedMemory；
- no full replay online fallback；
- no old-generation compatibility；
- no permanent Next/DerivedMemory2 fork。

任何实现选择若破坏其中一条，必须回到 Shape/Rule 层修改本文并重新审阅，不能用局部兼容代码绕过。

## 15. 完成定义

整个计划完成不是“Next 可以编译”，而是：

- 新领域模型完整覆盖 epoch stream → job → attempts → settlements → finalization → domain sets →
  static view + NthPrevious selection；
- Galatea core slots、reabsorption lookahead、bounded forgetting 和 representative trajectory acceptance
  已完成；
- non-genesis InputSet full authority join、cross-epoch continuity、job reprovision 与 outcome truth
  table 已完成；
- policy/prompt/model/roster revision continuity tests 已完成；
- current 关键行为测试已迁移，旧 implementation-detail tests 已删除；
- CLI/online E2E 使用新实现；
- 旧 production/test projects 已删除；
- 正式 assembly/namespace 无版本尾缀；
- active tree 无 old/new selector、compatibility reader 或双写；
- full solution 和 target acceptance 通过；
- 文档只把 v3 描述为 current，把旧 generation 放在 Git history/明确的 historical docs 中。
