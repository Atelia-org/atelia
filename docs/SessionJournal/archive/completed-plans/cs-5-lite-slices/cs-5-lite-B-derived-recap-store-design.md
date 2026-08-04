# CS-5-lite-B 设计：Derived Recap Store 最小库

> 状态：Design / Ready for Implementation
> 日期：2026-07-25
> 对应 brief：[CS-5-lite-B: Derived Recap Store 最小库](cs-5-lite-B-derived-recap-store.md)

## 1. 结论

第一版把 `Derived Recap Store` 做成 **SessionJournal repo 内的可删除 sidecar store**：

```text
<session-journal-repo>/
  derived/
    recaps/
      v1/
        artifacts/
          <artifact-id>.json
        indexes/
          latest-by-profile.json
```

暂不启用 `blobs/`。artifact JSON 同时内联完整 `memoryPack` 与 target block 的 `content`，这样后续 tail-only
projection 可以直接 materialize context header / `CompletionRequest` context，也能让人工打开单个 artifact
就看到当前 rolling summary。

推荐实现位置：

- 项目：`prototypes/SessionJournal/SessionJournal.csproj`
- 命名空间：`Atelia.SessionJournal.Derived`
- 依赖：继续沿用 `SessionJournal` 已有依赖，不新增对 `prototypes/ChatSession` 的项目引用

理由：`prototypes/SessionJournal` 是新的 LLM Session 基础设施主干，derived recap store 属于 raw
SessionJournal repo 的长期上下文派生层。旧 `prototypes/ChatSession` 的存储技术选型不再作为长期方向；
memory / recap / compaction 相关 abstraction 后续应逐步上移到 SessionJournal 主干，而不是再通过桥接项目固化
两套主干。

`prototypes/ChatSession.SessionJournal` 桥接项目降为不推荐的过渡备选。只有在短期必须同时保留两套公共 API、
且又无法及时整理 `SessionJournal` 命名空间时，才考虑临时使用；它不应成为 CS-5-lite-B 的正式落点。

B0 已经把 memory substrate 的长期归属收到 `prototypes/SessionJournal`。因此 B 不再围绕旧
`Atelia.ChatSession.MemoryPack` 或桥接项目设计正式 API：

- B 在 `SessionJournal` 内定义 store、artifact DTO、`EventAddress` text codec、latest index，以及
  `MemoryPack` 与 artifact JSON 之间的 snapshot codec。
- B 的 public API 优先接受/返回 `Atelia.SessionJournal.MemoryPack`、`MemoryPackBlockPath` 等权威类型。
- `MemoryPackSnapshotDto` 如果存在，应定位为 store wire DTO / codec 内部模型，而不是调用方长期手写的主模型。
- C/D 分片负责从 addressed replay 构造 `RecentHistorySlice`，调用新的 `Atelia.SessionJournal`
  maintainer/orchestrator，并把成功后的 `MemoryPack` 写入 B。
- 后续迁移已将该项目收口为
  `prototypes/SessionJournal.Maintainers` concrete MemoryMaintainer companion assembly；
  这不改变本分片的 DerivedRecapStore 设计。

推荐后续迁移方向：

```text
prototypes/SessionJournal
  - MemoryPack / carrier-block path 抽象
  - IMemoryBlockMaintainer / maintenance request-result / orchestrator
  - DerivedRecapStore / later ArtifactSet / compaction framework

maintainer implementation project
  - autobiography rewrite profile
  - world-understanding rewrite profile
  - concrete prompt resources and model-facing producers

prototypes/ChatSession
  - legacy/simple engine
  - 逐步改为引用 SessionJournal memory substrate，或在后续淘汰
```

## 2. Store 与 repo path 关联

store 只接受 SessionJournal repo root path，不自行打开或维护 raw EventJournal：

```csharp
public sealed class DerivedRecapStore {
    public static DerivedRecapStore Open(string sessionJournalRepoPath);
}
```

路径规范：

