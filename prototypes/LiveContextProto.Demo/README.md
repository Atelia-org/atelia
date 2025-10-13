# LiveContextProto.Demo

演示控制台保留了脚本化 `StubProviderClient`、示例工具与 `/demo` 命令，便于本地演练与 E2E 场景构造。

## 运行

```powershell
$env:ATELIA_DEBUG_CATEGORIES="History,Provider,Tools"
dotnet run --project prototypes/LiveContextProto.Demo/LiveContextProto.Demo.csproj
```

## 功能概览

- Stub Provider：从 `Provider/StubScripts/*.json` 加载脚本增量，可通过 `/stub <script>` 切换。
- 示例工具：`SampleMemorySearchTool` 与 `SampleDiagnosticsTool`，可用 `/tool sample|fail` 触发。
- `/demo conversation`：快速构造示例对话与 Notebook 快照。
- 其余命令与主项目一致（`/history`、`/notebook`、`/liveinfo` 等）。

> 提示：若需要真实模型调用，请改用主项目 `prototypes/LiveContextProto` 并配置 `ANTHROPIC_API_KEY`。
