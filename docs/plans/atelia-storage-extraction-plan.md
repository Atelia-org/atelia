# atelia-storage 分阶段拆仓实施计划

> 状态：2026-09-13 P0–P4 本地迁移已验收并形成三仓提交；P5 已确认公开远端、CI 和 GitHub 发布设置，待用户创建 NuGet Trusted Publishing 策略。用户已授权 subagents、Git、本地文件和必要网络操作。
> 实施基准：atelia `6242f3bb6b631079d2513288ca54d823f630802f`，durable-graph `4f74ee5662b54b68916bcd9d9759a708e802666b`，启动时两仓干净。
> 本文件是迁移任务的唯一实施计划；以下工作清单须结合阶段状态判断，不能把计划命令当作已执行证据。实验日志和冻结包位于仓库外 `../.storage-extraction/`。
> 精简裁决与证据边界见 [审视记录](atelia-storage-extraction-review.md)；续工只需先读本计划。

## 1. 目标与需求台账

将 atelia 中成熟的存储基础设施迁入一个 `atelia-storage` 仓库，使 atelia 和 durable-graph 通过明确版本的包正常构建；需要改库时可以显式使用本地源码。新会话能从简短入口发现版本、用法、源码和验证命令。

| 编号 | 必须保留的要求 | 来源与证据 |
| --- | --- | --- |
| R1 | 一个新仓，包含 Primitives、Data、Rbf、RbfSegmentStore、EventJournal 五个项目 | 用户本轮与上一轮决定；五项目当前 csproj 运行时引用形成闭包 |
| R2 | 新仓不引用 Analyzers.Style / Analyzers.Style.CodeFixes，也不为它们建立新包或替代框架 | 用户本轮明确决定；旧仓 Directory.Build.props 自动注入两者 |
| R3 | 优先提取相关 Git 历史；遇到难点允许复制当前文件并初始提交，无须再次征询此项取舍 | 用户本轮明确授权降级 |
| R4 | 保留现有程序集名、namespace、PackageId、公开 API、持久化格式与运行时行为 | 用户赞同上一轮建议；本任务为仓库/交付边界迁移 |
| R5 | 默认明确版本 PackageReference；本地联调为显式源码开关；先本地包验收，公开发布后置 | 用户赞同上一轮建议 |
| R6 | 两个实际消费仓都验证；包消费成功不能由源码引用成功代替 | 用户指定 atelia 与 durable-graph；已有 PackageConsumerProbe |
| R7 | 已有数据能冷读、续写、再次冷读；资源生命周期、校验及原有故障行为不改变 | 当前 RBF、EventJournal 测试；DurableGraph AGENTS.md 的 append-only 与离线救援约束 |
| R8 | 尽量自动化，文档和包对后续 Coding Agent 易发现、易验证 | 用户本轮明确目标 |

新仓运行时保持当前单 writer / lease 与 disposal 约束；本迁移不增添线程安全、多进程事务、跨存储原子提交或恢复策略。对正常读取、显式恢复、离线救援的区别沿用各库和消费者当前约定，不能借迁移统一成一种行为。

不纳入：StateJournal、Diagnostics、Completion、SessionJournal、生成器及其他业务项目的迁出；重命名或合并五个程序集；多目标框架；新存储后端；自建包服务；通用发布平台；性能优化。原 atelia 的 Analyzer 配置不因本任务整体移除。

## 2. 当前仓库证据与提取范围

方案形成时的只读基准：atelia `fecbff4485e98da49570bb843683ad815cde3c5e`；durable-graph `4f74ee5662b54b68916bcd9d9759a708e802666b`。这是调查定位，不替代 P0 的实施基准。核对发现：

