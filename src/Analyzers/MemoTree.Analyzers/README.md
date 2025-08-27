# MemoTree.Analyzers

Custom Roslyn analyzers & code fixes for MemoTree's LLM-centric development style.

## Implemented Rules

| ID | Title | Category | AutoFix | Notes |
|----|-------|----------|---------|-------|
| MT0001 | Multiple statements on one line | Formatting | Yes | One physical line = max one simple statement. |
| MT0002 | Initializer indentation normalization | Formatting | Yes | Ensures each element line indented exactly one level (+4 spaces). |

## Roadmap (Draft)
- MT0003 Multiple variable declarators -> split lines.
- MT0004 Enforce single indent inside switch case blocks (with braces).
- MT0005 Normalize trailing commas in multi-line constructs.
- MT0006 Expand single-line blocks when containing nested statements.

## Usage
Project references (or analyzer package once packed) will activate rules automatically. Run `dotnet build` to see diagnostics or apply code fixes in IDE / via `dotnet format` (future custom tooling).

### Self-Analyzing the Analyzer Project (Opt-in)
Normally the analyzer project does NOT load itself (avoids circular references & keeps builds fast). When you want to dogfood your own rules against the analyzer source, run a two‑phase build:

1. Regular build (produces fresh DLL):
	```
	dotnet build src/Analyzers/MemoTree.Analyzers/MemoTree.Analyzers.csproj
	```
2. Self-analysis build (re-loads the just-built DLLs as analyzers):
	```
	dotnet build src/Analyzers/MemoTree.Analyzers/MemoTree.Analyzers.csproj -p:UseSelfAnalyzers=true
	```

What happens in step 2:
* `Directory.Build.targets` detects `UseSelfAnalyzers=true` AND project name == `MemoTree.Analyzers`.
* It injects these assemblies (if present) as analyzer inputs:
  - `MemoTree.Analyzers.dll`
  - `MemoTree.Analyzers.CodeFixes.dll`
* The compiler then re-runs with your rules applied to their own source.

Why not always on?
* Prevents build graph cycles (Analyzer -> CodeFix -> Analyzer) and accidental stale DLL loading.
* Keeps normal solution builds lean and deterministic.

Troubleshooting:
* If no diagnostics appear, ensure the first (regular) build succeeded and produced the DLLs in `bin/Debug/netstandard2.0/`.
* Delete the `bin` / `obj` folders and repeat both steps if you suspect stale output.

### Applying CodeFixes via CLI
Use `dotnet format analyzers` to batch apply available code fixes (MT0001 / MT0002):
```
dotnet format analyzers --severity info --diagnostics MT0001,MT0002
```
To include self-analysis at the same time for the analyzer project itself, combine:
```
dotnet build src/Analyzers/MemoTree.Analyzers/MemoTree.Analyzers.csproj -p:UseSelfAnalyzers=true
dotnet format analyzers --severity info --diagnostics MT0001,MT0002
```
Add `--verify-no-changes` in CI to fail if formatting is required.

### Quick Reference
| Task | Command |
|------|---------|
| Build (normal) | `dotnet build src/Analyzers/MemoTree.Analyzers/MemoTree.Analyzers.csproj` |
| Self-analyze | `dotnet build src/Analyzers/MemoTree.Analyzers/MemoTree.Analyzers.csproj -p:UseSelfAnalyzers=true` |
| Apply fixes all projects | `dotnet format analyzers --severity info` |
| Apply only MT0001 & MT0002 | `dotnet format analyzers --diagnostics MT0001,MT0002 --severity info` |
| CI verify clean | `dotnet format analyzers --verify-no-changes --severity info` |

### Internal Implementation Note
Self-analysis intentionally uses already-built DLLs instead of a project reference to avoid RS1038-style workspace coupling issues inside the core analyzer assembly.
