using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Atelia.Analyzers.Style;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MT0001MultipleStatementsAnalyzer : DiagnosticAnalyzer {
    public const string DiagnosticId = "MT0001";
    public const string CanonicalName = "StatementSinglePerLine"; // DocAlias: SingleStatementPerLine
    private static readonly LocalizableString Title = "Multiple statements on one line";
    private static readonly LocalizableString MessageFormat = "Split multiple statements onto separate lines";
    private static readonly LocalizableString Description = "Improves debugging (line breakpoints) and diff granularity by ensuring one statement per line.";
    private const string Category = "Formatting";

    public static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        Title,
        MessageFormat,
    Category,
    DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: Description);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context) {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxTreeAction(AnalyzeTree);
    }

    private static void AnalyzeTree(SyntaxTreeAnalysisContext ctx) {
        var root = ctx.Tree.GetRoot(ctx.CancellationToken);
        // Iterate over statement lists: blocks and switch sections
        foreach (var block in root.DescendantNodes(descendIntoTrivia: false).OfType<BlockSyntax>()) {
            AnalyzeStatements(ctx, block.Statements);
        }
        foreach (var section in root.DescendantNodes(descendIntoTrivia: false).OfType<SwitchSectionSyntax>()) {
            AnalyzeStatements(ctx, section.Statements);
        }
    }

    private static void AnalyzeStatements(SyntaxTreeAnalysisContext ctx, SyntaxList<StatementSyntax> statements) {
        if (statements.Count < 2) {
            return;
        }

        var text = ctx.Tree.GetText(ctx.CancellationToken);
        foreach (var stmt in statements) {
            // Only check simple statements; skip blocks / local function / using / if etc.
            if (stmt is ExpressionStatementSyntax or LocalDeclarationStatementSyntax or ReturnStatementSyntax or ThrowStatementSyntax) {
                var lineSpan = text.Lines.GetLineFromPosition(stmt.GetFirstToken().SpanStart).Span;
                // If the statement extends beyond the first line's end -> it's multiline -> skip
                if (stmt.Span.End > lineSpan.End) {
                    continue;
                }
                // Count how many distinct statement start tokens share this physical line
                int countOnLine = 0;
                foreach (var other in statements) {
                    if (other == stmt) {
                        countOnLine++;
                        continue;
                    }
                    var otherStart = other.GetFirstToken().SpanStart;
                    if (lineSpan.Contains(otherStart)) {
                        countOnLine++;
                    }

                    if (countOnLine > 1) {
                        break;
                    }
                }
                if (countOnLine > 1) {
                    ctx.ReportDiagnostic(Diagnostic.Create(Rule, stmt.GetLocation()));
                }
            }
        }
    }
}
