using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.Diagnostics;

namespace MemoTree.Analyzers;

// Experimental: Enforce newline immediately after '(' for multiline parameter / argument lists.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class X0001OpenParenNewLineAnalyzer : DiagnosticAnalyzer {
    public const string DiagnosticId = "X0001"; // Experimental ID
    private static readonly LocalizableString Title = "Place newline after '(' for multiline list";
    private static readonly LocalizableString Message = "Add newline after '(' to mirror closing parenthesis layout (experimental)";
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
        if (ctx.Node is InvocationExpressionSyntax inv && inv.ArgumentList is { } list) AnalyzeList(ctx, list.OpenParenToken, list.CloseParenToken, list.Arguments.FirstOrDefault()?.GetFirstToken());
    }
    private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is ObjectCreationExpressionSyntax oc && oc.ArgumentList is { } list) AnalyzeList(ctx, list.OpenParenToken, list.CloseParenToken, list.Arguments.FirstOrDefault()?.GetFirstToken());
    }
    private static void AnalyzeMethod(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is MethodDeclarationSyntax m && m.ParameterList is { } pl && pl.Parameters.Count > 0) AnalyzeList(ctx, pl.OpenParenToken, pl.CloseParenToken, pl.Parameters.First().GetFirstToken());
    }
    private static void AnalyzeLocalFunction(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is LocalFunctionStatementSyntax l && l.ParameterList is { } pl && pl.Parameters.Count > 0) AnalyzeList(ctx, pl.OpenParenToken, pl.CloseParenToken, pl.Parameters.First().GetFirstToken());
    }
    private static void AnalyzeCtor(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is ConstructorDeclarationSyntax c && c.ParameterList is { } pl && pl.Parameters.Count > 0) AnalyzeList(ctx, pl.OpenParenToken, pl.CloseParenToken, pl.Parameters.First().GetFirstToken());
    }
    private static void AnalyzeDelegate(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is DelegateDeclarationSyntax d && d.ParameterList is { } pl && pl.Parameters.Count > 0) AnalyzeList(ctx, pl.OpenParenToken, pl.CloseParenToken, pl.Parameters.First().GetFirstToken());
    }
    private static void AnalyzeOperator(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is OperatorDeclarationSyntax o && o.ParameterList is { } pl && pl.Parameters.Count > 0) AnalyzeList(ctx, pl.OpenParenToken, pl.CloseParenToken, pl.Parameters.First().GetFirstToken());
    }
    private static void AnalyzeConversionOperator(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is ConversionOperatorDeclarationSyntax co && co.ParameterList is { } pl && pl.Parameters.Count > 0) AnalyzeList(ctx, pl.OpenParenToken, pl.CloseParenToken, pl.Parameters.First().GetFirstToken());
    }
    private static void AnalyzeRecord(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is RecordDeclarationSyntax r && r.ParameterList is { } pl && pl.Parameters.Count > 0) AnalyzeList(ctx, pl.OpenParenToken, pl.CloseParenToken, pl.Parameters.First().GetFirstToken());
    }

    private static void AnalyzeList(SyntaxNodeAnalysisContext ctx, SyntaxToken open, SyntaxToken close, SyntaxToken? firstItem) {
        if (firstItem == null) return;
        var text = open.SyntaxTree.GetText(ctx.CancellationToken);
        var openLine = text.Lines.GetLineFromPosition(open.SpanStart).LineNumber;
        var closeLine = text.Lines.GetLineFromPosition(close.SpanStart).LineNumber;
        if (openLine == closeLine) return; // single-line list
        // Already newline just after '('?
        if (open.TrailingTrivia.Any(t => t.Kind() == SyntaxKind.EndOfLineTrivia)) return; // compliant
        var firstLine = text.Lines.GetLineFromPosition(firstItem.Value.SpanStart).LineNumber;
        if (firstLine == openLine) {
            ctx.ReportDiagnostic(Diagnostic.Create(Rule, open.GetLocation()));
        }
    }
}
