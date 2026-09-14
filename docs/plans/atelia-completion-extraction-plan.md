# atelia-completion 分阶段拆仓实施方案

> 状态：2026-09-14 P0–P3 拆仓交付完成。四包 `0.1.0-preview.1` 已发布，公开还原/签名/符号验证通过，Atelia 已合入公开包引用并完成默认路径复验；证据见 [验收记录](atelia-completion-extraction-validation.md)。P4 DramaBoard 接入未开始，是后续独立消费者里程碑。
> 本文保留阶段要求，不替代实际验收证据。用户已完成 NuGet policy 并交回继续 P2/P3；发布按会话授权完成，不从文档推导额外权限。
> 只读调查基准：Atelia `60eb58a11649ba6097c15918c58566068ea68b0d`，DramaBoard `d3d6cefc5b4faee652e12fd47d3ed5626b226efb`；两仓调查时干净。实施前重新固定基准，不假定这些 HEAD 仍是最新。
> 精简裁决见 [审视记录](atelia-completion-extraction-review.md)；实施者以本文为准，无须复盘讨论过程。

## 1. 目标、边界与需求来源

把 Diagnostics、Completion.Abstractions、Completion、Completion.Tools 迁到 `Atelia-org/atelia-completion`，形成可独立构建、发布、使用的四个包。Atelia 改用新包，保留显式源码联调；DramaBoard 随后通过现有 `ILlmChatBackend` 接缝验证实际使用价值。

| 编号 | 要求 | 来源 |
| --- | --- | --- |
| R1 | 仅新建 completion 仓；不建立 basics，不移动 storage 中的 Primitives、Data | 用户选择方案 1 |
| R2 | 保留四项目、程序集名、namespace、PackageId、TFM 和现有业务/API/序列化语义 | 已接受的方案 1；当前项目和真实消费者 |
| R3 | 新仓去掉两项 Style Analyzer 项目引用；不连带清理原 Atelia 的 Analyzer | 沿用用户接受的前轮建议；根 props 的构建注入 |
| R4 | 默认明确版本 PackageReference；Atelia 可显式使用源码；包身份与来源可追溯 | 已接受的拆仓建议、上次交付经验 |
| R5 | 先验证原 Atelia 消费者，再验证 DramaBoard 的角色决策和记忆维护；Tools 的玩法接入后置 | 已接受的推进顺序；DramaBoard 现有后端接缝 |
| R6 | 保留完成状态、取消、结果不确定、usage 未知值、工具权限及宿主副作用责任 | 当前 Completion README、CompletionResult、ToolSession 与消费者测试 |
| R7 | 优先保留相关 Git 历史；有限尝试失败时用固定来源快照 | 沿用此前用户允许的拆仓取舍；不以历史完整性阻塞主目标 |
| R8 | 自动化入口少而明确；Agent 能找到匹配包版本的用法、源码、验证命令 | 用户的可分阶段实施目标与前轮 Agent 易用性要求 |

不纳入：日志框架替换、API 重命名、统一所有 Result 类型、增加 provider、修改凭据刷新职责、迁移存储格式、替换 DramaBoard 的全部后端、Tools 直接修改游戏世界、通用跨仓发布平台。发现独立产品问题先记录，不混入搬迁修复。

实际模型：HTTP/SSE 客户端调用由宿主传入 CancellationToken 控制期限；显式 provider terminal 决定完成状态；terminal 前流中断意味着结果不确定，不能透明重试。ToolSession 的权限与执行序号不等于持久化事务；副作用、世界仲裁与提交仍归宿主。已有 SessionJournal 会持久化 Completion 相关请求/消息，因此公开合同及既有回放测试仍需保留，但本次不复制 storage 的整套旧 writer/冷存储验收设施。

## 2. 当前事实与提取清单

MSBuild 求值确认下面的运行时关系；根 props 另注入两个 `OutputItemType=Analyzer`、`ReferenceOutputAssembly=false` 的构建引用。

```text
Completion ────────→ Completion.Abstractions
           └──────→ Diagnostics
Completion.Tools ─→ Completion.Abstractions
                 └→ Diagnostics
```

