# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**Atelia** (Autonomous Thinking, Eternal Learning, Introspective Agents) is an experimental .NET project for building autonomous AI agents with continuous self-directed operation and intrinsic goals. This is a personal research project in early rapid iteration with no downstream users, so interface changes are acceptable.

**Primary Language**: Simplified Chinese for communication and documentation (术语、标识符、专有名词等保持原始语言)

**Environment**: .NET 10.0 / C# 14

## Build & Development Commands

### Core Commands
```bash
# Build entire solution
dotnet build

# Run tests
dotnet test

# Format code (changed files only)
pwsh ./format.ps1 -Scope diff

# Format entire codebase
pwsh ./format.ps1 -Scope full

# Format staged files only
pwsh ./format.ps1 -Scope staged
```

### Running Individual Tests
```bash
# Run tests for a specific project
dotnet test tests/Data.Tests/Data.Tests.csproj

# Run a specific test
dotnet test --filter "FullyQualifiedName~TestMethodName"
```

## Architecture

### Core Libraries (`src/`)

- **Data**: High-performance data structures
  - `ChunkedReservableWriter`: Buffer writer with reserve-and-backfill support for serialization
  - Used for message framing, nested structures, length/CRC backfilling
  
- **StateJournal**: Memory and persistence system for agent state
  - Provides durable storage for agent memory and state transitions
  
