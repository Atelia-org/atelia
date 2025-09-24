# SymbolTreeB Builder 增量重构推进计划

## 版本信息
- 创建日期：2025-09-25
- 最近编辑：2025-09-25（当前会话）
- 维护人：CodeCortex V2 小组（调度：AI 负责人；实施：AI Coder 会话）
- 关联文档：`SymbolTree.WithDelta.md`、`CodeCortex_Phase1.md`

## 背景
`SymbolTreeB.WithDelta` 与 `SymbolTreeB.FromEntries` 分别承担增量与全量构建职责，但当前实现存在大量重复的局部函数和别名维护逻辑。新的 `SymbolTreeBuilder` 旨在承载共享的可变视图，统一别名、节点、命名空间链等操作，降低后续演进成本。该计划提供一条可循序渐进实施的路径，并在执行过程中维护进度透明度与风险管控。

## 执行模式与角色分工
- **调度角色**：由固定负责人掌舵（本计划引用为“AI 负责人”），负责拆解阶段、准备上下文、串联多轮 AI Coder 会话、合并成果并确保进度表更新。
- **实施角色**：每次编码会话由具备上下文的 AI Coder（与本助手相同的 LLM 形态）承担。每次会话以“回合”形式推进，需在结束时提交变更、测试结果与状态更新。
- **时间度量**：以 AI Coder 会话为一个基本执行单元（Session）。阶段内可包含多场 Session；每一场需具备明确目标、输入与验收。
- **输出管控**：所有代码改动通过 PR/变更集管理，并在 Session 结束时同步到本计划的进度表与“后续行动”章节。

> 建议在每次 Session 前预先整理任务清单、依赖与验证命令，以减少上下文切换成本。

## 总体目标
- **功能一致性**：保证重构后 `WithDelta` 与 `FromEntries` 行为一致、幂等且满足现有契约。
- **结构整合**：引入 `SymbolTreeBuilder`，集中管理节点与别名更新逻辑。
- **可验证性**：在每一阶段提供足够的测试或比对手段，确保可回退、可度量。
- **演进基础**：为后续的写时复制（COW）、别名索引优化、墓碑压缩奠定基础。

## 范围与边界
- **纳入范围**：`SymbolTreeB.WithDelta`、`SymbolTreeB.FromEntries`、别名层构建与维护、与上述逻辑直接关联的测试。
- **不纳入范围**：`SymbolTreeB.Search`/查询路径、Roslyn 同步器契约、上层触发策略（full rebuild vs delta）。如需变更另行评估。

## 渐进式实施路线

### 阶段 0：基线与验证护栏
- **前置条件**：现有单元测试通过；`SymbolTree.WithDelta.md` 中记录的 P0 行为准则明确。
- **核心任务**：
  - 收集或补充关键单元测试（空树 delta、级联删除、rename、别名一致性）。
  - 建立比较基线（例如 `WithDelta` vs `FromEntries` 全量重建结果比对脚本）。
  - 明确 DebugUtil 类别与日志开关策略。
  - 制定“Session 准入清单”：包含待执行测试列表、日志开关指引、必要的 `git status`/diff 检查流程。
- **最新进展（2025-09-25）**：
  - 新增 `tests/CodeCortex.Tests/Util/SymbolTreeSnapshot.cs`，提供快照指纹工具与断言，支持跳过墓碑节点、标准化 DocId。
  - 补充基线测试 `V2_SymbolTree_BuilderBaseline_Tests`（空树增量、rename 序列、别名标记一致性）。
  - `dotnet test tests/CodeCortex.Tests/CodeCortex.Tests.csproj` 全量通过（warning 维持既有遗留）。
- **验收标准**：新增测试全部通过；基线脚本可一键执行并记录输出。
- **回退策略**：保留当前实现分支；若测试不稳定则回滚新增测试并重新评估需求。

