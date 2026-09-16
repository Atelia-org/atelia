# V11 自主活动后的 RecapGrid 实例资产升级

日期：2026-09-16。范围：本机 `prototypes/Galatea/.atelia/galatea` 的 `cyber`、`gpt`。

状态：**升级、原路径切换与冷验收完成；服务保持停止，等待用户启动试运行。**
切换完成时间：2026-09-16 22:39（Asia/Singapore）。以下是本轮实测事实，不以历史验收替代当前验证。

## 原因与边界

这不是 V6 漏迁，也不是角色改名。`cc5e569f` 修改了
`docs/Galatea/prompt/recap-maintainer-family/system-zh-cn.md` 对 heartbeat 的解释。
该文件是 `Galatea.RecapGrid` 的嵌入资源，修改会改变实际 Family 和两个成员定义；
资产 selector 仍为 `galatea-rolling-rewrite-zh-cn-v7`。

- 旧 Family：`75839423ac000cc5401dcdc6d4bb6ef3a3ea365229e57896738d03624696c989`。
- 当前 Family：`42477a8838928993c2835a8863910b297ba1805136a37b27242054a47a60e090`。
- Galatea 原路径只读探针复现：`Mismatch`、`invalid/exact`、`character-asset-mismatch`。
- 当前错误中的“角色名或玩家名不一致”未准确表达此次 prompt 资产变化。

现有 V7 provision receipt 的 operation key 不变，不重试 `provision-asset` 来覆盖它。
通过公开 `put-family` / `put-definition` 注册当前代码生成的 canonical bytes，
建立新 Full recipe，构建后以零调用 proof 执行 promotion；旧资产、receipt 和摘要保留。

## 备份及迁移前检查

私有操作目录：`gitignore/galatea-upgrade-20260916/`，不提交凭据、正文或实际数据库。

- 未发现 Galatea Server 进程，3511 无监听；完整备份持有 27 个既有 `.lock`，
  复制 191 个文件、35,143,109 字节，逐文件 SHA-256 校验并 flush。
- `before/backup-manifest.json` 保存原始文件集合与摘要。
- 两个 raw Journal 均 Idle，cyber 为 256 个事件，gpt 为 490 个事件。
- 两角色 raw validate、Timeline/Control/Store verify 均通过。
- 配置已为 V11；Delegation 与 CharacterMemory 官方 dry-run 均 `AlreadyCurrent`，
  无须修改这些数据库或历史 Journal 事件。
- 使用显式本地 Completion feed restore，Server/CLI Release 构建通过，零警告、零错误。
  现有 Server assets 曾解析到旧 preview 包，显式 restore 后已确认为固定 dev 包；
  不发布 NuGet，不变更依赖版本。

## 执行与验收要求

所有新 Recap 先在 `candidate/cyber`、`candidate/gpt` 构建，保留配置中的
`gpt5-6-sol-codex`，两角色调用预算分别为 4 和 6；不产生角色主线动作。
gpt 初次构建 6 次调用全部结算，其中最后一行 world-understanding 被
`FullReplacementTextTooLarge` 拒绝，其他 5 个 cell 已提交。只读 progress 确认
唯一缺项后，另给 1 次预算，使用现有 `gpt5-6-luna-codex` 补建该项；原失败报告保留，
不放宽 32 KiB 校验、不更改成员定义，正式 runtime route 仍为 Sol。
正式切换前要求 candidate fulfilled、零调用 proof、promotion、各域 verify、
raw 语义承诺与文件一致，以及 Galatea 实际 target/readiness 只读检查。

