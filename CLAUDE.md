# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**Atelia** (Autonomous Thinking, Eternal Learning, Introspective Agents) is an experimental .NET project for building autonomous AI agents with continuous self-directed operation and intrinsic goals. This is a personal research project in early rapid iteration with no downstream users, so interface changes are acceptable.

**Primary Language**: Simplified Chinese for communication and documentation (术语、标识符、专有名词等保持原始语言)

**Environment**: .NET 10.0 / C# 14

## Build & Development Commands

Most storage libraries (Primitives, Data, Rbf, RbfSegmentStore, EventJournal) are consumed as pinned NuGet packages from nuget.org (see `eng/StorageDependency.props`). **The Completion packages (Diagnostics, Completion.Abstractions, Completion, Completion.Tools) are currently local-only dev packages not published to nuget.org** — default restore does not work for projects that use them. Restore with the local config:

```bash
# Restore projects that reference Completion packages
dotnet restore <proj>.csproj --configfile eng/NuGet.Completion.Local.config -p:UseCompletionSources=false -m:1 -nr:false

# Build/test after restore
dotnet build prototypes/Galatea/Galatea.Server.csproj --no-restore -c Release -p:UseCompletionSources=false -m:1 -nr:false
dotnet test tests/Galatea.Server.Tests/Galatea.Server.Tests.csproj --no-restore
```

Source-mode interop with the external repos (only for simultaneous upstream changes; re-restore to switch back):

```bash
# atelia-storage source mode (docs/storage-dependency.md)
dotnet build Atelia.sln -p:UseStorageSources=true -p:StorageSourceRoot=<abs-path-to-atelia-storage>

# atelia-completion source mode (docs/completion-dependency.md)
dotnet build Atelia.sln -p:UseCompletionSources=true -p:CompletionSourceRoot=<abs-path-to-atelia-completion>
```

### Running Tests

```bash
# Run tests for a specific project
dotnet test tests/SessionJournal.RecapGrid.Store.Tests/SessionJournal.RecapGrid.Store.Tests.csproj

# Run a specific test
dotnet test --filter "FullyQualifiedName~TestMethodName"
```

Test-project conventions in `tests/`: each assembly has a plain `.Tests` project plus a `PublicSurface.Tests` (public API surface contract) project; some have `CrashHarness` projects for crash-consistency testing. Galatea tests that hit a real provider are explicitly opt-in; provider-free tests are the default.

### Formatting

```bash
pwsh ./format.ps1 -Scope diff    # changed files only
pwsh ./format.ps1 -Scope full    # entire codebase
pwsh ./format.ps1 -Scope staged  # staged files only
```

The script merges `config/enforce.editorconfig` into a temp `.editorconfig`, runs `dotnet format analyzers --severity info`, and iterates up to 5 times per 60-file batch until stable. Run it before committing.

## Architecture

### Current Build Graph

The active build graph is much smaller than the repo's history suggests; old experiments live in `archive/`:

