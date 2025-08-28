# MT0004: Multiline Argument/Parameter List Closing Parenthesis on New Line

Status: Proposed (not yet implemented)
Severity (planned): Warning (Formatting)
AutoFix: Yes (line insertion & reindent)

## Decision Rationale
When a method/constructor *declaration* or *invocation* uses a multiline parameter/argument list, placing the closing parenthesis `)` immediately after the last parameter line (same line) causes strong local visual similarity to an opening method body brace `{` or following member, especially when the body starts right after `){`. Example from current codebase:

```csharp
public WorkspacePathService(
    IOptions<MemoTreeOptions> options,
    IOptions<StorageOptions> storageOptions,
    ILogger<WorkspacePathService> logger) {
    _options = options.Value;
    // ...
}
```
Visually, `logger)` merges with the subsequent ` {` token cluster; scanning tools (including LLM-based coders) must parse tokens mentally to disambiguate the structural boundary. Introducing a mandatory line break before `)` yields a clearer vertical block shape:

```csharp
public WorkspacePathService(
    IOptions<MemoTreeOptions> options,
    IOptions<StorageOptions> storageOptions,
    ILogger<WorkspacePathService> logger
) {
    _options = options.Value;
}
```
Benefits:
1. Shape Recognition: Parameter list becomes a visually enclosed rectangle, improving chunking.
2. Diff Friendliness: Adding/removing a trailing parameter alters only its own line; `)` line stays stable, reducing noisy diffs.
3. Consistency With Single-Item Trailing Commas (future MT0005): Facilitates a style where a trailing comma before `)` is cleanly isolated.
4. Cognitive Load Reduction: LLM and human readers can detect the parameter list terminator with a dedicated line sentinel.
5. Alignment Symmetry: Mirrors many ecosystems (e.g., some Kotlin/Go style guides) emphasizing vertical alignment for multi-line constructs.

## Interaction With MT0003
MT0003 already enforces that every parameter/argument line (excluding the line containing the opening parenthesis) has exactly one additional indent level. MT0004 would NOT alter those inner lines—only move `)` (and optional trailing comma enforcement will be handled separately by MT0005) to its own line with the *same indent as the invocation/declaration start line*.

Indent Rules Under Combined MT0003 + MT0004:
- Opening line (contains identifier + `(`) at indent = Base.
- Each parameter/argument line = Base + IndentSize (currently 4 spaces).
- Closing parenthesis line = Base.
- Following brace `{` (if any) stays on same line after a space OR on its own line per existing project brace style (current examples keep it on same line: `) {`). We will keep `) {` for now to minimize churn.

## Edge Cases & Clarifications
- Single-line argument lists are untouched (MT0004 only triggers when list spans multiple lines).
- If a trailing comment exists after the last parameter, code fix will move `)` below the parameters; comment stays with the parameter line, not migrated.
- Generic type argument lists `<T1, T2>` are out of scope (future possible rule if needed).
- Attribute argument lists considered separately—initial scope limited to invocation & declaration parameter lists to avoid noise.
- Lines beginning with comments inside the list remain ignored for indentation (MT0003 policy) but still precede the moved `)`.

## Proposed Diagnostic Message
"Place closing parenthesis of multiline argument/parameter list on its own line aligned with the start of the invocation/declaration." (Short title: "Closing parenthesis should be on new line")

## Implementation Sketch
1. Analyzer: Detect multiline lists where `)` token's line != first token's line AND last parameter token line == `)` line (i.e., currently inline). Report diagnostic at `)`.
2. CodeFix: Insert newline before `)`; compute base indent from the line containing `(`; apply exactly that indent; preserve trivia on `)` token.
3. Ensure idempotency: Re-running fix makes no further changes.
4. Reuse existing indentation utility logic from MT0003 (factor out base indent calculator if needed).

## Test Matrix (Planned)
- Method declaration (sample above).
- Invocation expression.
- Object creation with arguments.
- Local function declaration.
- Constructor, delegate, record primary constructor.
- Parameter list already correct (no diagnostic).
- Single-line list (no diagnostic).
- Last parameter with trailing comma (ensure preserved or normalized via MT0005 later).
- Comment after last parameter.

## Migration Strategy
- Introduce MT0004 as Warning but allow temporary suppression in bulk via `#pragma` or editorconfig severity tweak if churn too high.
- Provide batch code fix guidance (`dotnet format analyzers --diagnostics MT0004`).
- Land after verifying minimal merge conflicts in active branches.

## Open Questions
- Should we simultaneously introduce trailing comma normalization to avoid temporary mixed styles? (Leaning NO; defer to MT0005.)
- Should `{` move to next line as well? (Current decision: No change; revisit if later style pressure emerges.)

## Summary
MT0004 formalizes a structural clarity improvement already implicitly desired: a clean vertical enclosure for multiline parameter/argument lists. It synergizes with MT0003, reduces diff noise, and aids both human and AI code comprehension.
