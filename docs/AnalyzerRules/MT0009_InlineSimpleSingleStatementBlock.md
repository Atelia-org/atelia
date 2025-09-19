# MT0009 – InlineSimpleSingleStatementBlock (CompactSingleStatementBlocks)

Reduce low-information line count by collapsing blocks that contain exactly one simple statement into a single inline block form, while preserving comments and semantics. This complements MT0008 which adds braces without new lines.

- ID: MT0009
- CanonicalName: InlineSimpleSingleStatementBlock
- DocAlias: CompactSingleStatementBlocks
- Category: Brace
- Severity (recommended): suggestion/info (configurable)
- AutoFix: Yes (code fix removes inner newlines and compacts to one line)

## Goal & Motivation

When `csharp_new_line_before_else/catch/finally = true` is enabled to keep `else` on a new line (avoiding long else-if lines), short if/else blocks like

```csharp
if (c)
{
    return; // note
}
else
{
    continue;
}
```
can be safely compacted to

```csharp
if (c) { return; /* note */ }
else { continue; }
```

This rule removes only the block-internal newlines for specific safe cases to increase information density, pairing naturally with MT0008.

## Scope & Semantics (v1 conservative)

- Target contexts (v1):
  - `if (..) { … }` and `else { … }` bodies only.
- Block eligibility:
  - The body is a `BlockSyntax` with exactly one statement: `block.Statements.Count == 1`.
  - The single statement is NOT a nested block, NOT a local function, NOT an `if` (avoids altering else-if shape).
  - The single statement type is in an allow-list:
    - `return;` (optional: may later include `return expr;` behind a config switch)
    - `break;`
    - `continue;`
    - `throw;`
    - `i++;`, `i--;`, `++i`, `--i` (pre/post inc/dec) — can be made configurable.
- No standalone comment lines inside the block:
  - Between `{` and the statement's first token, and between the statement's last token and `}`, there MUST NOT be any comment trivia or preprocessor directives. At most a single EndOfLine is allowed (the one to be removed), plus whitespace.
- Single-physical-line statement:
  - The statement's Span must be on a single line (StartLine == EndLine via `GetLineSpan()`), preventing compaction of multi-line statements.
- No directives inside the block (e.g., `#if`, `#region`).

These structural checks avoid semantic classification of “simple expressions” and keep v1 predictable and safe.

## Code Fix Behavior

- Remove the newlines inside `{ … }`, producing a single-line inline block.
- Spacing:
  - Maintain one space before `{` and at least one space before `}`.
- Trivia migration (aligned with MT0008):
  - Keep the statement's leading trivia with the statement (or alternatively move it to `{` trailing if desired by implementation; net effect should not leak comments outside the block).
  - Split the statement's trailing trivia at the first newline:
    - Pre-EOL (same line) comments are placed immediately before `}`. Convert `// …` to `/* … */` and sanitize `*/` to `* /`; preserve multi-line comments; minimize whitespace between comments.
    - Post-EOL (from the first newline onward) is attached as the trailing trivia of `}` unchanged. In practice, after collapsing, this often becomes empty.
- Do not add formatter annotations; rely on minimal-space trivia to keep the inline form.

## Examples

### If with single return
Before:
```csharp
if (c)
{
    return; // note
}
```
After:
```csharp
if (c) { return; /* note */ }
```

### Else with continue
Before:
```csharp
if (!ready) { return; }
else
{
    continue;
}
```
After:
```csharp
if (!ready) { return; }
else { continue; }
```

### Preserving multi-line bodies (not eligible)
Before:
```csharp
if (c)
{
    Foo(1,
        2);
}
```
After (unchanged):
```csharp
if (c)
{
    Foo(1,
        2);
}
```

### Standalone comment line inside block (not eligible)
Before:
```csharp
if (c)
{
    // lead
    return;
}
```
After: unchanged (block is not compacted)

## Configuration

Optional `.editorconfig` keys (proposal):

```editorconfig
# Allow compacting pre/post inc/dec expression statements
atelia_style.MT0009.allow_increment_decrement = true

# Allow compacting return with expression (e.g., return x;)
atelia_style.MT0009.allow_return_expression = false

# Max resulting line length; if exceeded, do not compact (0 = unlimited)
atelia_style.MT0009.max_line_length = 0

# Extend beyond if/else bodies (future expansion)
atelia_style.MT0009.apply_to_other_blocks = false
```

## Interactions

- MT0008 – BraceRequireForEmbeddedStatement: Complementary. MT0008 adds braces inline without newlines; MT0009 removes inner newlines for eligible one-statement blocks.
- `csharp_new_line_before_else/catch/finally`: Compatible. This setting controls where `else/catch/finally` appear, while MT0009 compacts the block body.
- StyleCop SA1501 (statement must not be on a single line): Potential conflict; disable SA1501 if present.
- IDE0011: Should remain disabled to avoid multi-line expansion.

## Known Limitations

- Highly opinionated formatters might re-expand blocks; prefer applying via `dotnet format analyzers` or ensure formatter respects inline blocks.
- v1 is intentionally conservative; more statement kinds can be enabled later behind config.

## Implementation Notes

- Analyzer: Report on an if/else body block that meets eligibility checks above.
- Code fix: Rebuild the `{ stmt }` with adjusted trivia, converting EOL `//` comments to block comments before `}` and preserving post-EOL trivia on `}`.
- Tests: Cover happy path (return/break/continue/throw, inc/dec), negative cases (standalone comments, directives, multi-line statements), and interaction with MT0008.
