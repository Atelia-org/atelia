using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Atelia.Analyzers.Style;

// MT0005: Require newline immediately after '(' when a parameter/argument list spans multiple lines
// (Pure symmetric opening rule). Disabled by default; pairs with MT0004 (closing paren on its own line).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MT0005OpenParenNewLineAnalyzer : DiagnosticAnalyzer {
    public const string DiagnosticId = "MT0005";
    public const string CanonicalName = "NewLineAfterOpenParenMultilineList"; // See NamingConvention.md
    public const string DocAlias = "OpenParenNewLine"; // Short alias used in docs/examples
    private static readonly LocalizableString Title = "Place newline after '(' for multiline list";
    private static readonly LocalizableString Message = "Add newline after '(' for multiline parameter/argument list";
    // Category kept consistent with release tracking table (Formatting) to avoid RS2001 mismatches.
    private const string Category = "Formatting";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        Title,
        Message,
        Category,
        DiagnosticSeverity.Info,
        isEnabledByDefault: false); // opt-in only

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
        context.RegisterSyntaxNodeAction(AnalyzeLambda, SyntaxKind.ParenthesizedLambdaExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is InvocationExpressionSyntax inv && inv.ArgumentList is { } list && list.Arguments.Count > 0) {
            AnalyzeList(ctx, list.OpenParenToken, list.CloseParenToken, list.Arguments.First().GetFirstToken());
        }
    }
    private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is ObjectCreationExpressionSyntax oc && oc.ArgumentList is { } list && list.Arguments.Count > 0) {
            AnalyzeList(ctx, list.OpenParenToken, list.CloseParenToken, list.Arguments.First().GetFirstToken());
        }
    }
    private static void AnalyzeMethod(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is MethodDeclarationSyntax m && m.ParameterList is { } pl && pl.Parameters.Count > 0) {
            AnalyzeList(ctx, pl.OpenParenToken, pl.CloseParenToken, pl.Parameters.First().GetFirstToken());
        }
    }
    private static void AnalyzeLocalFunction(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is LocalFunctionStatementSyntax l && l.ParameterList is { } pl && pl.Parameters.Count > 0) {
            AnalyzeList(ctx, pl.OpenParenToken, pl.CloseParenToken, pl.Parameters.First().GetFirstToken());
        }
    }
    private static void AnalyzeCtor(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is ConstructorDeclarationSyntax c && c.ParameterList is { } pl && pl.Parameters.Count > 0) {
            AnalyzeList(ctx, pl.OpenParenToken, pl.CloseParenToken, pl.Parameters.First().GetFirstToken());
        }
    }
    private static void AnalyzeDelegate(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is DelegateDeclarationSyntax d && d.ParameterList is { } pl && pl.Parameters.Count > 0) {
            AnalyzeList(ctx, pl.OpenParenToken, pl.CloseParenToken, pl.Parameters.First().GetFirstToken());
        }
    }
    private static void AnalyzeOperator(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is OperatorDeclarationSyntax o && o.ParameterList is { } pl && pl.Parameters.Count > 0) {
            AnalyzeList(ctx, pl.OpenParenToken, pl.CloseParenToken, pl.Parameters.First().GetFirstToken());
        }
    }
    private static void AnalyzeConversionOperator(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is ConversionOperatorDeclarationSyntax co && co.ParameterList is { } pl && pl.Parameters.Count > 0) {
            AnalyzeList(ctx, pl.OpenParenToken, pl.CloseParenToken, pl.Parameters.First().GetFirstToken());
        }
    }
    private static void AnalyzeRecord(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is RecordDeclarationSyntax r && r.ParameterList is { } pl && pl.Parameters.Count > 0) {
            AnalyzeList(ctx, pl.OpenParenToken, pl.CloseParenToken, pl.Parameters.First().GetFirstToken());
        }
    }
    private static void AnalyzeLambda(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is ParenthesizedLambdaExpressionSyntax l && l.ParameterList is { } pl && pl.Parameters.Count > 0) {
            AnalyzeList(ctx, pl.OpenParenToken, pl.CloseParenToken, pl.Parameters.First().GetFirstToken());
        }
    }

    private static void AnalyzeList(
        SyntaxNodeAnalysisContext ctx,
        SyntaxToken open,
        SyntaxToken close,
        SyntaxToken firstItemToken) {
        var tree = open.SyntaxTree;
        if (tree is null) {
            return; // defensive
        }
        var text = tree.GetText(ctx.CancellationToken);
        var openLine = text.Lines.GetLineFromPosition(open.SpanStart).LineNumber;
        var closeLine = text.Lines.GetLineFromPosition(close.SpanStart).LineNumber;
        if (openLine == closeLine) {
            return; // single-line
        }
        if (open.TrailingTrivia.Any(t => t.IsKind(SyntaxKind.EndOfLineTrivia))) {
            return; // already has newline after '('
        }
        var firstLine = text.Lines.GetLineFromPosition(firstItemToken.SpanStart).LineNumber;
        if (firstLine == openLine) {
            ctx.ReportDiagnostic(Diagnostic.Create(Rule, open.GetLocation()));
        }
    }
}