- 构造时把 repo root 转为 `Path.GetFullPath(...)`。
- `StoreRoot = Path.Combine(repoRoot, "derived", "recaps", "v1")`。
- 所有写入只能发生在 `StoreRoot` 下。
- store 不创建、推进或读取 raw branch；需要校验 raw head 是否仍是当前 `main` 头时，交给 D/E 分片调用方处理。

这保持 B 的职责很窄：**只保存和索引派生产物，不判断 raw journal 语义正确性**。

## 3. Artifact JSON v1

### 3.1 推荐 wire shape

```json
{
  "schema": "atelia.session-journal.derived-recap.v1",
  "artifactId": "rr_01h8..._9f2a6c0d",
  "artifactKind": "rolling-summary",
  "createdUtc": "2026-07-25T12:34:56.7890123Z",
  "lineageKey": "rolling-summary|profile:rolling-summary|target:observation/session.rolling-summary",
  "profileId": "rolling-summary",
  "producer": "ChatSession.BacktestCli/replay-rolling-summary-session-journal",
  "producerFingerprint": "sha256:...",
  "sourceRawHead": "ej1:<32-hex>",
  "sourceStartExclusive": null,
  "sourceEndInclusive": "ej1:<32-hex>",
  "anchorRawEvent": "ej1:<32-hex>",
  "governingRuntimeConfigSetup": "ej1:<32-hex>",
  "governingSystemPromptSetup": "ej1:<32-hex>",
  "previousArtifact": null,
  "inputArtifacts": [],
  "target": {
    "carrier": "observation",
    "blockKey": "session.rolling-summary"
  },
  "memoryPack": {
    "schema": "atelia.session-journal.memory-pack.snapshot.v1",
    "system": [],
    "observation": [
      {
        "key": "session.rolling-summary",
        "text": "..."
      }
    ],
    "action": []
  },
  "content": {
    "storage": "inline",
    "text": "...",
    "sha256": "<lower-hex>"
  },
  "invocation": {},
  "callLogPaths": [],
  "status": "produced"
}
```

字段选择：

- `lineageKey` 显式写入 artifact，避免 index 和扫描端各自拼接出不同 key。
- `createdUtc` 采用 UTC ISO-8601 round-trip 字符串，用于人工诊断；latest 判定不主要依赖它。
- `inputArtifacts` 第一版通常为空或只包含 `previousArtifact`，但字段先保留为数组，贴合后续 Artifact Journal。
- `status` 第一版只写 `produced`；失败不写 artifact。
- `invocation` 直接复用 `CompletionDescriptor` 的 JSON 序列化结果，允许为 `null` 或 `{}`。

### 3.2 MemoryPack JSON

`MemoryPack` 现在归属 `Atelia.SessionJournal`，但它仍不应直接暴露 `OrderedDictionary` 的默认 JSON 形态作为
长期 wire contract。B 第一版应在 `Atelia.SessionJournal.Derived` 内提供专用 snapshot codec：

```csharp
internal sealed record MemoryPackSnapshotDto(
    string Schema,
    IReadOnlyList<MemoryPackBlockDto> System,
    IReadOnlyList<MemoryPackBlockDto> Observation,
    IReadOnlyList<MemoryPackBlockDto> Action
);

internal sealed record MemoryPackBlockDto(string Key, string Text);
```

如果实现者认为测试或调用侧需要可见 DTO，可把 DTO 设为 `public`，但 `DerivedRecapWriteRequest` /
`DerivedRecapArtifact` 的主要使用路径仍应提供 `MemoryPack` 视图，避免 B/C/D 再形成一套并行 memory 模型。

序列化规则：

- carrier 名使用 `system|observation|action` 语义，但 JSON 字段名固定为
  `system`、`observation`、`action`。
- 每个 carrier 用数组而不是 object，保留 `OrderedDictionary` 顺序。
- 反序列化到 `Atelia.SessionJournal.MemoryPack` 时按数组顺序 `UpsertBlock`。
- 空 carrier 写 `[]`，不省略字段。

同时保留 `content`，它必须等于 `memoryPack` 中 `target` 指向 block 的 text。读取 artifact 时如果二者不一致，
该 artifact 判为 corrupt，不进入 latest index。

## 4. EventAddress 字符串策略

