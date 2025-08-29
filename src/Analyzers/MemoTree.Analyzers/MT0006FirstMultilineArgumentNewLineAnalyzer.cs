using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace MemoTree.Analyzers;

// MT0006 / CanonicalName: NewLineFirstMultilineArgument
// Rule: The first multiline argument (one whose span crosses multiple lines) must start on a new line
//       rather than being inlined on the same line as '('. Later multiline arguments are ignored (strategy B′ minimal intervention).
// DocAlias: FirstMultilineArgNewLine
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MT0006FirstMultilineArgumentNewLineAnalyzer : DiagnosticAnalyzer {
    public const string DiagnosticId = "MT0006";
    public const string CanonicalName = "NewLineFirstMultilineArgument"; // See NamingConvention.md
    public const string DocAlias = "FirstMultilineArgNewLine";

    private static readonly LocalizableString Title = "First multiline argument should start on a new line";
    private static readonly LocalizableString Message = "Move first multiline argument to its own line";
    private static readonly LocalizableString Description = "Establishes a single structural newline anchor after '(' improving visual parsing and diff stability while leaving later multiline arguments untouched.";
    private const string Category = "NewLine"; // Chosen dominant dimension per naming convention.

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        Title,
        Message,
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: Description);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context) {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeObjectCreation, SyntaxKind.ObjectCreationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is InvocationExpressionSyntax inv && inv.ArgumentList is { } list && list.Arguments.Count > 0) {
            AnalyzeArgumentList(ctx, list);
        }
    }

    private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is ObjectCreationExpressionSyntax oc && oc.ArgumentList is { } list && list.Arguments.Count > 0) {
            AnalyzeArgumentList(ctx, list);
        }
    }

    private static void AnalyzeArgumentList(SyntaxNodeAnalysisContext ctx, ArgumentListSyntax list) {
        var args = list.Arguments;
        var text = list.SyntaxTree.GetText(ctx.CancellationToken);
        int openLine = text.Lines.GetLineFromPosition(list.OpenParenToken.SpanStart).LineNumber;

        // Find first multiline argument
        ArgumentSyntax? firstMultiline = null;
        foreach (var arg in args) {
            if (IsMultiline(arg, text)) { firstMultiline = arg; break; }
        }
        if (firstMultiline == null) return; // no multiline => nothing

        var firstTok = firstMultiline.GetFirstToken();
        int firstTokLine = text.Lines.GetLineFromPosition(firstTok.SpanStart).LineNumber;
        if (firstTokLine == openLine) {
            ctx.ReportDiagnostic(Diagnostic.Create(Rule, firstTok.GetLocation()));
        }
    }

    private static bool IsMultiline(ArgumentSyntax arg, SourceText text) {
        var first = arg.GetFirstToken();
        var last = arg.GetLastToken();
        int startLine = text.Lines.GetLineFromPosition(first.SpanStart).LineNumber;
        int endLine = text.Lines.GetLineFromPosition(last.Span.End).LineNumber;
        return startLine != endLine;
    }
}