| 项目／包 | TFM | 归属约束 |
| --- | --- | --- |
| Atelia.Diagnostics | netstandard2.0 | 通用调试工具，可单独消费；不依赖其他三个包 |
| Atelia.Completion.Abstractions | net10.0 | 合同层，不依赖 Diagnostics、provider 或 Tools |
| Atelia.Completion | net10.0 | provider、传输、调用与配置实现 |
| Atelia.Completion.Tools | net10.0 | schema、绑定、权限、分发；不依赖 Completion 实现层 |

四者均不依赖 Primitives、Data。同仓不等于捆绑引用，不新建总括四包的元包。Atelia 的 StateJournal 应继续只直接引用它实际需要的 Diagnostics。

| 处理 | 路径 | 说明 |
| --- | --- | --- |
| 迁出 | `src/Diagnostics/`、`src/Completion.Abstractions/`、`src/Completion/`、`src/Completion.Tools/` | 包含已有 README；保持项目名 |
| 迁出 | `tests/Completion.Tests/` | 一个现有测试项目覆盖合同、provider、传输、Tools；不拆成四个测试项目 |
| 迁出并整理 | `docs/Completion/` | 当前使用/传输/协议指南继续维护；实验记录逐项判定，原始 live 数据不是新仓构建前提 |
| 选择性复制 | `LICENSE`、`.editorconfig`、`.gitattributes`、`.gitignore` | 只带必需配置，不复制原仓全部业务规则 |
| 新建 | `Atelia.Completion.slnx`、`global.json`、根构建配置、README、AGENTS.md | 独立闭包；SDK 在 P0 选定并记录 |
| 检查后补充 | 测试资源、脚本、历史 rename 路径、必要指南依赖 | 由文件与 Git 清单确定，不凭目录名推断 |
| 留在原仓 | SessionJournal、Galatea、MemoPod、StateJournal 及其业务测试/规范 | 调整引用和边界断言；业务合同不搬到 Completion |

必须处理的已知接缝：

- `src/Completion/Properties/AssemblyInfo.cs` 向 Completion.Tests 和 `Atelia.SessionJournal.RecapGrid.Runtime.Tests` 开放 IVT；后者的 `RuntimeProviderProjectionTests.cs` 直接调用内部 converter。
- `tests/SessionJournal.RecapGrid.WalkingSkeleton.Tests/AssemblyDependencyBoundaryTests.cs` 写死 Completion 项目路径、包集合、源码位置及 IVT 文本，不是改 csproj 就能结束。
- 直接消费者至少含 StateJournal、SessionJournal、SessionJournal.Cli、RecapGrid、RecapGrid.Hosting、HistoryTimeline.O200k、Galatea、MemoPod、MemoPod.DebugApp 以及相关测试。P0 以全仓搜索和 MSBuild 补齐，不能把这份“至少”清单当全集。
- `docs/Completion/quick-start.md` 的旧入口假设本机 `localhost:8888` 服务；新仓默认示例应能离线运行，live endpoint 作为显式配置。
- `60eb58a1` 已支持 Linux/Windows 文件凭据读取；“只支持 Linux”不再是当前约束。仍不读取仅在 OS keyring 的凭据、不负责刷新/写回；可选 raw exchange 文件 sink 仍有限制，按当前源码和平台测试记录。

## 3. 最小交付约定

### 3.1 包、来源与联调

首次四包统一版本，以 `CompletionPackageVersion` 指定；不与现有 StoragePackageVersion 或下游自身版本绑定。每个不同内容用新版本，不用 `+metadata` 区分内容，不覆盖公开版本。示例 `0.1.0-dev.20260914.1` / `0.1.0-preview.1` 只是格式，P0/P3 查占用后选实际版本。

Atelia 以 `eng/CompletionDependency.props` 集中记录 `CompletionPackageVersion`、`CompletionSourceRevision`、`CompletionRepositoryUrl`。源码开关为 `UseCompletionSources=false`，显式启用时要求 `CompletionSourceRoot` 为绝对路径并包含四项目。包/源码引用互斥；缺失路径报错，不自动搜索兄弟目录或回退。Diagnostics 也参加同一开关，避免同一构建图引入两份身份相同的程序集。

