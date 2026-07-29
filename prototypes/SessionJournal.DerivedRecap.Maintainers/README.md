# SessionJournal.DerivedRecap.Maintainers

`Atelia.SessionJournal.DerivedRecap.Maintainers` 是依赖
`Atelia.SessionJournal` contracts 与 `Atelia.Completion.Abstractions` 的 concrete
RecapMaintainer companion assembly。

## Ownership 边界

- 本项目依赖 SessionJournal contracts。
- 本项目拥有具体 maintainer definitions，以及与之直接关联的 profiles、prompts、
  target paths、factories 和窄职责 helpers。
- `Atelia.SessionJournal` raw core 不得反向引用本项目。
- derived-memory planning、durable artifacts、epoch coordination、
  provisioning、orchestration 与 publication 属于 derived-memory subsystem
  或 host composition root，不属于本项目。
- 应用级 role 始终是 SessionJournal raw event / recovery contracts 之外的 policy。

稳定的 maintainer ID 和 target block key 是持久化身份。移动或重命名实现类型时，
不得隐式改变这些身份。prompt fingerprint 使用带 schema 与字段边界的 canonical
structured JSON，不以 NUL delimiter 拼接两个 prompt。

离线开发 composition root 是
[`SessionJournal.Cli`](../SessionJournal.Cli/README.md)：它通过
`RecapMaintainerProfileCatalog` 解析 stable role/profile descriptor，注入 Completion
client，并把 concrete maintainer 交给 DerivedMemory 的 exact-epoch runner 或 multi-role
orchestrator；online `run-online-turn` 也由 CLI 把相同 exact role executions 注入 generic
DerivedMemory lifecycle coordinator。history 切分、epoch lookup、transaction/settlement 与 artifact/set
persistence 不属于本程序集。CLI 和本 companion assembly 都不会被 SessionJournal raw
core 反向引用。
