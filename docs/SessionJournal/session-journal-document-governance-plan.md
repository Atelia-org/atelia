# SessionJournal 文档治理计划

状态：Active meta plan / DG0

Baseline：`bac6a6d627ad4cce1c496cccd5f56dd2c5c032eb`

适用范围：`docs/SessionJournal/`、SessionJournal family component README，以及 repository root 的文档路由

本文定义 SessionJournal 文档的发现、角色、生命周期、关闭与渐进整理规则。它先建立低维护的逻辑治理，
再允许后续工作包小批修正文档；它不是一次全量重写或目录迁移计划。

## 1. Purpose

SessionJournal 已形成 raw core、DerivedRecap Store、Planner、Maintainers、CLI/Galatea composition、
设计、实施计划、审阅与验收记录等多层文档。当前主要问题不是缺少内容，而是 Coding Agent 难以快速判断
首读入口、窄 claim owner、current/target/historical 边界，以及 review close 后仍须保留的负面证据。

本计划的目标是把这些问题收敛为一套可观察、可回滚、可逐包实施的 discovery governance：

1. 从 repository root 到 SessionJournal task entry 不超过两层路由；
2. 默认用两到四份、约 8k～12k tokens 的文档建立初始上下文；
3. role/lifecycle 正交表达，每个窄 claim 有明确 owner；
4. review close 保留负面证据，自动化只验证可机械证明的结构事实。

### 1.1 Non-authority clause

本计划、未来 router、metadata 和 discovery ledger 只负责**发现与阅读顺序**，不取代：

- current code 与 public API；
- focused tests、goldens、negative fixtures 与 mutation/crash tests；
- raw events 与 selected `RefId` Parent lineage；
- A/B/C wire fixtures、schema、codec 与 canonical bytes；
- exact-head、Prepared/Resume/Restore 与 typed recovery evidence；
- filesystem path safety、atomic publication、fsync/rename 与真实数据 acceptance evidence。

Router 中把某文档列为 canonical entry，不会把该文档升级为 raw、wire 或 recovery authority。
Metadata 与实现证据冲突时，必须按 §6 的窄 claim ownership 重新核对，不得以“index 如此写”覆盖证据。

## 2. Baseline inventory

以下 inventory 是 baseline checkout 的**时间点证据**，不是持续自动更新的真相：

| 范围 | tracked Markdown | 行数/说明 |
|---|---:|---|
| `docs/SessionJournal/` 全部 tracked 文档 | 42 | 合计 18,847 行 |
| root level | 23 | current、active、closed、mixed historical 并存 |
| `done/` | 17 | completion records 与已完成 slice 材料 |
| `superseded/` | 2 | 已被替代的 DerivedMemory V3 candidate |
| 用户提供的 untracked review report | 1 | 不计入 42；未经明确纳入前保持 untouched |

Baseline full commit：`bac6a6d627ad4cce1c496cccd5f56dd2c5c032eb`。

后续 checkout 必须重新生成 inventory。不得把 42、18,847、23、17、2 或当前文件清单当作永久断言，
也不得在 router 中复制这份完整 inventory。详细逐文件 ledger 只允许作为一次性迁移工件存在，完成迁移后不成为第二份
长期人工台账。

## 3. Experience constraints and v1 posture

过去围绕双向引用、反向链接与变更检查的尝试没有形成低成本的稳定工作流。旧 DocGraph 方向要求维护
`produce` / `produce_by` 等关系并构建文档图；这种模式会让同一关系拥有多个可漂移 writer，也会把解析器、schema、
修复器和生成器本身变成新的长期子系统。

因此 v1 低维护优先于图谱完备：观察真实 routing 失败后再增加规则，每包可单独 revert；不恢复 DocGraph，
不要求回链，不先大搬家，不强制 YAML frontmatter，也不并存 root/domain/per-document 三套 inventory。
目录、日期、标题或 `status` 字样均不单独构成 authority proof。

V1 只建立一条正向发现链：

```text
repository README
  -> docs/SessionJournal/README.md
     -> 2～4 个 task-specific current entries
        -> 必要时由 read_when / safety trigger 继续深入 code、tests 与 evidence
```

## 4. Two-axis classification

每份被治理的文档用两个逻辑轴理解：`doc_role` 回答“它负责什么”，`lifecycle` 回答“它现在处于什么阶段”。
两轴不能互相推导。

### 4.1 `doc_role`

