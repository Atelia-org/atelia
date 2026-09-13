# atelia-storage 拆仓验收证据

2026-09-13；阶段状态见[实施计划](atelia-storage-extraction-plan.md)。日志、冻结二进制与隔离候选在仓库外 `E:/repos/Atelia-org/.storage-extraction/`，以下路径以该目录为根。机器重启前中断的命令不算成功。

## 固定输入与交付边界

- 原 atelia：`6242f3bb6b631079d2513288ca54d823f630802f`；原 durable-graph：`4f74ee5662b54b68916bcd9d9759a708e802666b`。启动时均干净。
- 新仓：`E:/repos/Atelia-org/atelia-storage`，Git 历史过滤保留 259 个相关提交；过滤后 HEAD `8c43265b4d52a3f61d30dc3e7d6ac3c736ac8726`。清单、来源及 commit-map 保存在新仓 `docs/extraction-*`。
- 当前固定存储提交：`09d979941d2c671a1e7a8ffabfa6e2b340e00f69`，五个包统一版本 **`0.1.1-dev.20260913.4`**。程序集版本仍为 `1.0.0.0`，80 个 runtime C# 文件保持原基准字节。
- 新仓包含五库、五组测试、两个 benchmark 项目、独立 solution、MIT 许可证、包与验证脚本、公开 API 示例、GitHub CI 和手动发布 workflow。不包含原仓两个 Style analyzer 项目。
- Windows 上三个约 1 TiB 的 RBF 边界测试使用 sparse file 准备数据；只改测试准备，不改存储运行时逻辑。已删除本次旧测试遗留的一个确认过的 1 TiB 临时文件，证据 `removed-test-artifact.txt`。
- 两个消费者已从 `atelia-candidate`、`dg-candidate` 的完整二进制 diff 与新增文件哈希清单集成回原工作树；保留主线程的计划与旧数据 harness。Atelia 实际迁出 16 个目录、172 个受版本管理文件，并移除 12 个 solution 项目；旧 bin/obj 已移到实验根 `migrated-build-residue/`，不留重复源码。

## 已通过的验证

| 范围 | 结果与证据 |
| --- | --- |
| 原 DG 基线 | Release Rebuild 通过；2668 项测试通过，`baseline-dg-test-resumed.log` |
| 新存储 Windows | 739 项测试通过：Primitives 75、Data 212、Rbf 388、RbfSegmentStore 19、EventJournal 45；两个 benchmark 只构建 |
| 新存储 Linux | WSL Ubuntu 原生文件系统、固定 SDK 10.0.201、最终提交构建与 739 项测试通过；`/root/atelia-storage-lab/20260913/{build,test}-4.log` |
| 最终包独立消费 | Windows `storage-smoke-4.log`、Linux `smoke-4.log` 通过；单个 EventJournal PackageReference 经传递依赖取得五包，create/append/read/close/reopen |
| 包资产与来源 | 五包身份、MIT/README/XML、portable PDB、Source Link 映射和 80 个本地源码校验和通过；远端 Source Link 下载尚未验证 |
| 可重复包 | Windows 主分支、Windows 独立 detached clone、Linux 同一提交：全部五个 nupkg 和五个 snupkg 的 SHA256 一致，`package-repro-4.json` |
| 固定来源 Prepare | 两候选均从本地 Git 来源按最终完整 commit 检出并调用同一个上游 Pack；`dg-prepare-4.log`、`atelia-prepare-4.log` |
| DG 候选包模式 | `.2` 候选包 Release Rebuild、2668 项测试通过，`dg-candidate-{build,test}.log`；EventHistory/recovery 包 probes 通过，`dg-{eventhistory,recovery}-probe.log` |
| Atelia 候选包模式 | `.2` 候选包完整 Release Rebuild 通过；WalkingSkeleton 27 项全部通过，包括修改后的程序集依赖边界断言。全量测试的失败和覆盖缺口另列，不能宣称全绿 |
| 最终源码模式 | 两仓完整 Release Rebuild、五库 assets 路径检查通过；DG Storage 202 / Persistence 732、Atelia WalkingSkeleton 27 通过 |
| 错误配置与源码产包 | 两仓缺失 root、相对 root 各自明确失败；各自源码模式 pack 被拒绝，新输出目录均为 0 个 nupkg |
| 原 DG 最终包接入 | 完整 Release Rebuild、五包缓存哈希、EventHistory 与 recovery probes 通过；`dg-integrated-{build,eventhistory,recovery}.log` 与 `dg-integrated-assets.json` |
| Atelia 最终包测试 | WalkingSkeleton 27 项通过；StateJournal 分成慢用例 1 项与其余 1960 项，两段均通过；`mode-atelia-package-StateJournal-split-results/{slow-case,remainder}.trx` |

