# SessionJournal.DerivedRecap.Store

当前 production Store 是 direct-cut v8 shared-epoch sidecar，根目录为
`derived/recap/v8/refs/<ref-id>/`。旧 v4-v7 schema 没有 reader、migration 或 compatibility
fallback；需要 operator reset/rebuild。

一个 Building 只包含：

- canonical `manifest.json`：一个 complete ordered roster；
- canonical `epoch-input.json`：shared Start/Admission boundary、exact raw range commitment、
  frozen ordered history projection，以及 Empty 或 structured previous recap pack；
- `blocks/<id>.json`：每个 roster member 一个 epoch-bound direct final。

不存在 per-block cursor、`Inherit`、catch-up route、checkpoint 或 `work/`。相同正文的
KeepUnchanged 仍生成本 epoch的新 execution identity。只有 complete roster 能 publish；publication
commitments、manifest hash、ordinal、block definition 与 final payload 必须逐层一致。

恢复以 frozen Building/Published snapshot 为 authority。健康 final 跳过，missing 或完整 bounded
capture 后确认 damaged 的 slot 才签发写 authority；I/O、permission、oversize 不会被伪装成 damaged。
publication missing/canonical damage可由 ManifestWitness envelope-last reseal，identity conflict不可。

`DerivedRecapContextCandidateSource`按 selected raw lineage做 strict ordinal selection，descriptor绑定
RefId、admission、publication envelope、setup与completion raw head；materialize前后二次验证。

显式 full rebuild 使用独立 `derived/recap/rebuild/v1` execution-aid spool。spool只保存地址/header/
provenance/index，不保存 event body、prompt、recap或epoch policy；seal后仍须与当前 raw read view、
captured head及RefId重新绑定。删除spool不改变raw authority，也不影响从raw重建。

关键入口：

- `DerivedRecapV8Contracts.cs` / `DerivedRecapV8Codec.cs`
- `DerivedRecapEpochStore.cs` / `DerivedRecapEpochStoreContracts.cs`
- `DerivedRecapContextCandidateSource.cs`
- `DerivedRecapRebuildSpoolStore.cs`

Focused tests：`DerivedRecapEpochStoreCandidateTests`、`DerivedRecapV8CodecCandidateTests`、
`DerivedRecapRebuildSpoolTests`。CrashHarness只覆盖v8 final/publish/reset failpoints。
