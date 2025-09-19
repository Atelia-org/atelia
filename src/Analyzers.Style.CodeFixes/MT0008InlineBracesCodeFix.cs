using System.Collections.Immutable;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Atelia.Analyzers.Style.CodeFixes;

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(MT0008InlineBracesCodeFix))]
/// <summary>
/// MT0008 代码修复：为未成块（单语句体）的控制语句添加“内联花括号”。
///
/// 行为要点（与当前实现严格一致）：
/// - 适用节点：if、else、for、foreach、foreach(var)、while、do、using、lock、fixed。
/// - 豁免场景：
///   - else-if 链（else 直接挂 if 的情形）不强制加块，保持链式结构。
///   - using 链（using 的语句体还是另一个 using）不强制加块，以匹配分析器行为。
/// - 仅插入 “{” 与 “}”，不新增/删除任何换行（EndOfLineTrivia 的数量保持不变），以便与其他格式化/规则协同。
/// - 布局风格为“紧凑内联”：在 “{” 与 “}” 之前各放置一个空格，但不改变换行数。
/// - Trivia 迁移规则（见 <see cref="InlineBlock(Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax)"/>）：
///   1) 原语句的 leading trivia 改为 “{” 的 trailing（即进入块内，避免注释放在块外）。
///   2) 原语句的 trailing trivia 按首个换行分割：
///      - 首个换行之前（同一物理行）的部分，放到 “}” 之前：其中 // 注释会转换为 /* ... */；多行注释保持；空白等噪声不保留；并保证语句与 “}” 之间至少一个空格。
///      - 从首个换行起及其后续，作为 “}” 的 trailing，完整保留，从而保证换行计数不变。
///   3) 为避免重复，语句本体的 leading/trailing 会被清空后再放入块内。
/// </summary>
public sealed class MT0008InlineBracesCodeFix : CodeFixProvider {
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create("MT0008");

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context) {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) {
            return;
        }

        var diagnostic = context.Diagnostics.First();
        var span = diagnostic.Location.SourceSpan;
        var node = root.FindToken(span.Start).Parent;
        if (node is null) {
            return;
        }

        // 找到最近的控制语句/else 子句节点
        var target = node.AncestorsAndSelf().FirstOrDefault(n => IsControlNode(n) && IsControlStatementNeedingBlock(n));
        if (target is null) {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create("Add inline braces", ct => AddInlineBracesAsync(context.Document, root, target, ct), equivalenceKey: "MT0008_inline"),
            diagnostic);
    }

    private static bool IsControlStatementNeedingBlock(SyntaxNode n) => n switch {
        IfStatementSyntax ifs => ifs.Statement is not BlockSyntax,
        ElseClauseSyntax els => els.Statement is not BlockSyntax && els.Statement is not IfStatementSyntax,
        ForStatementSyntax f => f.Statement is not BlockSyntax,
        ForEachStatementSyntax fe => fe.Statement is not BlockSyntax,
        ForEachVariableStatementSyntax fev => fev.Statement is not BlockSyntax,
        WhileStatementSyntax w => w.Statement is not BlockSyntax,
        DoStatementSyntax d => d.Statement is not BlockSyntax,
        // 豁免 using 链：当语句体还是另一个 using 时，不认为需要成块（与分析器行为一致）。
        UsingStatementSyntax u => u.Statement is not BlockSyntax && u.Statement is not UsingStatementSyntax,
        LockStatementSyntax l => l.Statement is not BlockSyntax,
        FixedStatementSyntax fx => fx.Statement is not BlockSyntax,
        _ => false
    };

    private static bool IsControlNode(SyntaxNode n) => n is IfStatementSyntax or ElseClauseSyntax or ForStatementSyntax or ForEachStatementSyntax or ForEachVariableStatementSyntax or WhileStatementSyntax or DoStatementSyntax or UsingStatementSyntax or LockStatementSyntax or FixedStatementSyntax;

    private static Task<Document> AddInlineBracesAsync(Document document, SyntaxNode root, SyntaxNode controlNode, CancellationToken ct) {
        SyntaxNode replaced = controlNode switch {
            IfStatementSyntax ifs => WrapIf(ifs),
            ElseClauseSyntax els => WrapElse(els),
            ForStatementSyntax f => f.WithStatement(InlineBlock(f.Statement)),
            ForEachStatementSyntax fe => fe.WithStatement(InlineBlock(fe.Statement)),
            ForEachVariableStatementSyntax fev => fev.WithStatement(InlineBlock(fev.Statement)),
            WhileStatementSyntax w => w.WithStatement(InlineBlock(w.Statement)),
            DoStatementSyntax d => d.WithStatement(InlineBlock(d.Statement)),
            UsingStatementSyntax u => u.WithStatement(InlineBlock(u.Statement)),
            LockStatementSyntax l => l.WithStatement(InlineBlock(l.Statement)),
            FixedStatementSyntax fx => fx.WithStatement(InlineBlock(fx.Statement)),
            _ => controlNode
        };

        var newRoot = root.ReplaceNode(controlNode, replaced);
        return Task.FromResult(document.WithSyntaxRoot(newRoot));
    }

    private static SyntaxNode WrapIf(IfStatementSyntax ifs) {
        if (ifs.Statement is BlockSyntax) {
            return ifs;
        }

        var newIf = ifs.WithStatement(InlineBlock(ifs.Statement));
        // else 部分保持原样；若为 else-if 链则不改变其结构
        return newIf;
    }

    private static SyntaxNode WrapElse(ElseClauseSyntax els) {
        if (els.Statement is BlockSyntax || els.Statement is IfStatementSyntax) {
            return els;
        }

        return els.WithStatement(InlineBlock(els.Statement));
    }

    private static BlockSyntax InlineBlock(StatementSyntax body) {
        // 目标：仅插入 { 和 }，不新增/删除现有“换行”。
        // 概览：
        // - “{” 前放置一个空格；“{” 的 trailing = 原语句的 leading（将注释放入块内）。
        // - “}” 前至少一个空格；“}” 的 leading = （原 trailing 的首个换行之前的内容）经规则处理：
        //     • 单行注释 //text -> 块注释 /* text */；
        //     • 多行注释保持；
        //     • 其他空白/噪声忽略；并在注释之间插入最小空格以维持紧凑。
        // - “}” 的 trailing = 原 trailing 的“首个换行及其后续”，从而保留换行计数。
        // - 语句本体清空自身 leading/trailing，避免与花括号的 trivia 重复。

        var leading = body.GetLeadingTrivia();
        var trailing = body.GetTrailingTrivia();

        // 语句本体去除自身的 leading/trailing，避免与花括号的 trivia 重复。
        var cleanedBody = body
            .WithLeadingTrivia(SyntaxFactory.TriviaList())
            .WithTrailingTrivia(SyntaxFactory.TriviaList());

        // Open “{” 的放置策略：
        // 为避免把注释放到块外，将原 leading 作为 “{” 的 trailing（进入块内），并在 “{” 前放置一个空格。
        var open = SyntaxFactory.Token(SyntaxKind.OpenBraceToken)
            .WithLeadingTrivia(SyntaxFactory.Space)
            .WithTrailingTrivia(leading);

        // 按首个换行把 trailing 拆成两段：
        // - preEol：首个换行之前（同一物理行）的 trivia（如空格 + // 注释 或 /* 注释 */）——放入 “}” 之前；
        // - postEol：从首个换行起及其后续——保留在 “}” 之后。
        var trailingList = trailing.ToList();
        var firstEolIdx = trailingList.FindIndex(t => t.IsKind(SyntaxKind.EndOfLineTrivia));
        var preEol = firstEolIdx >= 0 ? trailingList.Take(firstEolIdx) : trailingList;
        var postEol = firstEolIdx >= 0 ? trailingList.Skip(firstEolIdx) : Enumerable.Empty<SyntaxTrivia>();

        // 将 preEol 中的单行注释转换为块注释，并安置在 “}” 之前；
        // 其余非常见 trivia（空白等）不保留，仅在注释前后提供最小空格。
        var closeLeading = new List<SyntaxTrivia>();
        // 至少保证语句与 “}” 之间有一个空格
        closeLeading.Add(SyntaxFactory.Space);

        foreach (var triv in preEol) {
            if (triv.IsKind(SyntaxKind.SingleLineCommentTrivia)) {
                // // text -> /* text */
                var text = triv.ToString();
                if (text.StartsWith("//")) {
                    text = text.Substring(2);
                }
                text = text.TrimStart();
                // 为避免生成无效块注释，转义内部的终止符号序列
                // e.g. "... */ ..." -> "... * / ..."
                if (text.Contains("*/")) {
                    text = text.Replace("*/", "* /");
                }
                var blockComment = SyntaxFactory.Comment($"/* {text} */");
                closeLeading.Add(blockComment);
                closeLeading.Add(SyntaxFactory.Space);
            }
            else if (triv.IsKind(SyntaxKind.MultiLineCommentTrivia)) {
                // 已是块注释则直接内置
                closeLeading.Add(triv);
                closeLeading.Add(SyntaxFactory.Space);
            }
            else {
                // 忽略空白/其他 trivia，以减少不必要的横向噪声
            }
        }

        var close = SyntaxFactory.Token(SyntaxKind.CloseBraceToken)
            .WithLeadingTrivia(SyntaxFactory.TriviaList(closeLeading))
            .WithTrailingTrivia(SyntaxFactory.TriviaList(postEol));

        var block = SyntaxFactory.Block(open, SyntaxFactory.List(new[] { cleanedBody }), close);
        return block;
    }

    // 说明：该实现不移除换行，保证与原代码的换行数量一致；仅在花括号两侧施加最小空格与注释内联规则，以便与其他格式化/规则配合。
}