不要依赖 `EventAddress.ToString()`。当前 `EventAddress` 是 record struct，默认 `ToString()` 不是稳定 wire
format；底层只有 16 字节 `EventAddressCodec`。

第一版定义 store 局部 codec，后续可上移到 `Atelia.EventJournal`：

```text
ej1:<ticket-packed-16hex><segment-number-8hex><hint-packed-8hex>
```

示例：

```text
ej1:0000000000010080000000013f4a91bc
```

规则：

- `ej1:` 后固定 32 个小写 hex 字符。
- 三段按 `EventAddressCodec.Encode` 的字段顺序表达，但使用 big-endian textual hex：
  `Ticket.Packed` 16 hex + `SegmentNumber` 8 hex + `Hint.Packed` 8 hex。
- parse 时拒绝长度不符、非 hex、ticket 为 0、segment 为 0。
- nullable address 在 JSON 中写 `null`，不要写全零地址字符串。

这个格式人工可读、可排序性不作为合同、不会被 record 输出污染。

## 5. artifactId 与 lineage

### 5.1 lineage key

第一版 lineage key：

```text
<artifactKind>|profile:<profileId>|target:<carrier>/<blockKey>
```

示例：

```text
rolling-summary|profile:rolling-summary|target:observation/session.rolling-summary
```

`profileId`、`artifactKind`、`carrier`、`blockKey` 必须使用 ordinal 比较。`profileId` 与 `blockKey`
第一版禁止包含 `|`，`carrier` 只能是 `system|observation|action`。

### 5.2 artifactId

推荐使用 **deterministic id + collision suffix**：

```text
rr_<source-end-short>_<body-hash-short>
```

其中：

- `rr` 是 `rolling-summary` 的短前缀；后续 kind 可扩展自己的短前缀。
- `source-end-short` 是 `sourceEndInclusive` 地址 hex 的前 12 个字符。
- `body-hash-short` 是 canonical identity hash 的前 16 个字符。

实现细节：

1. 构造 artifact DTO 时先把 `artifactId` 设为临时空值或固定占位。
2. 构造 deterministic identity view，并对该 view 的 canonical JSON UTF-8 序列化结果计算 SHA-256。
3. identity view 必须排除 `artifactId`、`createdUtc`、index/rebuild 时间、临时路径、diagnostic warning、
   本地绝对路径、文件 mtime、以及任何随机数或非决定性诊断字段。
4. identity view 应包含会改变 artifact 语义的字段，例如 `schema`、`artifactKind`、`lineageKey`、
   `profileId`、`producer`、`producerFingerprint`、source raw range、anchor、governing setup、
   `previousArtifact`、`inputArtifacts`、`target`、`memoryPack`、`content`、`invocation`、`callLogPaths`
   和 `status`。
5. `createdUtc` 只进入最终 artifact JSON，用于人工诊断和 latest 的后级 tie-break，不参与 deterministic id。
6. 得到 `artifactId` 后写入最终 artifact JSON。
7. 如果目标文件已存在且内容完全一致，视为 idempotent success。
8. 如果目标文件已存在但内容不同，追加 `_<n>` 后缀，`n` 从 2 开始。

取舍理由：纯随机 id 简单但不利于删除后重建和人工 diff；完全 deterministic id 遇到同一 source range
不同 producer/prompt 重跑时会冲突。短 hash 加冲突后缀兼顾可读、可重建与安全性。

## 6. latest-by-profile index

### 6.1 文件格式

```json
{
  "schema": "atelia.session-journal.derived-recap.latest-index.v1",
  "rebuiltUtc": "2026-07-25T12:35:00.0000000Z",
  "items": {
    "rolling-summary|profile:rolling-summary|target:observation/session.rolling-summary": {
      "artifactId": "rr_...",
      "artifactPath": "../artifacts/rr_....json",
      "sourceRawHead": "ej1:<32-hex>",
      "anchorRawEvent": "ej1:<32-hex>",
      "sourceEndInclusive": "ej1:<32-hex>",
      "createdUtc": "2026-07-25T12:34:56.7890123Z",
      "producerFingerprint": "sha256:..."
    }
  }
}
```