| 值 | 含义 | 典型 claim |
|---|---|---|
| `canonical-contract` | 当前支持面、wire 或明确 contract snapshot | Beta-supported roles、schema/version、已冻结边界 |
| `concept` | current vocabulary、概念边界与核心不变量 | Recap、Memory、authority ownership |
| `component-guide` | 贴近实现的组件用法与代码地图 | Host 使用、Store/Planner/Maintainers entry shape |
| `target-design` | Shape/Rule intent 与目标结构 | 尚未完全由 current code 证明的目标设计 |
| `plan` | 工作分解、顺序、范围与 gates | 实施或审阅如何推进 |
| `review` | findings、裁决与 residual risks | review report、candidate ledger |
| `runbook` | 可重复 operator workflow | staging、real-data acceptance、诊断步骤 |
| `evidence` | 验证结果、fixture、命令与观测 | test report、calibration、crash evidence |
| `completion-record` | 已完成工作包与 commit/evidence map | done slice、closed implementation record |
| `historical` | 解释旧设计、旧 wire 或演进背景 | frozen baseline、superseded candidate |

`doc_role` 不是全局唯一标签。一个 mixed document 可能承担多个 role，但 router 只应为任务暴露其必要的窄 claim，
后续 DG2～DG4 再判断是否应拆分。不得为了追求单标签而机械切文件。

### 4.2 `lifecycle`

| 值 | 含义 |
|---|---|
| `current` | 当前 claim 与 checkout 对齐，可作为该窄 claim 的首选入口 |
| `active` | 正在推进或仍有明确未关闭工作，不等于 current contract |
| `closed` | 工作或 review 已裁决；记录可继续保留，但不再承接新执行状态 |
| `historical` | 只描述过去事实或演进背景 |
| `superseded` | 同一窄 claim 已被明确 successor 替代 |
| `frozen` | 保留为稳定基线或审计证据，不随 current 实现继续更新 |

目录只提供弱提示：

- `done/` 通常包含 `closed` / `completion-record`；
- `superseded/` 通常包含 `superseded` / `historical`；
- root level 既可能是 `current`，也可能是 `historical`、`frozen` 或 mixed；
- component README 可能是 current guide，但仍需用 code/tests 核对具体 API。

任何目录都不自动授予或取消 authority。

## 5. V1 discovery ledger and routing shape

V1 的唯一人工 discovery ledger 是未来的 `docs/SessionJournal/README.md`。

它按任务列出首选入口和窄 claim，提供 `read_when`，区分 current/target/active/closed/historical，
并指向 component README、code/tests、evidence 与 safety escalation。

它不复制 42-file inventory，不收集 title/owner/date/全部链接，不生成 backlink，不保存 run-specific counts/HEAD，
不替代 component code map，也不为正文自动签发 canonical 身份。

Repository root 的未来 `README.md` 只单向路由到 SessionJournal ledger，不展开 SessionJournal 子文档。
Store、Planner、Maintainers、Core、CLI 与 Galatea 的 component README 继续持有贴近实现的代码地图和推荐 API；
domain ledger 不复制这些细节。

新增一份普通文档时，默认最多更新一个人工 discovery ledger entry。只有它取代既有窄 claim 时，才同时执行
§8 review close protocol。

## 6. Authority precedence and narrow claim ownership

不存在一条可以覆盖所有问题的“文档优先级”。必须先确定 claim，再选择 owner 与证据：

| Claim | 首选 owner / evidence | Router 或设计文档的角色 |
|---|---|---|
| raw event fact、顺序与 lineage | raw events + selected `RefId` Parent lineage | 解释，不取代 |
| wire schema、codec、accepted language、canonical bytes | current codec/schema + goldens/negative fixtures + canonical-byte tests | 提供入口和冻结说明 |
| Beta-supported public role | current code/tests + Beta contract snapshot | snapshot 汇总支持承诺 |
| Core/Store/Planner/Maintainers current API 使用 | component README + current code/tests | domain ledger 路由到组件 |
| current vocabulary 与 ownership | canonical concepts + code/contract cross-check | concept 文档拥有术语 claim |
| target Shape/Rule | target design | 不反向覆盖 current implementation |
| 实施完成状态 | implementation record + commit/test/acceptance evidence | plan 中的 checkbox 不是单独证明 |
| operator workflow | runbook + current CLI behavior + acceptance evidence | runbook 不改变 durable contract |
| review decision | closing review / candidate ledger | finding 原文仍可作为 retained evidence |

### 6.1 Narrow ownership rule

允许声明诸如“EADR V4 current vocabulary”“Beta-supported public roles”“Prepared v5 canonical bytes”这样的窄 claim。
禁止声明 broad `canonical_for: SessionJournal`、`canonical_for: recovery` 或“本文覆盖所有 current contract”。

