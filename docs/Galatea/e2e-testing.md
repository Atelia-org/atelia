# Galatea E2E 操作指南

状态：现行操作经验；随相关实现一起维护。本文回答“下一次怎样测”，[Scenario lab](scenario-lab.md)维护合成场景，[Server API](server-api.md)维护协议，[实机验收记录](identity-simplification-design.md#11-唯一-dev-实例-e2e2026-09-14)保存单次结果。历史通过数不代表当前版本已验证。

## 1. 维护位置与使用方式

项目内文档是本流程的维护入口。Galatea 的恢复状态、Note/邮件回执、RecapGrid 与浏览器行为都随代码演进，改动这些行为时在同一提交中更新相关说明。现在不建立第二份 skill；只有出现多个项目共用的稳定流程时，再考虑抽取通用部分，Galatea 细节仍留在仓库。

高频、稳定的机械步骤适合逐步提取为项目脚本，文档保留目的、边界、命令入口和失败处理。不要把含个人环境路径、真实实例内容或单次失败特例的临时 driver 直接升级为正式工具。

## 2. 先选择验证范围

| 范围 | 适用问题 | 不能替代什么 |
|---|---|---|
| 定向测试与合成 Scenario lab | 状态机、明确 crash 窗口、零重复调用、旧格式 fixture | 当前账号、真实中转站、现有实例能否运行 |
| 隔离 live canary | 当前 provider/凭据与真实转换器是否可用 | 长期保存的 Journal、Note、邮件和摘要能否一起重开 |
| 现有 Dev 实例 + 真实浏览器 | 旧数据升级、登录、恢复、发送、页面显示和冷启动 | 未实际触发的委派、自动轮次或其他模型路线 |

按本次改动选范围，不要求每个小修都调用真实模型。涉及持久格式或升级恢复时，至少使用独立的旧格式样本；不要只用当前 writer 反造“旧样本”，否则 writer 的变化可能一起污染测试输入。

## 3. Dev 实例的一次完整流程

1. **记录起点。** 核对 Git HEAD、工作区、实际 ConfigPath、用户的 session/CharacterMemory/delegation 路径、连接和后台 enrollment。检查进程及打开文件；锁文件存在本身不等于仍有 owner。沿用本轮已有的真实调用、恢复和撤销授权，不逐步重复询问。
2. **停服备份。** 备份覆盖本轮操作的完整数据范围，验证归档能打开。2026-09-14 的唯一实例是 `prototypes/Galatea/.atelia`，用户是 `cyber`/`gpt`；这些是当时配置，下一次重新读取。已有本机 `gitignore/backup-galatea.sh` 只覆盖 `.atelia/galatea`，不能把它说成完整 `.atelia` 备份。
3. **先做离线盘点。** 每个活动命名分支做 full audit，按需补 selected-lineage audit；记录 head、phase、Prepared 版本分布及相关 SQLite 健康状态。发现旧 Prepared/Started 时先确定恢复路径，不先发送 fresh 探针。`quick_check=ok` 只证明 SQLite 结构，不能证明应用的 strict open 或 Host attach 成功。
4. **启动实际配置。** 显式保证 ContentRoot/工作目录正确，使用现有连接与环境变量；先经真实浏览器登录，验证 current、recent、Agent、Mailbox、`recap-cadence-progress` 和 Recap 显示。已配置摘要时还应检查 context header 正文确实显示。HTTP 200、登录成功、数据库健康各自都不等于完整会话可用。
5. **执行有界操作。** 必要时明确恢复旧请求，再各测一条目标路线。确认终态后真正停服、重开，核对已显示的输入/回答，再发下一轮验证重启后可继续使用。每次 mutation 只提交一次；响应丢失或脚本报错后先查 current、SSE 与日志，不盲目重发。
6. **清理本次探针。** 如需要撤销，先核对最新真实用户卡片确属本轮测试，再使用页面 Undo。不要因为“知道点了几次发送”就连续撤销任意最近轮次。保留本来就待恢复的真实剧情结果。
7. **检查结束状态。** 再冷开页面，停服后复核 audit、数据库及关键旧记录；比较的是约定的语义状态和保留字段，不默认要求整个目录字节完全不变。记录是否留下运行进程、备份位置和无法由 Undo 撤销的影响。

后台已 enrollment 的用户会正常检查 Ready 回信并启动自动轮次；主线还可能调用 normalizer、recall、Note/邮件 extractor 和 DerivedInfo。它们影响调用数量和状态，不能把观察到的所有调用都算成主模型重试，也不能关闭这些功能后宣称验证了完整配置。

### 离线与非 Live 命令

从仓库根目录执行；先准备对应构建产物。CLI 的 `validate` 不接受 `--help`，用其文档和源码核对选项。

```bash
dotnet prototypes/SessionJournal.Cli/bin/Debug/net10.0/Atelia.SessionJournal.Cli.dll \
  validate --input <session-dir> --branch main --report-json <private-report-path>
```

现有 CLI 没有“一条命令完成所有分支与版本计数”的入口。需要时用短小的只读 harness 调用 `ListBranches`、`ScanCheckedAuditEvents` 和 `BeginSelectedLineageAudit`；`ListBranches` 只代表活动命名分支。不要拿会写入的 `timeline sync` 代替审计。

非 Live 全套使用明确的类过滤，而不是 `!~Live`：后者也会排除包含 `Delivery` 的名字。

```bash
galatea_nonlive_filter='FullyQualifiedName!~CharacterNoteTranscriptionLiveTests&FullyQualifiedName!~GalateaCodexDelegationLiveTests&FullyQualifiedName!~GalateaScenarioLabLiveTests'
env -u ATELIA_RUN_GALATEA_NOTE_LIVE \
    -u ATELIA_RUN_GALATEA_LAB_LIVE \
    -u ATELIA_RUN_GALATEA_CODEX_DELEGATION_LIVE \
  dotnet test tests/Galatea.Server.Tests/Galatea.Server.Tests.csproj \
    --no-restore -m:1 -nr:false --filter "$galatea_nonlive_filter" \
    -- xUnit.MaxParallelThreads=4
```

新增 Live 测试类时同步更新筛选条件。确认实际执行数大于零；不把跳过或提前返回当作外部调用成功。重 .NET 验证串行运行；短时限测试失败先隔离复现，再检查调度竞争，不为消除测试抖动放宽生产 deadline。

合成场景的既有入口是 `scripts/test_galatea_lab.sh`，其 `--live` 为独立显式 canary；具体用法见 [Scenario lab](scenario-lab.md)。它不会替你测试现有 Dev 实例。

## 4. 真实浏览器的观察方法

先确认本机已有的 Playwright/Chrome，避免每轮重新安装。2026-09-14 使用的是机器上的 `playwright-core` 和 `/usr/bin/google-chrome`，不是 Galatea 已声明的项目依赖；不要把当时的 NVM/OpenClaw 安装路径硬编码成项目合同。使用独立 browser context，不复用个人浏览器 profile。

当前 DOM 入口来自 [galatea.js](../../prototypes/Galatea/wwwroot/assets/galatea.js) 与 [GalateaServices.cs](../../prototypes/Galatea/GalateaServices.cs)：

| 操作/读取 | 选择器 |
|---|---|
| 登录 | `input[name="userId"]`、`input[name="password"]`、`form[action="/login"]` |
| 输入/发送 | `#message-input`、`#send-button` |
| 恢复/撤销 | `#resume-turn-button`、`#undo-last-button` |
| 状态/流式区域 | `#status-text`、`#live-turn` |
| 真实用户/回答卡片 | `#turn-list .turn-card.user:not(.context-header) > pre`，assistant 同理 |

发送的成功判据要连起来：**POST 接纳 → 对应 SSE `done` 且无 `error` → current=Idle → 页面解除 busy、状态错误清除、回答可见**。fresh 输入正常清空；Undo 则应把被撤销输入放回编辑框。恢复提示在恢复前是预期状态，不应被脚本当成初始页面故障。

关键经验：

- **不等待 `networkidle`。** 页面持续轮询。等具体接口结果与 DOM 条件，区分 `running`、`recovery-required` 和 `idle`。
- **不把 HTTP EOF 当成 SSE 终态。** 浏览器可能在读到终态后取消 reader；`response.finished()`/`response.text()` 不一定能及时取得流。观察完整 SSE 帧的 `done/error`，覆盖 LF、CRLF 和分片边界。必要时用 `Response.clone()` 独立观察，原 Response 原样返回前端；观察分支在终态后取消，不改生产 parser 来迁就脚本。
- **不假设 recent 必须再次 GET。** SSE `done` 可携带 recent，前端直接更新 DOM；旧 GET 摘要不等于当前页面。冷重开比较保存下来的实际可见输入/回答，私下做直接文本比较即可。
- **不假设测试输入字节不变。** normalizer 可能合法改写时间格式。优先核对本次已接纳轮次、实际归一化文本和最终 DOM；若用标记匹配，只接受已证实的等价形式，不扩大成模糊成功，也不靠再发一遍制造绿灯。
- **排除 context header。** 它也使用 user/assistant 卡片样式，属于摘要而非额外完成轮次，不能混入轮次数量或 Undo 目标。
- **同时捕获多种错误。** 记录 pageerror、console error、失败请求、API status/code，并检查 `#status-text`。有些错误被前端 catch 后只显示在页面，不会触发 pageerror。计划内停服的断连与运行中的失败分别记录。
- **mutation 拒绝也保存响应 code。** 单独的 HTTP 409 不能区分 `turn-busy` 与 `rewind-not-available`；在私有报告中保存经过处理的 `{code,error}`。Idle 并不保证会话锁空闲，recent/cadence 查询和后台检查也持有同一锁。不要通过禁用正常轮询来掩盖实际操作竞争。

密码从本地配置在内存读取；不要输出登录 body、Cookie、凭据或完整故事。私有报告可保留经过处理的错误 message/stack，只有摘要或 hash 会让真实失败难以定位。长操作定期打印阶段与计数，避免为了“查看进展”额外提交 mutation。

## 5. 失败时怎样少绕路

| 现象 | 优先核查 |
|---|---|
| SQLite 健康，但 current/attach 返回 500 | `Galatea.Api` 堆栈与应用 strict open；检查旧落盘事实是否被当前 renderer/默认值重新解释 |
| 旧 Note 回执因文案升级失效 | 冻结 body 是展示事实；检查来源/状态/投递关系，不重写真实数据或堆叠旧文案 renderer |
| Anthropic Models 404 | 对照当前 Client 的固化回退；metadata endpoint 缺失不等于 Messages 或模型不可用 |
| metadata 查询 TLS 失败 | 区分 GET 查询与 Messages POST；独立诊断请求不能代替完整 E2E，瞬时成功也不证明永不抖动 |
| POST 202 后脚本失败 | 查 SSE terminal、current、DOM、provider 日志，先判断是业务失败还是观察器假设错误 |
| Idle 下 Undo 返回 409 | 检查具体 code、runningTurn 与同刻的读取/后台检查；现有 pop 会有界等待短时锁，再核验 exact head |
| 满并行偶发时限失败 | 隔离原失败场景，核对 request 是否真正发出，再用受限测试并发复验 |

不要仅因取消一个过度校验就删除所有输入关系检查。2026-09-14 的回执修复保留了 Applied 来源/修订、UTF-8、预算和 Bound Observation 关系；真正被删除的是“旧文案必须等于当前 renderer 输出”的重复门槛。

## 6. 收尾记录与后续工具化

每轮只需留下：代码版本和数据范围、备份、初始/最终 phase/head、实际路线与成功/失败次数、冷重开及页面结果、修复提交、清理和残余影响。保留原失败报告；脚本误判由新的只读证据解释，不涂改单次结果。

**Undo 不是整目录或副作用回滚。** 原始事件、ref 移动历史、正常 setup 同步、Note 和邮件状态可能保留，已领取的回信不会自动变回 Ready。记录这些差异；不要为追求“完全还原”手工复活回执或删除数据库。

涉及 Recap CLI 装配改动时，可在停服后按[正式 CLI 用法](../../prototypes/SessionJournal.Cli/README.md#构建与即时诊断)执行只读 `progress`，再以现有 V3 catalog/route 和 `--max-new-calls 0` 执行 `build`。检查 `NewCalls/CellsCommitted/RowViewsCommitted`、实际文件变化和 call-log 目录；fulfilled 零调用只证明重开与复用，不证明缺失摘要的真实生成链。运行中的 call-log 文件可能只是已预留的空文件，应在调用结算后汇总，不能把暂时无法解析当作 provider 失败。

2026-09-14 的私有 driver、只读 audit harness 和逐次报告位于本机 `gitignore/galatea-identity-e2e-20260913T201405Z/`，仅作为调试参考，可能被清理，不是正式 runner。公开的结果见[实机记录](identity-simplification-design.md#11-唯一-dev-实例-e2e2026-09-14)。

下一步若继续高频使用，优先从这些脚本提取两个小入口：参数化的只读实例审计、参数化的浏览器操作与观察。去掉个人路径和单次失败例外，明确依赖、参数、超时、报告及退出码；仍由 operator/主线程控制启动、停服和每次真实 mutation。无需先建设场景 DSL、迁移框架或新 skill。

维护本页时同步检查涉及的 DOM/API/测试筛选条件，并运行文档检查。既有一次性验收数字继续保存在原记录中，不不断追加进这份操作指南。
