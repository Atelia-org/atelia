# RecapGrid Route Manifest V2

状态：Current candidate；V1 hard cut，无 compatibility reader。

## 1. Authority split

Route manifest 只拥有 exact runtime selection 与 Recap 调度 policy：

```text
(familyDigest, runtimeProtocolId, semanticModelId?)
  -> connectionId + maximumConcurrency + dispatchTimeoutMilliseconds
```

`connections.json` 拥有 connection 的 provider、model、endpoint、credential locator、reasoning 与cache。
切换 `connectionId` 时，这些配置作为一个整体切换；route 不得再声明`maximumOutputTokens` override。

Completion request与connection有意不暴露caller-selected output cap。若省略provider字段表示不限量或模型最大值，
adapter必须省略；若wire要求数值或省略会选择较低的model-varying default，adapter只能发送所选模型的
provider-reported maximum。这样已经计费的Recap generation不会被本地预算截断成不可用结果。

同一 provider/model 若要服务不同 client policy，应配置不同 connection id，而不是在 route 重新覆盖。

## 2. Canonical language

Root 是 exact ordered object：

```json
{"v":2,"routes":[]}
```

每个 route 是 exact ordered object：

```json
{"familyDigest":"<64-lowerhex>","runtimeProtocolId":"<id>","semanticModelId":null,"connectionId":"<id>","maximumConcurrency":1,"dispatchTimeoutMilliseconds":900000}
```

- root 只允许 `v`、`routes`，且 `v` 必须是 plain integer `2`；
- route property 顺序与集合必须 exact；`semanticModelId` 必须显式为 string 或 `null`；
- route key 必须 exact unique，按 family/runtime/semantic canonical 排序；
- document 最大 1 MiB、最多 4,096 routes；identifier 最大 128 strict UTF-8 bytes；
- `maximumConcurrency` 为 1..1,024；timeout 为 1 ms..1 day 的整毫秒值；
- unknown、missing、duplicate、wrong order、noncanonical encoding、V1 与 future version全部 fail closed。

### Deadline 所有权与宿主重试

默认 invoker 仍只执行一次；Runtime 在 dispatch 时启动该 route 的 timeout，等待调用与清理退出，
不会因超时另发重叠请求。借用 registry 的宿主可只为 maintenance route 注入
`IRecapCompletionAttemptDeadlineInvoker`：其 `AttemptTimeout` 必须为正值且严格等于 route timeout，
由它负责每次底层生成的 deadline、退避及清理。此时 Runtime 不再给整个逻辑调用叠加总期限；
caller Stop/shutdown token 仍贯穿调用。没有该类型化 owner 时不能关闭 Runtime 的单次期限。

Galatea 的 maintenance route 使用此接缝，route timeout 直接成为每 attempt 期限，优先于主生成/四个
feature binding 使用的 connection timeout map。`BindAgentExact` 返回的主角色 client 不受 maintenance
factory 装饰；它由主轮次单独装饰一次，避免两层重试。

重试只覆盖已投影 frozen request 的纯生成，Manager 的 work、cell 校验、`PutCell` 和行结算均在其外。
`NewCalls`/`MaximumNewCalls` 与 completion-started/settled telemetry 计数的是逻辑 work dispatch，
不是 decorator 内的物理 attempt/计费次数；它们不能宣称严格费用上限。物理 attempts 的诊断属于宿主。

## 3. Migration

V1 entry 的 `maximumOutputTokens` 被删除，不读取也不迁移。operator 必须停服并一起更新 current binary、
route manifest 与 referenced Completion connections V2：

1. route 改为 `v:2` 并删除 `maximumOutputTokens`；
2. connections改为`v:2`并删除所有`maxTokens`；
3. 重启 host，使 frozen connections 与 lazy route cache 同时从新配置建立。

Control、Timeline、Store、Family、Definition、Recipe 与已生成 recap artifact 不受此 operational contract
变更影响。历史 V1 manifest、immutable evidence 与 operator archive 保留为历史事实，不由 current writer
重写。
