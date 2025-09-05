using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Atelia.Analyzers.Style;

// Experimental: Only enforce closing parenthesis newline if the '(' was already followed by a newline ("Scheme B").
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class X0002ConditionalCloseParenAnalyzer : DiagnosticAnalyzer {
    public const string DiagnosticId = "X0002"; // Experimental ID
    private static readonly LocalizableString Title = ") should be on its own line (conditional)";
    private static readonly LocalizableString Message = "Place ')' on its own line to mirror opening newline (experimental)";
    private const string Category = "Formatting.Experimental";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        Title,
        Message,
        Category,
        DiagnosticSeverity.Info,
        isEnabledByDefault: true); // enable for experimental evaluation

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
        if (ctx.Node is InvocationExpressionSyntax inv && inv.ArgumentList is { } args) {
            AnalyzeList(ctx, args.OpenParenToken, args.CloseParenToken, args.Arguments.LastOrDefault()?.GetLastToken());
        }
    }
    private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is ObjectCreationExpressionSyntax oc && oc.ArgumentList is { } args) {
            AnalyzeList(ctx, args.OpenParenToken, args.CloseParenToken, args.Arguments.LastOrDefault()?.GetLastToken());
        }
    }
    private static void AnalyzeMethod(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is MethodDeclarationSyntax m && m.ParameterList is { } pl && pl.Parameters.Count > 0) {
            AnalyzeList(ctx, pl.OpenParenToken, pl.CloseParenToken, pl.Parameters.Last().GetLastToken());
        }
    }
    private static void AnalyzeLocalFunction(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is LocalFunctionStatementSyntax l && l.ParameterList is { } pl && pl.Parameters.Count > 0) {
            AnalyzeList(ctx, pl.OpenParenToken, pl.CloseParenToken, pl.Parameters.Last().GetLastToken());
        }
    }
    private static void AnalyzeCtor(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is ConstructorDeclarationSyntax c && c.ParameterList is { } pl && pl.Parameters.Count > 0) {
            AnalyzeList(ctx, pl.OpenParenToken, pl.CloseParenToken, pl.Parameters.Last().GetLastToken());
        }
    }
    private static void AnalyzeDelegate(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is DelegateDeclarationSyntax d && d.ParameterList is { } pl && pl.Parameters.Count > 0) {
            AnalyzeList(ctx, pl.OpenParenToken, pl.CloseParenToken, pl.Parameters.Last().GetLastToken());
        }
    }
    private static void AnalyzeOperator(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is OperatorDeclarationSyntax o && o.ParameterList is { } pl && pl.Parameters.Count > 0) {
            AnalyzeList(ctx, pl.OpenParenToken, pl.CloseParenToken, pl.Parameters.Last().GetLastToken());
        }
    }
    private static void AnalyzeConversionOperator(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is ConversionOperatorDeclarationSyntax co && co.ParameterList is { } pl && pl.Parameters.Count > 0) {
            AnalyzeList(ctx, pl.OpenParenToken, pl.CloseParenToken, pl.Parameters.Last().GetLastToken());
        }
    }
    private static void AnalyzeRecord(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is RecordDeclarationSyntax r && r.ParameterList is { } pl && pl.Parameters.Count > 0) {
            AnalyzeList(ctx, pl.OpenParenToken, pl.CloseParenToken, pl.Parameters.Last().GetLastToken());
        }
    }

    private static void AnalyzeList(SyntaxNodeAnalysisContext ctx, SyntaxToken open, SyntaxToken close, SyntaxToken? lastItem) {
        if (lastItem == null) {
            return;
        }
        var tree = open.SyntaxTree;
        if (tree is null) {
            return; // defensive: malformed token
        }
        var text = tree.GetText(ctx.CancellationToken);
        var openLine = text.Lines.GetLineFromPosition(open.SpanStart).LineNumber;
        var closeLine = text.Lines.GetLineFromPosition(close.SpanStart).LineNumber;
        if (openLine == closeLine) {
            return; // single-line
        }
        // Only enforce if '(' is followed immediately by newline (no inline items)
        if (!open.TrailingTrivia.ToFullString().Contains("\n")) {
            return; // scheme B condition not met
        }

        var lastLine = text.Lines.GetLineFromPosition(lastItem.Value.Span.End).LineNumber;
        if (lastLine == closeLine) {
            ctx.ReportDiagnostic(Diagnostic.Create(Rule, close.GetLocation()));
        }
    }
}