`.1`–`.3` 实验包保留用于溯源，不覆盖其内容。最终 Pack 固定 SDK，并规范化 .NET 10 生成的三份编译输入、等价 XML 和未签名 ZIP 元数据，以避免同版本在不同机器产生不同字节；产品源码、DLL/PDB payload 不作打包后修改。生成 XML 文档暴露原有 45 个注释警告，没有为消除这些警告改写 runtime 源码。

最终包模式 DG Storage 202 项通过；Persistence 首轮静默运行在 300 秒停止，有界复跑使用 90 秒无活动 watchdog 和逐项日志，732 项全部通过（约 244 秒）。保留首次超时及重跑日志 `mode-dg-package-Persistence-tests*.log`，不据此做性能等价声明。

StateJournal 最终包测试先后在 90 秒/3 分钟无活动阈值下中断，分别记录 636/836 项通过；没有把中断计数当作整组成功。唯一慢用例执行 516 次提交，最终在无其他 .NET 构建负载时单独通过（约 108 秒），其余 1960 项另一次全部通过。前两次日志和 Sequence 保留，最终覆盖是 **1 + 1960** 两段，不是单次整组通过。完整命令及边界见实验根 `source-mode-validation.md`。

补充编译体核查：`.2` 与 `.4` 五包全部 1394 个方法的名称、签名、属性、IL、local signature、maxstack、initlocals 和异常区间逐项相同（`runtime-il-comparison.json`）。这是限定范围的编译体比较，不是包文件或所有程序集元数据相同的声明。

原 Atelia 集成后完整 Release Rebuild 通过。首次 WalkingSkeleton 为 26/27：旧测试只用 `/bin/`、`/obj/` 判断，Windows 上误扫历史 DerivedRecap 的 obj JSON。修复复用既有 `IsBuildOutput` 并统一 `.atelia` 路径分隔符，保留全部产品断言和旧残留用于复验；这项修复同时进入候选和干净物化树，不能冒充初始候选快照的一部分。
原工作树修复后重跑 **27/27** 通过（`atelia-integrated-walking-fixed.log`），五个最终包的缓存哈希也匹配 Prepare manifest（`atelia-integrated-assets.json`）。独立 Reviewer 核实原筛选误纳入 15 个历史 obj 文件，修复没有放宽产品扫描范围或断言。

## 旧数据冷进程 witness

`old-feed` 的旧五库与四个 DG 包版本均为 `0.1.0-extraction-old.20260913.1`，由原基准生成并冻结；`old-package-hashes.json`、`cold-witness/seed.json` 保存身份。旧 writer、业务模型、DG 二进制及存档未被重建覆盖。

最终 `.4` 包使用 `Run-StorageExtractionProbe.ps1 -Stage Complete` 验证通过，日志 `cold-witness-complete-4.log`，结果目录 `cold-witness/attempts/bfca6beb51de4bb583a4f9818b818d1b`。三个独立进程完成只读冷开、续写、再次冷读；两条 lane 保持相同 DG 产品包和模型，只改变五个存储包。Schema、State、Journal event/ref-op/ref-object 的实际文件、领域环、容器及 ref/parent/head/pending 状态通过；18 个旧完整 frame 的地址和内容不变，Schema history 不变。没有把派生 cache 或整个续写后文件哈希当正确性判据。

## Atelia 既有失败与覆盖限制

原仓与候选均有大量 Windows 平台失败；部分测试直接依赖 Linux 工具或平台语义。原基线在宿主异常期间中断，重启后补测仍有长测试触发 3 分钟无活动 watchdog，因此不把已记录的通过计数当作整组通过。