切换只涉及每个 SessionJournal 的 Recap Store 和对应 Ref/Timeline 的 Control，
不替换 Timeline、Cadence、events 或 refs。旧目录保留在 `retired/`。
配置新增当前 Family 的 route/profile，保留旧规则及路由。
运行期 profile 除 Family allowlist 外与原 current profile 相同，仍为
`maximumBootstrapRows=2`、`maximumProjectedCalls=4`，不扩大角色的运行期权限或预算；
本次离线重建使用独立 operator admission 和明确的 4/6 次调用预算。
多目录与配置切换不是一个原子事务，整个过程要求无服务 owner，异常后保持停服，
不可盲目重跑或用旧备份覆盖后续新产生的历史。

## 实际结果

| 验收项 | cyber | gpt |
|:--|:--|:--|
| 新完整摘要行 / cell | 2 / 4 | 3 / 6 |
| 实际 provider 调用 | Sol 4，全部成功 | Sol 6，其中 1 次超长；Luna 补建 1 次成功 |
| 原路径 Galatea target / readiness | Aligned / ready / exact | Aligned / ready / exact |
| 原路径 zero-call build | Fulfilled，0 次请求 | Fulfilled，0 次请求 |
| raw 事件数 / 执行阶段 | 256 / Idle | 490 / Idle |

新 active recipe：

- cyber：`724c5294ce52efb9ad84470b7dd7fd7abdd5a8af5419739eece5e5f74f8964c9`。
- gpt：`4a21b5f0d29ac91b47da659f66708be53563b56675588093d773c88d6d9d732a`。

切换工具的 `--verify-only` 先通过，再执行一次 `--execute-once`：
全 live home 的文件集合与 SHA-256 必须匹配备份；candidate 在两个目标域之外
必须与 live 字节一致，且 active、proof、实际 Galatea 探针均符合预期。
全阶段记录在 `cutover-stages.log`，原目录完整保留在 `retired/`，没有删除旧数据。

`verify-live.sh` 在原路径重新执行 raw validate、Control/Timeline/Store verify、
Galatea 实际只读 target/readiness 方法、零调用 build 和两种数据库 dry-run，全部退出 0。
对 events、refs、HistoryTimeline、Cadence、Delegation、CharacterMemory 的逐文件比较通过；
raw head、RefId、事件数、执行阶段、历史语义承诺与升级前一致。
配置除 `runtime.recapGrid` 外语义相同，旧 profile/route 文件保留，新增当前资产路由。

证据位于 `after/`；备份为 `before/`，隔离完成品为 `candidate/`，退役派生域为 `retired/`。
这些目录含私有数据，应保留其权限，不提交 Git。

服务未自动启动，未发送角色主线测试动作，未做浏览器 E2E，未发布 NuGet。
可以按 [Completion 依赖指南](../completion-dependency.md) 使用本地 dev 包启动试运行；
后续 build/run 仍使用 `--no-restore` 或明确的本地 feed restore。
启动产生新历史后，不能直接拿 `before/` 覆盖整个实例回滚。

残余问题：此次发现摘要超过 32 KiB 会被拒绝；Runtime 当前未在 work tail 中显式传递
该字节上限，本轮未修改这一运行协议或自动重试策略。单项补建成功不等于已消除未来超长风险。

## 协作记录

采用 `two-layer-refactor-driving`：主线程负责实际数据与验收，两个 subagent 分别
核查 Recap 升级合同和其他存储格式；独立审阅促使切换范围缩小到两个确切派生域，
并要求完整文件同一性检查，而不只比较 main head。
两线程 full-history fork，继承主线程模型/effort，未显式覆盖；工具未报告实际模型或分项消耗。
资产导出工具首次构建通过；切换工具另由主线程审阅后才允许运行。
切换工具构建通过，存在两个 Linux 文件权限 API 的 CA1416 跨平台告警；本次仅在 Linux 使用。

验收探针的一次退出断言使用了不存在的 DTO 属性 `Status`，随后改为 `State`；
第一次增量编译仍沿用旧产物，显式 Rebuild 后 cyber 探针退出 0。
该错误发生在 provider-free 的工具断言中，未修改 Journal，也未导致重复摘要调用。
