# Galatea root config V7 current contract

状态：**Archived historical predecessor；current contract is [V8](galatea-root-config-v8.md)**  
Authority：current Galatea code、`GalateaRootConfigFieldLanguageTests`、
`GalateaConfigValidationTests`与`GalateaSessionProvisioningTests`  
Prior historical contracts：[V6](galatea-root-config-v6.md)、[V5](galatea-root-config-v5.md)、
[V4](galatea-root-config-v4.md)、[V3](galatea-root-config-v3.md)、
[V2](galatea-root-config-v2.md)、[V1 appendix](galatea-root-config-v1.md)

## 1. V7 delta与authority

V7把fresh/current Agent connection的缺省选择从共享`connections.json`迁移到
`config.json`的每个user。每个`GalateaUserFileConfig`与materialized
`GalateaUserConfig`都拥有一个required `defaultConnectionId`；`GalateaConfig`、
`GalateaCompletionOwner`与`GalateaHostService`不再拥有host-global default。

V6定义的character/player name、prompt composition、storage topology、session provisioning、
delegation、Character Memory、RecapGrid profile与bounds继续原样适用。本文只覆盖V7 delta，
未改动的完整规则见[历史V6合同](galatea-root-config-v6.md)。V7与sibling connections V3都没有
旧版本reader、dual field、compatibility fallback或automatic migration。

## 2. Root与user exact field language

Root仍是1 byte..1 MiB、max depth 32的Linux no-follow regular file；strict UTF-8、无BOM、
comment、trailing comma或trailing data。Property order不固定；unknown、wrong-case与
case-insensitive duplicate拒绝。`v`必须是raw exact integer token `7`；V1–V6、future、
versionless、string、fraction及exponent form全部拒绝。

Root fields保持V6集合：required `v`、`users`、`recapGrid`，optional `listenUrls`、
`callLogDir`、`maintenanceMode`。每个user exact fields为：

| Field | Shape | V7 rule |
|:--|:--|:--|
| `userId` | required string | nonblank，所有user中Ordinal unique |
| `password` | required string | nonblank |
| `characterName` / `playerName` | required string | 服从V6 shared name contract |
| `sessionDir` / `delegationStateDir` / `characterMemoryStateDir` | required string | 服从V6 path与total-topology contract |
| `sessionProvisioning` | required string | exact `existing-only`或`create-if-missing` |
| `defaultConnectionId` | required string | nonblank、strict UTF-8最多128 bytes、Ordinal exact命中catalog，并且必须在`selectableConnectionIds` allowlist |
| `characterContextTemplate` | optional string | 服从V6 context contract |
| `characterContextTemplateFile` | optional string-or-null | 服从V6 no-follow file contract |

`defaultConnectionId`不能指向hidden helper、RecapGrid-only或其他未selectable connection；
wrong-case与unknown ID都fail closed。不同user可以选择不同default。该字段不进入raw
SessionJournal、RecapGrid durable semantic identity或provider connection definition。

## 3. Completion-owned connections catalog V3

Galatea sibling `connections.json` hard-cut到Completion-owned strict catalog V3：

```json
{
  "v": 3,
  "connections": [
    {
      "id": "local",
      "kind": "openai-chat",
      "modelId": "model-id",
      "completionSurfaceId": "openai-chat/strict",
      "baseAddress": "http://localhost:8888/"
    }
  ],
  "selectableConnectionIds": ["local"],
  "bindings": {
    "galatea.input-normalizer": null,
    "galatea.outbound-mail-extractor": null,
    "galatea.character-note-extractor": null,
    "galatea.memo-recall": null
  }
}
```

V3 root required exact `v`与`connections`，optional `selectableConnectionIds`与`bindings`；
`defaultConnectionId`是unknown field并被拒绝。Completion的V2 decoder、
`CompletionConnectionsFileConfig`与default-fallback registry constructor继续服务其他consumer；
Galatea只调用V3 catalog API，并使用没有default的exact-only registry。

Galatea在Completion V3 normalization之上继续要求nonempty `selectableConnectionIds`，以及
exact四个binding key。Binding value只能是exact existing ID或`null`；helper binding与RecapGrid
route仍可命中hidden catalog connection，但fresh/current Agent selection不能绕过selectable allowlist。

## 4. Runtime selection语义

所有带optional `connectionId`的fresh/current入口服从同一规则：

1. request显式提供nonblank ID时，使用该ID，并要求它exact命中selectable allowlist；
2. request省略或为JSON `null`时，使用authenticated session user的`defaultConnectionId`；
3. unknown、wrong-case或hidden selection拒绝，不回退到任何其他connection。

Blank/whitespace `connectionId`仍是invalid HTTP input，不与omitted/null共用fallback语义。

HTML bootstrap为当前authenticated user输出其default。Browser已有的per-user `localStorage`
stored selection若仍在server提供的selectable列表中则优先；stored selection缺失或失效时才使用HTML
bootstrap中的user default。Browser显式选择后继续持久化该per-user selection。

Missing-session `create-if-missing` provisioning使用该user default connection的exact `ModelId`与
`CompletionSurfaceId`，以及该user finalized system prompt。Catalog order或其他user default不能影响它，
且该步骤仍不创建provider client或dispatch provider。

Recovery保持durable authority：Frozen completion只使用持久化的exact dispatch identity，不读取current
user default；只有需要current selection的new request与tool continuation fallback才使用user default。
改变V7 default不会重写existing durable setup或frozen request。

## 5. Bootstrap与migration

Bootstrap写exact root `v:7`，并为starter `alice`与`bob`分别写
`defaultConnectionId:"local"`。Connections template写exact V3，保留nonempty selectable与exact
四bindings，但不写global default。

V6 → V7与connections V2 → V3必须在停服、确认实际`Galatea:ConfigPath`并备份后由operator显式完成：

- root version改为7，并为每个user加入validated selectable `defaultConnectionId`；
- connections version改为3，并删除root `defaultConnectionId`；
- 其他connection bytes/values、selectable order与bindings保持operator选择。

Runtime与bootstrap都不自动迁移或重写existing config，也不会读取V6/V2后猜测per-user defaults。
