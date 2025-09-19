# MT0004: NewLineClosingParenMultilineParameterList (NewLineClosingParenParams)

Status: Proposed (not yet implemented)
Severity (planned): Warning (Formatting)
AutoFix: Yes (line insertion & reindent)

CanonicalName: `NewLineClosingParenMultilineParameterList`
DocAlias: `NewLineClosingParenParams`

NOTE: Although named with `ParameterList`, this rule (when implemented) will also apply to invocation argument lists until/unless a dedicated argument-list rule is introduced.

## Decision Rationale
(Ported from earlier draft, terminology normalized.)
When a method/constructor declaration or invocation uses a multiline parameter list, placing the closing parenthesis `)` on the same line as the final parameter visually fuses it with the following `{` or next token. Moving `)` to its own line produces a clean vertical enclosure.

Example before:
```csharp
public WorkspacePathService(
    IOptions<MemoTreeOptions> options,
    IOptions<StorageOptions> storageOptions,
    ILogger<WorkspacePathService> logger) {
    _options = options.Value;
}
```
After:
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
1. Shape recognition (rectangular block) aids human & LLM chunking.
2. Diff stability: parameter edits never touch the `)` line.
3. Synergy with future trailing comma rule (MT0005) by isolating `,` vs `)` concerns.
4. Reduced cognitive load: dedicated sentinel line for list termination.
5. Consistency with vertical alignment practices in other ecosystems.

## Interaction With MT0003
MT0003 enforces inner line indentation. MT0004 only relocates the closing parenthesis. Combined rules:
- Opening line indent = Base
- Each parameter line indent = Base + IndentSize
- Closing parenthesis line indent = Base
- Brace placement remains `) {` (unchanged) for minimal churn.

## Edge Cases
- Single-line lists ignored.
- Trailing inline comment after last parameter: stays with that parameter line; `)` moves below.
- Comment-only lines inside list remain ignored (MT0003 policy).
- Generic type argument lists and attribute argument lists out of scope initially.

## Diagnostic Message (Proposed)
"Place closing parenthesis of multiline parameter list on its own line aligned with the start of the declaration/invocation."

## Implementation Sketch
1. Detect multiline lists where `)` shares line with last parameter token.
2. Report diagnostic at `)` token location.
3. Code fix: insert newline before `)` with base indent.
4. Ensure idempotency.
5. Reuse indentation discovery logic from MT0003 (consider extraction).

## Planned Test Matrix
- Method declaration
- Invocation expression
- Object creation
- Local function
- Constructor / delegate / record primary constructor
- Already-correct case (no diagnostic)
- Single-line list (no diagnostic)
- Last parameter with trailing comma
- Last parameter followed by comment

## Migration Strategy
Introduce as Warning; allow batch fix via `dotnet format analyzers --diagnostics MT0004`.

## Open Questions
- Whether to couple with trailing comma normalization (answer: defer to MT0005).
- Potential future: splitting argument vs parameter list naming once semantics diverge.

## Summary
NewLineClosingParenMultilineParameterList clarifies the visual boundary of multiline parameter (and argument) lists, improving diff quality and comprehension across human and AI contributors.
