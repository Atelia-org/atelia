# Galatea `userId` 演化与身份迁移调研报告

> 状态：调研（未实施）  
> 日期：2026-09-15  
> 范围：`prototypes/Galatea`、`prototypes/MemoPod`、`prototypes/Galatea.RecapGrid`，以及 Galatea 持有的 SessionJournal、Character Memory、durable delegation 状态。

## 结论摘要

当前 V9 的 `users[].userId` 不是纯显示名。它同时承担：

- 登录输入、cookie claim 和 HTTP API 的认证主体；
- Galatea host 内用户、session lazy、extractor、RecapGrid expectation 和自动 Agent 名单的精确 key；
- Character Memory SQLite owner 的一部分；
- durable delegation SQLite owner 的一部分，以及 `gd1-*` dispatch ID 的哈希输入；
- 离线完成恢复证据中的用户选择器。

因此，已有用户**不能只编辑 `config.json` 的 `userId` 就透明改名**。但这不是不可演化的设计限制：通过一次显式、停服、可恢复的离线迁移，完全可以把稳定的 durable 身份与可变登录/显示身份分离。

推荐目标不是把该字段改名为 `characterId`，而是引入不可变 opaque `accountId`（或 `principalId`）并新增可变 `loginName`。`characterName`、`playerName` 已经是独立的叙事身份。若把 durable account 身份命名为 `characterId`，会掩盖其真实用途，并与“不同 user 可有相同 characterName”的现有模型冲突。

## 当前模型与影响面

### 配置、登录与运行时

`GalateaUserFileConfig` / `GalateaUserConfig` 都直接保存 `UserId`；strict reader 将其作为必需 JSON string，运行时再要求 nonblank 和 ordinal unique。`serverAgentUserIds` 必须逐项 exact match 配置中的 `userId`。

- 配置模型与校验：[GalateaConfig.cs](../../prototypes/Galatea/GalateaConfig.cs)、[GalateaStrictConfigReader.cs](../../prototypes/Galatea/GalateaStrictConfigReader.cs)、[GalateaServices.cs](../../prototypes/Galatea/GalateaServices.cs)
- 登录成功后 cookie 同时写自定义 user-id claim 和 `ClaimTypes.Name`；后续页面/API 从 claim 查用户：[Program.cs](../../prototypes/Galatea/Program.cs)
- host 以 `userId` 构建 `_users`、session lazy cache、extractor binding 和 RecapGrid expectation 字典：[GalateaServices.cs](../../prototypes/Galatea/GalateaServices.cs)
- 浏览器端以 bootstrap `userId` 区分本地 UI 状态：[galatea.js](../../prototypes/Galatea/wwwroot/assets/galatea.js)

这层可以重构；旧 cookie 在迁移后失效并要求重新登录是可接受、可恢复的认证效果，不是历史数据迁移的难点。

### SessionJournal 与 RecapGrid

SessionJournal 的连续性由 `sessionDir` 内的 repository/raw lineage 决定。Galatea 打开既有 session 时传入的是路径；RecapGrid Store 也以该 repository path/engine 为边界。新建 RecapGrid asset 使用 `characterName`、`playerName`，而不是 `userId`。

因此：**保持 canonical `sessionDir` 不变时，SessionJournal 与 RecapGrid 不需要按用户 ID 改写历史数据。** `userId` 在这里仅作为 Galatea 的运行时 lookup key。已有 session 的角色名修改仍是另一项 RecapGrid asset/recipe 迁移，不能混入本任务。

- [GalateaSessionRepositoryProvisioner.cs](../../prototypes/Galatea/GalateaSessionRepositoryProvisioner.cs)
- [GalateaRecapGridComposition.cs](../../prototypes/Galatea/GalateaRecapGridComposition.cs)
- [GalateaServices.cs](../../prototypes/Galatea/GalateaServices.cs)

### MemoPod 与 Character Memory

MemoPod core 没有 `userId`：其持久化定位是 `rootPath + MemoPodId`，其中 `MemoPodId` 为 32 位小写十六进制值。Galatea 默认 MemoPod 使用固定的 `...0001`，但这只是每个 Character Memory store 内的 Pod key，不是账户 ID。

Character Memory 则将 owner 持久化为：

```text
(userId, SHA-256(canonical sessionDir))
```

首次建库把 `user_id` 写入 `character_memory_meta`；每次打开均 exact compare owner，mismatch 立即拒绝。旧 V1 authority digest 也将 owner `UserId` 纳入承诺输入。因此现有 Character Memory store 不能在只改 config 后继续打开。

