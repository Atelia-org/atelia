using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Atelia.Analyzers.Style;

// MT0004 / CanonicalName: NewLineClosingParenMultilineParameterList
// Rule: For multiline parameter (and argument) lists the closing parenthesis ')' must appear on its own line
// aligned with the start line indentation of the invocation / declaration. (Brace placement unchanged.)
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MT0004ClosingParenNewLineAnalyzer : DiagnosticAnalyzer {
    public const string DiagnosticId = "MT0004";
    public const string CanonicalName = "NewLineClosingParenMultilineParameterList"; // DocAlias: NewLineClosingParenParams

    private static readonly LocalizableString Title = "Closing parenthesis should be on its own line";
    private static readonly LocalizableString Message = "Place closing parenthesis of multiline parameter list on its own line";
    private static readonly LocalizableString Description = "Improves visual enclosure and diff stability by isolating the closing parenthesis of multiline parameter / argument lists.";
    private const string Category = "Formatting";

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
        if (ctx.Node is InvocationExpressionSyntax inv && inv.ArgumentList is { } args) {
            AnalyzeParenList(ctx, args.OpenParenToken, args.CloseParenToken, args.Arguments.LastOrDefault()?.GetLastToken());
        }
    }

    private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is ObjectCreationExpressionSyntax oc && oc.ArgumentList is { } args) {
            AnalyzeParenList(ctx, args.OpenParenToken, args.CloseParenToken, args.Arguments.LastOrDefault()?.GetLastToken());
        }
    }

    private static void AnalyzeMethod(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is MethodDeclarationSyntax m && m.ParameterList is { } pl && pl.Parameters.Count > 0) {
            AnalyzeParenList(ctx, pl.OpenParenToken, pl.CloseParenToken, pl.Parameters.Last().GetLastToken());
        }
    }

    private static void AnalyzeLocalFunction(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is LocalFunctionStatementSyntax l && l.ParameterList is { } pl && pl.Parameters.Count > 0) {
            AnalyzeParenList(ctx, pl.OpenParenToken, pl.CloseParenToken, pl.Parameters.Last().GetLastToken());
        }
    }

    private static void AnalyzeCtor(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is ConstructorDeclarationSyntax c && c.ParameterList is { } pl && pl.Parameters.Count > 0) {
            AnalyzeParenList(ctx, pl.OpenParenToken, pl.CloseParenToken, pl.Parameters.Last().GetLastToken());
        }
    }

    private static void AnalyzeDelegate(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is DelegateDeclarationSyntax d && d.ParameterList is { } pl && pl.Parameters.Count > 0) {
            AnalyzeParenList(ctx, pl.OpenParenToken, pl.CloseParenToken, pl.Parameters.Last().GetLastToken());
        }
    }

    private static void AnalyzeOperator(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is OperatorDeclarationSyntax o && o.ParameterList is { } pl && pl.Parameters.Count > 0) {
            AnalyzeParenList(ctx, pl.OpenParenToken, pl.CloseParenToken, pl.Parameters.Last().GetLastToken());
        }
    }

    private static void AnalyzeConversionOperator(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is ConversionOperatorDeclarationSyntax co && co.ParameterList is { } pl && pl.Parameters.Count > 0) {
            AnalyzeParenList(ctx, pl.OpenParenToken, pl.CloseParenToken, pl.Parameters.Last().GetLastToken());
        }
    }

    private static void AnalyzeRecord(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is RecordDeclarationSyntax r && r.ParameterList is { } pl && pl.Parameters.Count > 0) {
            AnalyzeParenList(ctx, pl.OpenParenToken, pl.CloseParenToken, pl.Parameters.Last().GetLastToken());
        }
    }

    private static void AnalyzeParenList(SyntaxNodeAnalysisContext ctx, SyntaxToken open, SyntaxToken close, SyntaxToken? lastItemToken) {
        if (lastItemToken == null) {
            return; // empty list or no items
        }
        var tree = open.SyntaxTree;
        if (tree is null) {
            return; // defensive: malformed token
        }
        var text = tree.GetText(ctx.CancellationToken);
        int openLine = text.Lines.GetLineFromPosition(open.SpanStart).LineNumber;
        int closeLine = text.Lines.GetLineFromPosition(close.SpanStart).LineNumber;
        if (openLine == closeLine) {
            return; // single-line list
        }
        int lastItemLine = text.Lines.GetLineFromPosition(lastItemToken.Value.Span.End).LineNumber;
        if (lastItemLine == closeLine) {
            // Closing paren shares a line with the last parameter/argument => diagnostic
            ctx.ReportDiagnostic(Diagnostic.Create(Rule, close.GetLocation()));
        }
    }
}
