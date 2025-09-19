# MT0101 XmlDocEscapeAngleBrackets (DocAlias: XmlDocEscape)

Category: Documentation | Severity: Info | AutoFix: Yes | Status: Stable

## Summary
Escapes raw angle brackets in XML documentation comments to keep the doc XML well-formed. Any '<' or '>' that is not part of an allowed documentation tag is replaced with `&lt;` / `&gt;`. Known tags (e.g., `summary`, `see`, `code`, `param`, …) are preserved as-is.

Why: Unescaped angle brackets in XML doc text can break XML parsing (IDE tooltips, DocFX, analyzers) and hinder downstream processing by LLM/document tools. This rule ensures correctness while minimizing churn.

## Rule
Within a `///` or `/** ... */` documentation comment trivia:
- If the text contains any unescaped '<' or '>' that are not part of a recognized XML documentation tag, report a diagnostic (Info).
- A code fix is offered to replace them with `&lt;` and `&gt;` respectively.

Recognized tags (kept in sync with implementation):
- Core: `summary`, `remarks`, `para`, `c`, `code`, `see`, `seealso`
- Lists: `list`, `listheader`, `item`, `term`, `description`
- Members/blocks: `example`, `exception`, `include`, `param`, `paramref`, `typeparam`, `typeparamref`, `permission`, `value`, `returns`
- Others: `inheritdoc`, `note`

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

Preserve known tags:
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

Unknown/HTML-like tags are treated as text and will be escaped:
```xml
/// <summary>Use <b>bold</b> for emphasis.</summary>
```
Becomes:
```xml
/// <summary>Use &lt;b&gt;bold&lt;/b&gt; for emphasis.</summary>
```
If you intentionally allow extra tags, consider extending the known-tags list in the implementation in a future revision.

## Detection Algorithm (implementation notes)
- The analyzer inspects `DocumentationCommentTriviaSyntax` and obtains its full text.
- A single-pass scanner builds a "fixed" version by:
  - When encountering '<', it peeks the following characters to decide whether it starts a known tag name (letters/digits/`:`/`_`, with optional leading '/').
    - If it is a known doc tag, the entire tag up to the next '>' is copied verbatim.
    - If it is `<!` or `<?`, the content is copied through to the next '>'.
    - Otherwise, the '<' is replaced with `&lt;`.
  - A standalone '>' outside any recognized tag is replaced with `&gt;`.
- If the rebuilt text differs from the original, the analyzer reports MT0101 at the documentation trivia location.

Idempotency: Re-running the fixer produces no further changes.

## Code Fix & Fix All
- Scope: Replaces the diagnostic span (the whole doc trivia) with the escaped text.
- Provider: Batch fix is supported (Fix All in solution/project/document).
- Severity: Info (enabled by default), safe to apply automatically in CI using `dotnet format analyzers`.

## Edge Cases & Limitations
- Unknown but valid XML doc tags will be treated as text and escaped. If your codebase relies on extra tags, consider adding them to the known-tags set in both analyzer and code fix.
- Complex constructs beginning with `<!` (e.g., `<![CDATA[ ... ]]>`) or `<!-- ... -->` are copied through until the next '>'. While CDATA and comments are uncommon in C# XML docs, deeply nested '>' characters inside such constructs may not be fully modeled by this lightweight scanner.
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