- atelia 使用 `Atelia.sln`；durable-graph 使用 `DurableGraph.slnx`，不是同一种文件名。
- 五个生产项目 target 均为 `net10.0`；现有根 props 设置 `Atelia.$(MSBuildProjectName)`、MIT 元数据和 LICENSE 入包。
- atelia 活动消费者至少包括 `src/StateJournal`、`src/TextEditScript`、`prototypes/SessionJournal`、`prototypes/SessionJournal.Offline`、`prototypes/SessionJournal.RecapGrid`、`prototypes/SessionJournal.RecapGrid.Cadence` 和 `tests/SessionJournal.Offline.Tests`。
- durable-graph 的 `src/DurableGraph.Storage` 引用前四个库，`src/DurableGraph.Persistence` 引用 EventJournal；测试、PublicationCrashProbe 和多份 PackageConsumerProbe 脚本也存在旧路径。
- `tests/SessionJournal.RecapGrid.WalkingSkeleton.Tests/AssemblyDependencyBoundaryTests.cs` 在 C# 中写死 EventJournal 的 ProjectReference 和包集合，XML helper 也不求值 MSBuild 条件；必须随引用迁移调整，不能仅搜索 csproj。
- durable-graph 的多个 probe 当前用一个 `$Version` 给五个存储包和四个 DG 包一起打包；新仓版本独立后必须解除这个耦合。
- `src/EventJournal/README.md` 和 `src/RbfSegmentStore/README.md` 已有面向 Coding Agent 的使用说明，应复用并校正链接。
- `archive/` 和历史设计文件存在旧路径；区分活动入口与历史记录，不能全库盲替换。

### 2.1 初始路径清单

| 处理 | 路径 | 规则 |
| --- | --- | --- |
| 迁出 | `src/{Primitives,Data,Rbf,RbfSegmentStore,EventJournal}/` | 五个现有项目，保持目录及名称 |
| 迁出 | `tests/{Primitives,Data,Rbf,RbfSegmentStore,EventJournal}.Tests/` | 保持测试程序集名与 InternalsVisibleTo 对应 |
| 迁出 | `benchmarks/Data.Benchmarks/`、`benchmarks/Rbf.Benchmarks/` | 保留可构建性，默认不执行长 benchmark |
| 迁出 | `docs/Primitives/`、`docs/Data/`、`docs/Rbf/`、`docs/EventJournal/` | 规范和用法继续维护；历史讨论原样保留并标明来源 |
| 选择性复制 | `LICENSE`、`.editorconfig`、`.gitattributes`、`.gitignore` | 保留实际需要的条款与配置；不复制原仓业务设置 |
| 新建 | `Atelia.Storage.slnx`、`global.json`、根构建配置 | 独立构建；保留命名/MIT/语言配置；不引入两个 Analyzer |
| 检查后补充 | 测试数据、源码链接文件、历史旧路径、脚本 | 通过源码/项目文件/历史查找得到，不凭目录名遗漏 |
| 留在原仓 | 其余源码、业务测试、业务数据和业务文档 | 改消费方式，不迁走业务层 |

P0 必须将此清单核实成精确文件列表。强制链接验收限于根导航、现行 README、格式/API 指南及其必要规范依赖。旧仓活动文档改为指向新仓固定版本文档或入口；已经迁出的正文只留一个维护位置。历史聊天中的旧机器 `file:///repos/...` 路径不逐一修复，不为了修旧链接迁入无关业务文档；统一保留来源说明即可。

## 3. 最小交付模型

### 3.1 包与版本

- 保留五个包：`Atelia.Primitives`、`Atelia.Data`、`Atelia.Rbf`、`Atelia.RbfSegmentStore`、`Atelia.EventJournal`；初期统一版本、一起构建和交付。
- 本地开发包例：`0.1.0-dev.20260913.1`；公开预发布包例：`0.1.0-preview.1`。这是格式示例，不是已经占用的发布号。
- 每份不同内容使用新版本，不覆盖相同 PackageId + Version。不要只用 `+commit` metadata 区分不同内容。
- 正常消费明确选定版本，不用浮动版本；在每个消费仓集中管理一次存储包版本，不为升级逐文件修改版本字符串。
- 两消费仓各以一份 `eng/StorageDependency.props` 管理 `StoragePackageVersion`、`StorageSourceRevision` 和源码仓地址；新仓的五包版本为 S，DurableGraph 自身包版本为 G，二者独立。DG probes 不再给五库存储包套用 G；至少一次验收故意令 `S != G`，检查 nuspec 与实际 restore assets 都使用 S。
- 对最终应用/可执行验证入口可记录 `packages.lock.json` 并 locked restore；库自己的 lock file 不能替下游锁定依赖图。
- 包含 MIT 元数据、LICENSE、README、XML API 文档以及 Repository URL/commit、Source Link 和 PDB。P2 核验静态资产与来源对应，P5 才要求远端源码可取得；未提交候选按 P4 诚实记录身份。启用 XML 文档不意味着本次必须补全所有低层公开成员注释。

