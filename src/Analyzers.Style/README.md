# Atelia.Analyzers.Style

Custom Roslyn analyzers & code fixes for MemoTree's LLM-centric development style.

## Implemented Rules

Canonical naming convention: see `docs/AnalyzerRules/NamingConvention.md`.

| ID | CanonicalName | Alias | Category | AutoFix | Notes |
|----|---------------|-------|----------|---------|-------|
| MT0001 | StatementSinglePerLine | SingleStatementPerLine | Formatting | Yes | One physical line = max one simple statement. |
| MT0002 | IndentInitializerElements | IndentInitializers | Formatting | Yes | Each initializer element line exactly one indent (+4) from `{` line. |
| MT0003 | IndentMultilineParameterList | IndentMultilineParams | Formatting | Yes | Declaration parameter + invocation argument lists (temporary): each parameter line (excluding '(' line) indented one level; comment-start lines ignored. |

## Roadmap (Draft)
- MT0003 Multiple variable declarators -> split lines.
- MT0004 Enforce single indent inside switch case blocks (with braces).
- MT0005 Normalize trailing commas in multi-line constructs.
- MT0006 Expand single-line blocks when containing nested statements.

### Rule Decision Records
Design rationales for proposed / implemented rules live under `docs/AnalyzerRules/`.
Current:
- `MT0004_NewLineClosingParenMultilineParameterList.md` – closing parenthesis on its own line (proposed).
- `NamingConvention.md` – rule naming system.

## Usage
Project references (or analyzer package once packed) will activate rules automatically. Run `dotnet build` to see diagnostics or apply code fixes in IDE / via `dotnet format` (future custom tooling).

### Self-Analyzing the Analyzer Project (Opt-in)
Normally the analyzer project does NOT load itself (avoids circular references & keeps builds fast). When you want to dogfood your own rules against the analyzer source, run a two‑phase build:

1. Regular build (produces fresh DLL):
	```
	dotnet build src/Analyzers.Style/Analyzers.Style.csproj
	```
2. Self-analysis build (re-loads the just-built DLLs as analyzers):
	```
	dotnet build src/Analyzers.Style/Analyzers.Style.csproj -p:UseSelfAnalyzers=true
	```

What happens in step 2:
* `Directory.Build.targets` detects `UseSelfAnalyzers=true` AND project name == `Atelia.Analyzers.Style`.
* It injects these assemblies (if present) as analyzer inputs:
  - `Atelia.Analyzers.Style.dll`
  - `Atelia.Analyzers.Style.CodeFixes.dll`
* The compiler then re-runs with your rules applied to their own source.

Why not always on?
* Prevents build graph cycles (Analyzer -> CodeFix -> Analyzer) and accidental stale DLL loading.
* Keeps normal solution builds lean and deterministic.

Troubleshooting:
* If no diagnostics appear, ensure the first (regular) build succeeded and produced the DLLs in `bin/Debug/netstandard2.0/`.
* Delete the `bin` / `obj` folders and repeat both steps if you suspect stale output.

### Applying CodeFixes via CLI
Use `dotnet format analyzers` to batch apply available code fixes (MT0001 / MT0002 / MT0003):
```
dotnet format analyzers --severity info --diagnostics MT0001,MT0002,MT0003
```
To include self-analysis at the same time for the analyzer project itself, combine:
```
dotnet build src/Analyzers.Style/Analyzers.Style.csproj -p:UseSelfAnalyzers=true
dotnet format analyzers --severity info --diagnostics MT0001,MT0002,MT0003
```
Add `--verify-no-changes` in CI to fail if formatting is required.

### Quick Reference
| Task | Command |
|------|---------|
| Build (normal) | `dotnet build src/Analyzers.Style/Analyzers.Style.csproj` |
| Self-analyze | `dotnet build src/Analyzers.Style/Analyzers.Style.csproj -p:UseSelfAnalyzers=true` |
| Apply fixes all projects | `dotnet format analyzers --severity info` |
| Apply only MT0001 & MT0002 & MT0003 | `dotnet format analyzers --diagnostics MT0001,MT0002,MT0003 --severity info` |
| CI verify clean | `dotnet format analyzers --verify-no-changes --severity info` |

### Internal Implementation Note
Self-analysis intentionally uses already-built DLLs instead of a project reference to avoid RS1038-style workspace coupling issues inside the core analyzer assembly.
