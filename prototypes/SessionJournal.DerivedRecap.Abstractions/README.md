# SessionJournal.DerivedRecap.Abstractions

Provider-neutral DerivedRecap execution seam shared by Planner and concrete Maintainers。

- `RecapMaintenanceEpochInput` freezes one prior-context snapshot and one ordered history slab。
- `RecapMaintenanceSuccess` is the closed `Updated | KeepUnchanged` success union。
- `IRecapBlockMaintainer` exposes frozen identity、one opaque reference-only `RuntimeGroupAffinity` plus one
  asynchronous maintenance call；该affinity只用于未来operation-local scheduling，不持久化。
- `IRecapBlockMaintainerRegistry`、`RecapBlockMaintainerRegistry`与
  `DeferredRecapBlockMaintainerRegistry`提供exact opaque executable binding lookup；Planner不需要认识
  family、lane、client或model。

本项目只引用 raw `SessionJournal` data contracts与`Completion.Abstractions` message types；不引用 Store、
Planner、concrete Maintainers、provider client或Host composition。
