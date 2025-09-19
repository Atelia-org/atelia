using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Atelia.Analyzers.Style.CodeFixes;

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(MT0009InlineSimpleSingleStatementBlockCodeFix))]
public sealed class MT0009InlineSimpleSingleStatementBlockCodeFix : CodeFixProvider {
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create("MT0009");

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context) {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;

        var diagnostic = context.Diagnostics.First();
        var node = root.FindToken(diagnostic.Location.SourceSpan.Start).Parent;
        if (node is null) return;

        // 目标块：命中位置附近的 BlockSyntax（if/else 的主体）
        var block = node.AncestorsAndSelf().OfType<BlockSyntax>().FirstOrDefault();
        if (block is null) return;

        // 仅处理 if/else 的主体块
        if (!IsIfOrElseBody(block)) return;

        context.RegisterCodeFix(
            CodeAction.Create("Inline single-statement block", ct => InlineBlockAsync(context.Document, root, block, ct), equivalenceKey: "MT0009_inline"),
            diagnostic);
    }

    private static bool IsIfOrElseBody(BlockSyntax block) {
        var parent = block.Parent;
        if (parent is IfStatementSyntax ifs) return ifs.Statement == block;
        if (parent is ElseClauseSyntax els) return els.Statement == block && els.Statement is BlockSyntax;
        return false;
    }

    private static Task<Document> InlineBlockAsync(Document document, SyntaxNode root, BlockSyntax block, CancellationToken ct) {
        if (block.Statements.Count != 1) return Task.FromResult(document);

        // 构造当前目标块的新内联块
        var newBlock = BuildInlineBlock(block);

        // 同时移除 if/else 头与 '{' 之间的换行；如果是 if，还尝试同时内联 else 的单语句块（若存在）。
        SyntaxNode replacedParent;
        if (block.Parent is IfStatementSyntax ifs && ifs.Statement == block) {
            // 先准备 if 主体
            var newIf = ifs
                .WithCloseParenToken(NormalizeTrailingToSingleSpace(ifs.CloseParenToken))
                .WithStatement(newBlock);

            // 如果存在 else 且其主体也是单语句块，也一并内联，避免需要第二次诊断迭代。
            if (ifs.Else is { Statement: BlockSyntax elseBlock } && elseBlock.Statements.Count == 1) {
                var inlinedElseBlock = BuildInlineBlock(elseBlock);
                var newElse = ifs.Else
                    .WithElseKeyword(NormalizeTrailingToSingleSpace(ifs.Else.ElseKeyword))
                    .WithStatement(inlinedElseBlock);
                newIf = newIf.WithElse(newElse);
            }
            else {
                // 保留原 else（若有）
                newIf = newIf.WithElse(ifs.Else);
            }
            replacedParent = newIf;
        }
        else if (block.Parent is ElseClauseSyntax els && els.Statement == block) {
            var newEls = els
                .WithElseKeyword(NormalizeTrailingToSingleSpace(els.ElseKeyword))
                .WithStatement(newBlock);
            replacedParent = newEls;
        }
        else {
            // 回退：仅替换块（不理想，但不破坏）
            var fallbackRoot = root.ReplaceNode(block, newBlock);
            return Task.FromResult(document.WithSyntaxRoot(fallbackRoot));
        }

        var parent = block.Parent!;
        var newRoot = root.ReplaceNode(parent, replacedParent);
        return Task.FromResult(document.WithSyntaxRoot(newRoot));
    }

    private static BlockSyntax BuildInlineBlock(BlockSyntax block) {
        var stmt = block.Statements[0];
        // 构造最小内联形态："{ " + stmt(without leading/trailing) + preEolComments + " }"，并把 postEol 附到闭括号 trailing。
        var cleanedStmt = stmt.WithLeadingTrivia(SyntaxFactory.TriviaList()).WithTrailingTrivia(SyntaxFactory.TriviaList());

        // 语句 trailing 拆分：首个换行前（行内）与其后
        var trailingList = stmt.GetTrailingTrivia().ToList();
        int firstEol = trailingList.FindIndex(t => t.IsKind(SyntaxKind.EndOfLineTrivia));
        var preEol = firstEol >= 0 ? trailingList.Take(firstEol) : trailingList;
        var postEol = firstEol >= 0 ? trailingList.Skip(firstEol) : Enumerable.Empty<SyntaxTrivia>();

        // 行内注释转换并安置在 '}' 之前
        var closeLeading = new List<SyntaxTrivia> { SyntaxFactory.Space };
        foreach (var t in preEol) {
            if (t.IsKind(SyntaxKind.SingleLineCommentTrivia)) {
                var text = t.ToString();
                if (text.StartsWith("//")) text = text.Substring(2);
                text = text.TrimStart();
                if (text.Contains("*/")) text = text.Replace("*/", "* /");
                closeLeading.Add(SyntaxFactory.Comment($"/* {text} */"));
                closeLeading.Add(SyntaxFactory.Space);
            }
            else if (t.IsKind(SyntaxKind.MultiLineCommentTrivia)) {
                closeLeading.Add(t);
                closeLeading.Add(SyntaxFactory.Space);
            }
            else {
                // 忽略多余空白
            }
        }

        // '{'：不使用 leading 空白；trailing 使用一个空格（"{ ")
        var open = SyntaxFactory.Token(SyntaxKind.OpenBraceToken)
            .WithLeadingTrivia(SyntaxFactory.TriviaList())
            .WithTrailingTrivia(SyntaxFactory.Space);

        // '}'：leading 带上 preEol 注释，trailing 保留 postEol
        var close = SyntaxFactory.Token(SyntaxKind.CloseBraceToken)
            .WithLeadingTrivia(SyntaxFactory.TriviaList(closeLeading))
            .WithTrailingTrivia(SyntaxFactory.TriviaList(postEol));

        return SyntaxFactory.Block(open, SyntaxFactory.List(new[] { cleanedStmt }), close);
    }

    private static SyntaxToken NormalizeTrailingToSingleSpace(SyntaxToken token) {
        // 去除 token trailing 中的换行与多余空白，统一为一个空格；保留非空白注释与指令（极不常见于 if/catch 头部之后）。
        var kept = new List<SyntaxTrivia>();
        foreach (var t in token.TrailingTrivia) {
            if (t.IsKind(SyntaxKind.EndOfLineTrivia) || t.IsKind(SyntaxKind.WhitespaceTrivia)) {
                continue;
            }
            kept.Add(t);
            kept.Add(SyntaxFactory.Space);
        }
        if (kept.Count == 0) kept.Add(SyntaxFactory.Space);
        return token.WithTrailingTrivia(SyntaxFactory.TriviaList(kept));
    }
}