### 阶段 1：引入 `SymbolTreeBuilder` 骨架
- **前置条件**：阶段 0 通过。
- **核心任务**：
  - 在 `Index/SymbolTree` 命名空间内新增内部 `SymbolTreeBuilder` 类型，封装节点列表、别名字典及常用 helper。
  - 将现有 `WithDelta` 局部函数（命名空间链、别名增删、子树清理等）迁移到 Builder，保持 API 一致。
  - 调整 `WithDelta` 调用这些新方法，但暂不改变整体流程。
  - 会话指南：首次 Session 聚焦接口草图与迁移策略评审，次级 Session 负责代码落地与测试回归。
- **验收标准**：行为与主分支一致（所有测试通过）；Builder 中方法覆盖现有重复逻辑 ≥ 80%。
- **回退策略**：Builder 引入以 feature flag 形式合并，问题时可切回旧 helper。

### 阶段 2：`WithDelta` 使用 Builder 完整改写
- **核心任务**：
  - 将 `WithDelta` 内部状态复制/修改逻辑替换成 Builder 的生命周期操作（`CloneFrom`, `ApplyDelta`, `Build`）。
  - 精简 `WithDelta` 主体至流程编排（删→加→级联删除）。
  - 扩充日志（别名键变更数、节点新增/删除统计）。
  - 会话指南：每场 Session 按“准备→实现→自测→回报”节奏输出；需保留 Builder 与原实现之间的 diff 记录，便于审查。
- **验收标准**：测试全绿；`WithDelta` 主体长度显著下降，关键逻辑集中在 Builder。
- **回退策略**：保留旧实现分支；若发现性能回退，通过配置切换到旧路径并记录问题。

### 阶段 3：`FromEntries` 与 Builder 收敛
- **核心任务**：
  - 将 `FromEntries` 重写为 Builder 静态入口（例如 `SymbolTreeBuilder.FromEntries(entries)`）。
  - 清理重复的别名/节点生成代码，统一走 Builder。
  - 为全量构建增加与旧实现的快照比对测试（结构、别名桶）。
  - 会话指南：优先安排一场 Session 完成旧逻辑行为梳理，后续 Session 专注迁移与快照比对脚本的自动化。
- **验收标准**：原全量构建测试全部通过；对大型 entry 集合的比对误差为零。
- **回退策略**：保留旧 `FromEntries` 为紧急 fallback（编译期 #if 或 git tag 备份）。

### 阶段 4：P1 优化与技术债处理
- **核心任务**：
  - 引入父级子表索引或 docId 快速查找以降低兄弟链扫描成本。
  - 用写时复制（COW）策略优化节点和别名字典的内存占用。
  - 确定别名桶排序策略，保证快照确定性。
  - 会话指南：每项优化建议拆分为独立 Session，先评审设计再落地实现，以便量化性能影响。
- **验收标准**：性能基准对比显示无回退；新增指标记录于进度表。
- **回退策略**：按优化项独立 feature flag 控制。

### 阶段 5：P2 扩展
- **核心任务**：
  - 设计并实现墓碑压缩触发条件与操作流程。
  - 评估并落实大 delta 降级策略（退回全量重建）。
  - 结合监控/日志完善运行时观测指标。
  - 会话指南：需要跨组件协同的变更，建议在 Session 前准备清晰的上下游接口说明，必要时安排伴随 Session 的设计评审。
- **验收标准**：压缩/降级路径通过端到端测试；运行指标可在监控面板观测。
- **回退策略**：通过配置开关关闭压缩/降级逻辑。