一个文档可以拥有多个明确列举的窄 claim；每个 claim 都必须指出 owner、证据核对入口、冲突规则，
以及 lifecycle 改变时的 successor 或 closing record。

### 6.2 Conflict rule

发现冲突时必须人工裁决：

1. 写出冲突的确切 claim，并读取对应 code/tests/fixture/raw/recovery evidence；
2. 判断是实现缺陷、文档 drift、target 尚未落地，还是 intentional proof redundancy；
3. 修正 owner/文档并保留 closing/evidence link；若改变 contract，则按新 candidate 重新 gate。

不得用以下规则自动裁决：

- 日期更新者胜；
- root-level 文件胜；
- `README.md` 胜；
- 路径更短者胜；
- frontmatter 写了 `current` 或 `canonical` 者胜；
- implementation plan 标记“完成”即覆盖 code/tests。

## 7. Routing budget and safety escalation

### 7.1 Default routing budget

Coding Agent 接到普通 SessionJournal 任务时，domain ledger 应给出：

- 2～4 份初始文档；
- 合计约 8k～12k tokens；
- 一条 current contract / component guide 入口；
- 仅在需要理解 intent 时追加 target design；
- 仅在需要复核历史裁决或 residual risk 时追加 review/evidence。

`read_when` 只写正向提示，例如：

- `read_when: 修改 Prepared/Resume/Restore`；
- `read_when: 调整 DerivedRecap publication 或 strict ordinal`；
- `read_when: 运行 Galatea staging acceptance`。

不维护反向 `used_by` / `read_by` / `absorbed_by` 列表。一个入口没有被 router 列出，不代表它无价值或可删除。

### 7.2 Safety escalation triggers

出现以下任一主题时，初始 routing budget 立即失效，必须按 claim 扩展读取并核对 code/tests：

- wire、schema、version、codec、accepted/rejected language 或 canonical bytes；
- Prepared、Started、Resume、Restore、failed turn、tool continuation；
- migration、legacy import/export、real-data replay；
- filesystem path、symlink/reparse、lock、atomic publication、fsync/rename、crash consistency；
- authority token、descriptor、writer lifetime、exact-head fence；
- `RefId`、EventAddress、Parent lineage、bounded prefix/proof；
- raw/derived ownership、strict ordinal、missing-only repair、corruption handling。

触发后必须定位 current code owner 与 focused tests/fixture/golden/acceptance evidence，核对 target 没有被当作
checkout 事实；若接受 contract 变化，建立独立 candidate 与验证 gate。

## 8. Review close protocol

Review、plan 或 design 关闭时，不使用语义含混且可能暗示“内容已被完全吸收、旧证据可删除”的 `absorbed_by`。

分别使用以下关系：

| 关系 | 使用条件 | 不表示什么 |
|---|---|---|
| `superseded_by` | successor 确实接管同一窄 claim | 不表示旧证据可删除 |
| decision / closing record | findings 已逐条裁决并记录 Adopt/Reject/Retain/Defer | 不表示所有建议已实施 |
| `evidence_retained_at` | negative fixture、crash report、real-data acceptance 或 residual risk 被保留在稳定位置 | 不授予 evidence contract authority |

Close 前必须检查并保留：

- rejected findings 及否决理由；
- negative fixtures、mutation cases 与 corruption examples；
- crash/recovery、atomicity 与 path-safety evidence；
- residual risks、accepted risks 与尚未验证的 host/platform assumptions；
- commit map、candidate baseline 与实际验证边界；
- 被证伪 finding 的原始教训，避免未来重复同一审阅错误。

只有“同一窄 claim 已由 successor 明确接管”才能标 `superseded_by`。Review closed 通常只需要 closing record，
不应为了清爽而把 finding 原文改写成成功叙事。

## 9. Tooling scope

### 9.1 V1 checks

第一阶段只自动验证可机械证明的 router 结构事实：

1. 新 root/domain router 中的 repo-relative target 存在且大小写正确；
2. 一个窄 canonical role 在同一 ledger 中至多有一个 current entry；
3. router path 解析后仍位于 repository boundary 内；
4. `git diff --check` 通过。

### 9.2 Later checks

在 DG1～DG4 稳定后，才考虑 SessionJournal-scoped local link scan：

- 只扫描 `docs/SessionJournal/` 与显式纳入的 component README；
- 先报告、观察噪声，再决定是否 gate；
- historical monorepo path、generated anchor 与有意保留的 external reference 需要明确策略；
- checker 必须可独立运行，不要求恢复 DocGraph 数据模型。

