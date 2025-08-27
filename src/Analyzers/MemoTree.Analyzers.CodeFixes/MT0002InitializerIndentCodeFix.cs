using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MemoTree.Analyzers;

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(MT0002InitializerIndentCodeFix)), Shared]
public sealed class MT0002InitializerIndentCodeFix : CodeFixProvider {
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(MT0002InitializerIndentAnalyzer.DiagnosticId);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context) {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) {
            return;
        }

        foreach (var diagnostic in context.Diagnostics) {
            var targetNode = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            var initializer = targetNode.FirstAncestorOrSelf<InitializerExpressionSyntax>();
            if (initializer is null) {
                continue;
            }

            var expr = initializer.Expressions.FirstOrDefault(e => e.Span.Contains(targetNode.Span))
                       ?? initializer.Expressions.FirstOrDefault(e => e.Span.IntersectsWith(diagnostic.Location.SourceSpan));
            if (expr is null) {
                continue;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    title: "Fix initializer indentation",
                    createChangedDocument: c => FixSingleAsync(context.Document, initializer, expr, c),
                    equivalenceKey: "MT0002_FixIndent"),
                diagnostic);
        }
    }

    private static async Task<Document> FixSingleAsync(Document document, InitializerExpressionSyntax initializer, ExpressionSyntax exprToFix, CancellationToken token) {
        var root = await document.GetSyntaxRootAsync(token).ConfigureAwait(false);
        if (root is null) {
            return document;
        }

        var text = await document.GetTextAsync(token).ConfigureAwait(false);

        int openLine = text.Lines.GetLineFromPosition(initializer.OpenBraceToken.SpanStart).LineNumber;
        string openLineText = text.Lines[openLine].ToString();
        int baseIndent = openLineText.TakeWhile(char.IsWhiteSpace).Count();

        var options = document.Project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GetOptions(initializer.SyntaxTree);
        int indentSize = 4;
        if (options.TryGetValue("indent_size", out var indentStr) && int.TryParse(indentStr, out var parsed) && parsed > 0) {
            indentSize = parsed;
        }
        string expectedIndent = new string(' ', baseIndent + indentSize);

        var firstToken = exprToFix.GetFirstToken();
        int exprLine = text.Lines.GetLineFromPosition(firstToken.SpanStart).LineNumber;
        if (exprLine == openLine) {
            return document;
        }

        var leading = firstToken.LeadingTrivia;
        var filtered = leading.SkipWhile(t => t.IsKind(SyntaxKind.WhitespaceTrivia)).ToList();
        filtered.Insert(0, Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Whitespace(expectedIndent));
        var newToken = firstToken.WithLeadingTrivia(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.TriviaList(filtered));
        var newInitializer = initializer.ReplaceToken(firstToken, newToken);
        var newRoot = root.ReplaceNode(initializer, newInitializer);
        return document.WithSyntaxRoot(newRoot);
    }
}
