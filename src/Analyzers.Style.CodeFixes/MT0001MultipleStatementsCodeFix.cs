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
using Microsoft.CodeAnalysis.Formatting;

namespace Atelia.Analyzers.Style;

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(MT0001MultipleStatementsCodeFix))]
[Shared]
public sealed class MT0001MultipleStatementsCodeFix : CodeFixProvider {
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(MT0001MultipleStatementsAnalyzer.DiagnosticId);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context) {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) {
            return;
        }

        var diagnostic = context.Diagnostics.First();
        var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
        var statement = node.FirstAncestorOrSelf<StatementSyntax>();
        if (statement is null) {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                title: "Split statements onto separate lines",
                createChangedDocument: ct => ApplyFixAsync(context.Document, statement, ct),
                equivalenceKey: "SplitStatements"),
            diagnostic);
    }

    private static async Task<Document> ApplyFixAsync(Document document, StatementSyntax target, CancellationToken ct) {
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        if (root is null) {
            return document;
        }

        var parent = target.Parent;
        if (parent is not (BlockSyntax or SwitchSectionSyntax)) {
            return document;
        }

        var text = await document.GetTextAsync(ct).ConfigureAwait(false);
        var line = text.Lines.GetLineFromPosition(target.GetFirstToken().SpanStart);
        var containerStatements = parent switch {
            BlockSyntax b => b.Statements,
            SwitchSectionSyntax s => s.Statements,
            _ => default
        };
        if (containerStatements.Count == 0) {
            return document;
        }

        var lineStatements = containerStatements.Where(s => line.Span.Contains(s.GetFirstToken().SpanStart)).ToList();
        if (lineStatements.Count < 2) {
            return document;
        }

        var editorRoot = root;
        foreach (var stmt in lineStatements.Skip(1)) {
            var firstToken = stmt.GetFirstToken();
            if (firstToken.LeadingTrivia.All(t => !t.IsKind(SyntaxKind.EndOfLineTrivia))) {
                var newLeading = firstToken.LeadingTrivia.Insert(0, Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ElasticCarriageReturnLineFeed);
                editorRoot = editorRoot.ReplaceToken(firstToken, firstToken.WithLeadingTrivia(newLeading).WithAdditionalAnnotations(Formatter.Annotation));
            }
        }

        return document.WithSyntaxRoot(editorRoot);
    }
}
