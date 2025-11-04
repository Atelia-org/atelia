# 合并Atelia.Completion.Abstractions.ObservationMessage.Notifications/Windows（已完成）

- [x] 2025-11-04：在 `ObservationMessage` 中引入统一的 `string? Contents` 字段，并在 `ObservationEntry.GetMessage` 内拼接通知和窗口内容。
- [x] 同步更新 `ToolResultsMessage`、`HistoryEntry` 以及 Anthropic 转换器以适配新字段。

这是从业务层分离出来时残留的业务层逻辑，在调用补全服务时现已去语义化，合并为单一的可空 `string? Contents`。
