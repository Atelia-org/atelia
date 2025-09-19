# MT0005 NewLineAfterOpenParenMultilineList

Status: Disabled by default (opt‑in)  | Category: NewLine | Severity (when enabled): Info

## Summary
For any parameter or argument list that spans multiple lines, require a newline immediately after the opening parenthesis `(` so that the first item starts on its own line. This is the “pure symmetric” companion to MT0004 (which enforces the closing parenthesis on its own line).

MT0005 is intentionally disabled by default because MT0006 (FirstMultilineArgumentNewLine) provides a lighter‑weight structural anchor with lower vertical expansion. Teams wanting full enclosure symmetry may opt in to MT0005 in addition to MT0004.

## Rationale
- Symmetry: Mirrors MT0004 to create a vertical enclosure block.
- Visual scanning: Large multiline lists become columnar and easier to diff.
- Determinism: No heuristics / exemptions; any multiline list triggers the rule.

## Why Disabled by Default
1. Line Cost: Always expanding any multiline list adds two lines (open + close separation) even when only one parameter/argument is multiline (already handled acceptably by MT0006).
2. Churn Reduction: Keeping the rule off avoids broad diffs during iterative design where signatures oscillate between single/multi line forms.
3. Layered Strategy: MT0006 already prevents an inlined first multiline argument; MT0005 would be incremental aesthetic strictness rather than structural necessity.

## Interaction Matrix
| Scenario | Only MT0004 | MT0004 + MT0006 | MT0004 + MT0006 + MT0005 |
|----------|-------------|-----------------|--------------------------|
| Single‑line list | (no change) | (no change) | (no change) |
| One multiline argument (first) | Closing paren isolated | First multiline arg forced to new line; open may still share line | Open forced newline + first multiline arg newline (same visual) |
| Multiple multiline arguments | Only close isolated | First multiline arg isolated; later may remain inline | Every multiline list has leading newline, plus close isolated |

## Rule Logic (when enabled)
If (list spans >1 line) AND (open paren trailing trivia lacks end‑of‑line) AND (first item starts on same line as '(') THEN report at the open parenthesis.

No exemptions for single‑item lists or single‑line items (pure mode).

## Example
Violation:
```csharp
Execute(a,
    b,
    c); // '(' with 'a' inline, list spans lines
```
Fix:
```csharp
Execute(
    a,
    b,
    c);
```

## Opt‑In
Enable via an `.editorconfig` (Roslyn style) entry:
```
dotnet_diagnostic.MT0005.severity = suggestion
```
Or elevate:
```
dotnet_diagnostic.MT0005.severity = warning
```

## Code Fix
Inserts a newline plus one indentation unit after `(`; preserves existing trailing trivia/comments.

## Related Rules
- MT0004 NewLineClosingParenMultilineParameterList (paired close enforcement)
- MT0006 NewLineFirstMultilineArgument (lighter structural anchor)

## Migration Guidance
1. Adopt MT0004 + MT0006 first.
2. Sample a representative diff enabling MT0005 in CI dry‑run.
3. If vertical density cost acceptable, enable at suggestion level.
4. Optionally raise to warning after churn stabilizes.

## Non‑Goals
- Fine‑grained heuristics (e.g., exempt single multiline arg). Those tradeoffs handled by choosing MT0006 instead of MT0005.
- Reordering / indentation of interior items (handled elsewhere).

---
Historical Note: MT0005 supersedes experimental rule X0001 (and its Guard variants) by standardizing on the pure, unconditional form.
