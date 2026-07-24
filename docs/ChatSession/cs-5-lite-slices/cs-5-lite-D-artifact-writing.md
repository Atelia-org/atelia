# CS-5-lite-D: LLM 结果写入 Derived Recap Artifact

> 状态：Task Brief
> 日期：2026-07-25
> 父任务：[CS-5-lite](../cs-5-lite-sessionjournal-derived-recap-store.md)

## 目标

当 rolling summary maintainer 成功产生新 `MemoryPack` / target block 后，把结果写入 B 分片提供的
derived recap store，并在 replay JSONL 中链接 artifact 与 call log。

## 输入

- C 分片提供的 replay step 与 sliding fragment。
- A 分片提供的 source address/range。
- B 分片提供的 artifact store API。
- `SessionJournalEngine.ResolveGoverningSetup(sourceRawHead)` 提供的 setup provenance。

## 输出

- produced recap artifact。
- replay record 中的 `artifactId` / `artifactPath` / `anchorRawEvent` / `previousArtifact`。
- call log paths 保持现有机制。

## 非目标

- 不把失败结果写成 produced artifact。
- 不实现 rejected/superseded artifact lifecycle。
- 不做多 maintainer coherent ArtifactSet。

## 验收

- maintainer 成功时写 artifact。
- maintainer 失败时不写 produced artifact，runner 仍报告失败。
- artifact provenance 包含 source raw range、anchor、profile、target、previous artifact、invocation、
  governing runtime config setup、governing system prompt setup、call logs。
- raw SessionJournal event chain 未新增 recap/compaction event。
