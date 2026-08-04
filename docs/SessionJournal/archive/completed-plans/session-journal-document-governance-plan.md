# SessionJournal 文档治理计划

状态：Completed / Archived

Baseline：`bac6a6d627ad4cce1c496cccd5f56dd2c5c032eb`

适用范围：`docs/SessionJournal/` 与 SessionJournal family component README

关闭说明：DG0～DG6及后续 archive-first 整理已完成；current规则与入口现由root router、
current code map和文档checker承担。本文的baseline inventory只保留为cut-time evidence，不随HEAD续期。

本文定义 SessionJournal 文档的发现、角色、生命周期、关闭与渐进整理规则。它先建立低维护的逻辑治理，
再允许后续工作包小批修正文档；它不是一次全量重写或目录迁移计划。

## 1. Purpose

SessionJournal 已形成 raw core、DerivedRecap Store、Planner、Maintainers、CLI/Galatea composition、
设计、实施计划、审阅与验收记录等多层文档。当前主要问题不是缺少内容，而是 Coding Agent 难以快速判断
首读入口、窄 claim owner、current/target/historical 边界，以及 review close 后仍须保留的负面证据。

本计划的目标是把这些问题收敛为一套可观察、可回滚、可逐包实施的 discovery governance：

1. 先把候选 current claim 与 code/tests/fixtures 对证，再允许 router 发布该 claim；
2. 默认用两到四份、约 8k～12k tokens 的文档建立初始上下文；
3. role/lifecycle 正交表达，每个窄 claim 有明确 owner；
4. review close 保留负面证据，自动化只验证可机械证明的结构事实。

### 1.1 Non-authority clause

本计划、未来 router、metadata 和 discovery ledger 只负责**发现与阅读顺序**。它们不取代 current code/API，
tests/goldens/negative/crash fixtures，raw events + selected `RefId` Parent lineage，A/B/C wire codec/canonical bytes，
Prepared/Resume/Restore recovery evidence，或 path safety/atomic publication/fsync/real-data acceptance evidence。

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

V1 只建立一条经过事实核验的正向发现链：

```text
pilot claim registry
  -> against code / tests / fixtures verification
     -> docs/SessionJournal/README.md
        -> 2～4 个 task-specific current entries
           -> 必要时由 read_when / safety trigger 继续深入 code、tests 与 evidence
```

在 pilot 验证完成前，禁止发布自称 current/canonical 的 domain router entry。Repository root README 属于
repo-wide 信息架构，不在本 SessionJournal 计划内创建或修改；未来是否增加 root router 必须另做跨领域决策。

## 4. Two-axis classification

两个逻辑轴绑定在 **ledger claim entry**，而不是整篇文档：`doc_role` 回答该 claim 的用途，`lifecycle`
回答该 claim 当前所处阶段。一个 mixed document 可以对应多个 entry；两轴不能互相推导，也不能用某个 entry 的状态给
整篇文档背书。

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

`doc_role` 不唯一。一个 mixed document 可能承担多个 role，但 router 只应暴露任务所需的窄 claim；
后续 DG3～DG4 再判断是否应拆分。不得为了追求单标签而机械切文件。

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

### 4.3 Claim entry schema

Pilot 与后续 ledger entry 使用以下逻辑 schema；v1 可用 Markdown 表格或列表表达，不因此引入 frontmatter：

```text
claim_id: <stable narrow identifier>
document: <repo-relative path>
doc_role: <§4.1 value>
lifecycle: <§4.2 value>
owner: <the document/component responsible for this claim>
canonical_for: <human-readable narrow claim, never a whole subsystem>
verified_against:                         # required for current implementation/API/wire claims
  full_commit: <exact full commit>
  scope: <code/tests/fixtures actually checked>
  kind: implementation | api | wire | recovery | operational-evidence
  evidence: <portable pointers sufficient to repeat the check>
read_when: <positive task trigger>
superseded_by: <claim_id>                 # only when supersession actually occurred
decision_or_closing_record: <path/ref>    # only when a decision/close occurred
evidence_retained_at: <path/ref>          # only when retained evidence exists elsewhere
```

`claim_id` 在窄 claim 的文档路径或 owner 变化后仍保持稳定；不同语义不能为了复用 ID 而合并。
`current` implementation/API/wire/recovery claim 没有完整 `verified_against` 就不能进入 router。纯 normative intent
可以不声称 implementation verification，但必须使用 `target-design` 等准确 role/lifecycle，且不得暗示 checkout 已实现。