与 storage 的开关各自独立。本次验证“默认全包”与“Completion 源码＋Storage 包”两条必要路径，不默认扩成所有开关组合矩阵。源码模式只用于 build/test；消费仓 pack 若启用 Completion 源码应清晰拒绝。未来确有下游产包任务时，先打唯一版本的上游开发包，再在包模式选用它并核验依赖版本；本轮不新建下游实验包交付线。

新仓只提供两个薄入口：`eng/Pack.ps1` 负责四包构建、明确版本与来源/哈希摘要；`eng/Test-Package.ps1` 负责隔离目录中的离线 public API 验证。优先标准 dotnet/NuGet，不引入 ZIP/PE 归一化、跨机器字节一致承诺、通用 manifest 框架。SDK 固定并不等于编译宿主 CLR patch 也固定。

发布前各验证者消费一次 Pack 产生的冻结候选 feed。feed 通过显式临时 NuGet.Config 及唯一版本接入，隔离父级 MSBuild 配置与包缓存，候选四包的来源映射精确匹配。公开包可取得后普通 build 直接从 nuget.org restore，不强加 clone/Prepare 前置步骤。DramaBoard 首轮只做包消费，不为它预建第二套源码联调入口。

### 3.2 日志、测试边界与兼容性

保持 DebugUtil 当前语义，先明确包化后的区别，不借迁移重构日志：

- Release 编译 Completion/Tools 时，其中的 Trace/Info 调用已被 Conditional(DEBUG) 裁掉，下游 Debug 编译或环境变量无法恢复它们。
- 下游自己的 Debug 调用点可以保留 Trace/Info，但已发布 Diagnostics 的默认 sink 级别由其构建配置决定；需要显式环境级别配置。
- 文档说明 Warning/Error 和现有显式日志入口的可用范围；需要库内部 Debug 诊断时使用源码 Debug 联调。保留真实默认目录、控制台与文件行为，不承诺“设置类别即可恢复所有日志”。

跨仓 IVT 在 P1 首次冻结包之前移除：Completion 内部投影规则由新仓自身测试承担；P2 的 RecapGrid 仍用真实 runtime 产生请求，并通过 public client＋可控 HTTP handler 检查最终 wire 请求。保留原有 OpenAI Chat、Responses、Anthropic、Gemini 四路及既有字段断言（含 Anthropic cache_control），不扩成四 provider 的全部故障矩阵。不能把全部下游测试搬走而丢失组合覆盖，也不能删除结构断言或扩大 public API 来迁就测试。

没有 runtime 修改时，用来源文件差分及现有合同/回放/恢复测试证明搬迁边界。若发现必须修改序列化字段、fingerprint、reasoning payload 或恢复语义，先拆成独立问题；不能把行为变更伪装成搬家。受影响的 SessionJournal 已存请求/消息样本应复用现有固定 fixture；缺少必要样本时补一个有明确旧来源的窄样本，不新建存储兼容平台。

### 3.3 面向 Agent 的入口

README 包含四包地图、TFM、安装版本、一个可运行文本示例、构建/测试/Pack 命令和文档导航。Diagnostics 单独说明可独立引用，Tools 复用现有 README。AGENTS.md 只放协作与验证入口；消费者导航记录选定版本、固定 tag/commit 的文档和可选源码位置。

最低使用合同：client/HttpClient 的所有权与释放；调用者取消；Completed/Incomplete/Failed 与结果不确定的区别；正文与 reasoning 的边界；usage 的 null 与零；Tools 参数校验、权限和宿主副作用责任。Source Link 帮助定位对应源码，不会自动把源码或指南装入 Agent 上下文。

包内含 MIT/LICENSE、适合该包的 README、XML 文档、Repository URL/commit、portable PDB/Source Link。核验实际 nupkg 内的示例版本和链接，不只看仓库根 README。现行指南只维护一份；历史文档保留来源说明，不以全库修复历史聊天链接为完成条件。

## 4. 分阶段任务与退出条件

阶段实际状态以验收记录为准。P0–P3 完成拆仓交付；P4 完成首个新增消费者接入，两项里程碑分别报告。远端身份准备可与本地工作并行，不把账号等待变成停止本地验证的理由。

### P0：固定范围与基线

**输入**：本方案、各仓当前 AGENTS/项目状态、当前代码。

