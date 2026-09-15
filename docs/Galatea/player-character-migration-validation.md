# Player / Character 与结构化输入：真实实例迁移验收

状态：**停服预检和备份已完成；配置、数据库及 Recap 目标尚未转换，真实调用尚未执行。** 日期：2026-09-16（Asia/Singapore）。施工总入口见[实施工作单](player-character-implementation-work-order.md)。

本文只记录真实实例的实际结果。共享 Observation 投影补充后的 Galatea provider-free 全套为 1195/1195，CLI 全套为 162/162，独立审阅通过；这些结果不能代替真实迁移或真实 provider 验收。

## 1. 原实例与停服检查

- 配置：`prototypes/Galatea/.atelia/galatea/config.json`，仍为 V9，旧用户 `cyber`、`gpt`。
- 按 `/proc/*/cmdline` 的可执行文件/DLL 参数查找，没有发现 Galatea Server；`ss -ltnp 'sport = :3511'` 无监听。随后备份实际持有原文件锁，不以进程检查替代锁检查。
- `cyber` SessionJournal：`.atelia/galatea/sessions/cyber-session-journal-recap-grid`；`gpt`：`.atelia/galatea/sessions/gpt`。角色相关目录、owner 与 dispatch 身份保持原值。
- 两套旧密码不同；已分别运行配置转换 dry-run，两者均退出 0，原配置字节未变。新管理员拟为 `player-main`、保留原显示名；沿用哪套密码仍待用户选择，没有执行配置 apply。

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

## 4. 剩余迁移与验收

1. 共享 Observation projector / 公共 CLI mixed-history build 的补充已完成、全套通过并经独立审阅。
2. 已说明默认沿用 `cyber` 的原密码；使用已验证的配置转换工具 apply，随后分别 dry-run/apply Delegation V3→V5、CharacterMemory V3→V4，冷重开核对原 owner、终态及旧证明。
3. 两个角色分别登记 V7 资产、构建新 recipe、取得 Fulfillment 并 promotion，保留旧资产及历史依赖。按当前每角色 2 行、每行 2 列，预计共 8 次摘要调用；实际缺口以新 candidate progress 为准。
4. 真实 provider / Codex sidecar 验证，并验证新登录、显式目标、结构化输入和最终后台运行状态。未知外部结果仍按既有政策处理，不为得到通过结果自动重发。

第 1 项已完成，第 2–4 项尚未验收完成。旧备份只用于明确匹配的恢复步骤，不能覆盖恢复运行后产生的新事实。