### 9.3 Explicit non-goals

自动化不得判断正文真伪或自动选 authority，不得立即设全仓 hard gate、访问网络、强制 frontmatter、
注入 backlink/修改文档，或把未出现在 ledger 的文件报告为可删除 orphan。

## 10. Target logical layout

长期逻辑方向如下，但 v1 不要求物理目录立即匹配：

```text
docs/SessionJournal/
  README.md                    # 单一人工 discovery ledger
  current-contract/            # contract snapshot / canonical concepts
  target-design/               # Shape/Rule intent
  plans/                       # active implementation/review plans
  reviews/                     # closing decisions and findings
  runbooks/                    # operator workflows
  evidence/                    # bounded retained evidence
  historical/
    done/                      # completion records
    superseded/                # explicit successor exists
    frozen/                    # retained baseline/evidence
```

这只是长期 logical layout。物理移动永远是最后一步，因为它会同时影响 Markdown links、AGENTS/CLAUDE、
外部会话路径记忆、scripts/tests/runbooks、commit references 与 review evidence 可追溯性。

在 role、lifecycle、claim ownership 和 successor 未先明确前，不得按文件名或当前目录机械搬迁。

## 11. Work packages

每包必须独立 review、validation 与 commit；后包不能借“治理整理”顺带改变 product contract。

### DG0 — Active meta plan

- **Intent**：固化本文的 purpose、non-authority boundary、两轴模型、routing budget、close protocol、tooling scope 与工作包。
- **Out of scope**：root/domain router、per-file metadata、现有文档重写、checker、物理移动。
- **Validation**：本文 relative links（若有）解析；`git diff --check`；确认 dirty status 只有本文与既有用户 untracked report。
- **Done**：本文以独立 commit 落地，且没有 stage/修改用户 untracked report。

### DG1 — Two-level router

- **Intent**：新增 repository root `README.md` 与 `docs/SessionJournal/README.md`，建立 root → domain → task entry 的单向路由。
- **Out of scope**：完整 42-file inventory、frontmatter、现有文档状态重写、checker、目录移动。
- **Validation**：所有新增 relative links 存在并留在 repo 内；canonical role 无重复 current entry；cold-agent 可在两跳内找到 current contract/component guide。
- **Done**：两份 router 是唯一变更；domain ledger 给每个核心任务 2～4 份、约 8k～12k token 的默认入口。

### DG2 — Pilot current authority and closing

- **Intent**：用小样本验证 role/lifecycle/claim ownership/close protocol，范围限于 EADR concepts、Beta snapshot、EADR target + implementation、最近一次 tracked review，以及 Store/Planner README。
- **Out of scope**：Core/Recovery mixed docs、配置与 Host 全面治理、用户 untracked report、物理移动。
- **Validation**：逐个窄 claim 与 code/tests/fixtures 对照；target 不覆盖 current；review close 保留 rejected finding、evidence 与 residual risk；router 更新只有一处。
- **Done**：pilot 文档的 current/target/closed 边界无冲突，且没有 broad `canonical_for` 或双向 backlink。

### DG3 — Core and Recovery mixed documents

- **Intent**：裁决 raw Core、tail recovery、configuration access 与 architecture roadmap 中混合 current/historical 内容的路由和窄 ownership。
- **Out of scope**：改变 A-level wire、删除 proof redundancy、按章节机械拆文件、未经验证的历史搬迁。
- **Validation**：所有 Prepared/Resume/Restore、Parent lineage、bounded proof claim 核对 current code、focused tests 与 fixtures；historical 叙事不再成为默认入口。
- **Done**：Core/Recovery task route 清晰；mixed document 的安全升级点和 historical 边界显式；无 product contract 变化，或变化已另建 candidate gate。

### DG4 — DerivedRecap, Config and Host mixed documents

- **Intent**：治理 cadence、HistoryLoad、repo config、Host integration、Galatea plan/runbook 与 DerivedRecap component 文档间的 current/target/evidence 边界。
- **Out of scope**：统一 Store/Planner/Maintainers ownership、删除 frozen execution proof、将 Galatea plan 冒充 operator runbook。
- **Validation**：active config 与 frozen plan 分离；Store/Planner/Maintainers ownership 不漂移；Galatea plan 与 repeatable runbook 的 claim 分开；真实 acceptance evidence 可追踪。
- **Done**：DerivedRecap/Config/Host 每类任务都有窄入口和 safety triggers，旧 cadence/config 叙事不会覆盖 current implementation。

### DG5 — SessionJournal-scoped checker

