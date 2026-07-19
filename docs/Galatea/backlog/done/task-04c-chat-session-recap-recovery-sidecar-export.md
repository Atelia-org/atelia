# Task 04c: ChatSession Recap Recovery JSON Sidecar Export

> 状态：Done
> 建议执行者：适合接续 ChatSession recovery/report 的实现会话
> 依赖：Task 04b 的结构化 recovery report；可使用 `prototypes/Galatea/.atelia/galatea/sessions/cyber-copy` 做安全实验输入

## 背景

Task 04b 已返回结构化 finding/report，并且 timeline signature 已采用 `{kind}:{stableHash}:{preview}`。Task 04c 将 recovery report 持久化为 sidecar，供人工审阅、Markdown 导出器、后续 legacy recovery 或升级工具使用。

本任务只定义并实现一种 canonical sidecar：JSON。Markdown 不作为 sidecar 格式；若需要人类阅读，可以由 CLI / 调试命令从同一份 JSON 临时渲染 Markdown 或 text view。

理由：

- 自动升级工具需要稳定 schema、可验证字段和可版本化契约，JSON 更合适。
- 04b 的 signature 已经把人类可读 preview 放入结构化字段，JSON 可直接审阅，不必再维护一份 Markdown truth。
- JSON + Markdown 双输出容易产生一致性问题；Markdown 更适合作为 derived view，而不是持久 sidecar。
- 后续如果要把 inferred range 喂给导出器，JSON 可以直接作为输入；Markdown 只能给人看。

## 目标

新增 JSON sidecar exporter：

- 机器可读，包含 recovery metadata、timeline、findings、warnings。
- timeline entry 包含 `attribution`，用于把每次提交归因为 `initial-state`、`model-turn`、`compaction`、`revert-turn`、`update-system-prompt`、`redundant-save` 或 `other`。
- 每个 finding 包含 old/new head、range、confidence、reason。
- 明确区分 `anchored`、`inferred`、`unresolved`；04c 主要写出 `inferred` / `unresolved`，`anchored` 保留给已有 `RecapSourceAnchor` 或后续合并视图。
- 不修改原 ChatSession repo。
- 输出应适合对 `cyber-copy` 这类复制出来的实验 repo 反复生成、diff 和人工检查。

建议 API：

```csharp
public sealed record ChatSessionRecoverySidecarExportOptions(
	bool WriteIndented = true,
	DateTimeOffset? GeneratedAtUtc = null
);

public static class ChatSessionRecoverySidecarExporter {
	public static string ExportJson(
		ChatSessionLegacyRecapRecoveryReport report,
		string branchName = "main",
		ChatSessionRecoverySidecarExportOptions? options = null
	);

	public static void WriteJsonFile(
		ChatSessionLegacyRecapRecoveryReport report,
		string outputPath,
		string branchName = "main",
		ChatSessionRecoverySidecarExportOptions? options = null
	);
}
```

建议默认文件名：

```text
chat-session-recap-recovery.sidecar.json
```

若后续做 CLI，可以允许：

```bash
dotnet run --project prototypes/ChatSession ... recover-recap-sidecar \
	--repo prototypes/Galatea/.atelia/galatea/sessions/cyber-copy \
	--branch main \
	--out prototypes/Galatea/.atelia/galatea/sessions/cyber-copy/chat-session-recap-recovery.sidecar.json
```

CLI 不是本任务必需项；本任务优先完成 library exporter 与测试。

## JSON Schema 草案

顶层对象：

```json
{
  "schema": "atelia.chat-session.recap-recovery-sidecar.v1",
  "branchName": "main",
  "timeline": [],
  "findings": [],
  "warnings": []
}
```

字段约定：

| 字段 | 类型 | 含义 |
|---|---|---|
| `schema` | string | 固定 schema id，用于后续兼容演进 |
| `branchName` | string | 被分析的 ChatSession branch |
| `timeline` | array | Task 04b timeline，按 old → new |
| `findings` | array | recovery findings |
| `warnings` | array | recovery warnings |

可选 metadata：

| 字段 | 类型 | 含义 |
|---|---|---|
| `generatedAtUtc` | string | ISO-8601 UTC 生成时间；默认不输出，只有显式设置 options 时输出，避免同一输入反复导出产生无意义 diff |

timeline entry：

```json
{
  "ordinal": 3,
  "commit": "...",
  "source": "effective-parent",
  "messageCount": 4,
  "messageCountDeltaFromPrevious": -1,
  "attribution": {
    "kind": "compaction",
    "reason": "new snapshot is shorter, contains leading recap, and preserves a matching suffix"
  },
  "oldest3": ["recap:...:summary"],
  "newest3": ["observation:...:second", "action:...:keep"]
}
```

finding entry：

```json
{
  "kind": "inferred",
  "oldHead": "...",
  "newHead": "...",
  "recapIndex": 0,
  "sourceRange": {
    "startIndex": 0,
    "endExclusive": 2,
    "messageCountBefore": 4
  },
  "suffixMatchCount": 2,
  "confidence": "high",
  "reason": "new snapshot is shorter, contains leading recap, and preserves a matching suffix"
}
```

`kind` 规则：

| `kind` | 使用场景 |
|---|---|
| `inferred` | 有 high / medium / low confidence 的推断 range |
| `unresolved` | 观察到可疑变化但不能建立可靠 range |
| `anchored` | 已有真实 `RecapSourceAnchor`；04c 可先保留枚举值，不必主动生成 |

