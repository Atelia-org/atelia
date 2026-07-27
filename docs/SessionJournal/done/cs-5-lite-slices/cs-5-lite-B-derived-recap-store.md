# CS-5-lite-B: Derived Recap Store 最小库

> 状态：Task Brief / Needs Design
> 日期：2026-07-25
> 父任务：[CS-5-lite](../cs-5-lite-sessionjournal-derived-recap-store.md)

## 目标

在 SessionJournal repo 内建立一个轻量的 derived recap store，用于保存 rolling summary / MemoryPack
产物和 provenance。它必须可 reopen 加载，可删除后重建，不污染 raw SessionJournal event chain。

## 推荐目录

```text
<session-journal-repo>/
  derived/
    recaps/
      v1/
        artifacts/
          <artifact-id>.json
        blobs/
          <content-hash>.txt
        indexes/
          latest-by-profile.json
```

第一版可以先只写 artifact JSON；是否把大文本外置到 `blobs/` 由设计方案决定。无论如何，目录语义应保持：

- `artifacts/` 是 append-only 产物记录。
- `indexes/` 是可删除、可重建 read model。
- `blobs/` 若启用，则按 hash 去重，仍是 artifact body 的派生存储。

## 最小 artifact 语义

```json
{
  "schema": "atelia.session-journal.derived-recap.v1",
  "artifactId": "...",
  "artifactKind": "rolling-summary",
  "profileId": "...",
  "producer": "...",
  "producerFingerprint": "...",
  "sourceRawHead": "<EventAddress>",
  "sourceStartExclusive": "<EventAddress|null>",
  "sourceEndInclusive": "<EventAddress>",
  "anchorRawEvent": "<EventAddress>",
  "governingRuntimeConfigSetup": "<EventAddress>",
  "governingSystemPromptSetup": "<EventAddress>",
  "previousArtifact": "<artifact-id|null>",
  "target": {
    "carrier": "observation",
    "blockKey": "session.rolling-summary"
  },
  "memoryPack": {},
  "content": "...",
  "invocation": {},
  "callLogPaths": [],
  "status": "produced"
}
```

第一版建议把 lineage key 定义为：

```text
profileId + target.carrier + target.blockKey
```

这样 `rolling-summary`、`autobiographical-rewrite`、`world-understanding-rewrite` 可以共用 store 形状，但
不会互相覆盖 latest。

## 非目标

- 不实现完整 ArtifactSet。
- 不实现 rejected/superseded policy。
- 不实现 retrieval index、vector index 或 graph index。
- 不写 EventJournal event，也不创建第二个 EventJournal repo。
- 不处理多 branch coherent set；第一版只为 main branch 离线 replay 服务。

## 设计关注点

- `EventAddress` 序列化必须使用稳定字符串，并能 parse 回来或至少能在后续实现 parse。
- `MemoryPack` 当前没有公共 JSON codec；需要决定第一版是在 store 内写自有 JSON shape，还是先只写
  target block `content`。
- 写入应采用 temp file + atomic move；index 写坏不能破坏 artifacts。
- latest index 可由扫描 `artifacts/*.json` 重建，不能成为 correctness source。
- `artifactId` 应稳定且避免冲突；可考虑基于 source range、profile、target、content hash 的 deterministic id，
  也可用随机 id + artifact body hash。方案文档需明确取舍。

## 验收

- 可以在空 repo 下创建 derived recap store 并写入 artifact。
- reopen 后可按 lineage key 读取 latest artifact。
- 删除 `indexes/` 后可扫描 `artifacts/` 重建 latest。
- 删除整个 `derived/recaps/v1/` 后，后续 replay 可重新生成 artifact。
- raw SessionJournal `Project()` 的 head/context 不因写 derived store 而变化。
- 至少补最小单元测试覆盖 write/read/rebuild-index。

## 后续消费者

- D 分片会在 maintainer 成功后调用 store 写入 produced artifact。
- 后续 tail-only projection 会读取 latest usable artifact 并从 `anchorRawEvent` 之后 replay raw suffix。
