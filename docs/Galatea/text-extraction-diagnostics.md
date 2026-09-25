# TextExtractor 阶段诊断（开发期）

## 目的与边界

邮件和 Character Note 都通过 `TextExtractor` 从同一份可见 Action 提取结构化候选。一次 `action_capture.artifact_count=1` 只能说明最终持久捕获一条，不能单凭该字段区分模型少返回、工具调用拒绝、业务校验失败和捕获阶段失败。本诊断以一次提取尝试为单位，把这些阶段串起来。

诊断使用 `DebugUtil.Debug("Galatea.TextExtraction", json)`，仅在 DEBUG 构建中编译，文件位于工作目录 `.atelia/debug-logs/galatea_textextraction.log`。它不修改 SessionJournal、邮件/Note 持久格式、提取提示词、工具调用或重试策略。诊断写入失败不会改变业务结果。它不是审计记录，也不是恢复或补发依据。

## 关联键与事件

每行都是 `atelia.galatea.text-extraction-diagnostic.v1` JSON，顶层包含 `feature`（`outbound-mail` / `character-note`）、`attemptId`、`characterId`、`sourceAction`、`extractorContractId`、可见 Action SHA-256/UTF-8 字节数，以及阶段特有的 `details`。`attemptId` 是每次尝试新生成的诊断键；不能代替 durable capture、dispatch ID 或 Note ID。同一来源 Action 的不同重试有不同 `attemptId`。

| 事件 | 关键 `details` | 含义 |
|---|---|---|
| `text-extraction-started` | `modelId`, `connectionId`, `toolNames` | 准备调用提取模型。 |
| `text-extraction-completion-observed` | `completionOrdinal`, `termination`, `errorCount`, `rawToolCallCount`, 文本/推理块数 | Completion 返回后的原始块计数，先于调用执行；失败响应也尽量记录。当前单次调用的序号为 0。 |
| `text-extraction-completion` | 同上，另有诊断文本字节数和预览 | 完成 Action block 解析；普通文本仍不算 artifact。 |
| `text-extraction-candidate` | `completionOrdinal`, `candidateOrdinal`, tool 名称/ID、参数字节数/SHA-256 | 返回中的每个工具调用，尚未证明参数合法。未来 Tool-Loop 可用 `(completionOrdinal, candidateOrdinal)` 标识。 |
| `text-extraction-preflight` | `outcome`, `candidateCount` | 所有工具调用通过预检；拒绝时由 `text-extraction-finished` 给出固定失败类型和工具 ID。 |
| `text-extraction-tool-execution` | 候选序号、`outcome`, `reasonCode`, `status` | 工具参数解析/执行及 artifact 收集结果。 |
| `text-extraction-finished` | `outcome`, `stage`, `rawToolCallCount`/`artifactCount` 或 `reasonCode` | 底层提取成功、失败或取消。零调用成功与失败是不同状态。 |
| `text-extraction-business-candidate` | `artifactOrdinal`, `outcome`, `reasonCode` | 邮件或 Note 的逐项业务校验。 |
| `text-extraction-business-finished` | `rawArtifactCount`, `acceptedCount`, `outcome`, `rejectedOrdinal`, `reasonCode` | 业务校验后的批次结果。某项拒绝会抛错，已通过的前项不会作为部分批次返回。 |
| `text-extraction-capture` | `outcome`, `extractedCount`, `capturedCount`，邮件另有 `dispatchIds` / `captureSequence` | 提交、跳过、竞争或捕获失败。Note 的捕获与后续 MemoPod 应用仍是不同阶段。 |

空白 Action 跳过模型调用时产生 `text-extraction-skipped`，最终零捕获仍由持久层处理。对于已有 capture，reconciler 按现有幂等规则直接返回，不重新提取，也不生成新尝试。Mail 的既有 `outbound-mail-captured` 诊断继续在提交后逐封记录；Character Note 的既有 batch/Memo 诊断继续记录后续应用结果。

`reasonCode` 使用固定值：底层失败使用 `TextExtractionFailureKind` 名称，执行拒绝使用 `tool-execution-failed` / `artifact-capture-mismatch`，邮件校验使用 `mail-<field>-blank|too-long|line-break|invalid-utf8` 等，Note 校验使用 `note-text-blank|too-long|invalid-utf8`、`note-total-text-too-long`、`note-too-many-intents` 等。未知非致命异常只记录 `exception` 与异常类型；正文和异常消息不会写入失败事件。

## 内容与可靠性

成功工具调用只记录名称、ID、参数长度和哈希；被拒绝的工具调用可记录最多 4096 个字符的参数预览和工具结果预览。Completion 普通诊断文本也记录最多 4096 字符的预览。这些 DEBUG 预览**可能含有原文内容**，适合开发机本地诊断，不能当作 content-free 生产遥测。完整工具参数和原始响应没有持久化。Warning/Error 级别仍保持 content-free。

诊断事件通过 best-effort 写入；日志缺失不能解释为零候选。`text-extraction-finished` 失败或取消时，后续业务/捕获事件可能不存在。`text-extraction-capture` 的已捕获数量代表提交的 artifact 数；Note 是否最终成为可用 Memo 仍需看原有 Character Memory 记录。

## Tool-Loop 接入点

将来引入 Tool-Loop 时，同一 `attemptId` 覆盖整次提取；每轮 Completion 递增 `completionOrdinal`，轮内工具调用使用 `candidateOrdinal`，汇总后的业务校验与持久捕获使用全局 `artifactOrdinal`。每轮都应记录原始调用数、终止/错误状态和工具结果；最终完成、轮数上限、重复调用与拒绝分别使用固定原因码。日志只观察既有执行，不能驱动自动补发或绕过整批校验。

## 验证

`TextExtractorTests` 覆盖两次原始调用中第二次执行失败、零候选成功及诊断 sink 失败；`GalateaMailboxTests` 覆盖第二封在业务校验阶段被拒绝；`CharacterNoteExtractorTests` 覆盖第二条 Note 超限；`GalateaOutboundMailExtractionReconcilerTests` 覆盖提交数量与来源 Action 关联。DEBUG 测试检查事件内容，Release 构建确保诊断调用点不参与执行。