### 3.2 包准备与源码联调

本地阶段的消费入口应能从固定的新仓 commit 准备五个包到仓库内忽略的 feed，再执行普通 PackageReference restore/build。CI 用同一流程。禁止默认从任意兄弟工作目录的当前 HEAD 打包。

每个消费仓用一个薄 `eng/Prepare-Storage.ps1` 读取本仓 pin、取得并校验固定源码 revision，再调用新仓唯一的 `eng/Pack.ps1`。五库项目清单和 pack 算法只归新仓所有，不能复制到两个 Prepare 或 DG 各个 probe。DG probes 复用本仓一个小的准备函数，再取得五个 S 包与自己的 G 包组成完整 feed；不另建跨仓通用依赖管理工具。

迁移期间本地仓尚未发布时，可把明确的本地仓路径作为源码取得位置覆盖，依然校验指定 commit。P4 定义的未提交候选物化规则适用于 P2–P4：先把精确候选打成独立版本的包，再显式指定该版本/feed，以正常 PackageReference 图测试消费方。这是验收准备，不成为第三种日常依赖模式。

若 Pack/构建文件尚未提交，P3 不要求从旧提取 commit 运行不存在的 Pack；此时报告“候选包通过”。固定 commit 的 Prepare 端到端验收在 P4 存储仓提交后补跑，此前不能报告“固定来源取得通过”。这样既不漏掉未提交候选，也不形成 P3 等待 P4 的循环。

源码联调约定：`UseStorageSources=true` + `StorageSourceRoot=<absolute-path>`。默认 false；不能依据 `Exists(../atelia-storage)` 自动切换。显式请求源码而路径缺失时应清晰报错，不能静默退回包。需要的五库依赖在一个最终构建图中必须一致，不同时引入两份身份相同的程序集。切换模式时重新 restore，避免复用另一模式的 assets。

源码模式用于 build/test 联调；消费仓交付产包入口只走包模式，显式请求源码模式时清晰报错，内部调用固定 `UseStorageSources=false`。这避免下游 `PackageVersion=G` 传播到源码引用的存储项目。需要联调实验包时，先由新仓 Pack 生成唯一 S_dev，再显式选用该包打下游，不增加另一套发布模式。

### 3.3 Agent 的使用入口

新仓根 README 给出最小示例、包名、支持的 .NET 版本、构建/打包/验证命令和文档地图。根 AGENTS.md 只维护协作纪律和入口：读哪个指南、如何定位版本、怎样运行验证，不把旧 atelia 的业务规约整份复制过来。

优先维护现有两份库 README 和 RBF 使用指南，不再制造第二份同义 API 手册。调用方必须容易查到：Create/OpenReadOnly/Open 的差别、Dispose/Span/lease 生命周期、错误返回与异常边界、writer 约束、持久化/flush/recovery 保证以及最小读写示例。

两个消费仓各增加一小段依赖导航：版本配置位置、固定 tag/commit 文档链接、可选源码目录、包准备与源码联调命令。使用包不阻碍读取对应版本源码，但不应默认读取兄弟仓最新分支解释旧包。Source Link 是源码定位/调试支持，不是自动注入 Agent 上下文。

## 4. 分阶段实施与验收

各阶段开始先确认上阶段退出条件；以下阶段表记录当前实施进度。主线程拥有跨仓合同、共享文件集成及最终验收责任。受限子任务可并行编辑不重叠文件，但同一工作树中的 .NET 构建、测试、pack 串行运行。

