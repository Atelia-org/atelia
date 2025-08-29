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

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(MT0006FirstMultilineArgumentNewLineCodeFix))]
[Shared]
public sealed class MT0006FirstMultilineArgumentNewLineCodeFix : CodeFixProvider {
    private const string DiagnosticId = "MT0006";
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(DiagnosticId);
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context) {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root == null) {
            return;
        }
        var diag = context.Diagnostics.First();
        var firstToken = root.FindToken(diag.Location.SourceSpan.Start);
        var arg = firstToken.Parent?.FirstAncestorOrSelf<ArgumentSyntax>();
        if (arg == null) {
            return;
        }
        var argumentList = arg.Parent as ArgumentListSyntax;
        if (argumentList == null) {
            return;
        }
        context.RegisterCodeFix(
            CodeAction.Create(
                title: "Move first multiline argument to new line",
                createChangedDocument: ct => FixAsync(context.Document, root, argumentList, arg, ct),
                equivalenceKey: "MoveFirstMultilineArgNewLine"),
            diag);
    }

    private static async Task<Document> FixAsync(Document doc, SyntaxNode root, ArgumentListSyntax list, ArgumentSyntax arg, CancellationToken ct) {
        var open = list.OpenParenToken;
        var indentTrivia = await IndentationHelper.ComputeIndentTriviaAsync(doc, open, 1, ct).ConfigureAwait(false);
        var firstTok = arg.GetFirstToken();

        var text = await doc.GetTextAsync(ct).ConfigureAwait(false);
        var openLine = text.Lines.GetLineFromPosition(open.SpanStart).LineNumber;
        var argLine = text.Lines.GetLineFromPosition(firstTok.SpanStart).LineNumber;
        if (argLine != openLine) {
            return doc; // already fixed
        }

        var leading = firstTok.LeadingTrivia;
        var newLeading = leading.Insert(0, indentTrivia).Insert(0, SyntaxFactory.ElasticCarriageReturnLineFeed);
        var newFirstTok = firstTok.WithLeadingTrivia(newLeading);
        var newRoot = root.ReplaceToken(firstTok, newFirstTok);
        return doc.WithSyntaxRoot(newRoot);
    }
}
