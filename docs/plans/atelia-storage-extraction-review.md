# atelia-storage 拆仓计划：精简审视记录

> 日期：2026-09-13。目标：[实施计划](atelia-storage-extraction-plan.md)。
> 采用 dialectical-simplification：三名独立 Reviewer 第一轮提出主张，第二轮交叉质询，主线程检查原始证据并修订计划。没有执行迁移或运行产品测试。
> 本文保存裁决与覆盖边界，不是另一份实施清单；后续工作以实施计划为入口。

## 最小可执行模型

一个新仓维护五库和唯一 Pack；两个实际消费仓各维护一个 storage pin 和一个薄 Prepare。用一个 EventJournal 公开包示例证明新库可以单独使用，用一条本次拆仓的旧/新 DG 冷进程 witness 证明既有数据保持；两者都不能替代两个消费仓的包/源码依赖图与现有测试。

独立 Reviewer 分工为 Demand skeptic、Minimal architect、Semantic defender；三方读取同一完整初稿和需求台账，第一轮互不可见结论。主线程独立核对版本耦合、边界测试、下游固定 pin、资源与官方资料。第二轮之后，关键裁决已经收敛，没有需要第三轮裁决的实质争议。

## 采纳的裁决

| 裁决 | 位置与原主张 | 来源/实际消费者 | 最小替代与具体失败边界 | 置信度 |
| --- | --- | --- | --- | --- |
| delete | 新仓继承原 Directory.Build.props 的两个 Analyzer 引用 | 用户明确决定；原仓构建设置 | 新仓仅保留所需命名、MIT、SDK 配置；不另造 Analyzer 包或替代框架。原仓其余项目不受此删除影响 | 高，用户决定 |
| merge | P0 分别建立底层 RBF/EJ 与 DG 旧数据 harness | 已有 DG 场景生成 State/Schema/Journal/ref 数据 | 合并为一条本次拆仓旧/新 witness，独立 P2 示例仍保留。整条旧/新 witness 若删除，新包自己写读会掩盖旧数据不兼容 | 中高，实施时仍需核实实际覆盖 |
| merge | 五库打包列表散落消费仓与多个 probe | 当前 DG 多份脚本重复九包循环 | 新仓唯一 Pack；两仓薄 Prepare 调用它；DG probe 共用本仓准备函数。完全删 Prepare 会让公开发布前缺少 bootstrap，复制 Pack 会持续漂移 | 高，当前代码 |
| simplify | 九包共用一个 `$Version` | 当前 EventHistory/Recovery 等包验证入口 | 五库 S 与 DG G 独立，故意 `S != G` 验收。若 feed 恰好九包同版本，错误 nuspec 依赖也能被掩盖 | 高，当前代码 |
| simplify | 源码联调可能直接用于消费仓交付 pack | MSBuild 全局 PackageVersion 沿 ProjectReference 传播 | 源码模式用于 build/test，交付入口拒绝显式源码模式并使用包模式；实验包先由上游 Pack 生成唯一 S_dev。防止 nuspec 错依赖 G 版本存储包 | 高，当前构建图 |
| keep | atelia 结构测试是否可以视为过时而删除 | WalkingSkeleton 中已有业务依赖禁区、IVT、资源断言 | 只适配明确五库的外部身份；模式生效用 MSBuild/restore 结果核验。不自制条件解释器，也不忽略未知引用。仅改 csproj 会让字面 `$(StorageSourceRoot)` 被错误递归 | 高，已核实测试实现 |
| keep | 底层与 DG 不同 recovery 约定 | 底层默认可恢复、只读不修复；DG 严格打开禁恢复 | 继续运行原有测试；不加恢复矩阵。统一“全部自动恢复”或“全部永不恢复”都会改变一个实际消费者的行为 | 高，当前代码/测试 |
| simplify | P2 要求 Source Link 验证 | Agent 需定位正确版本，远端操作被后置 | 本地检查映射/来源/资产；源码在线取得在 P5。提前要求网络可取会让本地阶段依赖远端身份；完全删来源则容易读错版本 | 高，官方资料及阶段边界 |
| simplify | 所有带走文档的历史链接必须修复 | 现行指南需要可导航；旧聊天有大量已失效机器路径 | 只对活动导航、指南与必要规范依赖设退出条件，历史材料保留来源。修所有聊天路径会拖入不迁出的业务文档 | 高，已有文档 |
| simplify | P4 只写“固定 revisions” | 候选可能尚未提交，提交不应成为隐含前提 | commit 或 base commit + 完整 patch/新增文件哈希物化；明确候选身份。只 clone 旧 HEAD 会验证错误对象；未提交包不能冒称最终 commit | 高，工作流必然边界 |
| defer | 将 DramaBoard 升级也列为本次迁移门槛 | 它的 CI/脚本固定旧 DG 与 atelia commit | 原仓历史保留，所以旧 pin 不因删除当前目录失效；未来采用新 DG commit 时再适配。不扩大成第四仓改造 | 高，已核实固定 pin |
| defer | 新机器、VM、强制 Linux、nuget.org 前置 | 用户资源问题，当前有 Windows 开发环境及 WSL Ubuntu | Windows 干净目录/缓存做最低验收，WSL 和远端发布后置。没有当前需求要求购买机器或搭私有服务 | 高，当前范围 |

