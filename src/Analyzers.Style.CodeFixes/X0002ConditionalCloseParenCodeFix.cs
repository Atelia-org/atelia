using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;

namespace Atelia.Analyzers.Style;

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(X0002ConditionalCloseParenCodeFix))]
[Shared]
public sealed class X0002ConditionalCloseParenCodeFix : CodeFixProvider {
    private const string DiagnosticId = "X0002";
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(DiagnosticId);
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context) {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root == null) {
            return;
        }
        var diag = context.Diagnostics.First();
        var token = root.FindToken(diag.Location.SourceSpan.Start);
        if (!token.IsKind(SyntaxKind.CloseParenToken)) {
            return;
        }
        context.RegisterCodeFix(CodeAction.Create("Move ')' to new line (X0002)", ct => FixAsync(context.Document, root, token, ct), "X0002MoveCloseParen"), diag);
    }

    private static async Task<Document> FixAsync(Document doc, SyntaxNode root, SyntaxToken closeParen, CancellationToken ct) {
        var text = await doc.GetTextAsync(ct).ConfigureAwait(false);
        var open = closeParen.Parent?.ChildTokens().FirstOrDefault(t => t.IsKind(SyntaxKind.OpenParenToken)) ?? default;
        var anchorToken = open == default ? closeParen : open;
        var line = text.Lines.GetLineFromPosition(anchorToken.SpanStart);
        var lineText = line.ToString();
        int baseIndent = 0;
        while (baseIndent < lineText.Length && char.IsWhiteSpace(lineText[baseIndent])) {
            baseIndent++;
        }
        var indentTrivia = SyntaxFactory.Whitespace(new string(' ', baseIndent));
        var newClose = closeParen.WithLeadingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed, indentTrivia);
        var newRoot = root.ReplaceToken(closeParen, newClose);
        return doc.WithSyntaxRoot(newRoot);
    }
}
