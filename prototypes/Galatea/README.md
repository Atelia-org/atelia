# Galatea

Galatea 是面向真实 SessionJournal repository 的单会话 HTTP host。raw journal 和
selected `RefId` lineage 是会话 authority；RecapGrid Timeline、Control、Store 都是
可验证、可重建的 derived authority。

## 配置

`config.json` 必须包含 strict `recapGrid`：

```json
{
  "recapGrid": {
    "routeManifestPath": "recap-grid-routes.json",
    "agentControlProfileFiles": ["recap-grid-agent-control-profile.json"],
    "currentAgentControlProfileId": "default"
  }
}
```

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

## Readiness

`GET /api/recent-turns` 返回 `recapGridReadiness`。它绑定同一 read view 与 recent raw
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
