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

namespace Atelia.Analyzers.Style;

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(MT0007ClosingParenIndentCodeFix))]
[Shared]
public sealed class MT0007ClosingParenIndentCodeFix : CodeFixProvider {
    private const string DiagnosticId = "MT0007";
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(DiagnosticId);
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context) {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root == null) {
            return;
        }

        var diag = context.Diagnostics.First();
        var closeParen = root.FindToken(diag.Location.SourceSpan.Start);
        if (!closeParen.IsKind(SyntaxKind.CloseParenToken)) {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                title: "Align closing parenthesis indentation",
                createChangedDocument: ct => FixAsync(context.Document, root, closeParen, ct),
                equivalenceKey: "AlignCloseParenIndent"),
            diag);
    }

    private static async Task<Document> FixAsync(Document doc, SyntaxNode root, SyntaxToken closeParen, CancellationToken ct) {
        var parent = closeParen.Parent;
        if (parent == null) {
            return doc;
        }

        // Determine anchor token analogous to analyzer logic
        SyntaxToken anchorToken = parent switch {
            ArgumentListSyntax argList => argList.Parent switch {
                InvocationExpressionSyntax inv => inv.Expression.GetFirstToken(),
                ObjectCreationExpressionSyntax oc => oc.NewKeyword,
                _ => argList.OpenParenToken
            },
            ParameterListSyntax paramList => paramList.Parent?.GetFirstToken() ?? paramList.OpenParenToken,
            _ => closeParen
        };

        // Compute desired indentation trivia (aligned, no extra indent levels)
        var indentTrivia = await IndentationHelper.ComputeAlignedCloseParenIndentAsync(doc, anchorToken, ct).ConfigureAwait(false);

        // If already aligned do nothing
        var text = await doc.GetTextAsync(ct).ConfigureAwait(false);
        var closeLine = text.Lines.GetLineFromPosition(closeParen.SpanStart);
        var lineText = closeLine.ToString();
        int currentIndent = 0;
        while (currentIndent < lineText.Length && char.IsWhiteSpace(lineText[currentIndent])) {
            currentIndent++;
        }

        if (indentTrivia.ToString().Length == currentIndent) {
            return doc;
        }

        // Replace leading trivia of close paren line: remove existing leading whitespace trivia up to token
        var leading = closeParen.LeadingTrivia;
        // Case 1: Leading already starts with newline -> rebuild after first newline.
        // Normalize: ensure at most one newline. If previous token already ended with newline, don't add another.
        var prev = closeParen.GetPreviousToken();
        bool prevEndsWithNewLine = prev.TrailingTrivia.Any(t => t.IsKind(SyntaxKind.EndOfLineTrivia));
        SyntaxTriviaList newLeading;
        if (prevEndsWithNewLine) {
            newLeading = SyntaxFactory.TriviaList(indentTrivia);
        }
        else {
            newLeading = SyntaxFactory.TriviaList(SyntaxFactory.ElasticCarriageReturnLineFeed, indentTrivia);
        }
        var newToken = closeParen.WithLeadingTrivia(newLeading);
        var newRoot = root.ReplaceToken(closeParen, newToken);
        return doc.WithSyntaxRoot(newRoot);
    }
}
