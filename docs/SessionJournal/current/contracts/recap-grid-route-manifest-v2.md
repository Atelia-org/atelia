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

## 3. Migration

V1 entry 的 `maximumOutputTokens` 被删除，不读取也不迁移。operator 必须停服并一起更新 current binary、
route manifest 与 referenced Completion connections V2：

1. route 改为 `v:2` 并删除 `maximumOutputTokens`；
2. connections改为`v:2`并删除所有`maxTokens`；
3. 重启 host，使 frozen connections 与 lazy route cache 同时从新配置建立。

Control、Timeline、Store、Family、Definition、Recipe 与已生成 recap artifact 不受此 operational contract
变更影响。历史 V1 manifest、immutable evidence 与 operator archive 保留为历史事实，不由 current writer
重写。
