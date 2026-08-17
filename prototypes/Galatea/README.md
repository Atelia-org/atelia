# Galatea

Galatea 是面向真实 SessionJournal repository 的单会话 HTTP host。raw journal 和
selected `RefId` lineage 是会话 authority；RecapGrid Timeline、Control、Store 都是
可验证、可重建的 derived authority。

## 配置

`config.json` 使用单一 strict V1 language，必须包含exact integer `"v": 1`、至少一个user与strict
`recapGrid`：

```json
{
  "v": 1,
  "users": [
    {
      "userId": "alice",
      "password": "REPLACE_WITH_A_PRIVATE_PASSWORD",
      "sessionDir": ".atelia/galatea/sessions/alice",
      "systemPrompt": "你是家庭局域网里的私人助手。",
      "systemPromptFile": null
    }
  ],
  "listenUrls": ["http://0.0.0.0:3510"],
  "callLogDir": null,
  "maintenanceMode": false,
  "recapGrid": {
    "routeManifestPath": "recap-grid-routes.json",
    "agentControlProfileFiles": ["recap-grid-agent-control-profile.json"],
    "currentAgentControlProfileId": "default"
  }
}
```

writer固定把`v`放在首字段，reader不要求property order。missing version、future version、`null`、string、
`1.0`或`1e0`都拒绝；没有versionless compatibility reader或自动迁移。已有无版本文件必须在停服、备份并确认
实际`Galatea:ConfigPath`后人工加入`"v": 1`，应用不会重写其中的password或其他operator配置。

`connections.json` 只包含 Completion connections 与 exact default connection。历史
Agent Control profiles 必须继续保留，供 Prepared/ToolContinuation 按 frozen identity
绑定；current profile 只用于新 request。route manifest 延迟到首次 RecapGrid work 才
读取，没有 wildcard/default fallback。

该文件使用 Completion-owned strict V1 byte language：根必须包含 integer token
`"v": 1`、非空 `connections` 与 `defaultConnectionId`；每项必须显式提供
`completionSurfaceId`，并在 `baseAddress` / `baseAddressEnv` 中恰好选择一个，在
`apiKey` / `apiKeyEnv` 中至多选择一个。升级旧文件时必须人工增加 `v: 1`，并删除与
env locator 并存的空 inline source；没有 no-version compatibility reader，也不会自动
改写可能含有 secret 的文件。

GalateaHostService 唯一拥有一个 `RecapGridCompletionHost`，shutdown 顺序为：停止并
drain per-turn Online/runtime operation，再 dispose host-wide runtime，最后清理 distinct
Completion clients。CallLogDir 由统一 Completion factory decorator 服务 agent 与 recap
calls，不改变 durable identity。

## 恢复顺序

- Prepared：先 exact bind frozen completion 与 frozen tool identity；不打开 Online 或
  derived stores。
- Started：启动时 strict config/connections 已冻结；默认 Refuse 早于本次 current
  connection selection/client、route 与 derived owner。
- 当前 strict config/file loader 为 Linux-only V1：config、connections、profile、route 与
  `systemPromptFile` 都按 code-owned byte cap、existing-ancestor no-reparse 与 final-file
  no-follow regular-file 规则读取；bootstrap 也会在首次写前验证 parent chain。
- ToolContinuation：先 bind frozen tool profile/operation，再 bind current completion，
  最后打开 Online readiness。
- ToolResult NewRequest：使用 current profile，并保留 ToolResult raw tail。

Fresh/NewRequest 才创建 per-turn Online context。生命周期在合法 raw boundary 执行
Timeline reconcile/seal、必要的 Manager build，再由 Getter 产生 coherent candidate。
empty Timeline 或 no-active recipe 走 raw-only，不打开 Store 或 recap provider。

## HTTP V1 stable protocol

Galatea的first-party browser与server一起直接使用`/api/v1`；旧`/api/*`没有alias、redirect或compatibility
route。当前versioned endpoints是：