- **src/**: `Analyzers.Style` (+ `Analyzers.Style.CodeFixes`) — custom Roslyn analyzers auto-applied to all projects via `Directory.Build.props`; `TextEditScript` — reusable text-edit script model with strict XML parser (the "zero-surprise editing" primitive for DocUI)
- **prototypes/**: the real products — `Galatea` (the main application), the `SessionJournal` stack, and `MemoPod`
- **External packages**: storage libs in [atelia-storage](https://github.com/Atelia-org/atelia-storage) (`eng/StorageDependency.props` is the single source of pinned versions/commits), completion libs in [atelia-completion](https://github.com/Atelia-org/atelia-completion) (`eng/CompletionDependency.props`)
- **StateJournal is retired**: archived to its own repo, not in the active build graph. Do not add cross-repo references or make its internal APIs public (see `docs/statejournal-retirement.md`).

### Galatea — the Main Application

`prototypes/Galatea/Galatea.Server.csproj` is a Player/Character-separated role-play agent host built on SessionJournal. You interact via web UI, or let characters run autonomously on the server while you observe. Key subsystems (all in `prototypes/Galatea/`):

- `GalateaAutomaticTurnCoordinator` + `GalateaServerAgentHostedService`: per-character autonomous turns. `autonomyIntervalMinutes` (V11 config, per character; 0 = only recovery, no idle activation) controls `HeartbeatActivation`; pending generations always recover first. Every character is checked every 10s.
- `CharacterMemory`: per-character memory; notes and mail (inter-character `GalateaCharacterMail*`) are delivered on autonomous turns
- `GalateaDelegation*` + `GalateaDurableDelegateTransport`: durable Codex delegation with a SQLite store, leases, snapshots, and explicit operator recovery
- `GalateaCodexDurableSidecarClient` / `GalateaSidecarProcess`: Node/Codex sidecar process management
- `GalateaRecapGridComposition`: the single `RecapGridCompletionHost` that wires the RecapGrid stack
- `GalateaCompletionRetryClient`: transient retry for pure completions, with business-turn persistence boundaries

**Run it** (from repo root, requires local Completion dev feed):

```bash
dotnet restore prototypes/Galatea/Galatea.Server.csproj --configfile eng/NuGet.Completion.Local.config
dotnet run --no-restore -c Release --project prototypes/Galatea/Galatea.Server.csproj
```

Config lives at `.atelia/galatea/config.json` under ContentRoot (override with `--Galatea:ConfigPath=<path>`): V11 `config.json`, V3 `connections.json`, V4 `delegates.json`. `agentControlProfileFiles` must exist before bootstrap — scaffold them with `SessionJournal.Cli`'s `recap-grid scaffold`. If `connections.json` uses `openai-codex-responses`, set `ATELIA_CODEX_SUBSCRIPTION_ACCOUNT_FINGERPRINT` in the environment. First run with missing config generates templates and exits — templates are not runnable configs.

- Run guide (startup, login, autonomy, troubleshooting): `prototypes/Galatea/README.md`
- Documentation index (task-based): `docs/Galatea/README.md` — follow it instead of reading docs ad hoc
- Config reference: `docs/Galatea/configuration.md`; HTTP/SSE API: `docs/Galatea/server-api.md`; runtime mechanisms and recovery: `docs/Galatea/runtime.md`
- Root config contract: `docs/SessionJournal/current/contracts/galatea-root-config-v11.md` (older V10- for historical data identification only)

### SessionJournal / RecapGrid — the Memory Stack

Authoritative mental model from `docs/SessionJournal/current/architecture-and-code-map.md`:

```text
raw EventJournal events + selected RefId Parent lineage  (authority)
                     |
             SessionJournalEngine
                     |
           HistoryTimeline ledger
                     |
  Cadence + Control recipe graph + RecapGrid Store
                     |
         Manager / Runtime / Getter / Online
                     |
              CLI / Galatea
```

- Raw events are the append-only source of truth. HistoryTimeline, Cadence, Control, and RecapGrid Store are **companion state** with their own identity, head fences, and rebuild boundaries — they never write back to raw history.
- Ownership: `SessionJournal` owns typed raw input and Parent lineage; `SessionJournal.HistoryTimeline` owns the single-row-identity ledger; `RecapGrid.Cadence` owns per-Ref reserve-aware sealing; `RecapGrid` (namespaces: Abstractions, Control, Store, Manager, Runtime, Getter, Online, AgentControl) owns the grid; `RecapGrid.Hosting` owns strict completion composition; CLI/Galatea own the operator surface.
- Authority rules: selection/materialization always binds exact repository + `RefId` + Timeline/Control/Store identity — no latest/global scan, no cross-handle fallback. Failures are typed outcomes (`Busy`/`Stale`/`Invalid`/`Unsupported`/`Indeterminate`); hosts never message-map exceptions or blindly retry.
- New requests use `CompletionRequestPrepared v9` (semantic plans, structured `SessionInputContent`, projection via host-provided `ISessionInputProjector` before sending). Historical v7/v8 recover exactly; v5 is audit-readable but not executable. Contract docs: `docs/SessionJournal/current/contracts/`.
- Operator surface: `prototypes/SessionJournal.Cli` (`recap-grid ...`, `run-online-turn`); branch rewind (`rewind-branch`) is the operator path for abandoning a turn's selected suffix.

### MemoPod

`prototypes/MemoPod`: Linux-only Editable→Frozen document lifecycle for character notes (per `docs/Galatea/character-note-default-memopod-v1.md`). `FreezeAsync` durably commits; Frozen pods lazily project a deterministic provider-neutral recall prompt; `ResumeEditing` revokes the epoch. Single-owner, not thread-safe.

## Documentation Governance

Docs are first-class artifacts with their own maintenance rules (`docs/Galatea/README.md`):

- One detail has exactly one maintenance location; other docs link to it with short summaries. Run-guide docs only gain changes that affect real operations.
- New reference pages must be added to `docs/SessionJournal/session-journal-doc-check-scope.txt`, then run:

```bash
python3 scripts/check_session_journal_docs.py
git diff --check
```

- Never write local passwords, credentials, state files, or provider outputs into tracked docs. Do not start a live server to update docs.
- Work orders, designs, and old acceptance notes record their phase's intent and evidence — do not treat "已完成" in them as current deployment state.

## Documentation Standards

Follow `docs/spec-conventions.md` for all design documents:

### Clause IDs (Normative Clauses)
- `[F-NAME]`: Framing/Format — `[A-NAME]`: API — `[S-NAME]`: Semantics — `[R-NAME]`: Recovery
- Format: `SCREAMING-KEBAB-CASE` expressing the stance/decision (e.g., `[F-CASE-INSENSITIVE]`)

### Normative Language
- `MUST` / `MUST NOT` absolute; `SHOULD` / `SHOULD NOT` recommended; `MAY` optional; `(MVP 固定)` = "MUST for v2.x"

### Information Representation
Prefer machine-readable formats over ASCII art: nested lists for hierarchies, Markdown tables for 2D relationships, Mermaid for state machines/flows, range tables for bit layouts. Avoid box-drawing diagrams.

### Terminology Format
- Clause IDs: `SCREAMING-KEBAB-CASE` — Concept Terms: `Title-Kebab` (e.g., `Resolve-Tier`) — File Names: `lower-kebab-case`
- Artifact-Tiers methodology (five tiers): Resolve (值得做吗) → Shape (用户看到什么) → Rule (什么是合法的) → Plan (走哪条路) → Craft (怎么造出来)

## Naming Conventions

Defined in `docs/Atelia_Naming_Convention.md`, enforced by `Directory.Build.props`:

- Namespaces MUST start with `Atelia.` (build fails otherwise)
- Assembly names and package IDs default to `Atelia.{ProjectName}`; overridable per-project
- Directory structure: `src/{ProjectName}/` (no Atelia prefix), tests in `tests/{ProjectName}.Tests/`

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

### Line Endings
- Default: LF (`\n`); exceptions: CRLF for `.sln`, `.bat`, `.cmd`, `.ps1` (see `.gitattributes`, `docs/Line_Endings_Standard.md`)
- Never modify `core.autocrlf` settings

### Indentation
- C#: 4 spaces — JSON/YAML: 2 spaces — XML config: 2 spaces — project files: 4 spaces

## Development Philosophy

### Refactoring Over Compatibility
- **Prefer thorough refactoring** over compatibility layers. No downstream users, so breaking changes are acceptable. Remove obsolete code completely rather than deprecating. Avoid backwards-compatibility hacks.

### Debug Output
Use `DebugUtil` instead of `Console.WriteLine` or `Debug.WriteLine` (from atelia-completion's Diagnostics package):

```csharp
DebugUtil.Trace("category", "trace message");  // DEBUG only
DebugUtil.Info("category", "info message");    // DEBUG only
DebugUtil.Warning("category", "warning");      // Always logged
DebugUtil.Error("category", "error");          // Always logged
```

Control via environment variables:

```bash
export ATELIA_DEBUG_CATEGORIES="TypeHash,Test,Outline"   # or "ALL"
export ATELIA_DEBUG_FILE_LEVEL="Trace"
export ATELIA_DEBUG_CONSOLE_LEVEL="Info"
```

Logs go to `.atelia/debug-logs/{category}.log` (fallback `gitignore/debug-logs/`).

## Common Pitfalls

### AteliaResult<T>.Value is a property, not a method
- `result.Value;` alone fails with CS0201 (only assignment/call/increment can be used as statement)
- Fix: `_ = result.Value;` or `var v = result.Value;` — the discard `_ =` makes it a valid statement

### Git Operations
- Never use `--no-verify` or `--no-gpg-sign` unless explicitly requested
- Create NEW commits after hook failures, never amend

### Completion Restore
- Forget the `--configfile eng/NuGet.Completion.Local.config` on restore → NU1101 errors for `Atelia.Completion*` packages. The local feed and its manifest are gitignored local artifacts (`gitignore/completion-packages/`); a fresh clone cannot restore until the dev packages are published or the machine has a copy of the frozen feed.
- Switching between package mode and source mode (`UseCompletionSources` / `UseStorageSources`) requires a re-restore; mixing identities produces duplicate-assembly failures.

## Project Goals

From `AGENTS.md`, the high-level objectives:

1. Design and implement agents capable of long-term autonomous action
2. Establish Agent-Operating-System (能动体运转系统) theoretical framework
3. Implement custom LLM Agent framework
4. Design and implement DocUI (LLM-Agent-OS interaction interface)
5. Achieve "zero-surprise editing" for LLM agents with preview+confirmation
6. Implement memory and persistence systems (SessionJournal / RecapGrid / MemoPod)
7. Implement RBF (Reversible Binary Framing) serialization
8. Maintain documentation in LLM-comprehensible form

## Additional Resources

- **AGENTS.md**: Team onboarding for AI agents (shared across tools; contains Codex CLI-specific tool notes)
- **SUBAGENT-MODEL-SELECTION.md**: Experimental policy for choosing models/effort when delegating to subagents — check it before spawning subagents for non-trivial work
- **docs/spec-conventions.md**: Specification writing conventions
- **docs/Atelia_Naming_Convention.md**: Naming standards
- **docs/Line_Endings_Standard.md**: Line ending policies
- **docs/storage-dependency.md**: Storage package pinning and source-interop modes
- **docs/completion-dependency.md**: Completion package pinning, local dev feed, and call/debug boundaries
- **docs/工作流/**: Human's workflow decisions (e.g., staged writing/formatting: functional code unformatted, Roslyn CodeFix normalizes at commit time)
- **local-codex-mcp/**: Local Codex MCP package used for Codex delegation experiments