- [MemoPodStoreLayout.cs](../../prototypes/MemoPod/Store/MemoPodStoreLayout.cs)
- [MemoPodId.cs](../../prototypes/MemoPod/MemoPodId.cs)
- [CharacterMemorySessionComposition.cs](../../prototypes/Galatea/CharacterMemory/CharacterMemorySessionComposition.cs)
- [CharacterMemorySqliteStore.cs](../../prototypes/Galatea/CharacterMemory/CharacterMemorySqliteStore.cs)
- [CharacterMemorySqliteStore.Migration.cs](../../prototypes/Galatea/CharacterMemory/CharacterMemorySqliteStore.Migration.cs)

### Durable delegation

delegation store 同样持久化 owner：

```text
(userId, SHA-256(canonical sessionDir))
```

`delegation_meta.user_id` 在 strict open 时与配置 owner exact compare。更重要的是，当前 `CreateDispatchId(userId, sourceActionAddress, artifactOrdinal)` 把 `userId` 以 length-prefixed bytes 纳入 SHA-256；snapshot 重新计算并逐行核对每个已存 `dispatchId`。

`dispatchId` 还是 `outbound_mail` 主键、`operation_id`、`reply_notice` 关联键、`route_binding.active_dispatch_id` 外键目标，以及外部完成恢复证据的关键字段。`Started` 之后可能已有外部 effect，`OutcomeUnknown` / `RESULT_UNCONFIRMED` 更不能为了改名而重发。

- [GalateaDelegationSupervisor.cs](../../prototypes/Galatea/GalateaDelegationSupervisor.cs)
- [GalateaDelegationSqliteStore.Schema.cs](../../prototypes/Galatea/GalateaDelegationSqliteStore.Schema.cs)
- [GalateaDelegationSqliteStore.cs](../../prototypes/Galatea/GalateaDelegationSqliteStore.cs)
- [GalateaDelegationSqliteStore.Snapshot.cs](../../prototypes/Galatea/GalateaDelegationSqliteStore.Snapshot.cs)
- [GalateaDelegationDurableContract.cs](../../prototypes/Galatea/GalateaDelegationDurableContract.cs)
- [GalateaDelegationOperatorRecovery.cs](../../prototypes/Galatea/GalateaDelegationOperatorRecovery.cs)

## 两种需求的比较

| 需求 | 是否只是字段改名 | 正确目标 | 主要风险 |
|:--|:--|:--|:--|
| 将现有 `userId` 的值换成无语义随机值 | 否 | 引入 stable opaque durable identity，并迁移已有 owner/恢复边界 | dispatch ID、未决外部任务、历史 recovery evidence |
| 将配置字段名改为 `characterId`，但保持原有值和语义 | 可做但不推荐 | 仍是 account/login/durable identity，只是误导性的名称 | 名称掩盖 durable owner 语义，未来再次混淆 |
| 让 `characterId` 表示故事角色身份 | 是语义改变 | 另建角色实体/映射；不能取代 account identity | 多账户可扮演同一角色、角色改名/换角、delegation owner 串库 |
| 分离 `accountId`、`loginName`、现有角色字段 | 是一次 V10 身份重构 | 推荐 | 需要显式离线 migration，但之后 loginName 可透明修改 |

## 推荐的目标模型

建议 root config 升到一个新的 hard-cut version，例如 V10：

```json
{
  "accountId": "01j...opaque-and-immutable...",
  "loginName": "可修改的登录名",
  "password": "...",
  "characterName": "故事角色名",
  "playerName": "故事内玩家名"
}
```

规则应为：

1. `accountId` 是非空、严格格式、全局 unique 的 stable opaque ID；建议生成 ULID 或 UUID-N，不从名字、目录或角色推导。
2. `loginName` 是登录输入与 UI 展示名，可改；可以与 `accountId` 采用不同的格式和长度规则。
3. durable owner、dispatch identity、recovery evidence 的新版本只使用 `accountId`。
4. `characterName` / `playerName` 继续只表达叙事语义，不进入账户 owner 或 dispatch ID。
5. 不保留 V9/V10 的普通运行时双读；迁移命令是唯一兼容入口，正常 startup 只接受新版本。

## 程序改造清单

### 1. Root config、模型与 bootstrap

- 将 `GalateaUserFileConfig`、`GalateaUserConfig` 中的 `UserId` 拆为 `AccountId` 和 `LoginName`。
- 更新 `GalateaStrictConfigReader` 的字段白名单、required-field 检查、大小/格式校验和 strict version。
- 更新 config loader、默认 scaffold、`serverAgentUserIds`（建议改名 `serverAgentAccountIds`）及所有 storage-topology 错误文本。
- 更新 root-config contract、[configuration.md](configuration.md)、README、template JSON 与测试 fixture。