| 阶段 | 可验收结果 | 当前状态 |
| --- | --- | --- |
| P0 | 固定源清单、旧包与旧数据基准 | 已完成；旧九包、writer 和真实存档冻结，Seed 通过，Atelia 既有平台/长测限制已归因 |
| P1 | 新仓独立源码构建与测试 | 已通过；保留 259 个相关提交，80 个 runtime 源文件不变，Windows/Linux 各 739 项测试通过 |
| P2 | 五包与独立公开 API 示例 | 已通过；最终 `.4` 五包及独立 smoke、80 源校验和通过，三处环境 10 包哈希完全一致 |
| P3 | 两消费仓、双模式与跨版本旧数据验证 | 已完成并集成；最终 `.4` 双模式、负向检查、真实包 probes、旧数据 witness 通过；Atelia 既有失败单列 |
| P4 | 干净候选复验与本地交付 | 已完成；两仓干净物化、固定来源 Prepare、私有缓存、构建/重点测试与独立 review 通过，本地提交交付 |
| P5 | 远端发布及正式获取验证 | 两仓已公开 push，Storage CI 通过，GitHub environment/变量就绪；预发布候选已验证，尚未上传 NuGet |

实施入口与已执行证据见 [验收记录](atelia-storage-extraction-validation.md)。重启前未完成的验证不算成功；本轮按项目串行复验。新仓 RBF 的三个约 1 TiB 边界测试改用 Windows 稀疏测试文件，运行时代码不变。

### P0：冻结基准与确定清单

**输入**：本计划、两个仓当前源码及 AGENTS.md、五库测试、DurableGraph 当前包消费入口。

**工作**：

1. 记录两个仓完整 HEAD、工作树状态、remote、SDK 和相关工具版本。已有用户修改不 stash/reset/clean；基准中明确包含什么，不能把未提交修改悄悄漏掉或混入。
2. 核实 §2 清单，包括历史旧路径、test-data、props/targets、IVT、solution、活动脚本和 C# 结构测试。记录现有相关测试及两个 solution 的基线失败，防止把旧问题当迁移回归；历史聊天链接不构成阻断。
3. 在独立临时目录冻结旧五库的 Release 包、包哈希、runtime 源文件哈希和程序集名/目标框架/IVT。用固定 DG 源码与业务模型、冻结旧存储包生成一组真实数据及旧 writer 可执行物；不另造独立 RBF/EJ 旧数据 harness，也不新建通用 API 差分框架。
4. 选择一条本次拆仓的跨版本冷进程 witness：复用已有 `OrganizationMigrationConsumer` 的场景/帧清单思路，但不照搬 DB-071 namespace 分支。旧数据覆盖 Schema、State、Journal event/ref 的实际生成文件，以及 parent/ref/head/pending 状态。冻结旧包、writer 和样本，不允许后来重建“旧”基准冒充旧实现。

**输出**：一个提取清单和一份基准证据；大二进制/日志留在忽略的 artifacts，版本和摘要可提交。

**退出条件**：旧包可运行、现有失败已归因、源文件清单足够执行提取。没有声称所有现有 bug 都已解决。

### P1：建立独立源码仓

**输入**：P0 固定源 revision 与清单。

**工作**：

1. 在任务专用新 clone 运行 `git filter-repo`。本地 clone 使用 `--no-local`；多次 `--path` 取并集，保留当前目录结构；先检查历史 rename。不要在原 atelia 工作树执行过滤，也不要 force push 原仓。
2. 正常尝试一次，至多进行一次有明确原因的修正；若仍因历史路径/环境等问题耽误主目标，改为从固定 revision 导出清单文件并创建快照新仓。记录来源 commit 与降级原因。历史完整性不阻塞产品提取。
3. 新仓保存简短 `docs/extraction-origin.md`，记录原仓、原 commit、路径清单和提取方式；过滤成功时保存 commit-map。快照方式不声称保留文件历史。
4. 新建独立 solution/SDK/build 配置；不引用两个 Analyzer。核查 AssemblyName/RootNamespace/PackageId/IVT，复制 LICENSE 并整理文档链接。
5. 独立构建五库、五个测试项目及两个 benchmark 项目；运行五库测试。只因拆仓而修改必要构建/文档文件，不改运行时算法。

