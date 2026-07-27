# SessionJournal.Maintainers

`Atelia.SessionJournal.Maintainers` 是依赖 `Atelia.SessionJournal` contracts
的 concrete MemoryMaintainer companion assembly。

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
不得隐式改变这些身份。

离线开发 composition root 是
[`SessionJournal.Cli`](../SessionJournal.Cli/README.md)：它选择 concrete profile，
注入 Completion client，并从 addressed `SessionJournalEngine.ReplayHistory()` 运行
maintainer。CLI 和本 companion assembly 都不会被 SessionJournal raw core 反向引用。
