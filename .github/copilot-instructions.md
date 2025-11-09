dotnet build Atelia.sln
dotnet test
dotnet test --filter FullyQualifiedName~TestName
# Atelia – AI Agent Quickstart

**Project Stage**: Early experimental project, pre-release phase. No downstream users, so interface changes are acceptable.

**Development Model**: AI-first development where AI agents are the primary workforce for requirements analysis, design iteration, coding, and review. Human developers serve as "team leads" focusing on critical requirements, design decisions, development direction, and pacing based on deep external research and domain understanding.

## Architecture snapshot
- `src/`: production libs. `Directory.Build.props` forces assemblies/namespaces/package IDs to `Atelia.{ProjectName}` and fails builds when the prefix drifts.
- `prototypes/`: active agent runtimes. **Agent** and **Completion** (core focus); `LiveContextProto` proves the 3-stage pipeline (AgentState → Context Projection → Provider Router).
- `tests/`: mirror `{ProjectName}.Tests`; run after touching shared code.
- `docs/`: design notes per component (e.g., `docs/Atelia.Data-LLM-Guide.md`, `docs/AnalyzerRules/`).

### Naming Standards
- **Directories**: `src/{ProjectName}/` (no `Atelia.` prefix in folder names)
- **Namespaces**: `Atelia.{ProjectName}[.FeatureArea]`
- **Files**: C# files match type names; test files end with `Tests.cs`
- **Analyzer Rules**: Follow canonical naming in `docs/AnalyzerRules/NamingConvention.md`
- See `docs/Atelia_Naming_Convention.md` for full details

## Component cues
- **LiveContextProto** (`prototypes/LiveContextProto/`): streaming Anthropic client via `AnthropicProviderClient`, tool execution through `ToolExecutor` + `MethodToolWrapper`. Run with `dotnet run --project prototypes/LiveContextProto/LiveContextProto.csproj` once `ANTHROPIC_API_KEY` is set. See `prototypes/LiveContextProto/README.md`.
- **Agent / Agent.Core**: orchestration and sub-agent experiments; cross-check `docs/Agent_Environment_Requirements.md` and `docs/Agent_Memory_Editing_Architecture.md` when extending behaviors.
- **Completion / Completion.Abstractions**: shared contracts for completion pipelines consumed by Agent runtimes.
- **Data**: binary framing helpers built around `ChunkedReservableWriter`; follow the reserve → write → commit dance in `docs/Atelia.Data-LLM-Guide.md`.
- **Diagnostics**: emit telemetry through `DebugUtil.Print`; logs land in `.atelia/debug-logs/{category}.log`. See `src/Diagnostics/README.md`.

## Code formatting
- **编写代码时无需关注格式细节** - pre-commit hook会自动运行`format.ps1`规范化代码
- 项目使用自定义Roslyn analyzers (MT0001–MT0101)自动修复格式问题
- 目的：让AI会话专注功能实现，格式问题自动化处理

## Daily commands (PowerShell)
- Build: `dotnet build Atelia.sln`
- Test: `dotnet test` (avoid `--no-build` unless only tests changed)
- Format (optional): `pwsh ./format.ps1` - 提交时会自动运行，通常无需手动执行

## Debug flows
- Toggle logging categories via `$env:ATELIA_DEBUG_CATEGORIES="History,Provider,Tools"`; `ALL` enables everything.
- LiveContextProto console shortcuts: `/history`, `/reset`, `/notebook`, `/exit` for quick inspection/reset loops.

## Tool registration
- Decorate methods with `[Tool("tool.name", "desc")]`, annotate inputs using `[ToolParam(...)]`, keep a trailing `CancellationToken`, then register through `MethodToolWrapper.FromMethod(...)`.
- Example:
```csharp
[Tool("tool.name", "Description")]
public async ValueTask<LodToolExecuteResult> MyTool(
    [ToolParam("param description")] string param,
    CancellationToken cancellationToken
)
{
    // Implementation
}

// Register via MethodToolWrapper.FromMethod(instance, methodInfo)
```

## Working style
- Tests and prototypes expect Anthropic credentials; document env vars when scripting runs.
- Keep architecture decisions paired with code updates—`docs/` already houses the canonical references, so update them when behavior shifts.
- VS Code terminal may occasionally become unresponsive (no echo); close all terminals and create new instance, test with `Write-Output "hello"`.

## Common Patterns

### Testing Anti-Patterns
❌ **Avoid**: `dotnet test --no-build` after changing core logic
✅ **Use**: `dotnet test` (builds first) or explicit `dotnet build` + `dotnet test --no-build` only for test-only changes

### Reserved Writer Pattern (Atelia.Data)
```csharp
using var writer = new ChunkedReservableWriter(innerWriter);

// Reserve space for length field
var lengthSpan = writer.ReserveSpan(4, out int token, "length");

// Write data
WriteData(writer);
var dataLength = writer.WrittenLength;

// Commit reservation
BitConverter.GetBytes(dataLength).CopyTo(lengthSpan);
writer.Commit(token); // Must commit in order created
```

---

Questions or gaps? Ping the team lead or leave a TODO comment so we can sharpen these guidelines.
