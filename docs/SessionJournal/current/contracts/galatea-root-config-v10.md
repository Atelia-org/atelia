# Galatea root config V10 current contract

状态：**Current product contract；V9 → V10 显式离线转换**。本页拥有 root config 的当前字段与身份边界；
完整示例、启动和操作步骤见 [配置参考](../../../Galatea/configuration.md)。
[V9](galatea-root-config-v9.md)及更早页面保留历史版本规则，不能用于当前启动。

实现依据：[`GalateaStrictConfigReader`](../../../../prototypes/Galatea/GalateaStrictConfigReader.cs)、
[`GalateaConfig`](../../../../prototypes/Galatea/GalateaConfig.cs) 的 file DTO 与业务验证、
[`GalateaConfigLoader`](../../../../prototypes/Galatea/GalateaServices.cs)。字段验收入口：
[`GalateaRootConfigFieldLanguageTests`](../../../../tests/Galatea.Server.Tests/GalateaRootConfigFieldLanguageTests.cs)、
[`GalateaConfigValidationTests`](../../../../tests/Galatea.Server.Tests/GalateaConfigValidationTests.cs)。
旧 approval tag 不认证 V10 delta；真实迁移证据见[本轮验收](../../../Galatea/player-character-migration-validation.md)。

## 1. Strict root language

- Linux no-follow regular file；已有祖先拒绝 symlink/reparse。文件为 1 byte..1 MiB，JSON 最大深度 32。
- strict UTF-8，无 BOM、comment、trailing comma 或 trailing data。字段按解码后的 exact 名称识别；
  合法的字段名转义与属性重排可接受，unknown、wrong-case、重复字段（含大小写冲突）拒绝。
- `v` 必须是 raw exact integer token `10`；缺失、null、字符串、`10.0`、指数表示、旧版和未来版拒绝。
- 根字段恰为 `v`、`characters`、`players`、`runtime`，均必须存在且不能为 null。

| 对象 | 必需字段 | 可选字段与默认值 |
|:--|:--|:--|
| root | `v`；`characters` array（1..256）；`players` array（0..256）；`runtime` object | 无 |
| Character | string `id`、`name`、`sessionDir`、`delegationStateDir`、`characterMemoryStateDir`、`homeDir`、`defaultConnectionId`；`sessionProvisioning` | string `characterContextTemplate` 默认空串；string/null `characterContextTemplateFile` 默认 null；bool `heartbeatEnabled` 默认 false |
| Player | string `id`、`name`、`password` | 无 |
| Runtime | `recapGrid` object | string-array/null `listenUrls` 默认 null（数组最多 256 项）；string/null `callLogDir` 默认 null；bool `maintenanceMode` 默认 false |
| RecapGrid | string `routeManifestPath`、`currentAgentControlProfileId`；string-array `agentControlProfileFiles`（1..256） | 无 |

表中的默认空角色 context 不是“允许缺少角色设定”：loader 最终仍须取得有效 inline/file source。
可选字段只在表中明确列出 null 时接受 null；其他字段还须通过业务非空、身份、路径和依赖校验。
`sessionProvisioning` 只接受 `existing-only` 或 `create-if-missing`。

## 2. 两种身份和状态归属

Character 拥有连续 SessionJournal、home、delegation、CharacterMemory、默认连接和心跳配置；Player 只持有
外部登录身份。各集合的 ID 分别按 Ordinal 唯一，PlayerId 可以与 CharacterId 同值，调用方不能混淆种类。
名称沿现有 NFC/无首尾空白校验；Character 名在角色地址簿中唯一，精确 `Codex` 保留。

`players: []` 合法，不制造虚拟 Player；`heartbeatEnabled=true` 的 Character 仍由后台驱动。此字段是唯一的
周期 enrollment，不控制独立角色信 relay。所有仍在配置中的已认证 Player 都有同一级管理权限，操作目标
由显式 Character 路由决定，详见 [Server API](../../../Galatea/server-api.md)。旧 `users`、`serverAgentUserIds`、
Character 内 `password`/`playerName` 等字段不再接受。

`defaultConnectionId` 必须属于 sibling V3 catalog 的 selectable allowlist；Runtime 不复制连接清单。
`connections.json` 与 `delegates.json` 仍在 config 同目录，各自版本由各自 owner 管理；本次 root version
不是它们或任一 SQLite schema 的版本号。当前 delegates 为 V4，不能从历史 V9 页面推断为 V3。

## 3. 路径、prompt 与资源生命周期

相对状态路径、context-file、call-log、RecapGrid paths 继续以 config 文件目录解析；JSON 分组不改变基准。
home 必须预先存在、为 Linux canonical absolute directory、属于 delegates.allowedRoots，并满足既有
home/状态/call-log 互不相同或嵌套的 topology 校验。启动不创建 home、不修改 Unix HOME/CODEX_HOME。
session/delegation/CharacterMemory owner 及锁规则保留；路径缺失或冲突没有别处 fallback。

context-file 存在时覆盖 inline source。新角色 source 必须含 `${characterName}`，拒绝 `${playerName}`
和其他变量，不递归替换。SystemPromptSetup 保存有序源与绑定事实；实际替换和包装在 LLM 请求时生成，
规则见 [typed input / Prepared v9](completion-request-prepared-v9.md) 和 [prompt 资源](../../../Galatea/prompt/README.md)。
source、机读 setup、最终请求分别受其拥有者的限额约束，不能把 root 的 1 MiB/depth32 套到所有内部对象。

`runtime.callLogDir` 可选启用 Completion metadata 日志，不保存新请求/输出全文，也不成为派发证据。
maintenanceMode 继续阻止正常写操作和 missing-session 创建，不把读取入口变成修复工具。

## 4. Bootstrap 与升级

缺少根配置时 bootstrap 以 create-new 生成 V10 Characters（alice/bob）、独立示例 Player（player-main）、
Runtime 及 sibling 模板，然后退出等待操作者填写。它不覆盖已有文件或自动转换旧配置，不生成完整业务凭据。
缺失且允许生成的 context-file 仍按原配置目录约束处理；RecapGrid profile 等前置依赖须明确准备。

create-if-missing 仅对完全不存在的 session path 在 private staging 建立 raw setup、Cadence、empty Timeline、
Control、Store、Character 的 V7 asset 与 empty-Timeline active Full recipe，验证关闭后发布；没有 provider
调用，不 repair 既有残缺目录。

V9 升级保留旧 userId 值作为 CharacterId 及其状态路径，将 Player 单独配置；不重写历史作者、Prepared、
dispatch ID 或旧外部工作。配置/模板、数据库 schema、V7 Recap target 的采用分别验证，不能把改 `v` 当作
完整迁移。未知外部效果不因配置或 renderer 更换而自动重发。迁移步骤及结果以[实施工作单](../../../Galatea/player-character-implementation-work-order.md)
和验收记录为准，本合同不以旧 approval 或单一运行 PID 代替完整证据。
