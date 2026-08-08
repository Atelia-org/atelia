# SessionJournal.DerivedRecap.Abstractions

Provider-neutral DerivedRecap execution seam shared by Planner and concrete Maintainers。

- `RecapMaintenanceEpochInput` freezes one prior-context snapshot and one ordered history slab。
- `RecapMaintenanceSuccess` is the closed `Updated | KeepUnchanged` success union。
- `IRecapBlockMaintainer` exposes only frozen identity plus one asynchronous maintenance call。

本项目只引用 raw `SessionJournal` data contracts与`Completion.Abstractions` message types；不引用 Store、
Planner、concrete Maintainers、provider client或Host composition。