可观察的缩减：原先可被理解为两套以上旧数据 harness，收敛为一条跨版本 witness；各处重复的五库 pack 算法，收敛为新仓一个权威。保持两个薄消费入口和两类不同的包/持久化证据。阶段边界本身保留，未为了少几个标题合并不同验收责任。

## 交叉质询后的修订

- Demand skeptic 明确限制合并范围：一条旧/新 witness 只合并持久化证据，不能替代 atelia 架构边界、双模式引用图和 `S != G` 验收。
- Minimal architect 接受候选物化与结构测试适配，补充交付 pack 必须使用包模式；不引入完整 MSBuild 解释器或额外依赖模式。
- Semantic defender 接受不另造 EJ/RBF 旧数据 harness，条件是 runtime 源文件比对、现有恢复/格式测试、独立 EJ 包示例均保留，DG witness 的帧清单覆盖实际生成的 Schema、State、Journal event/ref。
- 三方一致要求区分同版 recovery probe 与真正旧包写出、新包读取；旧 DB-071 runner 的 namespace 分支不能拿来冒充本次拆仓。
- 最终文档核对又补齐候选到提交的衔接：P2–P4 可先用精确候选 feed；固定 commit 的 Prepare 在存储仓提交后验证，消除 P3/P4 等待环。提交后来源元数据改变，必须产出新 S_final 并同时更新消费 pin，不能覆盖 S_candidate。

## 直接核查的证据入口

- [原仓全局构建配置](../../Directory.Build.props)：Analyzer 注入与程序集/包命名。
- [EventJournal 项目](../../src/EventJournal/EventJournal.csproj)：完整五库依赖链。
- [结构边界测试](../../tests/SessionJournal.RecapGrid.WalkingSkeleton.Tests/AssemblyDependencyBoundaryTests.cs)：旧引用常量、包集合与不求值的 XML helper。
- [RbfSegmentStore 测试](../../tests/RbfSegmentStore.Tests/RbfSegmentStoreTests.cs)、[EventJournal 测试](../../tests/EventJournal.Tests/EventJournalTests.cs)：默认 recovery 与只读坏尾规则。
- [DG EventHistory runner](../../../durable-graph/experiments/PackageConsumerProbe/Run-EventHistoryProbe.ps1)、[Recovery runner](../../../durable-graph/experiments/PackageConsumerProbe/Run-EventHistoryRecoveryProbe.ps1)：九包同版本循环、同程序集的多进程恢复。
- [旧 OrganizationMigration runner](../../../durable-graph/experiments/PackageConsumerProbe/Run-OrganizationMigrationProbe.ps1)、[场景与帧清单](../../../durable-graph/experiments/PackageConsumerProbe/OrganizationMigrationConsumer/Program.cs)：可以复用的模式与不可照搬的旧命名迁移。
- [DramaBoard 包准备](../../../drama-board/scripts/Prepare-DurableGraph.ps1)、[CI](../../../drama-board/.github/workflows/ci.yml)：固定旧源 commit，继续有效的历史消费方式。
- 工具和发布规则的官方来源统一列在实施计划 §7，不另维护一份重复资料目录。

这些跨仓相对链接用于本轮本机审查；将来移动此记录时应固定为调查 commit 的来源链接，不作为新库的长期入门路径。

## 剩余事项与验证限度

没有发现需要现在改变用户产品决定的问题。GitHub 归属/可见性、nuget.org 账号和包名所有权留到实际远端发布前明确；PackageId 若冲突，需报告具体冲突，由用户选择是否改名。

本次交付是经过审视的计划。未证明提取后的库已构建、旧数据已迁移、WSL 内 SDK 可运行、账号已有发布权限或包名可用。这些均有明确后续阶段，不能把文档审阅当作实施验收。