**退出条件**：新仓在不访问原 atelia 的情况下完成构建和五库测试；runtime 源文件与冻结基准一致，差异都有清单说明。

### P2：独立公开 API 包消费 smoke

**输入**：P1 独立新仓。

**工作**：

1. 提供 `eng/Pack.ps1`：从明确版本构建五个 Release 包，输出本地 feed、来源 commit 和包哈希摘要；生产包清单明确，不把 tests/benchmark 打进发布集。
2. 在不会继承生产仓 Directory.Build.props/targets 的独立目录创建一个最小 EventJournal PackageReference 示例；由 NuGet 完成其余四库的传递依赖。不手工拷贝 DLL、不加 IVT、不从源码挂引用，也不把 DG 的 schema/history 验证体系搬进新库。
3. 在独立 `NUGET_PACKAGES` 目录恢复，使用显式任务 NuGet 配置确保候选五包取自该 feed。检查完整依赖、元数据、XML 文档、PDB/Source Link 映射与本地来源身份；远端源码尚不可取时明确标为未验证。示例执行 create → append → read → close → reopen。
4. 将这个可运行示例作为 README 示例的验证来源；整理 §3.3 导航和命令。

**退出条件**：一台机器上的干净目录仅用五个 nupkg 与正常第三方依赖即可运行示例；证据记录实际解析包版本和哈希。源码测试不能替代此条件。

### P3：接入两个真实消费者并验证已有数据

**输入**：P0 旧数据/旧包，P2 新包。

**工作**：

1. 在隔离消费工作树先接 durable-graph：更新 Storage/Persistence/测试/PublicationCrashProbe 引用、包准备和当前 PackageConsumerProbe；按 §3.1 解除九包同版本循环，以 `S != G` 实际 pack/restore。故意保留的历史包 lane 继续固定其旧输入，不机械改写。
2. 再接 atelia 活动消费者：包括 StateJournal、TextEditScript 和 SessionJournal 系列。在隔离候选中把删除已迁出的源/测试/benchmark/文档目录、移除 solution 项、替换引用与更新导航做成一组变更，验证后集成回共享树。不是只从 solution 隐藏旧源码；独立新仓和 P0 基准就绪前不删除原目录。
3. 明确调整 `AssemblyDependencyBoundaryTests.cs` 的 EventJournal 路径、包集合及受影响 helper。保留禁止上层依赖、IVT、资源等原有断言；五库作为明确的外部依赖终点，不跨仓递归，不忽略未知引用，不自制通用 MSBuild 解释器。双模式的实际引用图由标准 MSBuild 求值/restore assets 检查。
4. 两仓实现 §3.2 入口，默认 PackageReference 图先通过，再各验证一次显式源码模式及缺路径报错；记录实际解析五库来源。未提交阶段可使用明确的候选版本/feed，固定 commit 的 Prepare 取得验证留给 P4。消费仓 pack 明确走包模式，并验证显式源码产包请求被拒绝。
5. .NET 构建与测试按仓串行：`dotnet build DurableGraph.slnx -t:Rebuild` 后运行 `dotnet test DurableGraph.slnx --no-build`；同样构建并测试 `Atelia.sln`。命令的 Configuration 必须一致。P0 已有无关失败报告基线与新增差异，不宣称全绿。
6. 运行现有 EventHistory 包消费/恢复 probe，再运行 P0 定义的本次跨版本 witness。两个 lane 固定相同 DG 产品源码与业务模型，独立包缓存/输出目录，仅切换拆仓前后五库。记录实际加载的五库来源；新包进程对冻结旧样本只读不修改、续写、再冷读，校验旧完整 frame 的地址/内容及 parent/ref/head/领域状态。按旧 frame 区间或帧清单比较，不能要求续写后的整个文件哈希不变；派生 cache 不是正确性权威。
7. 搜索活动源码、solution、脚本和文档中的旧路径，处理新增悬空入口并记录历史例外。DramaBoard 当前脚本与 CI 固定 DG `4cea773…` / atelia `742fcd62…`；原仓保留历史时这些旧 pin 仍可取得，本次不扩大成第四仓升级。未来采用新 DG commit 时再适配其包准备脚本。