`verified_against.full_commit` 记录实际核验的 exact checkout。代码变化后必须先重跑声明的 scope，再更新 SHA；禁止
机械 bump SHA、复制旧 test totals 或只因文档未报错就续期 verification。`superseded_by`、
`decision_or_closing_record`、`evidence_retained_at` 都是事件型字段，仅在关系确已发生时填写。

## 5. V1 discovery ledger and routing shape

DG1 先建立并关闭小范围 pilot claim registry；DG2 才把核验通过的 entry 发布到
`docs/SessionJournal/README.md`。发布后，后者是 v1 唯一的 active 人工 discovery ledger，pilot report 只作为
closed decision/evidence record，不与 domain ledger 竞争 current ownership。

它按任务列出首选入口和窄 claim，提供 `read_when`，区分 current/target/active/closed/historical，
并指向 component README、code/tests、evidence 与 safety escalation。

它不复制 42-file inventory，不收集 title/owner/date/全部链接，不生成 backlink，不保存 run-specific counts/HEAD，
不替代 component code map，也不为正文自动签发 canonical 身份。

Store、Planner、Maintainers、Core、CLI 与 Galatea 的 component README 继续持有贴近实现的代码地图和推荐 API；
domain ledger 不复制这些细节。Repository root router 推迟到未来 repo-wide 决策，不属于 DG1～DG6。

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

一个文档可以拥有多个明确列举的窄 claim entry。每个 entry 复用下节的全局 conflict rule，不重复维护一份规则；
successor、closing record 或 retained-evidence relation 仅在实际发生时填写。

### 6.2 Conflict rule

发现冲突时必须人工裁决：

1. 写出冲突的确切 claim，并读取对应 code/tests/fixture/raw/recovery evidence；
2. 判断是实现缺陷、文档 drift、target 尚未落地，还是 intentional proof redundancy；
3. 修正 owner/文档并保留 closing/evidence link；若改变 contract，则按新 candidate 重新 gate。

不得用日期、root-level、`README.md`、短路径、frontmatter 的 `current/canonical`，或 implementation plan 的
“完成”标记自动裁决。

## 7. Routing budget and safety escalation

### 7.1 Default routing budget

普通 SessionJournal 任务应获得 2～4 份、约 8k～12k tokens 的初始文档，其中至少一条 current
contract/component guide；只在理解 intent 时追加 target design，在复核历史裁决或 residual risk 时追加
review/evidence。`read_when` 只写正向提示，例如修改 Prepared/Resume/Restore、调整 publication/strict ordinal，
或运行 Galatea staging acceptance。

不维护反向 `used_by` / `read_by` / `absorbed_by` 列表。一个入口没有被 router 列出，不代表它无价值或可删除。

### 7.2 Safety escalation triggers

遇到 wire/schema/codec/canonical bytes，Prepared/Resume/Restore/tool continuation，migration/import/replay，
path/lock/fsync/crash，authority token/exact-head，`RefId`/Parent lineage/bounded proof，或 derived
strict-ordinal/repair/corruption 等主题时，routing budget 立即失效，必须按 claim 扩展读取并核对 code/tests。

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

每个 closing claim 至少保留一份 **tracked、content-free 的 summary/manifest**，并包含足以复核结论的 portable
pointers，例如 exact commit、test name/command、tracked fixture/golden、artifact hash、bounded acceptance result 或
reproduction entry。被 `.gitignore` 忽略的大日志、机器本地路径或含 secret 的材料可以作为补充，但不能是唯一证据。
不得 track secret、credential、provider request/response 正文或用户私密 session 内容；需要证明这些材料存在时，只保留
content-free manifest/hash、权限边界和可重复生成步骤。

Close 前必须保留 rejected findings/理由、negative/mutation/corruption fixtures、crash/atomicity/path evidence、
residual/accepted risks、未验证 assumptions、commit/candidate/verification boundary，以及被证伪 finding 的教训。

只有“同一窄 claim 已由 successor 明确接管”才能标 `superseded_by`。Review closed 通常只需要 closing record，
不应为了清爽而把 finding 原文改写成成功叙事。

## 9. Tooling scope

### 9.1 V1 checks

第一阶段只自动验证可机械证明的 claim/router 结构事实：

1. domain router 中的 repo-relative target 存在且大小写正确；
2. 同一 `claim_id` 在 active ledger 中至多有一个 current owner；role 可以重复；
3. router path 解析后仍位于 repository boundary 内；
4. `git diff --check` 通过。

### 9.2 Later checks

