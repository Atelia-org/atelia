using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Atelia.Analyzers.Style;

// MT0008: 等价于“prefer_braces=always”的语义：嵌入语句必须使用大括号；
// 与内置 IDE0011 的区别：我们的 CodeFix 只插入大括号，不主动引入换行（解耦括号与换行）。
// 覆盖 if/else/for/foreach/while/do/using/lock/fixed 的嵌入语句；else-if 不改变绑定结构。
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MT0008InlineBracesAnalyzer : DiagnosticAnalyzer {
    public const string DiagnosticId = "MT0008";
    public const string CanonicalName = "BraceRequireForEmbeddedStatement"; // DocAlias: BracesNoNewLine

    private static readonly LocalizableString Title = "Embedded statement should be enclosed in braces";
    private static readonly LocalizableString Message = "Add braces to embedded statement (no newline will be introduced)";
    private static readonly LocalizableString Description = "Require braces for embedded statements; code fix adds braces without introducing new lines.";

    private const string Category = "Brace";

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

        context.RegisterSyntaxNodeAction(AnalyzeIf, SyntaxKind.IfStatement);
        context.RegisterSyntaxNodeAction(AnalyzeElse, SyntaxKind.ElseClause);
        context.RegisterSyntaxNodeAction(AnalyzeFor, SyntaxKind.ForStatement);
        context.RegisterSyntaxNodeAction(AnalyzeForEach, SyntaxKind.ForEachStatement, SyntaxKind.ForEachVariableStatement);
        context.RegisterSyntaxNodeAction(AnalyzeWhile, SyntaxKind.WhileStatement);
        context.RegisterSyntaxNodeAction(AnalyzeDo, SyntaxKind.DoStatement);
        context.RegisterSyntaxNodeAction(AnalyzeUsing, SyntaxKind.UsingStatement);
        context.RegisterSyntaxNodeAction(AnalyzeLock, SyntaxKind.LockStatement);
        context.RegisterSyntaxNodeAction(AnalyzeFixed, SyntaxKind.FixedStatement);
    }

    private static void AnalyzeIf(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is not IfStatementSyntax ifs) return;
        // 对 else-if 中的 if 也进行体块检查（只给 if 的主体加括号，不会把结构变成 else { if (...) }）
        if (ifs.Statement is not BlockSyntax) {
            ReportAtKeyword(ctx, ifs.IfKeyword);
        }
    }

    private static void AnalyzeElse(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is not ElseClauseSyntax els) return;
        // else if ... 不处理，避免改变绑定语义
        if (els.Statement is IfStatementSyntax) return;
        if (els.Statement is not BlockSyntax) {
            ReportAtKeyword(ctx, els.ElseKeyword);
        }
    }

    private static void AnalyzeFor(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is not ForStatementSyntax f) return;
        if (f.Statement is not BlockSyntax) ReportAtKeyword(ctx, f.ForKeyword);
    }

    private static void AnalyzeForEach(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is ForEachStatementSyntax fe && fe.Statement is not BlockSyntax) {
            ReportAtKeyword(ctx, fe.ForEachKeyword);
        }
        else if (ctx.Node is ForEachVariableStatementSyntax fev && fev.Statement is not BlockSyntax) {
            ReportAtKeyword(ctx, fev.ForEachKeyword);
        }
    }

    private static void AnalyzeWhile(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is not WhileStatementSyntax w) return;
        if (w.Statement is not BlockSyntax) ReportAtKeyword(ctx, w.WhileKeyword);
    }

    private static void AnalyzeDo(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is not DoStatementSyntax d) return;
        if (d.Statement is not BlockSyntax) ReportAtKeyword(ctx, d.DoKeyword);
    }

    private static void AnalyzeUsing(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is not UsingStatementSyntax u) return;
        // 仅旧式 using (expr) statement；using 声明不在此处
        // 豁免 using 链：当主体仍是 using 语句时，不对外层 using 报诊断（与 else-if 类似的结构保留）。
        if (u.Statement is UsingStatementSyntax) return;
        if (u.Statement is not BlockSyntax) ReportAtKeyword(ctx, u.UsingKeyword);
    }

    private static void AnalyzeLock(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is not LockStatementSyntax l) return;
        if (l.Statement is not BlockSyntax) ReportAtKeyword(ctx, l.LockKeyword);
    }

    private static void AnalyzeFixed(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is not FixedStatementSyntax f) return;
        if (f.Statement is not BlockSyntax) ReportAtKeyword(ctx, f.FixedKeyword);
    }

    private static void ReportAtKeyword(SyntaxNodeAnalysisContext ctx, SyntaxToken keyword) {
        ctx.ReportDiagnostic(Diagnostic.Create(Rule, keyword.GetLocation()));
    }
}