复用现有恢复测试，不扩建故障矩阵：`RbfSegmentStoreTests` 的默认打开恢复撕裂尾与只读坏尾不变、`EventJournalTests` 的只读坏尾、DG `PublicationSubstrateTests` 的严格打开保持字节。DG 默认禁恢复与底层默认可恢复是不同调用约定，均须保留。现有 `Run-EventHistoryRecoveryProbe.ps1` 使用同一程序集分进程执行，只证明同版恢复，不能替代以上旧/新 lane。

**退出条件**：两个真实消费仓的候选都通过默认包图，源码联调可用；冷进程 witness 通过；未引入新的重复程序集/包版本冲突；活动入口没有悬空旧路径。固定来源取得是否已验证另列，不冒充已通过。

### P4：干净环境复验与本地交付

**输入**：P3 三仓候选结果。

**工作**：

1. 在专用临时根目录物化三仓的精确候选：已提交时使用 commit；未提交时用基准 commit + 完整二进制安全 diff + 新增文件清单/哈希，覆盖新增、删除、重命名和相关未跟踪文件。先核对物化树摘要，再构建候选包并用新的缓存/feed 验证两个消费者。不能只 clone 旧 HEAD，却把结果算作未提交候选通过；不为此新建通用快照框架。
2. Windows 干净目录是最低验收；若现有 WSL Ubuntu 可用，在 Linux 自有文件系统目录补跑新库测试和包消费 smoke，声明平台覆盖边界。不要求购买第二台机器，也不把可选 Linux 结果冒充 Windows 文件系统证明。
3. 独立 Reviewer 核对 shared-tree diff、`git diff --check`、链接、包资产、数据 witness 和实际执行命令；主线程采纳前检查原始证据。
4. 本地收尾形成三仓可审阅变更；提交按实施会话已有授权执行。未提交候选的包记录 `base commit + candidate digest`，不把 base commit 冒充完整候选的最终 commit。获准提交后先提交存储仓，从新 commit 打出新的唯一 `S_final`，两消费仓同时更新包版本与来源 revision，并用正常 Prepare 端到端准备包、重核 RepositoryCommit/Source Link 和一次包 smoke。不得更新来源元数据后覆盖 `S_candidate`；相应消费包若内容改变也使用新版本。此前只能宣称候选树验证通过，不能宣称最终 commit 可取得。此计划不自行授予 push/公开发布权限。

**退出条件**：普通包消费、源码联调、已有数据、干净获取四类证据齐备，维护入口明确。未提交时交付状态明确为“候选树验收通过”；完成存储仓提交、两仓最终 pin 和正常 Prepare 后才报告“固定来源验收通过”，不将待完成项写成成功。

### P5：可后置的公开发布

P4 完成即构成本地迁移可验收结果，nuget.org 账号和远端操作不应阻塞 P0–P4 的本地准备。