- **Rbf** (Reversible Binary Framing): Binary serialization format
  - Implements `IRbfFrame` interface (ref struct can implement interfaces in C# 14)
  
- **DocGraph**: Documentation system with glossary and issue tracking
  - Supports distributed authoring with automatic key information aggregation
  
- **Diagnostics**: Debugging utilities
  - `DebugUtil.Trace/Info/Warning/Error`: Conditional debug output
  - Controlled by `ATELIA_DEBUG_CATEGORIES` environment variable
  - Logs to `.atelia/debug-logs/{category}.log` or `gitignore/debug-logs/{category}.log`
  
- **Primitives**: Core primitive types and utilities
  - Includes `AteliaResult` with `allows ref struct` constraint
  
- **Analyzers.Style**: Custom Roslyn analyzers for code style enforcement
  - Automatically applied to all projects via `Directory.Build.props`
  
- **DesignDsl**: Design specification DSL for formal documentation

### Prototypes (`prototypes/`)

Experimental implementations for rapid iteration:

- **Agent.Core**: Core agent framework implementation
- **LiveContextProto**: Live context management prototype
- **Completion**: LLM completion system
- **Completion.Abstractions**: Completion abstractions
- **PersistentAgentProto**: Persistent agent prototype

## Naming Conventions

All projects follow strict naming conventions defined in `docs/Atelia_Naming_Convention.md`:

- **Namespaces**: MUST start with `Atelia.` (e.g., `Atelia.Data`, `Atelia.StateJournal`)
- **Assembly Names**: `Atelia.{ProjectName}` (configured in `Directory.Build.props`)
- **Package IDs**: Same as assembly names
- **Directory Structure**: `src/{ProjectName}/` (no Atelia prefix in directory names)
- **Test Projects**: `tests/{ProjectName}.Tests/`

## C# 14 / .NET 10 Features

This project uses cutting-edge C# features that may not be in LLM training data:

### ref struct Implements Interfaces
```csharp
// ref struct can now implement interfaces without boxing
public ref struct RbfFrame : IRbfFrame { }
```

### allows ref struct Constraint
```csharp
// Generic constraint allowing ref struct type parameters
public readonly struct AteliaResult<T> where T : allows ref struct { }
```

### T? with notnull Constraint
**Important**: `T?` with `where T : notnull` is just a nullability annotation (NRT), NOT `Nullable<T>`:
```csharp
// T? here is just T with nullable annotation, no .HasValue/.Value
public void Method<T>(T? value) where T : notnull { }
```

## Code Style & Formatting

### Formatting
- Run `pwsh ./format.ps1 -Scope diff` before committing
- The script uses `dotnet format analyzers --severity info`
- Iterates up to 5 times per batch until no changes
- Custom analyzers from `Analyzers.Style` are automatically applied

### Line Endings
- **Default**: LF (`\n`) for all files
- **Exceptions**: CRLF (`\r\n`) for `.sln`, `.bat`, `.cmd`, `.ps1`
- Configured in `.gitattributes` and `.editorconfig`
- Never modify `core.autocrlf` settings

### Indentation
- **C# files**: 4 spaces
- **JSON/YAML**: 2 spaces
- **XML config files**: 2 spaces
- **Project files**: 4 spaces

## Documentation Standards

Follow `docs/spec-conventions.md` for all design documents:

### Clause IDs (Normative Clauses)
Use stable Clause-IDs for specification requirements:
- `[F-NAME]`: Framing/Format (wire format, alignment, field meanings)
- `[A-NAME]`: API (signatures, return values, parameter validation)
- `[S-NAME]`: Semantics (cross-API/format semantic invariants)
- `[R-NAME]`: Recovery (crash consistency, resync, corruption detection)

Format: `SCREAMING-KEBAB-CASE` expressing the stance/decision (e.g., `[F-CASE-INSENSITIVE]`)

### Normative Language
- `MUST` / `MUST NOT`: Absolute requirements
- `SHOULD` / `SHOULD NOT`: Recommendations (can deviate with justification)
- `MAY`: Optional
- `(MVP 固定)`: Equivalent to "MUST for v2.x"

### Information Representation
Prefer machine-readable formats over ASCII art:
- **Hierarchies**: Use nested lists, not box-drawing characters
- **2D relationships**: Use Markdown tables
- **State machines/flows**: Use Mermaid diagrams
- **Bit layouts**: Use range tables (row=field, columns=attributes)
- **Avoid**: ASCII art, box-drawing diagrams (mark as Informative if kept)

### Terminology Format
- **Clause IDs**: `SCREAMING-KEBAB-CASE` (e.g., `[S-DOC-NORMATIVE-LANGUAGE-RFC2119]`)
- **Concept Terms**: `Title-Kebab` (e.g., `Resolve-Tier`, `Shape-Tier`, `App-For-LLM`)
- **File Names**: `lower-kebab-case` (e.g., `why-tier.md`, `app-for-llm.md`)
- **Code Identifiers**: Follow language conventions (e.g., `WhyLayer`, `AppForLLM`)

### Artifact-Tiers Methodology
Five-tier framework for software development:
- **Resolve-Tier**: 值得做吗？(Is it worth doing?)
- **Shape-Tier**: 用户看到什么？(What does the user see?)
- **Rule-Tier**: 什么是合法的？(What is valid?)
- **Plan-Tier**: 走哪条路？(Which path to take?)
- **Craft-Tier**: 怎么造出来？(How to build it?)

## Development Philosophy

### Refactoring Over Compatibility
- **Prefer thorough refactoring** over compatibility layers
- No downstream users, so breaking changes are acceptable
- Remove obsolete code completely rather than deprecating
- Avoid backwards-compatibility hacks (unused parameter renames, re-exports, etc.)

### Debug Output
Use `DebugUtil` instead of `Console.WriteLine` or `Debug.WriteLine`:
```csharp
DebugUtil.Trace("category", "trace message");  // DEBUG only
DebugUtil.Info("category", "info message");    // DEBUG only
DebugUtil.Warning("category", "warning");      // Always logged
DebugUtil.Error("category", "error");          // Always logged
```

Control output via environment variables:
```bash
# Enable specific categories
export ATELIA_DEBUG_CATEGORIES="TypeHash,Test,Outline"

# Enable all categories
export ATELIA_DEBUG_CATEGORIES="ALL"

# Set minimum log levels
export ATELIA_DEBUG_FILE_LEVEL="Trace"
export ATELIA_DEBUG_CONSOLE_LEVEL="Info"
```

## Common Pitfalls

### Git Operations
- Always use `timeout: 0` for `run_in_terminal` with blocking commands
- Never use `--no-verify` or `--no-gpg-sign` unless explicitly requested
- Create NEW commits after hook failures, never amend

### Tool Usage (from AGENTS.md)
- **Avoid** `insert_edit_into_file` tool - use `apply_patch` or `replace_string_in_file` instead
- Use `timeout: 0` for all `run_in_terminal` calls (not 30000, 60000, etc.)
- For long-running processes, use `isBackground: true`

## Project Goals

From `AGENTS.md`, the high-level objectives:

1. Design and implement agents capable of long-term autonomous action
2. Establish Agent-Operating-System (能动体运转系统) theoretical framework
3. Implement custom LLM Agent framework (early code in `prototypes/Agent.Core`)
4. Design and implement DocUI (LLM-Agent-OS interaction interface)
5. Achieve "zero-surprise editing" for LLM agents with preview+confirmation
6. Implement StateJournal memory system
7. Implement RBF (Reversible Binary Framing) serialization
8. Maintain documentation in LLM-comprehensible form

## Additional Resources

- **AGENTS.md**: Team onboarding for AI agents (入门知识文件)
- **docs/spec-conventions.md**: Specification writing conventions
- **docs/Atelia_Naming_Convention.md**: Naming standards
- **docs/Line_Endings_Standard.md**: Line ending policies
- **docs/Atelia.Data-LLM-Guide.md**: Guide for using Atelia.Data components
