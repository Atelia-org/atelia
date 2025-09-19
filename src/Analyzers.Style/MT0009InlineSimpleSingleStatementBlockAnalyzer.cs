using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Atelia.Analyzers.Style;

// MT0009: 折叠仅含一条“简单语句”的 if/else 块为单行内联块（删除块内部换行，保持注释与语义）。
// v1 保守版：仅 if/else；块内只有一条语句；语句为 return/break/continue/throw 或 ++/-- 表达式；
// 语句单物理行；花括号与语句之间无注释/指令（允许空白与至多一个换行）；块内无预处理指令。
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MT0009InlineSimpleSingleStatementBlockAnalyzer : DiagnosticAnalyzer {
    public const string DiagnosticId = "MT0009";
    public const string CanonicalName = "InlineSimpleSingleStatementBlock"; // DocAlias: CompactSingleStatementBlocks

    private static readonly LocalizableString Title = "Block with single simple statement can be compacted inline";
    private static readonly LocalizableString Message = "Compact single-statement block into inline form";
    private static readonly LocalizableString Description = "Collapse if/else blocks containing exactly one simple statement into a single-line inline block to reduce low-information lines.";

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
    }

    private static void AnalyzeIf(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is not IfStatementSyntax ifs) return;
        if (ifs.Statement is not BlockSyntax block) return;
        if (!IsEligibleSingleSimpleBlock(ctx, block)) return;
        // 报在 '{' 位置，便于 CodeFix 替换块内部布局
        var open = block.OpenBraceToken;
        if (open.IsMissing) return;
        ctx.ReportDiagnostic(Diagnostic.Create(Rule, open.GetLocation()));
    }

    private static void AnalyzeElse(SyntaxNodeAnalysisContext ctx) {
        if (ctx.Node is not ElseClauseSyntax els) return;
        if (els.Statement is IfStatementSyntax) return; // else-if 不在本规则范围
        if (els.Statement is not BlockSyntax block) return;
        if (!IsEligibleSingleSimpleBlock(ctx, block)) return;
        var open = block.OpenBraceToken;
        if (open.IsMissing) return;
        ctx.ReportDiagnostic(Diagnostic.Create(Rule, open.GetLocation()));
    }

    private static bool IsEligibleSingleSimpleBlock(SyntaxNodeAnalysisContext ctx, BlockSyntax block) {
        // 仅一条语句
        if (block.Statements.Count != 1) return false;

        // 块内不得含预处理指令
        if (block.DescendantTrivia().Any(t => t.IsDirective)) return false;

        var stmt = block.Statements[0];

        // 语句类型白名单
        if (!IsAllowedSimpleStatement(stmt)) return false;

        // 语句单物理行（first/last token 在同一行）
    // 使用语句自身的 Span（不含 trailing trivia）来判断单物理行，避免因行尾注释或换行被误判。
    var stmtLineSpan = stmt.GetLocation().GetLineSpan();
    if (stmtLineSpan.StartLinePosition.Line != stmtLineSpan.EndLinePosition.Line) return false;

        // “{ 与语句首 token 之间”与“语句末 token 与 } 之间”不得出现注释/指令；允许空白与至多一个换行。
        var open = block.OpenBraceToken;
        var close = block.CloseBraceToken;
        if (open.IsMissing || close.IsMissing) return false;

    if (!IsTriviallyCompactableLeading(open.TrailingTrivia, stmt.GetFirstToken(includeZeroWidth: true).LeadingTrivia)) return false;
    if (!IsTriviallyCompactableTrailing(stmt.GetLastToken(includeZeroWidth: true).TrailingTrivia, close.LeadingTrivia)) return false;

        return true;
    }

    private static bool IsAllowedSimpleStatement(StatementSyntax stmt) {
        switch (stmt) {
            case ReturnStatementSyntax ret:
                // v1：先允许 return; 与 return expr; 都可由配置控制；为保守起见，这里允许无表达式或有表达式都先视为可选，后续可由 CodeFix 决定是否受配置限制。
                return true;
            case BreakStatementSyntax:
            case ContinueStatementSyntax:
            case ThrowStatementSyntax:
                return true;
            case ExpressionStatementSyntax es:
                return es.Expression is PrefixUnaryExpressionSyntax p && (p.IsKind(SyntaxKind.PreIncrementExpression) || p.IsKind(SyntaxKind.PreDecrementExpression))
                    || es.Expression is PostfixUnaryExpressionSyntax s && (s.IsKind(SyntaxKind.PostIncrementExpression) || s.IsKind(SyntaxKind.PostDecrementExpression));
            default:
                return false;
        }
    }

    private static bool IsTriviallyCompactableLeading(SyntaxTriviaList openTrailing, SyntaxTriviaList stmtLeading) {
        // 领先侧仅禁止出现注释或预处理指令；允许任意数量的空白和换行（CodeFix 会收敛）。
        var all = openTrailing.Concat(stmtLeading);
        foreach (var t in all) {
            if (t.IsDirective) return false;
            if (t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia) || t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)) {
                return false;
            }
        }
        return true;
    }

    private static bool IsTriviallyCompactableTrailing(SyntaxTriviaList stmtTrailing, SyntaxTriviaList closeLeading) {
        // 允许语句 trailing 中的“行内注释”（// 或 /* */），因为 CodeFix 会在 '}' 之前合并处理；
        // 但禁止 closeLeading（语句与 '}' 之间的下一行区域）出现任何注释或预处理指令，以避免“独立注释行”。
        // 同时限制两侧合计换行数不超过 1（即仅保留那一个将被删除的内部换行）。

        // 禁止任何指令
        if (stmtTrailing.Any(t => t.IsDirective) || closeLeading.Any(t => t.IsDirective)) return false;

        // 关闭花括号前不得出现注释（避免独立注释行）
        if (closeLeading.Any(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia)
                                || t.IsKind(SyntaxKind.MultiLineCommentTrivia)
                                || t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                                || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))) return false;

        return true;
    }
}
