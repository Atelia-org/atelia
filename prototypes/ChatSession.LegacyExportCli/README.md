# ChatSession.LegacyExportCli

`ChatSession.LegacyExportCli` 是旧 `ChatSession` 的只读归档/迁移出口。它只依赖
`ChatSession`，不会引用新的 `SessionJournal` 或 concrete MemoryMaintainer。

## export-json

把旧 repo 导出为版本化升级 JSON：

```bash
dotnet run --project prototypes/ChatSession.LegacyExportCli -- export-json \
  --input prototypes/FamilyChat.Server/.atelia/family-chat/sessions/<session> \
  --output gitignore/migrations/<session>.json
```

默认 schema 为 `atelia.chat-session.legacy-upgrade-export.v1`，默认 branch 为
`main`；`--compact` 关闭缩进。该 JSON 是
[`SessionJournal.Cli import-legacy-json`](../SessionJournal.Cli/README.md)
接受的交换格式。

## export-markdown

把旧 repo 导出为 fenced Markdown transcript：

```bash
dotnet run --project prototypes/ChatSession.LegacyExportCli -- \
  export-markdown \
  --input prototypes/FamilyChat.Server/.atelia/family-chat/sessions/<session> \
  --output gitignore/migrations/<session>.md
```

`--exclude-warnings` 可省略 exporter warnings。

两个命令都不修改输入 repo。输出必须位于输入 repo 外，输入/输出路径链不能包含
symlink/reparse point，并通过同目录临时文件、flush-to-disk 和 atomic move 发布。