候选全量运行排除了基线已经卡住的六项长测试（`atelia-baseline-incomplete-tests.txt`），除此之外执行整个剩余 solution，日志 `atelia-candidate-test.log`。原始 TRX 在 `baseline-results/` 与 `candidate-results/atelia/`；`summarize-tests.py` 对比精确测试名、错误消息和中断信息。独立审阅 `atelia-test-triage.md` 逐项归类，未发现已证实的迁移回归：

- 1141 个共有失败；92 个基线未见失败为 69 个 Manager、22 个 CLI 的共同平台前置失败，以及 1 个 OpenRouter live TLS EOF。原仓补测 2 个 Manager 和 8 个 CLI 代表，10/10 与候选同名同错；这不代表被平台前置阻断的业务场景通过。
- 15 个消息差异中，14 个仅为 console/TRX 显示缩进，1 个为原基线并行日志混入其他测试 stack。原始差异保留。
- 摘要脚本误将测试名的 Crash/Abort 当作宿主中止，已修复；实际只有 SessionJournal 一组中止，已记录 495 项通过，不是该项目全通过。
- 额外长用例 `SessionHistoryPlanningTests.DurableSetupSeed_KeepsTenThousandTurnColdPrefixOutOfReads` 在原仓独立补测与候选中均触发 3 分钟 watchdog，Sequence 均 `Completed=False`；记录为既有覆盖限制。原仓补测有跨工作树并行，不能据此声称性能等价。
- 保留 7 个共有失败缺少完整可比消息、9 个 xUnit 截断 theory 名以及 Galatea.Server 1 个 NotExecuted 的证据限制。

## 干净复验、交付与后续工作

P4 干净 Windows 物化复验通过，详见实验根 `fresh-validation-4.md` 和 `fresh-4/final-audit.json`：两仓均由完整 binary patch 与新增文件哈希物化，使用各自全新的 NuGet/HTTP/TEMP、工作树及输出目录。正常 Prepare 从最终存储 commit 重新打包，各 10 包哈希与 canonical feed 一致；完整 solution 构建、DG Storage 202 项、Atelia WalkingSkeleton 27 项和实际私有包缓存检查通过。Atelia 的 Windows guard 修正作为单文件 delta 单独记录并重编测试，最终候选 digest 为 `c0bf29784e26ec7ff149012893d05f5b4320ea4cd0817977fcd705066ca57691`；不把候选 digest 冒充消费者 commit。

两原仓的实际输出 DLL 也逐个与最终包内 DLL 比较，10/10 字节匹配（`integrated-output-dlls.json`）。独立 Reviewer 未留下阻断问题。DurableGraph 已本地提交 `f80da5e09237ef9b9639abf1a6037ab23f1372df`；Atelia 的最终改动及本记录随迁移提交交付，存储 pin 不变。原树比干净候选多出的收尾内容涉及计划、验证说明、固定版文档链接、DG 冷进程 harness 及下面的提交格式化；harness 已另行实际运行和独立审阅。

Atelia 已安装的 pre-commit hook 另对同一个架构测试文件执行四轮格式化并收敛，随后恢复原 `.editorconfig`。最终文件与干净候选的 6159 个 C# token kind/value 逐项相同，Roslyn `NormalizeWhitespace` 输出字节也完全相同，记录在 `hook-format-equivalence.json`；最终文件 SHA256 为 `6236b150c268488ff35a5db4939182982f4638936f0bf04658db3c3d327e02a7`。这项格式化不冒充初始快照字节。
格式化后的最终文件重新编译，WalkingSkeleton **27/27** 通过（`atelia-post-commit-walking.log`）。

没有创建 GitHub 远端、push 或发布 NuGet 包。现有 Git credential 的只读核实表明 Robird 对相关仓和 Atelia-org 有管理权限；用户仍需确定新仓可见性与 nuget.org 包账号。准备好的 workflow 使用 Trusted Publishing，用户无需在对话中提供 token。公开源可取得与远端 Source Link 必须在 P5 实测。