1. 记录 Atelia 的完整 HEAD、工作树差异、remote、SDK/CLR 与工具可用性；只读确认 DramaBoard 当前后端接缝，其实施基线留到 P4。当前 storage pin 作为不变对照记录；不升级 Storage 或 DurableGraph。
2. 固定路径清单，核查 props/targets、solution、IVT、test-data、嵌入资源、脚本和活动文档。用标准 MSBuild 求值与 restore assets 核实实际身份/依赖；记录 Diagnostics 的 netstandard2.0，不能被新根配置改成 net10.0。
3. 建立现有 Completion.Tests 的离线 Windows/Linux 基线；记录平台限定与 opt-in live 测试。验证进程显式关闭 live opt-in（当前包括 `ATELIA_RUN_CODEX_REASONING_REPLAY_LIVE`），不继承用户环境而意外发起真实调用。Atelia 做 Release solution build，运行受影响的结构、RuntimeProviderProjection、SessionJournal 请求/回放/恢复和相关消费测试。依据当前覆盖确定具体测试集合及命令，记录原有失败，不复制上次测试数量冒充本轮结果。
4. 对启动时已有修改，保留用户工作；候选身份为 base＋`git diff HEAD --binary`＋新文件内容/哈希，不能仅 clone HEAD 漏掉 staged 删除或新文件。冻结范围内源码和必要旧合同样本的身份。

**输出／退出条件**：可执行路径清单、精确验证命令、来源及基线摘要；能区分已有失败与迁移变化。工具/平台暂缺时明确哪项基线待补，不记为通过。

### P1：独立源码与候选包

1. 在任务专用 fresh clone 中按清单做 `git filter-repo`，追查必要旧路径；不在原仓过滤历史。正常尝试一次，至多一次有明确原因的修正，仍阻塞时改用固定来源快照。记录原 commit、路径、方式；过滤成功保留 commit-map。保留原仓历史和旧 tag。
2. 新建独立 solution、精确 SDK 配置及最小 props/targets，去掉两项 Analyzer，保持包/程序集/TFM 身份。来源说明写入新仓 `docs/extraction-origin.md`。四库和迁出测试在不访问 Atelia 的环境中构建、运行。
3. 保留 Completion 自身测试 IVT，删除对 RecapGrid 测试的跨仓 IVT，再冻结候选包；原 Atelia 此时仍用原源码，P2 再替换其测试入口。迁出当前指南并修正新包消费入口。
4. 实现 §3 的 Pack/Test-Package。一个独立可执行 smoke 只选一种现有 provider 协议，通过真实 public client 与可控 HTTP/SSE 输入验证文本、显式非成功、取消、terminal 前 EOF、未知 usage；Tools 用一个无副作用 DTO/方法验证 schema、绑定和执行。完整 provider 矩阵复用迁出的 Completion.Tests，不另复制。不得只伪造 ICompletionClient 返回值来证明 provider 包可用。
5. 核验四包闭包和单独 Diagnostics/Abstractions 的依赖：前者不拉入 Completion，后者不拉入 Diagnostics。检查 nupkg 文档、XML、PDB 与本地来源对应；远端 Source Link 留给 P3 验证。

**退出条件**：新仓独立构建及离线测试通过，冻结候选 feed 可在独立缓存中运行 public API 示例，实际解析的四包版本/来源有记录；原仓尚未删除源码。

### P2：Atelia 原消费者切换与集成

1. 在隔离候选工作树中把引用切换、移除四源码项目及 Completion.Tests 的 solution 项、迁出源码/测试/文档、更新导航作为同一变更集。原仓保留迁移计划及业务规范；不只从 solution 隐藏旧源码。
2. 实现 `CompletionDependency.props` 与双模式。逐一处理直接消费者；Diagnostics-only 项目不增加 Completion 依赖。现有 StorageDependency 配置不变。
3. 按 §3.2 改写 RecapGrid 的组合投影测试，使用已无跨仓 IVT 的候选库；调整 WalkingSkeleton 的路径/包集合/源码检查，把实现内部检查移到归属新仓的测试，保留消费者架构断言。外部引用只列明确终点，不以跳过未知路径让测试变绿。若集成中仍需修改新库，分配新开发版本、重新冻结 feed 并复验受影响包路径，不能复用 P1 旧包冒充新源码。
4. 默认包模式先 Release Rebuild，再执行 P0 的受影响测试；显式源码模式 build/test 并核实四项目来源，验证无效路径与源码 pack 请求失败。源码模式与包模式切换均重新 restore。
5. 在干净物化目录、私有缓存中复验完整候选，核对没有遗漏 untracked 文件或 staged 删除；审查最终 diff、旧路径与候选集成结果。所有修正纳入实际验收输入，提交 hook 改文件后按影响复验。候选可在隔离分支本地提交，但不将默认引用仅存在于任务 feed 的版本合入或推送 Atelia 的共享 main。