`artifactPath` 使用从 `indexes/` 到 artifact 的相对路径，只作为诊断和人工查看；读取时以 `artifactId`
重新定位 `artifacts/<artifactId>.json`，避免 path traversal。

### 6.2 latest 选择规则

扫描 `artifacts/*.json` 重建 index 时：

1. 跳过无法解析、schema 不匹配、status 非 `produced`、`content` 与 target block 不一致的 artifact。
2. 按 `lineageKey` 分组。
3. 对每组优先使用 `previousArtifact` DAG 后继关系选择 latest：
   - 若某个候选可从组内其他候选沿 `previousArtifact` 链到达，后继候选优先于前驱候选。
   - 若存在唯一没有组内后继的 produced artifact，选择它。
   - 若出现分叉、断链、环或多个无后继候选，进入下一层比较，并记录 warning。
4. 在 CS-5-lite 的 main-only 假设下，用 `sourceEndInclusive` 的 EventAddress physical coordinate 比较：
   - 比较 tuple：`(SegmentNumber, Ticket.Offset, Ticket.Length, Hint.Packed)`。
   - tuple 更大的候选视为更新。
   - 这只是 main-only append 顺序启发式，不等价于 parent-chain ancestry 校验。
5. 若 physical coordinate 仍无法唯一，例如地址缺失、parse 失败或完全相同，则比较 `createdUtc`。
6. 若 `createdUtc` 仍无法唯一，则使用 `artifactId` ordinal 比较作为最终 deterministic tie-break，并记录 warning。

第一版不做 parent-chain ancestry 校验，因为 B 不打开 raw journal；D/E 在使用 latest 前可以根据 A 或
`SessionJournalEngine` 做更强校验。

### 6.3 读写 API

推荐最小 API：

```csharp
public sealed class DerivedRecapStore {
    public ValueTask<DerivedRecapArtifact> WriteProducedAsync(
        DerivedRecapWriteRequest request,
        CancellationToken ct = default);

    public ValueTask<DerivedRecapArtifact?> TryReadLatestAsync(
        DerivedRecapLineageKey lineageKey,
        CancellationToken ct = default);

    public ValueTask<DerivedRecapArtifact?> TryReadArtifactAsync(
        string artifactId,
        CancellationToken ct = default);

    public ValueTask<DerivedRecapLatestIndex> RebuildLatestIndexAsync(
        CancellationToken ct = default);
}
```

`DerivedRecapWriteRequest` 应接受 `MemoryPack memoryPack` 与 `MemoryPackBlockPath target`，由 store 负责生成
snapshot JSON、校验 target block 存在并计算 `content.sha256`。这让 D 分片的调用代码保持自然：runner 只提交
维护后的权威 `MemoryPack`，不需要理解 store 的 wire DTO 细节。

`TryReadLatestAsync` 流程：

1. 尝试读取 `indexes/latest-by-profile.json`。
2. index 缺失、损坏、schema 不匹配或指向 artifact 不可用时，调用 `RebuildLatestIndexAsync`。
3. rebuild 后仍无对应 lineage，则返回 `null`。

## 7. 原子写入与损坏处理

写 artifact：

1. `Directory.CreateDirectory(artifacts)` 与 `Directory.CreateDirectory(indexes)`。
2. 写入 `artifacts/<artifactId>.json.<guid>.tmp`，使用 UTF-8 no BOM。
3. flush 后 `File.Move(tmp, final, overwrite: false)`。
4. final 已存在且内容一致时删除 tmp 并返回成功。
5. final 已存在且内容不同则换冲突后缀重试。
6. artifact 写成功后再重写 latest index。

写 index：

1. 从 artifacts 扫描构造完整 index，不在旧 index 上局部 patch。
2. 写 `latest-by-profile.json.<guid>.tmp`。
3. `File.Move(tmp, latest-by-profile.json, overwrite: true)`。

损坏策略：

