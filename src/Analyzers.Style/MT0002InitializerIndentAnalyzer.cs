using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Atelia.Analyzers.Style;

// MT0002: 规范对象 / 集合 / 数组初始化器的元素缩进：
// 规则：多行初始化器中，每个元素应当位于独立行，其缩进 = 初始化器 '{' 所在行缩进 + 一个 indent_size (默认4)。
// 如果元素顶格（与 '{' 对齐）或缩进层级不一致，则报告诊断。
// 设计目的：
//  - 稳定 diff（新增/删除元素时最小行影响）
//  - 避免历史“顶格元素”遗留造成视觉噪声
//  - 为后续 trailing comma / 排序规则打基础
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MT0002InitializerIndentAnalyzer : DiagnosticAnalyzer {
    public const string DiagnosticId = "MT0002";
    public const string CanonicalName = "IndentInitializerElements"; // DocAlias: IndentInitializers
    private static readonly LocalizableString Title = "Initializer indentation inconsistent";
    private static readonly LocalizableString Message = "Initializer element indentation inconsistent";
    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        Title,
        Message,
        category: "Formatting",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Elements inside multi-line object / collection / array initializers must be indented exactly one level from the line containing '{'.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context) {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ObjectInitializerExpression, SyntaxKind.CollectionInitializerExpression, SyntaxKind.ArrayInitializerExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context) {
        if (context.Node is not InitializerExpressionSyntax init || init.Expressions.Count == 0) {
            return;
        }

        var tree = init.SyntaxTree;
        var text = tree.GetText(context.CancellationToken);
        var options = context.Options.AnalyzerConfigOptionsProvider.GetOptions(tree);
        int indentSize = 4;
        if (options.TryGetValue("indent_size", out var indentStr) && int.TryParse(indentStr, out var parsed) && parsed > 0) {
            indentSize = parsed;
        }

        int openLine = text.Lines.GetLineFromPosition(init.OpenBraceToken.SpanStart).LineNumber;
        var openLineText = text.Lines[openLine].ToString();
        int baseIndent = LeadingSpaceCount(openLineText);
        int expected = baseIndent + indentSize;

        foreach (var expr in init.Expressions) {
            var firstToken = expr.GetFirstToken();
            int lineNumber = text.Lines.GetLineFromPosition(firstToken.SpanStart).LineNumber;
            // 同行（紧凑单行）不处理，让别的规则/人工控制
            if (lineNumber == openLine) {
                continue;
            }

            var lineText = text.Lines[lineNumber].ToString();
            int actual = LeadingSpaceCount(lineText);
            if (actual != expected) {
                context.ReportDiagnostic(Diagnostic.Create(Rule, expr.GetLocation()));
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