`confidence` 输出使用小写字符串：`high` / `medium` / `low` / `unresolved`。

## 输出稳定性要求

- 属性名使用 camelCase。
- enum 值使用小写字符串，不输出 C# enum 原名。
- `BranchHistoryAddressSource` 输出为 kebab-case：`effective-head` / `effective-parent`。
- `ChatSessionCommitAttributionKind` 输出为 kebab-case：`initial-state` / `model-turn` / `compaction` / `revert-turn` / `update-system-prompt` / `redundant-save` / `other`。
- timeline 和 findings 保持 Task 04b report 的顺序，不按 hash 或 head 重排。
- 默认 indented JSON，便于人工 diff。
- JSON 使用 relaxed encoder，不转义非 ASCII 字符；中文 preview 应保持可读。
- 默认不输出生成时间；需要审计时间时显式设置 `GeneratedAtUtc`。
- 不输出绝对路径，避免 sidecar 在不同机器上产生无意义 diff。
- 不把完整 message 正文写入 sidecar；正文只通过 signature preview 暴露，避免把旧会话内容复制成第二份完整历史。
- 不修改原 ChatSession repo。

## 非目标

- 不把推断结果写回 durable record。
- 不实现 repo 原地升级。
- 不实现 recap 展开导出全流程；只提供 sidecar 给后续工具消费。
- 不把 Markdown 作为 canonical sidecar 格式。
- 不定义长期公开 API；当前仍是项目内离线恢复工具，可随后续实测调整 schema。

## 验收

- 能从 Task 04b report 生成 JSON sidecar。
- sidecar 顶层包含 `schema`、`timeline`、`findings`、`warnings`。
- timeline 保留 `oldest3` / `newest3` 的 `{kind}:{stableHash}:{preview}` signature。
- timeline 输出每次提交的 `attribution.kind` 与 `attribution.reason`。
- finding 输出 `kind`，并能区分 `inferred` 与 `unresolved`。
- unresolved finding 在输出中明确标注，不被误称为已恢复。
- 输出包含 recovery warnings。
- 输出不包含输入 repo 的绝对路径。
- 有测试覆盖：空 finding、high-confidence inferred finding、unresolved finding、warnings、enum 小写序列化。

## 实施结果

- 已新增 `ChatSessionRecoverySidecarExporter.ExportJson(...)` 与 `WriteJsonFile(...)`。
- sidecar schema 固定为 `atelia.chat-session.recap-recovery-sidecar.v1`。
- 默认输出 indented JSON，默认不输出 `generatedAtUtc`，确保同一输入反复导出时保持稳定 diff。
- 默认不转义非 ASCII 字符，保证中文 preview 在 sidecar 中可直接阅读。
- enum 输出为小写字符串；`BranchHistoryAddressSource` 输出为 `effective-head` / `effective-parent`。
- timeline entry 已包含 commit attribution；当前保守识别 `model-turn`、`compaction`、`revert-turn`、`update-system-prompt`、`redundant-save`，其他复杂变化标为 `other`。
- `generatedAtUtc` 仅在 `ChatSessionRecoverySidecarExportOptions.GeneratedAtUtc` 显式设置时输出。
- unresolved finding 会输出 `kind: "unresolved"`、`confidence: "unresolved"`，且保留 `recapIndex: null`。

已验证：

```bash
dotnet test tests/FamilyChat.Server.Tests/FamilyChat.Server.Tests.csproj --filter "FullyQualifiedName~ChatSessionRecoverySidecarExporter|FullyQualifiedName~ChatSessionLegacyRecapRecovery"
```

在 `prototypes/Galatea/.atelia/galatea/sessions/cyber-copy` 上实际导出：

```text
timeline=77
findings=2
warnings=0
attribution=initial-state:1, model-turn:71, compaction:2, update-system-prompt:3
output=prototypes/Galatea/.atelia/galatea/sessions/cyber-copy/chat-session-recap-recovery.sidecar.json
```

已确认 sidecar 不再把中文 preview 序列化为 `\uXXXX` 形式；原先 3 个 `other` 的完整 message signature 序列与前一 commit 相同，且 root `systemPrompt` 发生变化，现归因为 `update-system-prompt`。

两个 finding 均为 `kind=inferred` / `confidence=high`：

| # | oldHead | newHead | recapIndex | sourceRange | suffixMatchCount |
|---|---|---|---:|---|---:|
| 0 | `seg:1:000000000838cb8b` | `seg:1:000000000841759e` | 0 | `[0, 38)` / before 66 | 28 |
| 1 | `seg:1:0000000008d8eb2b` | `seg:1:0000000008e1dd6e` | 0 | `[0, 29)` / before 57 | 28 |

## 后续目标回顾

- 下一步可以把 sidecar 接入 ChatSession Markdown export：遇到无 anchor legacy recap 时，优先查 sidecar 的 inferred range 并在导出中标注 best-effort。
- 更根本的后续方向：在 `prototypes/ChatSession` 的每次 `Commit()` 路径中显式写入 commit purpose / commit type metadata，例如 `model-turn`、`compaction`、`revert-turn`、`update-system-prompt`。当前 attribution 是 legacy 数据的推断层，以后新数据不应再靠猜。
- 如果要做真正 upgrade/export 工具，建议仍保持只读输入 repo，输出 upgraded markdown / sidecar / report，不原地改写 durable records。
- 如果后续发现 raw reflog 中存在 effective parent-chain 看不到的 reroll/revert，可再补 StateJournal transition 诊断 API。
