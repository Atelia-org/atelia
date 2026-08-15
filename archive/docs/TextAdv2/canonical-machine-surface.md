# TextAdv2 Canonical Machine Surface

> 状态：基于当前已落地的 cross-host parity guard 基线
> 适用范围：当前 `TextAdv2.GameServer` 与 `TextAdv2.E2eCli` 的 machine-consumable surface

当前文档只声明“已被 parity guard 守住的 canonical machine surface”。

它们都落在两类入口上：

- `GameServer` 的 JSON endpoints
- `E2eCli --json-only` 下的单条 JSON operation

只有下面列出的 seam，当前才应被视为已经建立 cross-host parity guard 的 canonical contract。新增或修改 machine seam 时，应先把它补进这份列表，再补 parity tests。

## 当前已钉住的 canonical seams

- observe location
  - `GET /admin/locations/{locationId}/observation`
  - `... --json-only --observe-location <locationId>`
- observe navigation
  - `GET /admin/locations/{locationId}/navigation`
  - `... --json-only --observe-navigation <locationId>`
- observe actor
  - `GET /actors/{actorId}/observation`
  - `... --json-only --observe-actor <actorId>`
- observe actor context
  - `GET /actors/{actorId}/context`
  - `... --json-only --observe-actor-context <actorId>`
  - `availableMoves` 是唯一 canonical actor-facing action surface
  - `currentLocation` 只保留 `locationId` / `locationName` / `locationDescription` / `presentActors`
  - `currentLocation.exits` 不属于该 seam，且不应再出现在 JSON 中
- observe actor navigation
  - `GET /actors/{actorId}/navigation`
  - `... --json-only --observe-actor-navigation <actorId>`
- observe time
  - `GET /admin/time`
  - `... --json-only --observe-time`
- move result
  - `POST /actors/{actorId}/moves/{passageId}`
  - `... --json-only --move-actor <actorId> <passageId>`
- runtime route trace
  - `GET /actors/{actorId}/runtime-route-trace/json`
  - `... --json-only --trace-actor-runtime-route-json <actorId>`
  - top-level `runtimeEpochId` 是显式 runtime-boundary token
  - cross-host parity 要求两端都输出该字段，但比较时忽略它的具体值
  - 该 seam 是 canonical machine seam，但不是 durable history seam
- actor route plan
  - `GET /actors/{actorId}/plan-route/{toLocationId}`
  - `... --json-only --plan-actor-route <actorId> <toLocationId>`
- location-to-location route plan
  - `GET /admin/routes/{fromLocationId}/{toLocationId}`
  - `... --json-only --plan-route <fromLocationId> <toLocationId>`
- minimal authoring snapshot
  - `POST /admin/locations`
  - `... --json-only --create-location <locationId> <name> <description>`
  - `POST /admin/actors`
  - `... --json-only --create-actor <actorId> <name> <currentLocationId>`
  - `POST /admin/passages`
  - `... --json-only --create-passage <passageId> <locationAId> <exitNameFromA> <locationBId> <exitNameFromB>`
  - `POST /admin/passages`
  - `... --json-only --create-passage <passageId> <locationAId> <exitNameFromA> <locationBId> <exitNameFromB> <travelMode> <baseTravelCost>`

## Surface Rules

- parity 比较以 JSON 语义为准，不以 DTO 引用相等或原始字符串字节相等为准
- `E2eCli` 的 machine 调用优先使用 `--json-only`
- `GameServer` 的 machine 调用优先使用对应 JSON endpoint
- 当前文档中的 canonical seam 默认都指“当前已被 parity suite 实际 guard 的运行基线”，不是对所有 runtime 子状态都已完成收口的广义承诺
- `create-passage` 当前进入 canonical machine surface 的范围，只覆盖上面两条 success-path snapshot
- host JSON request 与 CLI positional 参数的更广义 authoring 语义差异，当前仍不是这份文档承诺已统一的内容

## Seam Boundaries

- `WorldState` / durable world truth
  - authoritative world topology
  - actor 当前所在地点
  - logical time
  - reopen / host restart 后保持
- `WorldSpatialSnapshot` / canonical spatial seam
  - 是从 durable world truth 派生的只读空间快照
  - 供 observation、planner、heuristic、authoring validation 共用
  - 不是 public machine contract，本质上是内部 derived seam
- runtime-owned seam
  - `runtimeEpochId`
  - runtime route trace
  - route acceleration snapshot / cache
  - reopen / host restart / reset sample world 后 reset

canonical machine surface 只回答“跨宿主 contract 是否已被 parity guard 守住”，不等于“底层状态是否 durable”。

## Runtime Boundary Notes

- durable logical time
  - `currentTick` 属于 authoritative world truth
  - reopen / host restart 后保持
  - 因而 `observe time` 既是 canonical seam，也是 durable seam
- runtime route trace
  - `TraceActorRuntimeRoute(...)` 是 runtime-owned seam
  - 它现在已经进入 canonical machine surface，但语义上仍是 ephemeral runtime trace，而不是 durable movement history
  - `runtimeEpochId` 的存在是为了把这条边界显式暴露给机器调用方，而不是为了提供跨 runtime 稳定 ID
  - reopen / host restart / reset sample world 后 trace 会 reset，且新的 DTO 会携带新的 `runtimeEpochId`
- route acceleration
  - 当前仍是 process-local runtime cache
  - `plan-route` / `plan-actor-route` 当前已进入 canonical machine surface，但前提仍是默认 planning baseline
  - 如果先 `rebuild-route-acceleration`，或进入 topology changed 后的 stale runtime，再去比较 route plan JSON，当前还不应默认视为已被完整 cross-host parity guard 守住
  - topology 变化后可 stale，reopen / host restart / reset sample world 后 reset
  - `route-acceleration` 相关 JSON 仍未进入 canonical machine surface，也不应默认视为 durable contract

## 当前不属于 canonical machine surface 的内容

- 尚未进入完整 parity guard 的其他 JSON seam
  - `observe-route-acceleration`
  - `rebuild-route-acceleration`
- `observe actor context` 中任何试图通过 `currentLocation.exits` 暴露 actor 可行动面的旧形状
  - 需要 actor-facing action list 时，使用 `availableMoves`
  - 需要地点级完整出口观察时，使用 `observe location`
- CLI 默认 stdout
  - repo banner
  - 分段标题
  - 文本输出
- `E2eCli help`
- `E2eCli status`
- `GameServer /healthz`
- `GameServer /admin/runtime-status`
- 文本输出如 `GET /admin/world`、`GET /actors/{actorId}/runtime-route-trace`

其中 `route-acceleration` 相关 JSON 当前尤其不应默认视为 canonical contract。当前已明确把它的定位限定为 runtime cache seam，而不是已承诺长期稳定的 agent-facing contract。

这些 surface 仍然有价值，但它们当前承担的是 host-specific operability / human-debug 角色，不是 cross-host parity contract。