在 DG2～DG4 稳定后，才考虑 SessionJournal-scoped local link scan：

- 只扫描 `docs/SessionJournal/` 与显式纳入的 component README；
- 先报告、观察噪声，再决定是否 gate；
- historical monorepo path、generated anchor 与有意保留的 external reference 需要明确策略；
- checker 必须可独立运行，不要求恢复 DocGraph 数据模型。

### 9.3 Explicit non-goals

自动化不得判断正文真伪或自动选 authority，不得立即设全仓 hard gate、访问网络、强制 frontmatter、
注入 backlink/修改文档，或把未出现在 ledger 的文件报告为可删除 orphan。

## 10. Target logical layout

长期 logical layout 是一个 `docs/SessionJournal/README.md` discovery ledger，加上逻辑分区
`current-contract/`、`target-design/`、`plans/`、`reviews/`、`runbooks/`、`evidence/`，以及
`historical/{done,superseded,frozen}/`。V1 不要求物理目录立即匹配。

这只是长期 logical layout。物理移动永远是最后一步，因为它会同时影响 Markdown links、AGENTS/CLAUDE、
外部会话路径记忆、scripts/tests/runbooks、commit references 与 review evidence 可追溯性。

在 role、lifecycle、claim ownership 和 successor 未先明确前，不得按文件名或当前目录机械搬迁。

## 11. Work packages

每包必须独立 review、validation 与 commit；后包不能借“治理整理”顺带改变 product contract。

### DG0 — Active meta plan

- **Intent**：固化本文的 purpose、non-authority boundary、两轴模型、routing budget、close protocol、tooling scope 与工作包。
- **Out of scope**：claim pilot、domain router、per-file metadata、现有文档重写、checker、物理移动。
- **Validation**：本文 relative links（若有）解析；`git diff --check`；确认 dirty status 只有本文与既有用户 untracked report。
- **Done**：本文以独立 commit 落地，且没有 stage/修改用户 untracked report。

### DG1 — Pilot claim registry and fact verification

- **Intent**：用 §4.3 schema 建立小范围 pilot claim registry，覆盖 EADR concepts、Beta snapshot、EADR target + implementation、最近一次 tracked review，以及 Store/Planner README，并逐项 against code/tests/fixtures 核验。
- **Out of scope**：发布 `docs/SessionJournal/README.md`、repository root README、Core/Recovery mixed docs、用户 untracked report、全量 inventory、物理移动。
- **Validation**：每个 current implementation/API/wire claim 都有真实 `verified_against`；同一 `claim_id` 只有一个 current owner；target 不冒充 implementation；closing entry 具有 tracked content-free summary/manifest 与 portable evidence pointers。
- **Done**：pilot decision/closing record 可独立复核；未核验 claim 不发布为 current/canonical，ignored/secret-bearing evidence 不是唯一证据。

### DG2 — SessionJournal domain router

- **Intent**：只把 DG1 已核验的 accepted entries 发布到新 `docs/SessionJournal/README.md`，建立 domain → task entry 单向路由。
- **Out of scope**：repository root README、未经核验的 current/canonical entry、完整 42-file inventory、frontmatter、checker、目录移动。
- **Validation**：所有 relative links 存在并留在 repo 内；同一 `claim_id` 无重复 current owner；`verified_against` 与 DG1 closing record 可追踪；cold-agent 能在 domain router 后获得 2～4 份、约 8k～12k tokens 的入口。
- **Done**：domain router 成为唯一 active discovery ledger；DG1 report 保持 closed evidence，不成为第二个 current ledger。

### DG3 — Core and Recovery mixed documents

- **Intent**：裁决 raw Core、tail recovery、configuration access 与 architecture roadmap 中混合 current/historical 内容的路由和窄 ownership。
- **Out of scope**：改变 A-level wire、删除 proof redundancy、按章节机械拆文件、未经验证的历史搬迁。
- **Validation**：所有 Prepared/Resume/Restore、Parent lineage、bounded proof claim 核对 current code、focused tests 与 fixtures，并记录 `verified_against`；closing claim 保留 tracked summary/manifest 与 portable pointers；historical 叙事不再成为默认入口。
- **Done**：Core/Recovery task route 清晰，安全升级点和 historical 边界显式；ignored 大日志不是唯一证据；无 product contract 变化，或变化已另建 candidate gate。

### DG4 — DerivedRecap, Config and Host mixed documents

