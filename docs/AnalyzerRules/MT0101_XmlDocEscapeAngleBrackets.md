# MT0101 XmlDocEscapeAngleBrackets (DocAlias: XmlDocEscape)

Category: Documentation | Severity: Info | AutoFix: Yes | Status: Stable

## Summary
Escapes raw angle brackets in XML documentation comments to keep the doc XML well-formed. Any '<' or '>' that does not belong to a well-formed element (including standard XML doc tags and matching HTML-like tags) is replaced with `&lt;` / `&gt;`. Balanced, syntactically valid tags are preserved verbatim.

Why: Unescaped angle brackets in XML doc text can break XML parsing (IDE tooltips, DocFX, analyzers) and hinder downstream processing by LLM/document tools. This rule ensures correctness while minimizing churn.

## Rule
Within a `///` or `/** ... */` documentation comment trivia:
- If the text contains any unescaped '<' or '>' that are not part of a structurally valid element (matching start/end or self-closing tag), report a diagnostic (Info).
- A code fix is offered to replace them with `&lt;` and `&gt;` respectively.

Also passed through as-is: processing instructions / declarations and comment-like constructs that begin with `<!` or `<?`.

## Examples
Violation (raw angle brackets in text):
```xml
/// <summary>
/// Returns x < 0 when negative and > 0 when positive.
/// </summary>
```
Fixed:
```xml
/// <summary>
/// Returns x &lt; 0 when negative and &gt; 0 when positive.
/// </summary>
```

Preserve well-formed tags (including standard XML doc elements):
```xml
/// <summary>See <see cref="System.Collections.Generic.List{T}"/> for details.</summary>
```
No change. The `<see .../>` tag is preserved intact.

Inside <c> or <code> content, the inner angle brackets get escaped (as they are text, not tags):
```xml
/// <remarks>
/// Prefer <c>List&lt;T&gt;</c> over textual <c>List<T></c> (the fixer produces the first form).
/// </remarks>
```

Balanced HTML-like tags remain intact:
```xml
/// <summary>Use <strong>bold</strong> for emphasis.</summary>
```
No change. Unmatched tags (e.g., `Use <strong>bold text.`) are still escaped to `&lt;strong&gt;`.

## Detection Algorithm (implementation notes)
- The analyzer inspects `DocumentationCommentTriviaSyntax` and obtains its full text.
- A lightweight scanner walks the string once to identify spans that represent structural tags:
  - `<!` / `<?` constructs are copied through to the next `>`.
  - For `<name ...>` tokens, the scanner validates the tag name, walks attributes while respecting quotes, and determines whether the tag is self-closing or paired with a matching `</name>` later in the text (using a small stack to track nesting).
  - Only tags that are syntactically balanced are marked to be preserved.
- In a second pass, the analyzer rebuilds the text, copying preserved spans verbatim and escaping every other `<` / `>` as entities.
- If the rebuilt text differs from the original, the analyzer reports MT0101 at the documentation trivia location.

Idempotency: Re-running the fixer produces no further changes.

## Code Fix & Fix All
- Scope: Replaces the diagnostic span (the whole doc trivia) with the escaped text.
- Provider: Batch fix is supported (Fix All in solution/project/document).
- Severity: Info (enabled by default), safe to apply automatically in CI using `dotnet format analyzers`.

## Edge Cases & Limitations
- Tags must be syntactically valid (proper name, quoted attributes, matching end tag or `/` before `>`). Malformed tags fall back to entity escaping.
- Complex constructs beginning with `<!` (e.g., `<![CDATA[ ... ]]>`) or `<!-- ... -->` are copied through until the next `>`. Deeply nested `>` characters inside such constructs may still confuse the lightweight scan, mirroring the previous heuristic.
- Generic type names inside attributes like `cref` are preserved because whole tags are copied verbatim. Textual generics inside element content (e.g., in `<c>List<T></c>`) will be escaped to `List&lt;T&gt;`, which is the correct XML form.

## Rationale
- Ensures XML doc well-formedness without forcing broader formatting changes.
- Improves IDE/DocFX reliability and downstream tooling (including LLM chunkers).
- Conservative by design: only escapes clearly unsafe angle brackets; preserves the standard XML doc vocabulary.

## Canonical Naming
- CanonicalName: `XmlDocEscapeAngleBrackets`
- DocAlias: `XmlDocEscape`
- Category: `Documentation`

## Related
- Naming convention: see `docs/AnalyzerRules/NamingConvention.md` (this rule extends categories with `Documentation`).
