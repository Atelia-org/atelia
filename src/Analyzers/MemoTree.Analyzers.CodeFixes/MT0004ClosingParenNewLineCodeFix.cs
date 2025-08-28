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

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(MT0004ClosingParenNewLineCodeFix))]
[Shared]
public sealed class MT0004ClosingParenNewLineCodeFix : CodeFixProvider {
    private const string DiagnosticId = "MT0004"; // Duplicate to avoid cross-assembly type reference
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(DiagnosticId);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context) {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root == null) {
            return;
        }
        var diag = context.Diagnostics.First();
        var token = root.FindToken(diag.Location.SourceSpan.Start);
        if (token.IsKind(SyntaxKind.CloseParenToken)) {
            context.RegisterCodeFix(
                CodeAction.Create(
                    title: "Move ')' to new line",
                    createChangedDocument: ct => FixAsync(context.Document, root, token, ct),
                    equivalenceKey: "MoveCloseParenNewLine"),
                diag);
        }
    }

    private static async Task<Document> FixAsync(Document doc, SyntaxNode root, SyntaxToken closeParen, CancellationToken ct) {
        // Find its matching open paren ancestor list node
        var parent = closeParen.Parent;
        if (parent == null) {
            return doc;
        }

        // Determine base indentation by locating the first token of the construct (invocation/declaration)
        SyntaxToken anchorToken = parent switch {
            ArgumentListSyntax argList => argList.Parent switch {
                InvocationExpressionSyntax inv => inv.Expression.GetFirstToken(),
                ObjectCreationExpressionSyntax oc => oc.NewKeyword,
                _ => argList.OpenParenToken
            },
            ParameterListSyntax paramList => paramList.Parent?.GetFirstToken() ?? paramList.OpenParenToken,
            _ => closeParen
        };

        var text = await doc.GetTextAsync(ct).ConfigureAwait(false);
        var indentTrivia = await IndentationHelper.ComputeIndentTriviaAsync(doc, anchorToken, 0, ct).ConfigureAwait(false);

        // If already on its own line, no-op
        var closeLine = text.Lines.GetLineFromPosition(closeParen.SpanStart).LineNumber;
        // Compute last non-whitespace token before close on the same line to detect inline case
        var previousToken = closeParen.GetPreviousToken();
        if (!previousToken.IsKind(SyntaxKind.None)) {
            var prevLine = text.Lines.GetLineFromPosition(previousToken.Span.End).LineNumber;
            if (prevLine != closeLine) {
                return doc; // already alone
            }
        }

        // Insert a newline + indent before closeParen preserving trailing trivia
        var newClose = closeParen.WithLeadingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed, indentTrivia);

        // Ensure following '{' (if any) stays on same line: replace its leading trivia with a single space
        var next = closeParen.GetNextToken();
        SyntaxNode newRoot;
        if (next.IsKind(SyntaxKind.OpenBraceToken)) {
            // Remove any leading trivia so '{' immediately follows ')'
            var adjustedNext = next.WithLeadingTrivia();
            newRoot = root.ReplaceTokens(new[] { closeParen, next }, (orig, _) => orig == closeParen ? newClose : adjustedNext);
        } else {
            newRoot = root.ReplaceToken(closeParen, newClose);
        }
        return doc.WithSyntaxRoot(newRoot);
    }
}