**退出条件**：Atelia 候选的两种必要模式通过；已有失败与未完成项单列；候选内旧目录不再保留活动源码；跨仓 IVT 已解除且原组合行为仍受验证。记录 Atelia 候选与新库候选的精确对应，不以兄弟仓最新 HEAD 代替版本身份。共享 main 的切换留到 P3 公开包可取之后。

### P3：公开交付与默认构建收尾

1. 准备新 GitHub 仓、MIT、Windows/Linux 离线 CI、`publish.yml` 与 `nuget` environment。四包初始同版交付；先 Diagnostics/Abstractions，后 Completion/Tools。发布触发精确关联 commit/tag/version，并保存 run ID。
2. 采用 nuget.org Trusted Publishing；现有 storage policy 不适用于新仓。新 policy 建议名 `atelia-completion-publish`，包 owner 使用现有 Atelia，目标 GitHub `Atelia-org/atelia-completion`、workflow `publish.yml`、environment `nuget`。按实际四包设置 scopes/patterns；个人登录账号与组织 owner 区分，实施时查官方当前规则。
3. 正式发布前，针对实际候选 ref 构建的 Release 包再次跑离线 smoke，检查包内安装版本、固定文档链接、来源及许可证。未提交文件不得混入公开产包身份。发布过程中部分成功时核实服务状态，不用同版本不同内容补发，不默认删除/unlist 旧版。
4. 完整四包可从 V3 实际下载后，用新缓存、仅公开源运行同一 smoke；验证签名、包身份和所承诺的符号/远端 Source Link。上传成功、搜索可见与包可还原分开记录，不能用本地 snupkg 冒充公开符号可取。
5. Atelia 候选更新为最终公开版本与对应 revision，默认 restore 不需要新仓检出或准备脚本；Rebuild 并复验受影响测试后，按已有授权集成到共享 main 并推送。发布日志只留摘要与证据链接；日常入口回到 README/依赖指南。

**退出条件**：四个公开包实际可还原，公开包 smoke 与 Atelia 正常获取路径通过；各仓提交/远端状态如实报告。身份配置暂缺时保留 P2 的本地验收结果并报告 P3 未完成。

### P4：DramaBoard 的窄接入

**输入**：P3 的明确公开版本；开始时重读 DramaBoard 项目状态，固定 HEAD/工作树差异，并在修改前运行 Player.Llm 与 Demo 相关基线。遵循该仓既有包准备流程，避免与其其他任务交叉改动。

1. 在 Player.Llm 内增加一个基于 ICompletionClient 的 `ILlmChatBackend` 实现，并给现有 Demo 组合提供可选择的实际入口；首轮选一个已有协议路径即可，不同时切换所有后端或重写 app-server。
2. 角色决策与记忆维护都使用这个后端接缝。仅 Completed 正文进入现有业务解析；Incomplete/Failed 明确失败，结果不确定不进入透明重试。保留 LlmPlayerDriver 对成功完成但业务格式非法的正文进行一次格式纠正请求的既有行为。只映射有依据的 usage，缺失字段保留 null；不把未证明的 queue/service 时间填成零。沿用 Demo composition 先收口 driver/记忆任务、再释放共享 backend/client 的所有权顺序，不在单次调用结束时释放共享传输。
3. 新适配器承接现有 RequestTimeout 配置：每次调用由宿主/适配边界创建 linked CTS，对完整调用施加预算；用户取消按原 caller token 报告，自身期限到达明确报告超时。可复用该仓 CodexAppServerBackend 的现有约定，不给 Completion 库新增超时策略。旧 HTTP 后端使用 ResponseHeadersRead，其 HttpClient.Timeout 不保证覆盖后续正文读取；这里明确的是新适配器的完整调用预算，不宣称旧实现已有该保证。
4. 用真实包、public client、可控 HTTP 输入运行 Player.Llm 与 Demo 组合路径，覆盖成功、非成功、用户取消、请求超时、流中断和 usage 未知值；普通后端 fake 的测试不能替代这一组合证据。确认 Player/Kernel/Protocol 的边界没有被 Completion 类型侵入。
5. 真实 provider smoke 为有凭据时的独立可选验收，明确 provider/model/platform/结果，不把离线 fixture 描述成真实在线调用。Tools 的游戏接入、UI 流式展示、记忆持久化对齐继续按各自真实需求推进。

