# MT0008 – BraceRequireForEmbeddedStatement (BracesNoNewLine)

Require braces for embedded statements in control flow constructs while not introducing new lines. This decouples “add braces” from “add new lines”, providing the compact inline form preferred for high-density, AI-friendly reading.

- ID: MT0008
- CanonicalName: BraceRequireForEmbeddedStatement
- DocAlias: BracesNoNewLine
- Category: Brace
- Severity (recommended): info/warning (configurable)
- AutoFix: Yes (code fix adds braces inline)

## Goal & Motivation

The built-in `csharp_prefer_braces` (IDE0011) ensures braces are present, but its code fix typically expands into multi-line blocks:

- Input: `if (c) return;`
- IDE default output: 
  ```csharp
  if (c)
  {
      return;
  }
  ```

For LLM-assisted workflows and dense code windows, the extra line breaks reduce information density. MT0008 keeps the safety of braces without the line break expansion:

- MT0008 output:
  ```csharp
  if (c) { return; }
  ```

## Scope & Semantics

- Applies to control statements with an embedded statement (non-block body):
  - `if (..) statement;`
  - `else statement;` (see else-if exception below)
  - `for (...) statement;`
  - `foreach (...) statement;`
  - `while (...) statement;`
  - `do statement; while (...);`
  - `using (expr) statement;` (using-statement, not using-declaration)
  - `lock (expr) statement;`
  - `fixed (...) statement;`
- Else-if exception: `else if (...) ...` is preserved as-is to avoid changing binding semantics (we do not wrap it into `else { if (...) ... }`).
- Using-chain exemption: when a `using (..) statement` directly contains another `using (..) statement`, the outer using is preserved without adding braces (keeps the chain form). The final embedded non-block statement (if any) is still wrapped inline.
- Multi-line embedded statements: If the embedded statement already spans multiple physical lines (e.g., a long invocation split across lines), MT0008 still only wraps it with braces and does not introduce additional line breaks around the braces.
- Comments: End-of-line (EOL) comments following the embedded statement are moved just before the closing brace. Single-line `//` comments are converted to block comments `/* ... */` (with `*/` sanitized to `* /` if present); multi-line comments are preserved as-is.
- Labels: Labeled statements are supported; labels inside a block are valid in C#.

### Trivia migration rules (implementation-aligned)
- No EndOfLine trivia is added or removed — the total number of newlines remains unchanged.
- The embedded statement's leading trivia becomes the trailing trivia of the opening brace `{`.
  - Important: The newline between the control header and the embedded statement (e.g., `if (c)\nstmt;`) belongs to the control statement's trailing trivia, so it stays before `{`. You will observe `if (c)\n { ... }` (newline before `{`). The statement's own leading trivia (indent/comments) appears after `{` on the next line(s).
- The embedded statement's trailing trivia is split at the first EndOfLine:
  - Pre-EOL (same physical line): attached immediately before `}`. `//` comments are converted to `/* ... */` (with `*/` sanitized to `* /` if present); multi-line comments retain their form; non-comment whitespace is minimized; multiple comments are separated by a single space.
  - Post-EOL (from the first newline onwards): attached as the trailing trivia of `}` intact, preserving the original newline count.
- Minimal spacing: one space before `{`, and at least one space before `}`.

## Examples

### SameLine Body If/Return
- Before:
  ```csharp
    if (c) /*LeadingTrivia*/ return; // TrailingTrivia
    // NextLeading
    NextStatement();
  ```
- After:
  ```csharp
    if (c) { /*LeadingTrivia*/ return; /* TrailingTrivia*/ }
    // NextLeadingTrivia
    NextStatement();
  ```

### OptionA NewLine Body If/Return
- Before:
  ```csharp
    if (c) // PreviousTrailing
      //LeadingTrivia
      return; // TrailingTrivia
    // NextLeading
    NextStatement();
  ```
- After:
  ```csharp
    if (c) // PreviousTrailing {
      //LeadingTrivia
      return; /* TrailingTrivia*/ }
    // NextLeading
    NextStatement();
  ```

### Else Single Statement
- Before:
  ```csharp
  if (!ready) return; else Log();
  ```
- After:
  ```csharp
  if (!ready) { return; } else { Log(); }
  ```

### Else-If Preserved
- Before:
  ```csharp
  if (a) return; else if (b) return;
  ```
- After (unchanged else-if structure):
  ```csharp
  if (a) { return; } else if (b) { return; }
  ```

### Multi-line Body Preserved
- Before:
  ```csharp
  if (c)
      Foo(1,
          2);
  // NextLeadingTrivia
  NextStatement();
  ```
- After:
  ```csharp
  if (c) {
      Foo(1,
          2);
  }
  // NextLeadingTrivia
  NextStatement();
  ```

### Preserve EOL Comment
- Before:
  ```csharp
  if (c) return; // note
  ```
- After:
  ```csharp
  if (c) { return; /* note */ }
  ```

### Do-While with same-line EOL comment (sanitization)
- Before:
  ```csharp
  do i++; // note */ while(i < 2);
  ```
- After (conceptual layout):
  ```csharp
  do { i++; /* note * / while(i < 2); */ }
  ```
  Notes:
  - Because a single-line `//` comment lexically consumes the rest of the physical line, the trailing `while(i < 2);` text is part of the comment. MT0008 converts that EOL comment into a block comment and sanitizes any `*/` into `* /` to avoid premature termination.
  - The block's closing brace `}` appears after the converted comment (and any post-EOL trivia), maintaining the original newline count.

## Configuration

Recommended `.editorconfig` (IDE0011 disabled; MT0008 on):

```editorconfig
dotnet_diagnostic.IDE0011.severity = none
# Enable MT0008 in your preferred severity
# e.g. in config/enforce.editorconfig
# dotnet_diagnostic.MT0008.severity = warning
```


```bash
# dotnet format analyzers --diagnostics MT0008 --severity info
```
> Note: Use the analyzers subcommand to avoid the full formatter reflow that might try to re-expand blocks.

## Interactions with Other Rules

- MT0001 – StatementSinglePerLine: Compatible. Inline `{ return; }` remains a single simple statement inside a block and does not trigger multi-statement warnings.
- MT0003/MT0004/MT0006/MT0007 – Parameter/Argument list indentation and newline rules: Orthogonal; these operate on parentheses and list layout, not statement bodies.
- IDE0011 – Prefer braces: Should be disabled to prevent conflicting multi-line expansion.

## Known Limitations & Notes

- Formatter influence: The code fix intentionally avoids adding formatter annotations to keep braces inline. If your global formatting settings aggressively expand blocks, prefer applying MT0008 via `dotnet format analyzers`.
- Using-declaration: This rule targets the classic `using (expr) statement` form, not C# 8 using-declarations.

## Implementation Notes

- Analyzer: reports when the embedded `StatementSyntax` is not a `BlockSyntax` for supported control statements; skips `else if`.
- Tests: cover inline if/else, preserve else-if, EOL comments (with sanitization), newline migration, and multi-line embedded bodies.
