# SessionJournal.DerivedRecap.Maintainers

`Atelia.SessionJournal.DerivedRecap.Maintainers` 是依赖
`Atelia.SessionJournal` contracts 与 `Atelia.Completion.Abstractions` 的 concrete
RecapMaintainer companion assembly。

## Ownership 边界

- 本项目依赖 SessionJournal contracts。
- 本项目拥有具体 maintainer definitions，以及与之直接关联的 profiles、prompts、
  target paths、factories 和窄职责 helpers。
- `Atelia.SessionJournal` raw core 不得反向引用本项目。
- event-addressed Building/Published persistence、strict ordinal 与 structural inspection 属于
  `SessionJournal.DerivedRecap.Store`。
- trigger、Maintain/Inherit plan、Building Resume、Published Restore 与 bounded catch-up 属于
  `SessionJournal.DerivedRecap.Planner`。
- Completion client、Store、Planner、policy 与 concrete Maintainers 的装配属于 CLI/Agent Host
  composition root，不属于本项目。
- 应用级 role 始终是 SessionJournal raw event / recovery contracts 之外的 policy。

稳定的 `MaintainerId`、target block key 和 embedded prompt `LogicalName` 是 durable/fingerprint
identity 的一部分。程序集已更名为 `Atelia.SessionJournal.DerivedRecap.Maintainers`，但八个 prompt
资源仍有意保留 `Atelia.SessionJournal.Maintainers.Prompts.*` logical names；这是 identity 保持，
不是旧程序集依赖或待清理的 namespace。移动实现类型、源文件或 prompt 文件时，不得顺手改变这些
logical names、对应常量、MaintainerId 或 block key。若确实要升级 identity，应显式做 schema/profile
cutover并更新 golden tests。

prompt fingerprint 使用带 schema 与字段边界的 canonical structured JSON，不以 NUL delimiter
拼接两个 prompt。

每个descriptor还计算opaque
`MaintainerCapabilityFingerprint`。canonical preimage schema固定为
`atelia.session-journal.recap-maintainer-capability.v1`，UTF-8 JSON字段顺序固定为
`schema`、`implementationId`、`maintainerId`、`target`（`carrier`、`blockKey`）、
`promptFingerprint`；输出格式固定为`sha256:<64 lowercase hex>`。model、connection、secret、
logging path不属于语义能力，不进入fingerprint。实现或prompt语义变化产生新fingerprint时，完整catalog
应同时保留仍可能被旧Building/Published set引用的旧descriptor；active profile只决定新planning。

Store与Planner把fingerprint当作opaque token；canonical preimage与具体实现版本由本companion
assembly拥有。raw `Atelia.SessionJournal`只在neutral `IRecapBlockMaintainer` contract上暴露该token，
不会依赖本程序集或理解其preimage。
`RewriteRecapBlockMaintainer`的public constructor始终从exact profile与`ImplementationId`计算该值，
不接受caller-supplied fingerprint；operator遇到旧v4 sidecar仍必须显式abandon/reset后重建。

离线开发 composition root 是
[`SessionJournal.Cli`](../SessionJournal.Cli/README.md)：它通过
`RecapMaintainerProfileCatalog` 解析 stable role/profile descriptor，注入 Completion
client，并先通过public `DerivedRecapOperationPreparer`取得exact authority，再把完整 concrete
maintainer registry交给`DerivedRecapPreparedExecutor`或online lifecycle composition。Host不应把
registry直接传给Planner的internal workflow executor。Store只报告durable structure/capability，
Planner决定何时调用哪个maintainer；本程序集既不打开Store，也不拥有Building/Published workflow。

CLI 和本 companion assembly 都不会被 SessionJournal raw core 反向引用。未来其他 Host 可以使用
同样的注入边界组合不同 catalog/policy，而无需让 raw core 认识具体 maintainer。