### 2. 认证、HTTP 与浏览器

- 登录表单按 `loginName` 查 user；cookie 的稳定 subject claim 写 `accountId`。`ClaimTypes.Name` 应明确选择 loginName 或 accountId，不能无意混用。
- 所有 API 从稳定 claim 取 `accountId`，再取得 config user；`/me` DTO 明确返回哪个字段，避免前端把 durable ID 当展示名。
- host 的 `_users`、session cache、extractor binding、RecapGrid expectation、自动 Agent coordinator 都改以 `accountId` 为 key。
- `galatea.js` 的 local UI key 改为 `accountId`；登录页 label 与 API 文案改为“登录名”。

主要文件：`Program.cs`、`GalateaServices.cs`、`GalateaAutomaticTurnCoordinator.cs`、`GalateaServerAgentHostedService.cs`、`GalateaDelegationSupervisor.cs`、`wwwroot/assets/galatea.js` 及相关 DTO/test。

### 3. Character Memory owner 与升级器

- 将 `CharacterMemoryStoreOwner.UserId` 改为 `AccountId`，并使 attach composition 从 config 传入它。
- 新 schema version 的 `character_memory_meta` 用 `account_id` 替代 `user_id`；严格 schema/owner validation、snapshot、错误码与测试同步更新。
- 实现专用 offline upgrade：先按**旧 owner** strict-open/read 验证，再以新 owner 写入 staging/copy 或一个明确事务，并用新 binary strict-reopen 验证。
- 若需要读取/升级带 V1 authority digest 的旧库，必须先以旧 owner 验证历史承诺，再按新版本规则建立新承诺；不能先改一格 metadata 再宣称历史已验证。

### 4. Delegation durable identity 与恢复

这是核心工作包。

- 将 `GalateaDelegationStoreOwner.UserId` 拆为 `AccountId`；`delegation_meta` 升为新 schema，strict open 以新 owner 校验。
- 将 `CreateDispatchId` 的输入从当前 `userId` 改为明确的 `dispatchIdentityKey` / `accountId`，并让 snapshot 的 dispatch revalidation 按每条历史记录正确选择其 key。
- **保留既有 `dispatchId` 字节不变。** 为历史 mail 写入/派生其 legacy dispatch key；新 mail 只使用 `accountId`。这样不必重写 `outbound_mail` 主键、`operation_id`、notice ID、route active FK、reply lease 外键及外部 Codex 记录。
- 新旧 dispatch identity 的 version/key 必须是 durable data，不可由“当前 loginName”反推；否则未来改登录名会再次让历史 snapshot 失效。
- 将 recovery evidence 升为含 `accountId` 的新版本；对既有 evidence 仅在 migration manifest 明确声明的 legacy mapping 下接受，并仍要求 dispatch/thread/turn/task 的全部 exact match。
- 更新 operator recovery、sidecar protocol 的 identity/ownership checks、日志字段、状态 DTO 和所有 durable store tests。

不能接受的实现包括：批量 `UPDATE delegation_meta SET user_id = ...`、重算并替换所有 dispatch IDs、自动重发 `Started`/`OutcomeUnknown` mail，或用当前角色名匹配旧 recovery evidence。

### 5. 无需迁移但需回归验证的层

- SessionJournal raw events、frozen request、RecapGrid Store/Control/Timeline：保持 `sessionDir` 时不修改历史 bytes。
- MemoPod core documents：不修改 Pod ID 或文档；只通过 Character Memory store upgrade 恢复其 owner attachment。
- `homeDir`：不属于账户 ID，但需保持原路径/权限校验；本迁移不搬迁个人文件或 Codex 私有 SQLite。

## 离线数据升级方案

### 迁移入口与输入

新增单独 operator 命令，例如：

```text
Galatea.Server operator migrate-user-identity \
  --config <new-v10-config-absolute-path> \
  --migration <absolute-manifest-path> [--apply]
```

manifest 至少逐用户指定：旧 `userId`、新 `accountId`、新 `loginName`、三个现有 state 路径的 canonical identity，以及操作者确认的未决任务处理策略。默认 dry-run；`--apply` 才能写入。

### 前置条件