| Method | Path | Success |
|:--|:--|:--|
| GET | `/api/v1/me` | `{userId,maintenanceMode}` |
| GET | `/api/v1/recent-turns` | latest 6 completed turns、rewind token与同head RecapGrid readiness |
| POST | `/api/v1/chat/turns` | 202 `{turnId}` |
| POST | `/api/v1/chat/turns/resume` | 202 `{turnId}` |
| POST | `/api/v1/chat/turns/pop-latest` | `{poppedUserText}` |
| GET | `/api/v1/chat/turns/current` | `status,turnId,connectionId,restartRequired,recoveryHead` |
| POST | `/api/v1/chat/turns/{turnId}/stop` | 204 empty |
| GET | `/api/v1/chat/turns/{turnId}/events` | SSE V1 stream |

JSON body只接受`application/json`与可选UTF-8 charset，不接受`Content-Encoding`；exact camelCase，unknown、
wrong-case、duplicate、missing required、wrong type、required null、comment和trailing comma均拒绝。request body上限
为1 MiB，original与normalized message各为64 KiB UTF-8，connection id为128 UTF-8 bytes。matched V1 endpoint
failure除busy使用`{code,error,turnId}`外统一为`{code,error}`；unknown或retired route保持exact 404，但不承诺
该endpoint-owned envelope。diagnostic文本不作为machine branch。

recent operation共享最多4,096次physical header preview visit与16 MiB cumulative decoded logical payload，
最终production JSON最多4 MiB。pop的display source最多256 KiB UTF-8，exact receipt最多2 MiB；receipt在CAS前
预编码，response-loss只允许browser做current/recent reconciliation，不能自动重发mutation。

## SSE V1 stable protocol

SSE只接受下列closed event language：

```text
status          { code, changed? }
reasoning-delta { delta }
text-delta      { delta }
done            { recent: RecentTurnsResponseV1 | null }
error           { code, message }
```

`status.code`为`generating|normalizing-input|input-normalization-finished|using-tools`；只有
`input-normalization-finished`携带required `changed:boolean`。`error.code`为
`operator-stop|server-shutdown|completion-failed|turn-unavailable|internal-failure`。frame使用strict UTF-8与LF：
exact一个`event:`行、一个单行`data:` JSON和终止空行；id、retry、comment、multi-data与CRLF均不是V1 grammar。

nonterminal preview最多4 MiB / 16,383 events，terminal reserve为5 MiB / 1 event，whole replay最多9 MiB /
16,384 events；subscriber channel容量为256 frame references。preview cap hit只进入internal
`PreviewSuppressed`并丢弃后续preview，不停止provider或改变durable outcome。browser在decode前限制每connection
9 MiB、每raw frame 5 MiB，并使用fatal UTF-8 decoder。process-alive nonfatal turn必须exactly-one terminal；
fatal transport EOF可能没有terminal，browser必须查询current并有限重试，绝不能当success。durable completion后的
view不可用表达为`done {recent:null}`，typed原因由独立HTTP recent读取。

这些HTTP/SSE bounds、terminal/reconciliation语义与tracked first-party browser已由
`session-journal-contract-r2-approved-surfaces-v1`批准为Stable V1。该批准不包含deployment/provider readiness、
diagnostic逐字文本、cookie实现或ignored operator state。没有真实需求前不增加pagination、cursor、
Last-Event-ID、ack或dual grammar；breaking change必须形成新candidate/version。

## Readiness

`GET /api/v1/recent-turns` 返回 `recapGridReadiness`。它绑定同一 read view 与 recent raw
head：先用 Getter resolve；仅 nonempty active 且 unfulfilled 时调用 Manager 的只读
`InspectBuildProgress`。状态为 `ready`、`frontier`、`blocked`、`no-rows`、`no-active`、
`unprovisioned`、`busy`、`stale` 或 `invalid`，并携带可证明的 Timeline/Control/Store/
recipe/row authority 与 bounded metrics。该读取不 dispatch provider、不 build、不写。

Galatea 不自动 Create/Provision/Activate Grid。operator 应先使用 SessionJournal.Cli 的
`recap-grid scaffold`生成strict admission/profile/route files，再用`recap-grid init`、
`recap-grid control provision-asset --asset galatea-rolling-rewrite-zh-cn-v3`、
Control compose/put-recipe/activate 与 build 命令完成显式配置。该asset提供一个shared Family下的
`world-understanding`与`autobiography`两列；实际connection/model只来自route/connections配置，不进入durable semantic identity。
scaffold不会构造provider、
Timeline、Control或Store；Galatea仍只消费其strict canonical outputs。