- **Intent**：在前述信息结构稳定后实现 scoped、read-only 的 local link/role/path checker。
- **Out of scope**：全仓 hard gate、网络 URL、frontmatter schema、内容真伪判断、自动修复与 backlink 注入。
- **Validation**：positive/negative fixtures覆盖 missing target、case mismatch、repo escape 与 duplicate current role；现有 historical 噪声有显式处置；checker 不修改文件。
- **Done**：命令可重复运行，默认只扫描已治理 scope；先 report-only，观察稳定后才单独决定是否 gate。

### DG6 — Small-batch historical moves and link repair

- **Intent**：仅在 logical classification 稳定后，小批移动确定的 historical/superseded/closed 文档并同时修复链接。
- **Out of scope**：一次性大搬家、按文件名猜 lifecycle、移动仍承载 current/mixed safety claim 的文档。
- **Validation**：每批枚举 inbound links、scripts/tests references 与 external operational references；移动前后 scoped checker通过；`git diff --check`；独立人工 review。
- **Done**：每批可独立 revert，router/links 无断裂，commit message明确 old→new mapping，且没有丢失 retained evidence。

## 12. High-risk documents: no mechanical moves

以下类别包含 current、historical、target、evidence 或 safety claim 的混合，禁止仅凭标题、日期或目录机械移动：

- Tail-only execution recovery design；
- Session configuration access notes；
- event-sourced architecture roadmap；
- EADR V4 implementation plan；
- HistoryLoad、repo config 与 Host integration mixed docs；
- Galatea implementation/cutover plan 与 staging acceptance runbook。

处理前必须先裁决 current 与 historical 窄 claim、retained negative/crash/recovery evidence、真实 successor，
并枚举 inbound links 与依赖稳定路径的 operator workflow。

用户提供的 untracked
`docs/SessionJournal/session-journal-derived-recap-design-review-2026-08.md`
在另行明确纳入前必须保持 untouched：不修改、不移动、不添加 metadata、不 stage、不 commit，也不把它计入 tracked ledger。

## 13. Success criteria

本治理计划成功需要同时满足：

1. 新 Agent 从 root 两跳内到达 SessionJournal domain ledger 和 task entry；
2. 普通任务默认只需 2～4 份、约 8k～12k tokens 文档建立上下文；
3. safety trigger 能可靠把 Agent 导向 code/tests/fixtures，而不是被 routing budget 截断；
4. current/target/closed/historical/superseded/frozen 不再由目录隐式推断；
5. 每个 canonical entry 只拥有明确窄 claim，没有 broad `canonical_for`；
6. 新文档通常只需更新一个人工 ledger，不产生强制 backlink；
7. review close 保留 rejected findings、negative fixtures、crash evidence 与 residual risks；
8. checker 只验证结构事实，误报与维护成本低于它阻止的 drift；
9. 每个工作包可独立 review、commit 与 revert；
10. 文档治理没有改变 raw/wire/recovery authority，也没有把 metadata 变成第二真源。

## 14. Stop and rollback conditions

出现以下任一条件，当前工作包必须停止扩展；先回到最近独立 commit，裁决后再继续：

- 需要同时维护 root inventory、domain inventory 和 per-document metadata 才能保持一致；
- 自动化开始推断内容真伪/authority/lifecycle，或同一窄 claim 出现两个无法裁决的 current owner；
- 为满足 router/checker 必须大批修改 unrelated historical documents；
- path move 的 inbound-link blast radius 未能完整枚举；
- 变更触及 wire/schema/codec/canonical bytes 或 recovery semantics，却没有独立 candidate gate；
- checker 持续高噪声，或新规则要求 backlink/frontmatter/DocGraph；
- 文档整理可能删除 rejected finding、negative fixture、crash evidence 或 residual risk；
- 用户 untracked report 将被修改、stage、移动或隐式纳入；
- 不能证明本包可独立 revert 而不破坏后续包。

Rollback 优先删除或 revert 本包新增的 router/metadata/checker，不保留 compatibility layer。
若逻辑分类有效但物理移动失败，应保留逻辑治理、撤回移动；若 checker 噪声过高，应退回 report-only 或移除 checker，
而不是修改历史证据迎合工具。

## 15. Immediate next step

DG0 合入后，下一包只执行 DG1：建立 root 与 SessionJournal 两级 router。
不要在 DG1 顺带引入 frontmatter、全量 ledger、checker、现有文档重写或目录移动。
详细逐文件 inventory 与迁移判断留给后续一次性工件；它们不得成为新的长期人工 truth source。
