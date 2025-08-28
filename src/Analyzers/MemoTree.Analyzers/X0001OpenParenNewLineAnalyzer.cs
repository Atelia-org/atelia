using System.Collections.Immutable;
using System.Collections.Generic;
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

    // Development-time switch: when true, even if every item is single-line we still require
    // a newline if any item contains a block body (lambda with { }) or an initializer expression.
    // Default false = pure AllItemsSingleLine exemption (Guard A only).
    // private const bool GUARD_BLOCK_OR_INITIALIZER = false; // flip to true to evaluate Guard B
    private const bool GUARD_BLOCK_OR_INITIALIZER = true; // flip to false to evaluate Guard A

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
        context.RegisterSyntaxNodeAction(AnalyzeLambda, SyntaxKind.ParenthesizedLambdaExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is InvocationExpressionSyntax inv && inv.ArgumentList is { } list && list.Arguments.Count > 0) {
            AnalyzeList(ctx, list.OpenParenToken, list.CloseParenToken, list.Arguments.First().GetFirstToken(), list.Arguments.Select(a => (SyntaxNode)a));
        }
    }
    private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is ObjectCreationExpressionSyntax oc && oc.ArgumentList is { } list && list.Arguments.Count > 0) {
            AnalyzeList(ctx, list.OpenParenToken, list.CloseParenToken, list.Arguments.First().GetFirstToken(), list.Arguments.Select(a => (SyntaxNode)a));
        }
    }
    private static void AnalyzeMethod(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is MethodDeclarationSyntax m && m.ParameterList is { } pl && pl.Parameters.Count > 0) {
            AnalyzeList(ctx, pl.OpenParenToken, pl.CloseParenToken, pl.Parameters.First().GetFirstToken(), pl.Parameters.Select(p => (SyntaxNode)p));
        }
    }
    private static void AnalyzeLocalFunction(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is LocalFunctionStatementSyntax l && l.ParameterList is { } pl && pl.Parameters.Count > 0) {
            AnalyzeList(ctx, pl.OpenParenToken, pl.CloseParenToken, pl.Parameters.First().GetFirstToken(), pl.Parameters.Select(p => (SyntaxNode)p));
        }
    }
    private static void AnalyzeCtor(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is ConstructorDeclarationSyntax c && c.ParameterList is { } pl && pl.Parameters.Count > 0) {
            AnalyzeList(ctx, pl.OpenParenToken, pl.CloseParenToken, pl.Parameters.First().GetFirstToken(), pl.Parameters.Select(p => (SyntaxNode)p));
        }
    }
    private static void AnalyzeDelegate(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is DelegateDeclarationSyntax d && d.ParameterList is { } pl && pl.Parameters.Count > 0) {
            AnalyzeList(ctx, pl.OpenParenToken, pl.CloseParenToken, pl.Parameters.First().GetFirstToken(), pl.Parameters.Select(p => (SyntaxNode)p));
        }
    }
    private static void AnalyzeOperator(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is OperatorDeclarationSyntax o && o.ParameterList is { } pl && pl.Parameters.Count > 0) {
            AnalyzeList(ctx, pl.OpenParenToken, pl.CloseParenToken, pl.Parameters.First().GetFirstToken(), pl.Parameters.Select(p => (SyntaxNode)p));
        }
    }
    private static void AnalyzeConversionOperator(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is ConversionOperatorDeclarationSyntax co && co.ParameterList is { } pl && pl.Parameters.Count > 0) {
            AnalyzeList(ctx, pl.OpenParenToken, pl.CloseParenToken, pl.Parameters.First().GetFirstToken(), pl.Parameters.Select(p => (SyntaxNode)p));
        }
    }
    private static void AnalyzeRecord(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is RecordDeclarationSyntax r && r.ParameterList is { } pl && pl.Parameters.Count > 0) {
            AnalyzeList(ctx, pl.OpenParenToken, pl.CloseParenToken, pl.Parameters.First().GetFirstToken(), pl.Parameters.Select(p => (SyntaxNode)p));
        }
    }
    private static void AnalyzeLambda(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is ParenthesizedLambdaExpressionSyntax l && l.ParameterList is { } pl && pl.Parameters.Count > 0) {
            AnalyzeList(ctx, pl.OpenParenToken, pl.CloseParenToken, pl.Parameters.First().GetFirstToken(), pl.Parameters.Select(p => (SyntaxNode)p));
        }
    }

    private static void AnalyzeList(
        SyntaxNodeAnalysisContext ctx,
        SyntaxToken open,
        SyntaxToken close,
        SyntaxToken firstItemToken,
        IEnumerable<SyntaxNode> items
    ) {
        var text = open.SyntaxTree.GetText(ctx.CancellationToken);
        var openLine = text.Lines.GetLineFromPosition(open.SpanStart).LineNumber;
        var closeLine = text.Lines.GetLineFromPosition(close.SpanStart).LineNumber;
        if (openLine == closeLine) {
            return; // single-line list overall
        }
        // Already newline just after '('? (there is at least one EndOfLine trivia directly trailing)
        if (open.TrailingTrivia.Any(t => t.IsKind(SyntaxKind.EndOfLineTrivia))) {
            return; // compliant already
        }

        // Exemption: all items individually occupy a single source line (PURE mode)
        if (AllItemsSingleLine(items, text)) {
            if (GUARD_BLOCK_OR_INITIALIZER) {
                // Guard B: if any item structurally owns a block body or initializer, we still enforce.
                if (!ContainsBlockOrInitializer(items)) {
                    return; // exempt
                }
            } else {
                return; // exempt in pure mode
            }
        }

        var firstLine = text.Lines.GetLineFromPosition(firstItemToken.SpanStart).LineNumber;
        if (firstLine == openLine) {
            ctx.ReportDiagnostic(Diagnostic.Create(Rule, open.GetLocation()));
        }
    }

    private static bool AllItemsSingleLine(IEnumerable<SyntaxNode> items, SourceText text) {
        foreach (var item in items) {
            if (item == null) {
                continue;
            }

            var firstTok = item.GetFirstToken();
            var lastTok = item.GetLastToken();
            var startLine = text.Lines.GetLineFromPosition(firstTok.SpanStart).LineNumber;
            var endLine = text.Lines.GetLineFromPosition(lastTok.Span.End).LineNumber;
            if (startLine != endLine) {
                return false;
            }
        }
        return true;
    }

    private static bool ContainsBlockOrInitializer(IEnumerable<SyntaxNode> items) {
        foreach (var item in items) {
            if (item == null) {
                continue;
            }
            // ArgumentSyntax -> inspect argument.Expression; ParameterSyntax usually simple; fall back to descendant search.
            SyntaxNode inspect = item is ArgumentSyntax a && a.Expression != null ? (SyntaxNode)a.Expression : item;
            if (inspect == null) {
                continue;
            }

            if (inspect.DescendantNodesAndSelf().Any(n => n is BlockSyntax || n is InitializerExpressionSyntax)) {
                return true;
            }
        }
        return false;
    }
}