- **Intent**：治理 cadence、HistoryLoad、repo config、Host integration、Galatea plan/runbook 与 DerivedRecap component 文档间的 current/target/evidence 边界。
- **Out of scope**：统一 Store/Planner/Maintainers ownership、删除 frozen execution proof、将 Galatea plan 冒充 operator runbook。
- **Validation**：active config 与 frozen plan 分离；Store/Planner/Maintainers ownership 不漂移；Galatea plan 与 repeatable runbook 的 claim 分开；current claims 有真实 `verified_against`；closing evidence 有 tracked content-free summary/manifest 与可复核 pointers。
- **Done**：DerivedRecap/Config/Host 每类任务都有窄入口和 safety triggers；旧 cadence/config 不覆盖 current implementation，secret/provider正文不被 tracked，ignored 日志不成为唯一 evidence。

### DG5 — SessionJournal-scoped checker

- **Intent**：在前述信息结构稳定后实现 scoped、read-only 的 local link/role/path checker。
- **Out of scope**：全仓 hard gate、网络 URL、frontmatter schema、内容真伪判断、自动修复与 backlink 注入。
- **Validation**：positive/negative fixtures覆盖 missing target、case mismatch、repo escape 与同一 `claim_id` 的 duplicate current owner；重复 role 合法；现有 historical 噪声有显式处置；checker 不修改文件。
- **Done**：命令可重复运行，默认只扫描已治理 scope；先 report-only，观察稳定后才单独决定是否 gate。
- **实施状态（2026-08-04）**：Done。默认15份tracked active/current入口为clean；stdlib fixture suite
  17/17。首次`--all-tracked --report-only`观察51份tracked Markdown，报告19项`MISSING_TARGET`，均位于
  默认scope外的historical/done/superseded记录；这是DG5的initial observation，不接CI、不校验anchors。
  DG6 closeout后同一all-tracked corpus为51份 / 0项，默认scope仍为clean。

### DG6 — Conditional small-batch historical moves and link repair

- **Intent**：仅当 logical classification 稳定且存在明确净收益时，小批移动确定的 historical/superseded/closed 文档并同时修复链接；没有合适候选也是成功结果。
- **Out of scope**：一次性大搬家、按文件名猜 lifecycle、移动仍承载 current/mixed safety claim 的文档。
- **Validation**：每批枚举 inbound links、scripts/tests references 与 external operational references；移动前后 scoped checker通过；`git diff --check`；独立人工 review。
- **Done**：若实施，每批可独立 revert、links 无断裂、commit明确 old→new mapping 且 retained evidence 不丢失；若成本或风险不合算，记录 retain-in-place decision 后关闭 DG6，不强求搬迁。
- **实施状态（2026-08-04）**：Done / retain-in-place / no moves。逐项核对19条historical
  `MISSING_TARGET`后，没有文档具有足以抵消inbound/external-reference风险的搬迁收益。DG6-A在
  `ed83a310`修复12条相对路径与2条tracked Maintainers successor；DG6-B把剩余4条deleted code path和
  1条machine-local/ignored prompt改为inline historical记录，不建立伪successor。两批均不删除、不移动
  文档；all-tracked observation由51/19关闭为51/0。

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

1. Current/canonical router entry 只在 claim against code/tests/fixtures 核验后发布；
2. 普通任务默认只需 2～4 份、约 8k～12k tokens 文档建立上下文；
3. safety trigger 能可靠把 Agent 导向 code/tests/fixtures，而不是被 routing budget 截断；
4. current/target/closed/historical/superseded/frozen 不再由目录隐式推断；
5. 每个 `claim_id` 至多有一个 current owner，role 可重复且没有 broad `canonical_for`；
6. 新文档通常只需更新一个人工 ledger，不产生强制 backlink；
7. review close 保留 tracked content-free summary/manifest、portable evidence pointers、rejected findings、negative fixtures、crash evidence 与 residual risks；
8. checker 只验证结构事实，误报与维护成本低于它阻止的 drift；
9. 每个工作包可独立 review、commit 与 revert，DG6 无合适候选时可 retain-in-place 关闭；
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

DG0 tail-fix 合入后，下一包只执行 DG1：建立 pilot claim registry，并对 proposed current claims 执行
against code/tests/fixtures verification。DG1 不发布 router；DG2 只能把核验通过的 entry 发布到
`docs/SessionJournal/README.md`。

不要在 DG1 顺带引入 root/domain router、frontmatter、全量 ledger、checker、现有文档大规模重写或目录移动。
Repository root README 等待未来 repo-wide 决策。
详细逐文件 inventory 与迁移判断留给后续一次性工件；它们不得成为新的长期人工 truth source。