- artifact JSON 损坏：跳过并通过 `DebugUtil.Warning("DerivedRecap", ...)` 记录；不得阻塞其他 artifact。
- index JSON 损坏：删除或覆盖重建；index 不是 correctness source。
- tmp 文件：open/rebuild 时可删除超过 24 小时的 tmp；第一版也可以只忽略。
- `derived/recaps/v1/` 整体删除：下一次 D 分片 replay 可重新创建，不影响 raw `Project()`。

## 8. 与 B 以外分片的边界

- A 提供 addressed replay message 和 `EventAddress` source range；B 不设计 cursor。
- C/D 决定何时触发 maintainer、如何构造 `RecentHistorySlice`、成功后调用 B。
- C/D 不应把旧 `Atelia.ChatSession.MemoryPack` 作为正式输入传给 B；legacy replay 边界若仍产出旧类型，应先转换为
  `Atelia.SessionJournal.MemoryPack`。
- D 负责传入 `sourceRawHead`、`sourceStartExclusive`、`sourceEndInclusive`、`anchorRawEvent`、
  governing setup addresses、`previousArtifact`、`invocation`、`callLogPaths`。
- E 做端到端命令和“raw chain 未变化”验收。
- B 不写 EventJournal event，不创建 branch，不做 `ArtifactSetCommitted`，不继续迁移旧 `ChatSession` 主链。

## 9. 最小测试集

推荐放入现有 SessionJournal 测试：

- `tests/SessionJournal.Tests`

B 的 store 测试不应依赖 `prototypes/ChatSession`；需要 maintainer 结果时，用
`Atelia.SessionJournal.MemoryPack` 直接构造。

测试用例：

1. `WriteProduced_CreatesArtifactAndLatestIndex`
   - 空临时 repo path 下写一个 artifact。
   - 断言 artifact JSON 与 index 文件存在。
   - reopen 后 `TryReadLatestAsync` 返回同一个 artifact。

2. `RebuildLatestIndex_AfterIndexDeleted`
   - 写两个同 lineage artifact，第二个 `previousArtifact` 指向第一个。
   - 删除 `indexes/`。
   - `TryReadLatestAsync` 触发 rebuild，并返回第二个。

3. `CorruptIndex_DoesNotLoseArtifacts`
   - 写 artifact 后把 latest index 改成非法 JSON。
   - 读取 latest 应 rebuild 成功。

4. `CorruptArtifact_IsSkippedDuringRebuild`
   - 放入一个非法 artifact JSON 和一个合法 artifact。
   - rebuild 后只索引合法 artifact。

5. `ContentMustMatchTargetBlock`
   - 构造 `content.text` 与 `memoryPack` target block 不一致的 artifact。
   - rebuild 跳过该 artifact。

6. `DerivedStoreDoesNotModifyRawJournal`
   - 创建最小 SessionJournal repo，记录 main head 和 `Project().Context`。
   - 写 derived artifact。
   - 再次读取 head/context，断言不变。

7. `EventAddressTextCodec_Roundtrip`
   - 覆盖正常 address、null JSON 字段、非法长度、非 hex、zero ticket/segment。

## 10. 残余风险

- latest rebuild 第一版不验证 `sourceEndInclusive` 是否属于当前 main parent chain；这会在多 branch / rewind
  场景下误选旁支 artifact。CS-5-lite 当前只承诺 main branch 离线 replay，后续 ArtifactSet 或 planner 必须补 ancestry
  校验。
- deterministic id 依赖 canonical JSON 序列化稳定性。实现时应固定 `JsonSerializerOptions`，不要让属性顺序随 DTO
  或 dictionary 枚举漂移。
- 内联 `memoryPack` 会让 artifact 文件随 summary 变大。第一版 rolling summary 可接受；当内容超过人工可读阈值或
  多 artifact 共享大正文时，再启用 `blobs/<sha256>.txt`。
- `producerFingerprint` 的具体算法留给 D。B 只保存字符串并参与 hash，不解释它的语义。
- 第一版会短期并存两套 memory 类型：`SessionJournal` 内的新权威 substrate，以及旧 `ChatSession` 内的 legacy
  substrate。B/C/D 应只使用前者；旧类型只允许停留在 legacy 边界。
