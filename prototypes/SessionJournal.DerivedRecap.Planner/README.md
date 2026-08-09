# SessionJournal.DerivedRecap.Planner

当前 production Planner 只实现 single shared epoch + complete roster。一次 planning decision只有：

- `NoBuild(reason)`：零 Maintainer 调用；
- `Build(admission)`：冻结一个 shared slab，完整 roster每个member恰好一个logical invocation。

member结果只能是 Updated 或 KeepUnchanged。首轮没有 previous block，KeepUnchanged拒绝；后续Keep从
structured `PriorRecapPackSnapshot`按id/target复制正文，同时写入新epoch execution identity。

`DerivedRecapEpochCampaignExecutor`先恢复 frozen Building/Published damage；这一阶段只使用 frozen
artifact、Host capability与code-owned recovery caps，不读取active config。只有需要规划新epoch时，
才惰性加载 `RecapEpochActiveConfiguration`。active topology变化返回typed FullRebuildRequired，不做
partial bootstrap。

normal online path只读bounded prefix/window。超过raw growth或authority cap返回typed
FullRebuildRequired，零spool、零full scan。一个operation可连续发布多个无gap/overlap epoch；预算不足
时保留已发布进度并返回MoreWorkPending，不能把中间publication当Ready。

online lineage proof与frozen epoch wire共享code-owned binary raw authority bound：单epoch最多512个raw
events，online prefix最多513个headers；active v3只能收紧，不能扩界。恢复按frozen
`RawEventCount`重放exact slab，不再使用进程启动时的default/config snapshot截断合法Building。

显式 rebuild 由 `DerivedRecapFullRebuildAuthorityPreparer`建立sealed selected-lineage authority，
`RunExplicitRebuildAsync`从bootstrap顺序选择bounded replay-safe slabs，并复用同一serial kernel与v8
Store。cadence boundary不写入spool；crash后从latest Published admission重新定位。

`RecapEpochConfigCodec`是strict canonical v3。旧schema、unknown/duplicate字段、旧的
`MaxMaintainerCallsPerBuild`与`MaxRouteEndpointsPerBlock`直接拒绝。当前执行字段为
`MaxMaintainerCallsPerEpoch`、`MaxEpochsPerOperation`、`MaxMaintainerCallsPerOperation`、
`MaxRecapBlockCount`及aggregate Store caps。

关键入口：

- `DerivedRecapEpochCampaignContracts.cs`
- `DerivedRecapEpochCampaignExecutor.cs` / `DerivedRecapExplicitRebuildExecutor.cs`
- `DerivedRecapSerialEpochKernel.cs`
- `DerivedRecapOnlineLifecycleCoordinator.cs`
- `RecapEpochConfigDocument.cs`

R4之前执行刻意保持serial；parallel/family cache调度尚未进入production。
