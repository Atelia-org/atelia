# MT0006 NewLineFirstMultilineArgument (DocAlias: FirstMultilineArgNewLine)

Category: NewLine  | Severity: Warning | Status: Experimental (stabilizing)

## Summary
For any invocation or object creation that contains at least one multiline argument, the FIRST multiline argument must start on its own line (i.e. not share the line with the opening parenthesis). Later multiline arguments are intentionally ignored. This establishes a single, stable vertical anchor improving diff clarity and LLM patch locality while minimizing formatting churn.

## Motivation (Key Decision Factors)
1. Patch Stability: Only enforcing a single anchor avoids cascading rewrites if additional multiline arguments are later added.
2. Readability: A newline after '(' before the structural block (lambda / initializer) visually separates call head from embedded block logic.
3. Minimal Intervention: Not reformatting subsequent multiline arguments keeps diffs small and preserves developer intent when mixing styles.
4. Synergy with MT0004: Combined with MT0004 (closing parenthesis on its own line) yields a strong enclosure pattern: `Name(\n <first anchor> ... \n)` without mandating full vertical expansion.
5. Token Economy (LLM Context): Adding only one newline typically costs +1 physical line, giving ~80% of the boundary clarity benefits of full expansion (strategy B) with lower line overhead.

## Rule
Given an argument list where the first multiline argument's first token appears on the same source line as the opening parenthesis `(`, produce a diagnostic at that token.

Multiline argument = argument whose span crosses at least two source lines (start line != end line). Single-line lambdas or initializers are not treated as multiline.

Ignored cases:
- No multiline arguments present.
- The first multiline argument already starts on a different line than the `(`.
- Subsequent multiline arguments (by design, to minimize churn).

## Examples
Violation:
```csharp
DoWork(42, x => {
    Log(x);
    return x + 1;
}, more);
```
Fixed:
```csharp
DoWork(
    42, x => {
        Log(x);
        return x + 1;
    }, more);
```

Multiple multiline arguments (only first enforced):
```csharp
Process(
    setup => {
        Init();
    }, run => {
        Execute();
    }, tail);
```
(Second multiline `run => { ... }` may remain inlined – no diagnostic.)

No diagnostic (no multiline argument):
```csharp
Foo(1, 2, Bar(3));
```

## Interactions
- MT0004 ensures the closing parenthesis is isolated; apply MT0006 first then MT0004 for deterministic formatting.
- Future expansion (reserved MT0005) may add a rule to require newline immediately after '(' (symmetric opening enclosure) — MT0006 co-exists by handling ONLY the first multiline argument case.

## CodeFix Behavior
Inserts a newline + one indentation level (respecting `indent_size`) before the first token of the offending multiline argument. Other trivia / comments are preserved.

## Non-Goals
- Reordering or vertically expanding all arguments.
- Handling attribute argument lists (scope may expand if evidence warrants).
- Enforcing spacing or indentation inside the multiline argument body (delegated to other rules).

## Metrics to Monitor
- Percentage of invocations with >1 multiline argument.
- Average added lines per fix.
- Re-run idempotency (2 consecutive formatter passes produce identical bytes).

## Migration Strategy
1. Introduce rule (experimental) in Warning or Info mode; gather metrics.
2. If prevalence of second multiline arguments remains low and user feedback favors stronger uniformity, consider an upgrade path to a stricter variant (new ID) that enforces all multiline arguments on new lines.

## Related Rules
- MT0004 NewLineClosingParenMultilineParameterList
- (Planned) MT0005 NewLineAfterOpenParenMultilineList (reserved)
