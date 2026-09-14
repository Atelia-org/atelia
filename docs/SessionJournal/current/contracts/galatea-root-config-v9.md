# Galatea root config V9 current contract

状态：**Current product contract；hard cut from V8**  
Authority：current Galatea code、`GalateaRootConfigFieldLanguageTests`、
`GalateaConfigValidationTests`、`GalateaTrackedPromptTemplateTests`  
Prior historical contract：[V8](galatea-root-config-v8.md)

## 1. V9 delta 与目录权威

Root `config.json` 的每个 user 增加 required string `homeDir`。这是个人文件目录以及该 user
Codex 新任务的默认 CWD；不同 user 仍共享 sidecar/app-server。sibling `delegates.json`
同步 hard-cut 到 V3，删除 route 的 `cwd` 字段；不能保留旧字段或设置 fallback。

`homeDir` 是启动配置，可修改后重启，不是 user、thread 或历史任务身份。既有 Codex thread
继续使用；已派发任务先按原任务身份查询结果，后续新派发才采用新 home。目录改变不搬移文件，
不改变 Unix `$HOME`、`~`、`CODEX_HOME`、登录身份或 SessionJournal/Character Memory/delegation
状态路径。本功能不承诺强隔离。

V8 的 optional `serverAgentUserIds`、V7 的 per-user `defaultConnectionId`、Completion catalog V3，
以及未被本页替换的 storage、prompt、provisioning 和 RecapGrid 规则继续适用，见
[V8](galatea-root-config-v8.md)及其引用。

## 2. Exact field language

Root 仍为 1 byte..1 MiB、max depth 32 的 Linux no-follow regular file，strict UTF-8，无 BOM、
comment、trailing comma 或 trailing data。Unknown、wrong-case 和 case-insensitive duplicate
property 拒绝。`v` 必须是 raw exact integer token `9`；versionless、旧版、未来版、string、
fraction 或 exponent form 均拒绝。

每个 user 必须显式提供 string `homeDir`，缺失、null、non-string 或 blank 均拒绝。目录必须：

- 是已存在的 Linux absolute canonical realpath；不接受相对路径、非规范路径或 symlink 别名。
- 落在 delegates 的 `allowedRoots` 中。
- 不与其他 home 相同或互为祖先；不与任意 user 的 session、delegation、Character Memory 或
  call-log 目录相同或嵌套。

Loader 和直接构造的 host 使用相同目录语义校验。启动不会创建 home 或修补权限。
这是配置错误检查，不是阻止任意绝对路径访问的安全隔离。

## 3. Prompt 与进程环境

outbound-mail capability 启用时，角色最终 prompt 增加实际 homeDir 与相对路径说明。
路径作为数据在原有 closed template renderer 之后追加，不解释路径中的 `${...}`；完整
prompt 仍受同一个 UTF-8 上限约束。未启用 outbound 时不增加该说明。
新鲜请求使用当前 prompt；冻结的 Prepared request 不重新拼装。

sidecar/app-server 的进程 CWD 固定为 `/`，不再发送 `CODEX_BRIDGE_DEFAULT_CWD`。
业务目录由创建 thread 和启动 turn 请求显式传递，不能 fallback 到进程目录。
通用 MCP 默认目录接口和全局 Codex 配置发现规则不因本页发生改变。

## 4. Bootstrap 与升级

Bootstrap 输出 exact `v:9`，示例 alice/bob 的 home 分别为 `/galatea-homes/alice` 和
`/galatea-homes/bob`。当前实际部署的初始布局是 `/galatea-homes/cyber` 与 `/galatea-homes/gpt`；
两者都只是初始配置，不能在 runtime 硬编码为所有用户的唯一根目录。

V8 → V9 需要 operator 显式增加每个 homeDir、预先创建可写目录、将 root version 改为 9，
并把 delegates version 改为 3、删除 route.cwd、将 allowedRoots 指向计划使用的 home 根。
配置 loader 与 bootstrap 不自动迁移旧 JSON。

create-if-missing 的 SessionJournal bootstrap 仍只创建 raw/Cadence/empty Timeline/empty
Control，不创建 Store、asset、recipe 或 provider effect；普通 GetSessionAsync() 从不 repair
既有 session path。若 operator 要把已有 raw-only/partial session 完整启用 RecapGrid，必须使用
[已有 SessionJournal 的 RecapGrid 显式升级](../../../Galatea/recap-grid-existing-session-upgrade.md)的停服、
备份、bounded build 与 promote 流程；这不是 V9 config migration 的隐式副作用。

首次 delegation store 格式升级和运行时恢复由
[per-user home 方案](../../../Galatea/user-home-design.md)及
[delegation durability contract](../../../Galatea/codex-delegation-durability-design.md)拥有；
本页不将改 JSON 描述为旧 store 的完整升级步骤。之后仅修改 homeDir 不需要专门的历史 CWD 迁移。
