# TextAdv2 Canonical Machine Surface

> 状态：F8b 第一拍 + F9 runtime boundary freeze 第一拍 + T1 cross-host parity 第二批（read-only observation/navigation + location-to-location plan-route）
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
  - `dotnet run --project prototypes/TextAdv2.E2eCli/TextAdv2.E2eCli.csproj -- --json-only --observe-actor <actorId>`
- observe actor context
  - `GET /actors/{actorId}/context`
  - `... --json-only --observe-actor-context <actorId>`
- observe actor navigation
  - `GET /actors/{actorId}/navigation`
  - `... --json-only --observe-actor-navigation <actorId>`
- observe time
  - `GET /admin/time`
  - `... --json-only --observe-time`
- move result
  - `POST /actors/{actorId}/moves/{passageId}`
  - `... --json-only --move-actor <actorId> <passageId>`
- actor route plan
  - `GET /actors/{actorId}/plan-route/{toLocationId}`
  - `... --json-only --plan-actor-route <actorId> <toLocationId>`
- location-to-location route plan
  - `GET /admin/routes/{fromLocationId}/{toLocationId}`
  - `... --json-only --plan-route <fromLocationId> <toLocationId>`
- minimal authoring snapshot
  - `POST /admin/locations`
  - `... --json-only --create-location <locationId> <name> <description>`

## 约定

- parity 比较以 JSON 语义为准，不以 DTO 引用相等或原始字符串字节相等为准
- `E2eCli` 的 machine 调用优先使用 `--json-only`
- `GameServer` 的 machine 调用优先使用对应 JSON endpoint

## Runtime Boundary Notes

- canonical machine surface 只回答“跨宿主 contract 是否已被 parity guard 守住”，不等于“底层状态是否 durable”
- durable logical time
  - `currentLogicalTick` 属于 authoritative world truth
  - reopen / host restart 后保持
  - 因而 `observe time` 既是 canonical seam，也是 durable seam
- movement trace
  - `TraceActorRoute` 当前仍是 session-owned runtime debug seam
  - `route-trace` 当前仍不应默认视为完整 cross-host canonical seam
  - 当前 parity suite 只守住了 fresh-session empty-trace baseline；一旦进入“先移动、再读 trace”的运行态，`GameServer` 与按 invocation 重开 session 的 `E2eCli` 还不会给出同一份 trace
  - reopen / host restart / reset sample world 后会 reset，这一点是设计边界，不是偶然现象
- route acceleration
  - 当前仍是 session-owned runtime cache
  - topology 变化后可 stale，reopen / host restart / reset sample world 后 reset
  - `route-acceleration` 相关 JSON 仍未进入 canonical machine surface，也不应默认视为 durable contract

## 当前不属于 canonical machine surface 的内容

- 尚未进入 parity guard 的其他 JSON seam
  - `create-actor`、`create-passage`
  - `route-trace/json`
  - `observe-route-acceleration`、`rebuild-route-acceleration`
- CLI 默认 stdout（包括 repo banner、分段标题和文本输出）
- `E2eCli help`
- `E2eCli status`
- `GameServer /healthz`
- `GameServer /admin/session-status`
- 文本输出如 `GET /admin/world`、`GET /actors/{actorId}/route-trace`

其中 `route-acceleration` 相关 JSON 当前尤其不应默认视为 canonical contract。
F9 第一拍已经把它的定位冻结为 runtime cache seam，而不是已承诺长期稳定的 agent-facing contract。

这些 surface 仍然有价值，但它们当前承担的是 host-specific operability / human-debug 角色，不是 cross-host parity contract。