**当前交接**：用户已将 atelia `721897c6` 和 atelia-storage `09d9799` push 到公开远端；[Storage CI](https://github.com/Atelia-org/atelia-storage/actions/runs/34760578290) 通过。GitHub 已创建 `nuget` environment，并设置 repository variable `NUGET_USER=Robird`。NuGet 个人账号为 `Robird`，组织为 `Atelia`；不创建长期 API Key。

用户在 nuget.org 的 Trusted Publishing 页面创建策略：Package owner 选择 `Atelia`，Repository owner 填 `Atelia-org`，Repository 填 `atelia-storage`，Workflow file 填 `publish.yml`（只有文件名），Environment 填 `nuget`；允许发布新包和新版本，包名 glob 为 `Atelia.*`。workflow 的登录用户名仍为个人账号 `Robird`，不改成组织名。

首次发布候选定为 `0.1.1-preview.1`，源码仍为 `09d979941d2c671a1e7a8ffabfa6e2b340e00f69`。实验根 `release-preview-1/` 已保存本地候选五包/符号及 manifest，独立 smoke 和 80 源校验通过；`remote-source-verification.json` 另证实固定 commit 的 80 个 runtime 源可从 GitHub 下载且字节匹配。尚未运行发布 workflow，不把这些本地产物当作已经上传的公开包。

策略建好后的顺序：从 `main` 手动触发现有 `publish.yml`，输入上述版本；等待五包可索引后，用全新缓存下载公开包并核验公开 API、来源和 Source Link；再调整两消费仓的版本与 NuGet source mapping，使正常构建从 nuget.org 取得五库。NuGet 仓库签名可能改变 nupkg 容器字节，公开下载的哈希应单独记录，不能冒充本地未签名包哈希。DG 本地迁移提交 `f80da5e` 尚未 push，随最终消费配置同步。当前远端 DG 仍是 `4f74ee5`。

1. Agent 核实五个 PackageId 的可用性，用户确定 `Atelia-org/atelia-storage` 的归属/可见性与包账号所有权。若包名已被无权限主体占用，停止该发布并报告具体冲突，不擅自修改本计划保留的 PackageId；本地工作可继续。
2. Agent 准备 GitHub 仓库/CI 和发布配置的可审阅结果，发布 workflow 调用同一 Pack/验证入口。包按依赖顺序上传；五包都能在独立缓存恢复后，消费者才切到公开版本。
3. 用户完成登录、2FA、条款/邮箱确认与凭据授权；优先配置 nuget.org 官方支持的 GitHub Actions Trusted Publishing，由 workflow 换取短期凭据。实际 push/发布遵循实施会话已有授权；缺授权时在可审阅结果就绪后提出一次具体请求。
4. 下载正式发布的包重新验证最小消费者，并验证 Source Link 能取得正确 commit 的源码，再更新两个消费仓的版本与文档。发布部分失败时保留旧消费 pin，处理未完成上传，不用同版本不同内容补发。

**退出条件**：所有五个公开包可还原，两仓引用同一套发布版本；新机器不再需要本地包准备来完成正常消费。

## 5. 自动化边界与续工协议

### 5.1 Agent 可自主处理与用户交互

实施获得授权后，Agent 自行处理：安装任务局部 git-filter-repo、核实文件/依赖/旧路径、创建本地 clone/worktree/实验目录、构建测试、生成唯一包版本、检查账号/包名可用性、撰写文档和准备发布配置。历史提取降级已获用户明确许可。

需要用户的实际动作集中在远端/身份环节：账号注册与交互登录、组织权限授予、2FA/条款确认、公开发布与 push 授权。已有授权不重复请求；密码和 token 不通过对话收集。资源暂缺时继续独立可做的本地工作。

### 5.2 子任务包

| 子任务 | 可写范围 | 输入与返回 | 依赖 |
| --- | --- | --- | --- |
| 提取/构建 | 新仓源码清单、solution、构建文件 | 固定源 revision；返回差异、方式、构建测试证据 | P0 |
| 包与示例 | 新仓 pack/验证脚本、包配置、示例 | 新仓候选；返回实际 nupkg 清单/哈希、公开消费证据 | P1 |
| durable-graph 接入 | 该仓活动引用、脚本、文档 | 已验收新包；返回引用图、测试及 cold witness | P2 |
| atelia 接入 | 旧仓 solution、活动消费者、边界测试、导航 | 已验收新包；返回迁出清单、测试及路径例外 | P2 |
| 独立验收 | 默认只读；不与实施者同时修目标 | 三仓 diff 和证据；返回阻断问题与证据边界 | P3 |

主线程先分配独占文件；props/版本配置等共享文件由一个负责人改。两仓编辑可以并行，.NET 验证由主线程安排串行，避免 Windows DLL 锁和旧生成物干扰。

### 5.3 每阶段交接只保留这些信息

`阶段/状态；三仓 revision 与工作树差异；包版本/commit/哈希摘要；已执行命令及退出码；未解决问题；下一步入口。`

本计划维护阶段状态与入口；长日志、一次性调查和 review 证据另存，不把完成过程堆入活动上下文。迁移完成后将计划冻结为迁移记录，长期用法留在新仓 README/AGENTS 与相应指南。不要额外创建另一份重复管理同一迁移状态的 backlog。

可给后续 Coding Agent 的启动文字：

> 阅读 `docs/plans/atelia-storage-extraction-plan.md` 与本阶段相关仓库 AGENTS.md，核验当前树后从首个未完成阶段实施。由主线程维护跨仓边界，按 §5.2 分配独占子任务，检查 shared-tree diff 与原始验证证据。新仓不带两个 Analyzer；历史提取可按 P1 降级。默认包消费与旧数据冷进程验证是必要验收。不要把规划中的脚本当作已有实现；远端操作按本会话实际授权执行。

## 6. 用户资源准备清单

以下为实施后的资源状态；不要求另备电脑、VM 或包服务。

| 资源 | 已准备/验证 | 仍需用户参与 |
| --- | --- | --- |
| Windows 工具 | Git、PowerShell 7、.NET SDK 10.0.201；三仓构建与本地包工作流可运行 | 无 |
| 新仓与历史 | `E:/repos/Atelia-org/atelia-storage`；历史过滤成功，259 个相关提交 | 无须学习或手动运行 filter-repo |
| 实验目录 | `E:/repos/Atelia-org/.storage-extraction/`，包括冻结旧九包、旧 writer/存档、独立缓存、日志与候选 | 暂保留供溯源；不是产品构建的隐式源码依赖 |
| WSL Ubuntu | Linux 自有文件系统 `/root/atelia-storage-lab/20260913/`；739 项测试与公开 API 包 smoke 通过 | 无 |
| 固定 SDK | 新仓 `global.json` 固定 10.0.201；WSL 的 SDK 安装在任务专属目录，未修改系统 PATH | 换机器时安装此 SDK；升级 SDK 时随源码提交产生新包版本 |
| 真实存档 | 冻结旧包生成的真实业务样本，最终新包冷读/续写/冷读通过 | 无需提供私人业务目录 |
| GitHub | 两仓已公开 push，Storage CI 通过；`nuget` environment 与 `NUGET_USER=Robird` 已配置 | 当前无需额外操作 |
| nuget.org | 用户已创建个人账号 Robird 和组织 Atelia；发布 workflow 已准备 | 按 P5 当前交接创建 Trusted Publishing 策略；不通过对话交付 token |
| 正式发布 | workflow 调用同一个 Pack 与 smoke，按依赖顺序上传 | P5 确认账号/包所有权及发布范围后再执行；发布后验证远端 Source Link 与全新缓存还原 |

本地首次 Prepare 可明确指定 `-SourceRepository E:/repos/Atelia-org/atelia-storage`，仍按固定 commit 取得源码。公开端点尚未可用时，不把本地 Prepare 成功写成远端取得成功。

## 7. 核验资料

- [GitHub：拆分子目录](https://docs.github.com/en/get-started/using-git/splitting-a-subfolder-out-into-a-new-repository)
- [git-filter-repo：路径筛选、历史 rename、commit-map 与 fresh clone](https://github.com/newren/git-filter-repo/blob/main/Documentation/git-filter-repo.txt)
- [NuGet：本地 feed](https://learn.microsoft.com/en-us/nuget/hosting-packages/local-feeds)
- [NuGet：缓存与包恢复](https://learn.microsoft.com/en-us/nuget/consume-packages/managing-the-global-packages-and-cache-folders)
- [NuGet：PackageReference 与 lock file 边界](https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files)
- [NuGet：版本规范与 metadata](https://learn.microsoft.com/en-us/nuget/concepts/package-versioning)
- [nuget.org：个人账号与 2FA](https://learn.microsoft.com/en-us/nuget/nuget-org/individual-accounts)
- [nuget.org：Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)
- [.NET：Source Link](https://learn.microsoft.com/en-us/dotnet/standard/library-guidance/sourcelink)
- [Codex：AGENTS.md 发现规则](https://learn.chatgpt.com/docs/agent-configuration/agents-md)

资料用于核实工具行为；产品取舍仍以 §1 用户决定和当前仓库事实为准。