**退出条件**：DramaBoard 的真实包适配器从现有业务入口可用，角色决策＋记忆维护及错误/取消路径通过；说明在线验证是否执行。不升级其 DurableGraph/存储依赖，不改变 Intent 仲裁与世界提交。

## 5. 自动化分工、证据与资源

后续实施可按“新仓交付”“Atelia 消费与结构测试”“DramaBoard 适配”划分不重叠子任务，主线程掌握来源/包版本合同、共享构建文件和最终集成。P1 产包前不让消费者自行重建同版本库；P4 可先只读调查，依赖包就绪后再实现。独立 reviewer 审查最终产物与真实消费者，不用 worker 摘要代替验收。同一可写 .NET 构建图中的 build/test/pack 串行运行。

只维护一份实施验收记录，实施时再创建 `atelia-completion-extraction-validation.md`，记录阶段状态、来源/版本、命令、TRX 运行状态、已有失败、未完成项和日志位置。大日志/包/临时工作树放在仓外任务目录或忽略的 artifacts；过期日志不是新消费者的日常依赖。中止的测试运行不能因已有 Passed 用例就算完成，不为无关长测反复扩充全量验证。

| 资源 | 何时需要 | Agent 可做的准备／需要用户配合 |
| --- | --- | --- |
| 本机 Git、可用 Python/filter-repo、.NET SDK、PowerShell、临时磁盘目录 | P0/P1 | Agent 检查实际可执行路径，建立任务目录；不改系统服务或全局缓存 |
| Linux 环境 | P0/P1/P3 平台验证 | 优先已有 WSL 任务目录或 GitHub Linux runner；不要求额外机器/VM，本轮不重跑 storage 的大型边界测试 |
| 新 public GitHub 仓 `Atelia-org/atelia-completion` | P3 | Agent 先准备本地可审查结果；已有授权与账号权限够时自动创建/推送，否则给出准确操作项 |
| 现有 nuget.org Robird 账号与 Atelia 组织 | P3 | 不需要新账号或长期 API Key；用户在身份页面创建匹配新仓的 Trusted Publishing policy，必要时完成登录/2FA；Agent 准备 workflow/environment/变量和精确字段 |
| DramaBoard 工作树 | P0/P4 | Agent 读取并保留已有修改；与并行任务冲突时先隔离，确实无法决定再请用户协调 |
| 模型 endpoint 或 Codex 文件凭据 | 仅可选 live smoke | 不阻塞离线构建/发布；需要在线证明时再请求对应可用配置，不把 token/auth 文件复制进新仓或日志 |

此方案没有需要现在中断文档工作向用户索取的资源。未来仅在身份交互、缺少实际权限、无法隔离的用户改动冲突或必须改变产品语义时请求参与，并说明具体未完成步骤。

## 6. 官方核验入口

实施时核对当前工具规则，不把 storage 案例中的包清单和脚本照搬：

- [Git 子目录拆分](https://docs.github.com/en/get-started/using-git/splitting-a-subfolder-out-into-a-new-repository)
- [NuGet PackageReference](https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files)
- [Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing) 与 [NuGet/login](https://github.com/NuGet/login)
- [NuGet 包元数据](https://learn.microsoft.com/en-us/nuget/reference/nuspec) 与 [Source Link](https://learn.microsoft.com/en-us/dotnet/standard/library-guidance/sourcelink)

前次经验入口：[storage 验收记录](atelia-storage-extraction-validation.md)。本次以当前代码与 P0 基线为准；前次成功不能替代本轮验证。
