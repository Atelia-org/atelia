# Galatea root config V11 current contract

状态：**Current product contract**。本页拥有当前 root `config.json` 的字段语言与自主激活边界；完整示例、启动和操作步骤见[配置参考](../../../Galatea/configuration.md)。
[V10](galatea-root-config-v10.md)及更早页面都是历史版本，当前 loader 一律拒绝它们；正常启动不读取、写入或转换旧配置。

实现依据：[`GalateaStrictConfigReader`](../../../../prototypes/Galatea/GalateaStrictConfigReader.cs)、[`GalateaConfig`](../../../../prototypes/Galatea/GalateaConfig.cs)、[`GalateaConfigLoader`](../../../../prototypes/Galatea/GalateaServices.cs)。字段验收入口是[`GalateaRootConfigFieldLanguageTests`](../../../../tests/Galatea.Server.Tests/GalateaRootConfigFieldLanguageTests.cs)与[`GalateaConfigValidationTests`](../../../../tests/Galatea.Server.Tests/GalateaConfigValidationTests.cs)。

## 1. Strict root language

- Linux no-follow regular file；已有祖先拒绝 symlink/reparse。文件为 1 byte..1 MiB，JSON 最大深度 32。
- strict UTF-8，无 BOM、comment、trailing comma 或 trailing data。字段按解码后的 exact 名称识别；合法字段名转义与属性重排可接受，unknown、wrong-case、重复字段拒绝。
- `v` 必须是 raw exact integer token `11`；缺失、null、字符串、`11.0`、指数表示、旧版和未来版拒绝。
- 根字段恰为 `v`、`characters`、`players`、`runtime`，均必须存在且不能为 null。

| 对象 | 必需字段 | 可选字段与默认值 |
|:--|:--|:--|
| root | `v`；`characters` array（1..256）；`players` array（0..256）；`runtime` object | 无 |
| Character | string `id`、`name`、`sessionDir`、`delegationStateDir`、`characterMemoryStateDir`、`homeDir`、`defaultConnectionId`；`sessionProvisioning`；integer `autonomyIntervalMinutes` | string `characterContextTemplate` 默认空串；string/null `characterContextTemplateFile` 默认 null |
| Player | string `id`、`name`、`password` | 无 |
| Runtime | `recapGrid` object | string-array/null `listenUrls` 默认 null（数组最多 256 项）；string/null `callLogDir` 默认 null；bool `maintenanceMode` 默认 false |
| RecapGrid | string `routeManifestPath`、`currentAgentControlProfileId`；string-array `agentControlProfileFiles`（1..256） | 无 |

`autonomyIntervalMinutes` 是必填 JSON integer，合法闭区间为 `0..525_600`。`0` 不是缺字段默认值；负数、浮点、指数、overflow 与超过上界的值均拒绝。V11 已删除 `heartbeatEnabled`，它是 unknown field，不保留兼容或优先级规则。

角色 context 的最终 source 仍必须有效；可选字段只在表中明确列出 null 时接受 null。`sessionProvisioning` 只接受 `existing-only` 或 `create-if-missing`。其余身份、路径、prompt 和 RecapGrid 资源约束延续现有 owner 规则，见[配置参考](../../../Galatea/configuration.md)。

## 2. 自主 interval 与 durable reply

`autonomyIntervalMinutes` 是唯一的**无 Ready reply 时**自主激活 authority：

- 正整数：Character 以该分钟数的 process-local monotonic cadence 等待；10 秒 probe 只带来发现延迟。成功的主线回合重新计时，重启重新 arm 且不补停机任务，autonomous 非完成回合只暂停空激活。
- `0`：不创建/arm cadence，绝不因空 tick 创建 `HeartbeatActivation`；它不关闭 delegation、character-mail relay 或人工交互。
- 已 durable 的 Ready reply 或 active reply lease 是独立的外部工作证据。即使 interval 为 `0`，10 秒 fallback 仍先做只读 wake probe，存在该证据才 attach 并沿 `TurnLock → reconcile → exact Idle → PrepareFreshTurnAdmissionAsync → BeginCutoff` 领取或恢复 `DelegateReply`。无证据、uninitialized/unavailable/quarantined/backoff store 都 fail closed，不 attach、不 provision、不调用 provider。

因此 `0` 的 Agent status 是 `waiting`，`nextActivationAtUnixTimeMilliseconds=null` 表示没有自主 deadline、仍可监视 durable reply；`disabled` 只表示不存在的 Character。Ready preflight 不是 lease claim；最终 cutoff 的 Empty/busy/race 不得退化为 heartbeat activation。

`players: []` 合法，不制造虚拟 Player。Character 仍拥有连续 SessionJournal、home、delegation、CharacterMemory 和默认连接；Player 只持有外部登录身份。旧 `users`、`serverAgentUserIds`、Character 内 `password`/`playerName` 与 `heartbeatEnabled` 均不接受。

## 3. Observation 与版本边界

`autonomyIntervalMinutes` 是新 heartbeat Observation 的持久语义 snapshot，而不是读历史时从当前 config 重算：

| 记录 | 严格 reader | 新 writer / renderer |
|:--|:--|:--|
| 已存 `galatea.observation.v1` heartbeat | 只接受 `externalIntervalMinutes: 10` | 保留原有“十分钟”投影，不重写历史 |
| 新 `galatea.observation.v2` heartbeat | 只接受 `1..525_600` | 保存本轮 snapshot；Markdown、recall query 与机读 action 使用同一值 |
| player-action、delegate-reply、inbound-mail | 继续使用 v1 | 不因本次改动制造等价 v2 编码 |

这不改变 SessionJournal、delegation SQLite、CharacterMemory 或已有 Observation 的 schema。`connections.json`、`delegates.json`、SQLite schema 与 root version 各由自己的 owner 管理。

## 4. Bootstrap 与唯一开发实例切换

缺少根配置时 bootstrap create-new 生成 V11 Characters（alice/bob）、独立示例 Player（player-main）和 sibling 模板，然后退出等待操作者填写；不会覆盖已有文件或自动转换旧配置。

本仓当前只有一个 V10 开发实例，因此切换不是产品化 migration：

1. 正常停服，确认进程退出；不要在 writer 仍运行时编辑配置。
2. 在实例状态目录**之外**备份 `config.json`。
3. 人工将 `v` 改为 `11`，并把每个 `heartbeatEnabled:true` 改为 `autonomyIntervalMinutes:10`、`false` 改为 `0`；删除旧字段，按需要再调整正分钟数。
4. 使用新 binary 重启。它会严格拒绝仍为 V10 的文件；不会自动迁移、回写或转换。

这一步不修改 SessionJournal、delegation SQLite、CharacterMemory 或历史 Observation。本合同也不声称已停服、已备份、已切换实例或已验证真实 provider；这些是 operator 的显式运行操作。
