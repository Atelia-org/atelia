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

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(X0001OpenParenNewLineCodeFix))]
[Shared]
public sealed class X0001OpenParenNewLineCodeFix : CodeFixProvider {
    private const string DiagnosticId = "X0001";
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(DiagnosticId);
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context) {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root == null) {
            return;
        }
        var diag = context.Diagnostics.First();
        var token = root.FindToken(diag.Location.SourceSpan.Start);
        if (!token.IsKind(SyntaxKind.OpenParenToken)) {
            return;
        }
        context.RegisterCodeFix(CodeAction.Create("Add newline after '(‘", ct => FixAsync(context.Document, root, token, ct), "AddNewlineAfterOpen"), diag);
    }

    private static async Task<Document> FixAsync(Document doc, SyntaxNode root, SyntaxToken open, CancellationToken ct) {
        var indentTrivia = await IndentationHelper.ComputeIndentTriviaAsync(doc, open, 1, ct).ConfigureAwait(false);

        var newOpen = open.WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed, indentTrivia);
        var newRoot = root.ReplaceToken(open, newOpen);
        return doc.WithSyntaxRoot(newRoot);
    }
}
