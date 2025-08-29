# Analyzer Rule Naming Convention

We use a structured naming scheme to keep rules machine-friendly, human-scannable, and evolution-ready.

## Components
Pattern (CanonicalName): `<Category><Object><Condition><Qualifier?>`

- Category: Fixed small set indicating dominant dimension.
  - `Statement` | `Indent` | `NewLine` | `Space` | `Comma` | `Brace` | `Block` (future additions must update this file)
- Object: Roslyn / structural noun (e.g. `InitializerElements`, `ParameterList`, `ClosingParen`, `SingleLineBlock`).
- Condition: Optional scoping word (e.g. `Multiline`, `Nested`, `PerLine`, `Between`).
- Qualifier: Optional disambiguator when needed.
- DocAlias: A shorter, documentation-friendly alias (table or quick reference) that may compress words (e.g. `Params`, `ClosingParen`).

## Current Canonical Names
| ID | CanonicalName | DocAlias | Notes |
|----|---------------|----------|-------|
| MT0001 | StatementSinglePerLine | SingleStatementPerLine | One physical line must not contain multiple simple statements. |
| MT0002 | IndentInitializerElements | IndentInitializers | Elements inside multiline object / collection / array initializers indented one level from `{` line. |
| MT0003 | IndentMultilineParameterList | IndentMultilineParams | Applies to declaration parameter lists AND (temporarily) invocation argument lists. Future split may add `IndentMultilineArgumentList`. |
| MT0004 | NewLineClosingParenMultilineParameterList | NewLineClosingParenParams | Closing parenthesis isolated on its own line for multiline parameter/argument lists. |
| MT0005 (disabled) | NewLineAfterOpenParenMultilineList | NewLineAfterOpenParen | Pure symmetric opening newline for any multiline parameter/argument list (opt-in; pairs with MT0004). |
| MT0006 | NewLineFirstMultilineArgument | FirstMultilineArgNewLine | First multiline argument must start on its own line (minimal anchor). |

## Principles
1. No mixed `ParameterArgument` compounds—prefer picking one domain noun. We currently anchor on `ParameterList` and document the temporary broader scope.
2. Keep CanonicalName descriptive even if slightly longer; DocAlias provides brevity where needed.
3. One rule = one dominant Category. If a behavior spans multiple concerns, split into multiple rules.
4. Avoid filler words (`For`, `Of`, `Before`) unless clarity would suffer.
5. Stable CanonicalName: once published, avoid renaming; if semantics shift materially, deprecate old ID and introduce new rule.

## Adding a New Rule Checklist
- Pick Category ensuring ≥80% of the rule's effect lies in that dimension.
- Select Object using Roslyn syntax terminology when possible.
- Add Condition only if needed to avoid ambiguity or express scope (e.g. Multiline, Nested).
- Add file: `docs/AnalyzerRules/<ID>_<CanonicalName>.md`.
- Update table above.
- Add `public const string CanonicalName = "...";` to analyzer class.
- Reference CanonicalName in README rule table.

## Future Extensions
- Potential split: `IndentMultilineArgumentList` (if argument and parameter indentation policies diverge).
- Trailing comma normalization: likely `CommaEnsureTrailingInMultilineLists` (Alias: `TrailingCommaMultiline`).
- Single-line block expansion: `BlockExpandNestedSingleLine`.

## Rationale
This convention aligns with `.editorconfig` option grouping (indent / new_line / space domains) while retaining semantic precision for custom structural rules that cannot be expressed as pure formatter options.
