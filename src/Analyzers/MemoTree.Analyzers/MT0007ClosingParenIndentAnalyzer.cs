using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace MemoTree.Analyzers;

// MT0007 / CanonicalName: IndentClosingParenMultilineParameterList
// Rule intent: When a parameter/argument list spans multiple lines and the closing parenthesis is already
// on its own line (MT0004 satisfied), ensure that ')' is horizontally aligned with the starting anchor
// (invocation expression first token or declaration first token) indentation.
// This keeps MT0004 (NewLine domain) and MT0007 (Indent domain) orthogonal.
// Temporary scope covers both parameter lists (declarations) and argument lists (invocations/object creations).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MT0007ClosingParenIndentAnalyzer : DiagnosticAnalyzer {
    public const string DiagnosticId = "MT0007";
    public const string CanonicalName = "IndentClosingParenMultilineParameterList"; // DocAlias suggestion: ClosingParenAlign

    private static readonly LocalizableString Title = "Closing parenthesis indentation";
    private static readonly LocalizableString Message = "Closing parenthesis of multiline list must align with start";
    private static readonly LocalizableString Description = "Ensures the ')' of a multiline parameter / argument list is aligned with the construct start line indentation.";
    private const string Category = "Indent";

    public static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        Title,
        Message,
    Category,
    DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: Description);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context) {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeObjectCreation, SyntaxKind.ObjectCreationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeMethod, SyntaxKind.MethodDeclaration);
        context.RegisterSyntaxNodeAction(AnalyzeLocalFunction, SyntaxKind.LocalFunctionStatement);
        context.RegisterSyntaxNodeAction(AnalyzeCtor, SyntaxKind.ConstructorDeclaration);
        context.RegisterSyntaxNodeAction(AnalyzeDelegate, SyntaxKind.DelegateDeclaration);
        context.RegisterSyntaxNodeAction(AnalyzeOperator, SyntaxKind.OperatorDeclaration);
        context.RegisterSyntaxNodeAction(AnalyzeConversionOperator, SyntaxKind.ConversionOperatorDeclaration);
        context.RegisterSyntaxNodeAction(AnalyzeRecord, SyntaxKind.RecordDeclaration);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is InvocationExpressionSyntax inv && inv.ArgumentList is { } list && list.Arguments.Count > 0) {
            var anchor = inv.Expression.GetFirstToken();
            AnalyzeList(ctx, list.OpenParenToken, list.CloseParenToken, anchor, list.Arguments.Last().GetLastToken());
        }
    }

    private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is ObjectCreationExpressionSyntax oc && oc.ArgumentList is { } list && list.Arguments.Count > 0) {
            var anchor = oc.NewKeyword;
            AnalyzeList(ctx, list.OpenParenToken, list.CloseParenToken, anchor, list.Arguments.Last().GetLastToken());
        }
    }

    private static void AnalyzeMethod(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is MethodDeclarationSyntax m && m.ParameterList is { } pl && pl.Parameters.Count > 0) {
            AnalyzeList(ctx, pl.OpenParenToken, pl.CloseParenToken, m.GetFirstToken(), pl.Parameters.Last().GetLastToken());
        }
    }

    private static void AnalyzeLocalFunction(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is LocalFunctionStatementSyntax l && l.ParameterList is { } pl && pl.Parameters.Count > 0) {
            AnalyzeList(ctx, pl.OpenParenToken, pl.CloseParenToken, l.GetFirstToken(), pl.Parameters.Last().GetLastToken());
        }
    }

    private static void AnalyzeCtor(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is ConstructorDeclarationSyntax c && c.ParameterList is { } pl && pl.Parameters.Count > 0) {
            AnalyzeList(ctx, pl.OpenParenToken, pl.CloseParenToken, c.GetFirstToken(), pl.Parameters.Last().GetLastToken());
        }
    }

    private static void AnalyzeDelegate(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is DelegateDeclarationSyntax d && d.ParameterList is { } pl && pl.Parameters.Count > 0) {
            AnalyzeList(ctx, pl.OpenParenToken, pl.CloseParenToken, d.GetFirstToken(), pl.Parameters.Last().GetLastToken());
        }
    }

    private static void AnalyzeOperator(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is OperatorDeclarationSyntax o && o.ParameterList is { } pl && pl.Parameters.Count > 0) {
            AnalyzeList(ctx, pl.OpenParenToken, pl.CloseParenToken, o.GetFirstToken(), pl.Parameters.Last().GetLastToken());
        }
    }

    private static void AnalyzeConversionOperator(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is ConversionOperatorDeclarationSyntax co && co.ParameterList is { } pl && pl.Parameters.Count > 0) {
            AnalyzeList(ctx, pl.OpenParenToken, pl.CloseParenToken, co.GetFirstToken(), pl.Parameters.Last().GetLastToken());
        }
    }

    private static void AnalyzeRecord(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is RecordDeclarationSyntax r && r.ParameterList is { } pl && pl.Parameters.Count > 0) {
            AnalyzeList(ctx, pl.OpenParenToken, pl.CloseParenToken, r.GetFirstToken(), pl.Parameters.Last().GetLastToken());
        }
    }

    private static void AnalyzeList(SyntaxNodeAnalysisContext ctx, SyntaxToken open, SyntaxToken close, SyntaxToken anchorToken, SyntaxToken lastItemToken) {
        var tree = open.SyntaxTree;
        if (tree == null) {
            return;
        }

        var text = tree.GetText(ctx.CancellationToken);
        int openLine = text.Lines.GetLineFromPosition(open.SpanStart).LineNumber;
        int closeLine = text.Lines.GetLineFromPosition(close.SpanStart).LineNumber;
        if (openLine == closeLine) {
            return; // single-line list => out of scope
        }

        // If close paren shares line with last item => MT0004 handles newline responsibility; skip here.
        int lastItemLine = text.Lines.GetLineFromPosition(lastItemToken.Span.End).LineNumber;
        if (lastItemLine == closeLine) {
            return;
        }

        // Compute indentation column of anchor line.
        var anchorLine = text.Lines.GetLineFromPosition(anchorToken.SpanStart).LineNumber;
        var anchorLineText = text.Lines[anchorLine].ToString();
        int anchorIndent = LeadingWhitespaceWidth(anchorLineText);

        var closeLineText = text.Lines[closeLine].ToString();
        int closeIndent = LeadingWhitespaceWidth(closeLineText);
        if (closeIndent != anchorIndent) {
            ctx.ReportDiagnostic(Diagnostic.Create(Rule, close.GetLocation()));
        }
    }

    private static int LeadingWhitespaceWidth(string line) {
        int i = 0;
        while (i < line.Length && char.IsWhiteSpace(line[i])) {
            i++;
        }

        return i;
    }
}
