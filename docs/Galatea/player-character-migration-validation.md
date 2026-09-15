# Player / Character 与结构化输入：真实实例迁移验收

状态：**真实实例迁移与验收已完成，服务已恢复运行。** 日期：2026-09-16（Asia/Singapore）。完整交付与故障验证见[实施验收](player-character-implementation-work-order.md)。

本文只记录真实实例的实际结果。共享 Observation 投影补充后的 Galatea provider-free 全套为 1195/1195，CLI 全套为 162/162，独立审阅通过；这些结果不能代替真实迁移或真实 provider 验收。

## 1. 原实例与停服检查

- 原配置：`prototypes/Galatea/.atelia/galatea/config.json`，V9，旧用户 `cyber`、`gpt`；当前已转换为 V10。
- 按 `/proc/*/cmdline` 的可执行文件/DLL 参数查找，没有发现 Galatea Server；`ss -ltnp 'sport = :3511'` 无监听。随后备份实际持有原文件锁，不以进程检查替代锁检查。
- `cyber` SessionJournal：`.atelia/galatea/sessions/cyber-session-journal-recap-grid`；`gpt`：`.atelia/galatea/sessions/gpt`。角色相关目录、owner 与 dispatch 身份保持原值。
- 两套旧密码不同；两种来源的转换 dry-run 均退出 0。随后按向用户说明的默认选择沿用 `cyber` 原密码，实际新管理员为 `player-main`，保留原显示名；凭据值未输出到报告。

## 2. 备份与冷读取证据

本机私有操作目录：`gitignore/galatea-identity-rendering-migration/20260915T163219Z/`。正文、凭据、配置和逐文件摘要只保存在该忽略目录，不复制到本文。

- `before/` 是完整 `.atelia/galatea` 文件树副本：185 个文件、33,327,183 字节，无符号链接。
- 临时 .NET 备份工具先以 `FileShare.None` 取得 27 个既有 `.lock` 文件，再以只读共享句柄持有其余源文件；持锁期间复制、逐文件比较源/副本 SHA-256，并复查源目录成员集合。副本文件权限为 0600、目录为 0700，文件 flush 后执行 `sync -f`。
- 工具源代码和构建记录保存在 `operator-tools/`；工具构建零警告、零错误。此证据不声称模拟过整机断电。
- 从备份路径重新运行公开 `validate`，两个 Journal 的 ref、head、事件数、执行阶段和历史语义承诺均与原实例一致。
- 四个备份 SQLite 的 `integrity_check` 均为 `ok`，仍保持 V3。

## 3. 只读盘点结果

对每个角色串行执行公开 CLI 的 `validate`、Cadence inspect、Timeline inspect/verify、Control inspect/verify、Store inspect/verify、HistoryLoad inspect，18 条命令全部退出 0。报告位于操作目录的 `inventory/`。

| 项目 | cyber | gpt |
|:--|:--|:--|
| Journal 执行阶段 | Idle | Idle |
| Journal 事件数 | 251 | 389 |
| 已封存 selected Timeline rows | 2 | 2 |
| 既有 Recap cells / row views | 4 / 2 | 4 / 2 |
| Delegation schema / SQLite integrity | V3 / ok | V3 / ok |
| 出站邮件终态 | Completed 2 | Completed 16、Failed 2 |
| 已消费回信 | 2 | 18 |
| 活跃 reply lease | 0 | 0 |
| CharacterMemory schema / SQLite integrity | V3 / ok | V3 / ok |
| Note capture | Applied 2、ZeroCaptured 13 | Applied 10、ZeroCaptured 68 |
| DerivedInfo | Applied 2 | Applied 10 |
| Note receipt | Delivered 2 | Delivered 8 |

各域没有发现未结算工作。两个 `route_binding` 均为既有 Bound 线程绑定，继续保留；它不等于有一项未知外部请求。

## 4. 已执行的升级与真实通信

