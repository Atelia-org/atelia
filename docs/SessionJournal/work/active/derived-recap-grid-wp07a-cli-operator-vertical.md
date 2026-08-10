# DerivedRecap Grid WP-07A：CLI 与 Operator Vertical Candidate

状态：Planned；依赖 WP-01..06 complete

只需加载：目标设计、Master、WP-06 handoff、本文与WP-07B摘要。

## Intent

用disposable repository把Timeline/Control/Grid/Manager/Composer串成可操作但不替换production default的CLI vertical；先锁
read-only/no-secret diagnostics与各durability domain的destructive authority。

## In scope

- `timeline inspect/export/verify/backup/restore/abandon`；
- `recap-control inspect/export`、register Family/Definition/Recipe、explicit activate CAS；
- `recap-grid inspect/export/verify/reset`；
- build recipe/candidate fulfill/full rebuild commands；
- materialization inspect的strict `--nth-previous`；
- bounded progress：active recipe、Timeline head、fulfilled-through、missing assignments；HistoryLoad仅表示Timeline cadence；
- current CLI自写parser的strict command/confirmation/error mapping。

## Candidate boundary

CLI入口必须是明确candidate command group，不能成为long-livedfeature flag。diagnostic命令read-only/no-create且Completion
factory throwing；destructive action要求exact store/timeline scope确认。Grid reset只删Grid，raw/Timeline/Control bytes exact不变；
Timeline abandon是另一条高权操作，不能借Grid reset触发。

## Acceptance matrix

1. absent/existing inspect前后filesystem snapshot一致，正文默认隐藏并有limit/max-errors；
2. Agent/developer注册definition、overlay/full/A-B recipe，build与activate分离；
3. same recipe restart只调用missing cells；partial candidate不出现在materialize；
4. strict nth=0/1/N、branch sibling、missing/damaged slot不跳邻居；
5. wrong confirmation、wrongRef/scope、unauthorized family、over budget零mutation；
6. closed-store Grid reset crash old-or-empty-valid，raw/Timeline/Control exact不变；
7. Timeline restore/abandon独立演练；
8. provider failure保留successful Cells但不commitpartial RowView；retry只补missing；
9. no active/nonempty BuildTarget明确raw-only；active unfulfilled/Invalid fail closed。

## No-Go

- diagnostic创建Store或加载provider/secret；
- CLI把Control/Grid/raw事实merge成“latest”；
- reset跨durability domain；
- candidate command变成新旧runtime长期switch。

## Done when

disposable CLI E2E、crash/confirmation/read-only gates、build/docs/diff和independent review green。

## Handoff to WP-07B

交付strict command surface、operator evidence、disposable repo/fake provider和两个Host都可复用的composition factory。
