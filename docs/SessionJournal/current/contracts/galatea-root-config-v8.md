# Galatea root config V8 current contract

> 当前配置由 [V10 合同](galatea-root-config-v10.md) 定义。下文的字段、current/writer 描述与验收证据
> 均属于本页版本当时的历史语境，保留原义；当前 loader 不接受该旧版本。

状态：**Archived historical predecessor；current contract is [V10](galatea-root-config-v10.md)**  
Authority：historical Galatea code、`GalateaRootConfigFieldLanguageTests`、
`GalateaConfigValidationTests`与`GalateaSessionProvisioningTests`  
Prior historical contract：[V7](galatea-root-config-v7.md)

## 1. V8 delta与authority

V8在root `config.json`加入optional `serverAgentUserIds`，作为服务端持续驱动的用户集合。
每个user的required `defaultConnectionId`继续拥有main connection缺省选择；不增加另一份
server-agent connection配置，也没有CLI enrollment override。

V7的per-user connection、Completion catalog V3、storage topology、prompt composition、
session provisioning、delegation、Character Memory与RecapGrid规则继续适用。未变规则见
[V7](galatea-root-config-v7.md)及其引用的[V6](galatea-root-config-v6.md)。

## 2. Exact field language

Root仍是1 byte..1 MiB、max depth 32的Linux no-follow regular file；strict UTF-8，无BOM、
comment、trailing comma或trailing data。Unknown、wrong-case与case-insensitive duplicate
property拒绝。`v`必须是raw exact integer token `8`；旧版本、future、versionless、string、
fraction及exponent form全部拒绝。

Root fields为required `v`、`users`、`recapGrid`，optional `listenUrls`、`callLogDir`、
`maintenanceMode`、`serverAgentUserIds`。新增字段遵守：

- 省略等价于空集合；bootstrap显式输出`[]`。
- 显式`null`、non-array或non-string item拒绝。
- 最多`MaximumUserCount`（256）项；每项nonblank，按Ordinal比较unique。
- 每项必须Ordinal exact命中`users[].userId`；unknown与wrong-case拒绝，不trim或猜测。
- 列表顺序保留，但不赋予用户优先级。

Loader与普通构造产生的 `GalateaConfig.ServerAgentUserIds`为non-null只读快照；record initializer可覆盖该属性。
Production与injected host construction重新使用同一语义校验并持有独立快照，外部mutable list
及record initializer不能在host构造后改变enrollment。构造失败必须发生在启动delegation supervisor前。

## 3. 启动语义与边界

列表只授权已列出的user接受server-owned automatic admission；未列出的user继续按需attach。
自动回合的main connection使用该session user的`defaultConnectionId`，并服从现有selectable
connection校验。Enrollment不重写durable SessionJournal或recovery dispatch identity。

`maintenanceMode`仍然禁止正常运行；非空enrollment不会覆盖maintenance。
配置文件是启动时的authority，不提供hot reload或持久化运行时管理API。后续管理后台可以增加
process-local暂停/恢复，但重启仍按静态配置决定成员集合。

## 4. Bootstrap与migration

Bootstrap输出exact root `v:8`、`serverAgentUserIds:[]`，starter users仍分别具有
`defaultConnectionId:"local"`；sibling `connections.json`保持exact V3。

V7 → V8由operator显式迁移：把root version改为8；省略新增字段或设置`[]`保持全部用户
不受后台驱动，启用时写入明确的user ID集合，例如`"serverAgentUserIds":["gpt"]`。
其他user、connection、binding与storage选择保持不变。Runtime与bootstrap均不自动迁移旧文件。