- `d07e4d46` 的 Release Server 与 CLI 均已编译通过，零警告、零错误。
- 用 Release Server 执行配置 dry-run/apply：V10 根为 `v/characters/players/runtime`，一个 Player、两个 Character；逐字段确认角色名、ID、全部状态路径、home、provisioning、默认连接和 heartbeat enrollment 与旧值对应。配置/模板的专用备份位于操作目录 `config-conversion/`。
- 每个角色分别执行 `upgrade-delegation-store`、`upgrade-character-memory-store` 的 dry-run→apply→再次只读 reopen，12 条命令全部退出 0。Delegation 当前为 V5，CharacterMemory 为 V4。
- 四个库的 SQLite integrity 均为 `ok`。与完整备份逐表比较所有原有列，唯一允许且已单独验证的变化为 metadata 的 `schema_version`（3→5 或 3→4）；其余原列保持一致，包括 owner、body、状态与原证明。比较覆盖 cyber 的 21/22 行和 gpt 的 116/108 行，结果见 `inventory/sqlite-after-upgrade.json`。
- 首次真实 Codex canary 在 Ready 前以 `SIDECAR_EXITED` 失败，没有发送任务；原因尚未确认。随后同配置 sidecar 初始化与直接 initialize 探测分别在 2.21/2.07 秒成功。证据保留在 `initialization-probes/`。
- 初始化探测之后的真实 canary **1/1 通过，8 秒**：临时仓库中的 EnsureBinding→Start→重复 dispatch 拒绝→Inspect Completed 成功，临时仓库与子进程按测试合同清理。证据：`real-codex-canary-after-ready-probe.log` 与 `galatea-semantic-real-codex-after-ready-probe.trx`。第一次失败日志保留，不将原因标为已修复。
- `cyber` 的 V7 采用已完成：4 次实际 `gpt5-6-sol-codex` 调用、4 个新 cell、2 个新 row view，provider 请求全部结束；零新调用的 progress 返回完整 Fulfillment proof，promotion 后 Control/Store/Timeline verify 与 raw audit 均通过。新 active recipe 为 `9a71bd2345ad80ad3d7e617e8ea457e488502b4594ac018236dcecf74257a6ae`；原 Journal head、事件数与历史语义承诺不变，旧资产保留。证据位于 `cyber-v7/`。
- `gpt` 同样完成 4 次调用、4 个新 cell、2 个新 row view，取得完整 Fulfillment 并 promotion；新 active 为 `f109365e93d304da2c05759995d2946438e2670052a40e637a02aae1bd4c17c6`。Control/Store/Timeline verify 与 raw audit 通过，原始历史未改变。证据位于 `gpt-v7/`。
- 公开 scaffold 生成的运行期 V7 profile 已发布：与原 current profile 比较，权限、capability fingerprints、carrier、列前缀及容量上限相同，只将允许的 Family 换为 V7。配置现有 3 个 profile、2 条 exact route，旧 profile/route 保留；两角色分别用合并后的正式 route 执行零新调用预算的 build，均 fulfilled。配置发布前后副本在 `runtime-publication/`。
- 启动前另做完整一致备份 `ready-before-first-turn/`：191 个文件、34,240,773 字节，持有 27 个锁、逐文件校验并 flush。它与最初 V9 备份分别对应不同状态边界。

## 5. 真实 Player 主线、冷审计与最终运行

- 实际 `/login` 页面 200，未认证 `/api/v1/me` 为 401；用 `player-main` 登录后 `/me`、角色目录、两个角色页面、静态 JS、current/recent/agent/mailbox API 均通过。未知 Character 返回 404。
- 向 `gpt` 只提交一次 Player 验证动作，未提供 connection override，使用其默认 `gpt-6-astra`。轮次 `a62771cef6b34b9981b0f1aaa59ffbcc` 收到 exactly-one SSE `done`，耗时 38.94 秒，回复包含正确 sender 值；随后 current 为 Idle、Recap ready。证据为 `player-canary-accepted.json`、`player-canary-result.json`，未重复发送该动作。
- 完成后正常停止首个服务进程，再以只依赖 SessionJournal 的只读 probe 冷开：gpt 为 394 个事件，最新 SystemPromptSetup/Observation 为事件 v2 的 structured 内容，sender 为 `player/player-main`，本轮 Prepared v9、Started v2，整条 lineage 审计通过；公共 CLI `validate` 也通过。无 Galatea/MdJson projector 参与该 probe。
- `cyber` 仍为原来的 251 个事件，尚无新动作，其历史 text SystemPromptSetup v1 保持原样。只读 attach 不主动改写历史；下一次 fresh 在 `ReconcileDesiredSetup` 的合法边界采用配置中的新机读指令。不能为使验收数字一致而改写旧记录。
- 最后一次 SQLite 完整性检查通过，未增加出站邮件；gpt 新增一个 `ZeroCaptured` Note capture，无新 Note 或待投递回执。没有未结算 lease/outbox 工作。证据为 `post-canary-stores.json`。
- 已重新启动 Release 服务，验收时 PID `591954`，地址 `http://127.0.0.1:3511`。重启后两个角色均 Idle、Recap `ready/exact`、mailbox `no-mail`；cyber 的 heartbeat 仍 disabled，gpt 为 waiting。相隔 11 秒观察 gpt 的剩余等待时间递减，无 admissionFailure。证据为 `final-running-state.json`。

操作工具及启动 PID/日志保存在本机私有操作目录；该进程延续原有手动管理方式，不新增开机自启或崩溃自动重启部署。Web 功能通过真实 HTTP 页面/API 与浏览器协议 Node 测试验收，未宣称运行了浏览器自动化。

最初 sidecar 初始化退出的根因仍未定位；成功探测和实际调用证据不能反推它从未发生。新轮次和新摘要已经落盘，不能用旧备份覆盖它们；恢复需选择匹配状态边界并明确保留后续事实。
