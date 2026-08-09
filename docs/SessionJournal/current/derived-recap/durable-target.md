# DerivedRecap v8 durable target

状态：current direct-cut durable contract。

```text
derived/recap/v8/refs/<ref>/
  store.json
  building/<admission>/
    manifest.json
    epoch-input.json
    blocks/<block-id>.json
  published/<admission>/
    manifest.json
    epoch-input.json
    blocks/<block-id>.json
    publication.json
```

Store header、manifest、epoch input、final和publication均为strict next-generation canonical wire；旧
generation直接拒绝。Building在install后self-contained，不依赖raw或previous publication恢复prompt。

manifest只持有RefId、admission、epoch-input commitment、complete ordered roster与payload hash。
epoch input持有Start/Admission boundary+setups、exact raw count/hash、closed frozen messages，以及Empty或
structured previous pack。final不持有per-block coverage/checkpoint；publication commitments必须逐ordinal
覆盖完整roster。

写入遵循同目录staging、flush/fsync、atomic replace/rename和directory barrier。authority只由bounded
captured bytes、exact descriptor和CAS state签发；oversize/I/O/permission fault不产生repair authority。

aggregate caps在Building install、final write和publish/next-prior形成前fail closed。成功Published必须
能成为下一epoch的structured prior。损坏previous source在Building install之后不会影响Resume/Restore。

full rebuild spool位于独立`derived/recap/rebuild/v1`，不被v8 reset或publication selection消费为事实源。