1. 停止 Galatea host、所有 delegation writer 与相关 offline operator，确认 exclusive lock 可取得。
2. 对 config、CharacterMemory DB、delegation DB 及必要 SQLite sidecar 文件做逐文件备份与 SHA-256 清单；不读取或改写 Codex 私有 SQLite/rollout。
3. 用旧 binary/旧 owner 对所有源 store strict-open、`integrity_check`、schema 和 projection 做验证；源状态不合法时拒绝迁移。
4. 分类每个 delegation mail：terminal、Queued、Binding、Started、Accepted、OutcomeUnknown、Quarantined，以及 reply lease 状态。
5. 对 `Started`、`Accepted`、`OutcomeUnknown`、`RESULT_UNCONFIRMED` 先走既有 inspect/recovery/人工核查路径；不得为了获得“空队列”而盲目重发或删除。最小首版可要求这类状态全部结算或 quarantine 后才允许迁移。

### 执行与断电恢复

1. 读 manifest 和新 config，验证新 `accountId` 的格式、全局唯一性、路径不变性及 old-to-new 一一映射。
2. 为每个 store 在同目录 staging copy 上完成转换，严格验证新 schema、owner、全部 dispatch/foreign-key/projection 不变量。
3. 对 delegation，初始化所有历史 mail 的 legacy dispatch identity key，保留原 `dispatchId` 与外键字节；meta 改为新 account owner，新建 mail 使用新 key。
4. 对 Character Memory，验证旧 owner 后转换 meta owner；如旧 authority commitment 参与升级，则按新规则重算并验证。
5. 用新 binary 对 staging state strict-open；只有全体用户、两类 state 均通过后才 promote。跨两个 SQLite DB 没有天然的全局事务，因此 migration manifest 必须记录阶段、备份和 promote 状态，任意中断都 fail closed，并可从备份/已验证 staging 明确恢复。
6. 最后原子安装 V10 config，启动新 binary；旧 cookie 一律重新登录。

### 验收证据

- dry-run 零写，源数据库与 config SHA-256 完全不变；
- apply 前后有可核对的 backup/staging/promote manifest；
- 新 binary strict-open 两套 store，SQLite `quick_check` / `integrity_check` 通过；
- SessionJournal raw bytes、selected head、RecapGrid active recipe 与 MemoPod documents byte-for-byte 不变；
- 每条旧 `dispatchId`、关联 notice/lease/route reference 保持不变；新 mail 使用新 account identity 生成的 dispatch ID；
- 旧 recovery evidence 只能命中其声明的 legacy mapping，错误 account/login/dispatch/thread/turn/task 一律拒绝；
- `NotDispatched` 才可重排；`MayHaveDispatched` / `RESULT_UNCONFIRMED` 不产生第二次外部 `turn/start`；
- 认证、手动 turn、自动 Agent、Character Note、Memo recall、SessionJournal/RecapGrid reopen 的 focused tests 全部覆盖新字段及错误映射。

## 建议的实施切分

1. **设计冻结**：确定 `accountId` 格式、loginName policy、旧 evidence 支持期和“未决任务必须先结算还是保留 legacy mode”的产品决策。
2. **纯代码模型**：V10 strict config、runtime key 分离、认证/API/UI 改造，以及无真实数据库的单元测试。
3. **delegation schema + compatibility boundary**：实现 versioned dispatch identity 和 snapshot/恢复验证；先以 fixture 覆盖 terminal、Queued、Accepted、OutcomeUnknown、lease/quarantine。
4. **Character Memory upgrade**：独立实现、独立 fault-injection/backup/reopen 测试。
5. **operator migration CLI**：manifest、dry-run、staging、promote、恢复说明与端到端 fake-sidecar 验证。
6. **经单独授权的本机迁移**：先备份，再每次只迁一个用户，保留完整证据；绝不把测试绿灯等同于真实 pending external work 已消失。

## 尚待决定的问题

1. 新 `accountId` 是否允许以后完全替换，还是永远 immutable？推荐 immutable；若必须轮换，应新增 account-alias/key-rotation 模型，而不是再次改 owner。
2. `loginName` 是否允许重复、大小写折叠或历史 alias 登录？应独立定义，不能复用 durable owner 的 ordinal uniqueness。
3. 首版 migration 是否拒绝存在 active/unknown delegation 的 user？推荐拒绝并要求先恢复/人工结算，随后再实现保留 legacy key 的高级迁移。
4. operator recovery evidence 的 V1 legacy 接受期、manifest 保存周期和撤销策略是什么？该映射是恢复 authority，不能做无期限模糊匹配。

## 相关既有合同

- [Galatea 配置参考](configuration.md)
- [per-user home 设计与实施](user-home-design.md)
- [delegation durable state machine](codex-delegation-durability-design.md)
- [Codex 完成恢复 operator runbook](codex-delegation-operator-recovery.md)
- [Character Note Default MemoPod V1](character-note-default-memopod-v1.md)
