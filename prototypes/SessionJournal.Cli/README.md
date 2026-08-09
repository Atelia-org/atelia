# SessionJournal.Cli

DerivedRecap当前operator surface：

```text
recap planner-config init|inspect
recap history-load inspect
recap create
recap inspect
recap materialize-inspect
recap run
recap rebuild --campaign <id> [--reset --confirm-ref <ref-id>]
recap reset --confirm-ref <ref-id>
```

旧 `resume`、`restore`、`abandon-building`命令已删除。`recap run`内部自动优先恢复Building或修复
Published，随后才惰性加载active config并规划新shared epoch。正常run超过bounded raw authority只返回
FullRebuildRequired，不创建spool。

`recap rebuild`是显式operator path：首次用`--reset --confirm-ref`在sealed raw audit完成后重置v8
truth Store；若返回MoreWorkPending，使用相同campaign且不再传`--reset`继续。campaign绑定exact
RefId/head；head变化fail closed。

`planner-config init`写canonical v3到`config/recap-planner-config.json`；inspect严格拒绝旧schema、
unknown/duplicate字段和非canonical bytes。frozen recovery不依赖该文件可用。

`run-online-turn`使用同一个`DerivedRecapOnlineLifecycleCoordinator`和deferred Maintainer registry；
NoBuild、全healthy Resume或选择失败前不会构造provider client。R6 production acceptance仍待重跑，
因此本README不声称real-provider/staging已经通过。
