using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace MemoTree.Analyzers;

// MT0003: 规范多行参数列表中参数行的缩进：
// 规则：如果调用/创建的参数列表跨越多行（即 '(' 与 ')' 不在同一行），则除首行外所有参数起始行都应当缩进为 调用起始行 + 一个 indent_size。
// 不强制一参一行；只要该行第一个参数 token 所在行缩进满足即可（同行多个参数允许）。
// 暂不处理 ')' 的换行与缩进；未来可能在 MT0004 中扩展。
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MT0003ArgumentIndentAnalyzer : DiagnosticAnalyzer {
    public const string DiagnosticId = "MT0003";
    public const string CanonicalName = "IndentMultilineParameterList"; // DocAlias: IndentMultilineParams (temporarily also covers arguments)
    private static readonly LocalizableString Title = "Multiline argument list indentation";
    private static readonly LocalizableString Message = "Parameter line indentation inconsistent (expect one indent beyond invocation start)";
    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        Title,
        Message,
        category: "Formatting",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "In multiline argument lists, each parameter line (excluding the line containing '(') must be indented exactly one level beyond the invocation line.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context) {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeObjectCreation, SyntaxKind.ObjectCreationExpression);
        // 扩展到声明(ParameterList)：方法 / 构造函数 / 本地函数 / 委托 / 运算符 / 转换 / 记录主构造
        context.RegisterSyntaxNodeAction(AnalyzeMethodDeclaration, SyntaxKind.MethodDeclaration);
        context.RegisterSyntaxNodeAction(AnalyzeLocalFunction, SyntaxKind.LocalFunctionStatement);
        context.RegisterSyntaxNodeAction(AnalyzeCtorDeclaration, SyntaxKind.ConstructorDeclaration);
        context.RegisterSyntaxNodeAction(AnalyzeDelegateDeclaration, SyntaxKind.DelegateDeclaration);
        context.RegisterSyntaxNodeAction(AnalyzeOperatorDeclaration, SyntaxKind.OperatorDeclaration);
        context.RegisterSyntaxNodeAction(AnalyzeConversionOperatorDeclaration, SyntaxKind.ConversionOperatorDeclaration);
        context.RegisterSyntaxNodeAction(AnalyzeRecordDeclaration, SyntaxKind.RecordDeclaration);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is not InvocationExpressionSyntax invoke || invoke.ArgumentList is null) {
            return;
        }

        AnalyzeArgumentList(ctx, invoke.ArgumentList, invoke.Expression.GetFirstToken());
    }

    private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is not ObjectCreationExpressionSyntax creation || creation.ArgumentList is null) {
            return;
        }

        AnalyzeArgumentList(ctx, creation.ArgumentList, creation.NewKeyword);
    }

    private static void AnalyzeMethodDeclaration(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is MethodDeclarationSyntax m && m.ParameterList is { } pl && pl.Parameters.Count > 0) {
            AnalyzeParameterList(ctx, pl, m.GetFirstToken());
        }
    }

    private static void AnalyzeLocalFunction(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is LocalFunctionStatementSyntax l && l.ParameterList is { } pl && pl.Parameters.Count > 0) {
            AnalyzeParameterList(ctx, pl, l.GetFirstToken());
        }
    }

    private static void AnalyzeCtorDeclaration(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is ConstructorDeclarationSyntax c && c.ParameterList is { } pl && pl.Parameters.Count > 0) {
            AnalyzeParameterList(ctx, pl, c.GetFirstToken());
        }
    }

    private static void AnalyzeDelegateDeclaration(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is DelegateDeclarationSyntax d && d.ParameterList is { } pl && pl.Parameters.Count > 0) {
            AnalyzeParameterList(ctx, pl, d.GetFirstToken());
        }
    }

    private static void AnalyzeOperatorDeclaration(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is OperatorDeclarationSyntax o && o.ParameterList is { } pl && pl.Parameters.Count > 0) {
            AnalyzeParameterList(ctx, pl, o.GetFirstToken());
        }
    }

    private static void AnalyzeConversionOperatorDeclaration(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is ConversionOperatorDeclarationSyntax co && co.ParameterList is { } pl && pl.Parameters.Count > 0) {
            AnalyzeParameterList(ctx, pl, co.GetFirstToken());
        }
    }

    private static void AnalyzeRecordDeclaration(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is RecordDeclarationSyntax r && r.ParameterList is { } pl && pl.Parameters.Count > 0) {
            AnalyzeParameterList(ctx, pl, r.GetFirstToken());
        }
    }

    private static void AnalyzeArgumentList(SyntaxNodeAnalysisContext ctx, ArgumentListSyntax argList, SyntaxToken invocationFirstToken) {
        if (argList.Arguments.Count == 0) {
            return;
        }

        var tree = argList.SyntaxTree;
        var text = tree.GetText(ctx.CancellationToken);
        var options = ctx.Options.AnalyzerConfigOptionsProvider.GetOptions(tree);
        int indentSize = 4;
        if (options.TryGetValue("indent_size", out var indentStr) && int.TryParse(indentStr, out var parsed) && parsed > 0) {
            indentSize = parsed;
        }

        int openLine = text.Lines.GetLineFromPosition(argList.OpenParenToken.SpanStart).LineNumber;
        int closeLine = text.Lines.GetLineFromPosition(argList.CloseParenToken.SpanStart).LineNumber;
        if (openLine == closeLine) {
            return; // 单行参数列表
        }

        // 基础缩进：调用表达式第一 token 所在行
        int invocationLine = text.Lines.GetLineFromPosition(invocationFirstToken.SpanStart).LineNumber;
        var invocationLineText = text.Lines[invocationLine].ToString();
        int baseIndent = LeadingSpaceCount(invocationLineText);
        int expected = baseIndent + indentSize;

        foreach (var arg in argList.Arguments) {
            var firstToken = arg.GetFirstToken();
            int argLine = text.Lines.GetLineFromPosition(firstToken.SpanStart).LineNumber;
            if (argLine == openLine) {
                continue; // 第一行（与 '(' 同行）跳过
            }

            var lineText = text.Lines[argLine].ToString();
            // 仅当该参数 token 是该行第一个非空白字符时才参与检查；
            // 否则说明它与前一个参数共享一行（允许的多参数同行情形），避免产生“行内缩进”误判。
            var line = text.Lines[argLine];
            int firstNonWsOffset = 0;
            while (firstNonWsOffset < lineText.Length && char.IsWhiteSpace(lineText[firstNonWsOffset])) {
                firstNonWsOffset++;
            }
            var lineStartPos = line.Start;
            if (firstToken.SpanStart != lineStartPos + firstNonWsOffset) {
                // 参数在行中部（例如前面有 "," 或其它 token），跳过。
                continue;
            }
            // 如果行内没有参数起始（例如纯注释行），再单独处理
            int actual = LeadingSpaceCount(lineText);
            if (actual != expected) {
                ctx.ReportDiagnostic(Diagnostic.Create(Rule, firstToken.GetLocation()));
            }
        }

        // 项目指导原则：跳过以注释开头的行（包括纯注释或注释+代码），不诊断也不修复。
    }

    private static void AnalyzeParameterList(SyntaxNodeAnalysisContext ctx, ParameterListSyntax plist, SyntaxToken declFirstToken) {
        var tree = plist.SyntaxTree;
        var text = tree.GetText(ctx.CancellationToken);
        int openLine = text.Lines.GetLineFromPosition(plist.OpenParenToken.SpanStart).LineNumber;
        int closeLine = text.Lines.GetLineFromPosition(plist.CloseParenToken.SpanStart).LineNumber;
        if (openLine == closeLine) {
            return; // 单行
        }

        var options = ctx.Options.AnalyzerConfigOptionsProvider.GetOptions(tree);
        int indentSize = 4;
        if (options.TryGetValue("indent_size", out var indentStr) && int.TryParse(indentStr, out var parsed) && parsed > 0) {
            indentSize = parsed;
        }

        int declLine = text.Lines.GetLineFromPosition(declFirstToken.SpanStart).LineNumber;
        var declLineText = text.Lines[declLine].ToString();
        int baseIndent = LeadingSpaceCount(declLineText);
        int expected = baseIndent + indentSize;

        foreach (var p in plist.Parameters) {
            var token = p.GetFirstToken();
            int lineNumber = text.Lines.GetLineFromPosition(token.SpanStart).LineNumber;
            if (lineNumber == openLine) {
                continue; // 与 '(' 同行
            }

            var lineText = text.Lines[lineNumber].ToString();
            int firstNonWs = 0;
            while (firstNonWs < lineText.Length && char.IsWhiteSpace(lineText[firstNonWs])) {
                firstNonWs++;
            }

            if (token.SpanStart != text.Lines[lineNumber].Start + firstNonWs) {
                continue; // 行中部（例如逗号后多个参数）
            }

            if (lineText.TrimStart().StartsWith("//") || lineText.TrimStart().StartsWith("/*")) {
                continue; // 注释行跳过
            }

            int actual = LeadingSpaceCount(lineText);
            if (actual != expected) {
                ctx.ReportDiagnostic(Diagnostic.Create(Rule, token.GetLocation()));
            }
        }
    }

    private static int LeadingSpaceCount(string s) {
        int i = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i])) {
            i++;
        }

        return i;
    }
}