## 进度追踪
| 项目 | 所属阶段 | 当前状态 | 调度负责人 | 实施主体 | 预估完成时间 | 备注 |
| --- | --- | --- | --- | --- | --- | --- |
| 基线测试补充 | 阶段 0 | 进行中 | AI 负责人 | 当次 Session 指派的 AI Coder | 2025-09-24 | `SymbolTreeSnapshot` 快照 helper + 三项基线测试已落地 |
| `SymbolTreeBuilder` 骨架 | 阶段 1 | 已完成 | AI 负责人 | 当次 Session 指派的 AI Coder | 2025-10-07 | 2025-09-25：Builder 初版合入，`WithDelta` helper 已迁移并通过基线测试 |
| `WithDelta` Builder 化 | 阶段 2 | 进行中 | AI 负责人 | 当次 Session 指派的 AI Coder | 2025-10-14 | 2025-09-25：`CloneBuilder` + `ApplyDelta` 上线，`WithDelta` 主体压缩并回归 `dotnet test`。<br>2025-09-25：空树路径迁移至 Builder，补齐中间类型节点/别名与 `FromEntries` 快照对齐，`dotnet test tests/CodeCortex.Tests/CodeCortex.Tests.csproj` 通过；待进行性能抽样与 Stage2 收尾评估 |
| `FromEntries` 收敛 | 阶段 3 | 进行中 | AI 负责人 | 当次 Session 指派的 AI Coder | 2025-10-21 | 2025-09-25：`SymbolTreeB.FromEntries` 改写为复用 `SymbolTreeBuilder.SeedFromEntries`；回归 `dotnet test tests/CodeCortex.Tests/CodeCortex.Tests.csproj` 全绿。后续需补充快照 diff 校验与大规模样本抽测 |
| COW / 索引优化 | 阶段 4 | 未开始 | AI 负责人 | 当次 Session 指派的 AI Coder | 2025-10-31 | 根据指标决定具体策略 |
| 墓碑压缩 & 降级策略 | 阶段 5 | 未开始 | AI 负责人 | 当次 Session 指派的 AI Coder | 2025-11-15 | 需监控支持 |

> 进度状态枚举：未开始、进行中、阻塞、已完成。更新频率建议每周一次或重大里程碑发生时。

## 风险与应对
| 风险 | 等级 | 说明 | 缓解措施 |
| --- | --- | --- | --- |
| 重构中行为回退 | 高 | 删/加逻辑高度耦合，易引入幽灵别名或漏删节点 | 分阶段合并，使用基线比对脚本；必要时灰度发布 |
| 性能退化 | 中 | Builder 层新增抽象可能带来常数开销 | 在阶段 2/3 结束时跑性能基准，触发阈值反向合入优化 |
| 测试覆盖不足 | 中 | 缺少端到端 delta 序列验证 | 阶段 0 补齐测试，必要时引入随机化差异对比 |
| Session 上下文漂移 | 中 | 跨多轮 AI Coder 会话可能遗失上下文或重复劳动 | 每场 Session 结束记录操作日志与检查清单；调度负责人在下一场开场前做重点回顾 |
| 需求变更 | 低 | 上游同步器契约若调整，会影响路径 | 每阶段评审时同步上游团队，保证契约一致 |

## 质量闸与验证策略
- **单元测试**：覆盖增删、幂等、命名空间级联、别名生成与删除。
- **基线对比**：依赖 `SymbolTreeSnapshot` 快照工具，对 `FromEntries` 与连续 delta 应用结果做结构、别名桶 diff。
- **性能基准**：针对典型 delta 序列（100 小 delta、1 大 delta）记录执行时间与分配。
- **诊断日志**：启用 `SymbolTreeB.Delta`、`SymbolTreeB.Alias` 分类，验证异常路径是否被捕获。
- **Session 报告**：每场会话结束时需回填变更摘要、测试结论、待办事项到本计划或关联 Issue，供下一场会话快速接续。

## 后续行动（今日视角）
1. 对 Stage2 变更进行性能抽样（小 / 大 delta）并记录 CPU / 内存基线，支撑后续优化决策。
2. 编写 Stage2 变更说明（空树路径、别名重复策略、复用池机制）并组织内部评审。
3. Stage3 后续：整理 `FromEntries` 改写影响面，补充快照 diff 校验脚本与大规模样本抽测，确认 builder 路径与旧版输出一致。

## 完整性自检清单
- [x] 目标、范围、阶段划分已明确。
- [x] 每阶段包含前置/任务/验收/回退四要素。
- [x] 提供了进度追踪表和风险矩阵。
- [x] 质量闸（测试、基线、性能、日志）覆盖关键风险。
- [ ] 负责人与日期尚未指派（需项目经理更新）。
- [ ] Session 模板及执行日志机制待创建。

> 待补项：各阶段负责人、实际日期、相关 PR 链接与测试结果需在推进过程中持续更新。
