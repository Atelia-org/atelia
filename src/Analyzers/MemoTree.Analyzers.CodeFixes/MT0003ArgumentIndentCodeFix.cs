using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MemoTree.Analyzers;

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(MT0003ArgumentIndentCodeFix)), Shared]
public sealed class MT0003ArgumentIndentCodeFix : CodeFixProvider {
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(MT0003ArgumentIndentAnalyzer.DiagnosticId);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context) {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) {
            return;
        }

        foreach (var diagnostic in context.Diagnostics) {
            var targetNode = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            var arg = targetNode.FirstAncestorOrSelf<ArgumentSyntax>();
            if (arg is null) {
                // 尝试参数列表声明（ParameterSyntax）
                var parameter = targetNode.FirstAncestorOrSelf<ParameterSyntax>();
                if (parameter != null && parameter.Parent is ParameterListSyntax plist) {
                    context.RegisterCodeFix(CodeAction.Create(
                            title: "Fix parameter indentation",
                            createChangedDocument: c => FixParameterAsync(context.Document, plist, parameter, c),
                            equivalenceKey: "MT0003_FixIndent_Param"), diagnostic);
                    continue;
                }
                // 可能是注释行；尝试向上找 ArgumentList（目前注释行已不处理，可忽略，这里保留旧逻辑用于兼容历史诊断）
                var argList = targetNode.FirstAncestorOrSelf<ArgumentListSyntax>();
                if (argList is null) {
                    continue;
                }

                context.RegisterCodeFix(
                    CodeAction.Create(
                        title: "Fix argument indentation",
                        createChangedDocument: c => FixCommentLineAsync(context.Document, argList, diagnostic.Location, c),
                        equivalenceKey: "MT0003_FixIndent"),
                    diagnostic);
                continue;
            }

            var list = arg.Parent as ArgumentListSyntax;
            if (list is null) {
                continue;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    title: "Fix argument indentation",
                    createChangedDocument: c => FixArgumentAsync(context.Document, list, arg, c),
                    equivalenceKey: "MT0003_FixIndent"),
                diagnostic);
        }
    }

    private static async Task<Document> FixArgumentAsync(Document document, ArgumentListSyntax list, ArgumentSyntax arg, CancellationToken token) {
        var root = await document.GetSyntaxRootAsync(token).ConfigureAwait(false);
        if (root is null) {
            return document;
        }

        var text = await document.GetTextAsync(token).ConfigureAwait(false);

        // 获取调用起始行缩进
        var invocationToken = list.Parent switch {
            InvocationExpressionSyntax inv => inv.Expression.GetFirstToken(),
            ObjectCreationExpressionSyntax creation => creation.NewKeyword,
            _ => list.OpenParenToken
        };
        int invocationLine = text.Lines.GetLineFromPosition(invocationToken.SpanStart).LineNumber;
        var invocationLineText = text.Lines[invocationLine].ToString();
        int baseIndent = invocationLineText.TakeWhile(char.IsWhiteSpace).Count();

        var options = document.Project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GetOptions(list.SyntaxTree);
        int indentSize = 4;
        if (options.TryGetValue("indent_size", out var indentStr) && int.TryParse(indentStr, out var parsed) && parsed > 0) {
            indentSize = parsed;
        }

        string expectedIndent = new string(' ', baseIndent + indentSize);

        var firstToken = arg.GetFirstToken();
        int argLine = text.Lines.GetLineFromPosition(firstToken.SpanStart).LineNumber;
        // 只有当参数 token 是该行第一个非空白字符才进行缩进修复；否则避免在行中部插入缩进造成多余空格。
        var line = text.Lines[argLine];
        var lineText = line.ToString();
        int firstNonWs = 0;
        while (firstNonWs < lineText.Length && char.IsWhiteSpace(lineText[firstNonWs])) {
            firstNonWs++;
        }
        if (line.Start + firstNonWs != firstToken.SpanStart) {
            return document; // 非行首参数，跳过。
        }
        var leading = firstToken.LeadingTrivia;
        int lastEolIndex = -1;
        for (int i = 0; i < leading.Count; i++) {
            if (leading[i].IsKind(SyntaxKind.EndOfLineTrivia)) {
                lastEolIndex = i;
            }
        }
        var builder = new System.Collections.Generic.List<SyntaxTrivia>();
        if (lastEolIndex >= 0) {
            // 保留到最后一个换行
            for (int i = 0; i <= lastEolIndex; i++) {
                builder.Add(leading[i]);
            }
            // 跳过换行后的所有前置空白
            int j = lastEolIndex + 1;
            while (j < leading.Count && leading[j].IsKind(SyntaxKind.WhitespaceTrivia)) {
                j++;
            }
            builder.Add(SyntaxFactory.Whitespace(expectedIndent));
            // 追加剩余 trivia（注释等）
            for (; j < leading.Count; j++) {
                builder.Add(leading[j]);
            }
        } else {
            // 行首没有换行（少见），直接重写前导空白
            int k = 0;
            while (k < leading.Count && leading[k].IsKind(SyntaxKind.WhitespaceTrivia)) {
                k++;
            }
            builder.Add(SyntaxFactory.Whitespace(expectedIndent));
            for (; k < leading.Count; k++) {
                builder.Add(leading[k]);
            }
        }
        var newToken = firstToken.WithLeadingTrivia(SyntaxFactory.TriviaList(builder));
        var newList = list.ReplaceToken(firstToken, newToken);
        var newRoot = root.ReplaceNode(list, newList);
        return document.WithSyntaxRoot(newRoot);
    }

    private static async Task<Document> FixCommentLineAsync(Document document, ArgumentListSyntax list, Location location, CancellationToken token) {
        var root = await document.GetSyntaxRootAsync(token).ConfigureAwait(false);
        if (root is null) {
            return document;
        }

        var text = await document.GetTextAsync(token).ConfigureAwait(false);

        var invocationToken = list.Parent switch {
            InvocationExpressionSyntax inv => inv.Expression.GetFirstToken(),
            ObjectCreationExpressionSyntax creation => creation.NewKeyword,
            _ => list.OpenParenToken
        };
        int invocationLine = text.Lines.GetLineFromPosition(invocationToken.SpanStart).LineNumber;
        var invocationLineText = text.Lines[invocationLine].ToString();
        int baseIndent = invocationLineText.TakeWhile(char.IsWhiteSpace).Count();

        var options = document.Project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GetOptions(list.SyntaxTree);
        int indentSize = 4;
        if (options.TryGetValue("indent_size", out var indentStr) && int.TryParse(indentStr, out var parsed) && parsed > 0) {
            indentSize = parsed;
        }

        string expectedIndent = new string(' ', baseIndent + indentSize);

        // 定位行首 token：我们通过文本替换方式处理该行前导空白
        int lineNumber = text.Lines.GetLineFromPosition(location.SourceSpan.Start).LineNumber;
        var line = text.Lines[lineNumber];
        var lineText = line.ToString();
        int prefixLen = lineText.TakeWhile(char.IsWhiteSpace).Count();
        var newLineText = expectedIndent + lineText.Substring(prefixLen);
        var newText = text.Replace(line.Span, newLineText);

        // 额外：同时尝试修复紧随其后的首个参数行（常见场景：注释行后就是参数且同样缩进错误，用户期望一次修好）。
        int nextLine = lineNumber + 1;
        if (nextLine <= text.Lines.Count - 1) {
            var nextLineText = newText.Lines[nextLine].ToString();
            if (!string.IsNullOrWhiteSpace(nextLineText)) {
                var trimmed = nextLineText.TrimStart();
                if (!trimmed.StartsWith("//") && !trimmed.StartsWith("/*")) {
                    int nextPrefix = 0;
                    while (nextPrefix < nextLineText.Length && char.IsWhiteSpace(nextLineText[nextPrefix])) {
                        nextPrefix++;
                    }
                    if (nextPrefix != expectedIndent.Length) {
                        var adjustedNext = expectedIndent + nextLineText.Substring(nextPrefix);
                        newText = newText.Replace(newText.Lines[nextLine].Span, adjustedNext);
                        System.Console.WriteLine($"[MT0003 CodeFix] Adjusted following arg line from {nextPrefix} to {expectedIndent.Length} spaces.");
                    }
                }
            }
        }

        return document.WithText(newText);
    }

    private static async Task<Document> FixParameterAsync(Document document, ParameterListSyntax list, ParameterSyntax parameter, CancellationToken token) {
        var root = await document.GetSyntaxRootAsync(token).ConfigureAwait(false);
        if (root is null) {
            return document;
        }
        var text = await document.GetTextAsync(token).ConfigureAwait(false);
        var declFirstToken = list.Parent?.GetFirstToken() ?? list.OpenParenToken;
        int declLine = text.Lines.GetLineFromPosition(declFirstToken.SpanStart).LineNumber;
        var declLineText = text.Lines[declLine].ToString();
        int baseIndent = declLineText.TakeWhile(char.IsWhiteSpace).Count();
        var options = document.Project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GetOptions(list.SyntaxTree);
        int indentSize = 4;
        if (options.TryGetValue("indent_size", out var indentStr) && int.TryParse(indentStr, out var parsed) && parsed > 0) {
            indentSize = parsed;
        }
        string expectedIndent = new string(' ', baseIndent + indentSize);
        var firstToken = parameter.GetFirstToken();
        int paramLine = text.Lines.GetLineFromPosition(firstToken.SpanStart).LineNumber;
        var line = text.Lines[paramLine];
        var lineText = line.ToString();
        int firstNonWs = 0;
        while (firstNonWs < lineText.Length && char.IsWhiteSpace(lineText[firstNonWs])) {
            firstNonWs++;
        }
        if (line.Start + firstNonWs != firstToken.SpanStart) {
            return document; // 行中部
        }
        if (lineText.TrimStart().StartsWith("//") || lineText.TrimStart().StartsWith("/*")) {
            return document; // 注释行
        }
        var leading = firstToken.LeadingTrivia;
        int lastEolIndex = -1;
        for (int i = 0; i < leading.Count; i++) {
            if (leading[i].IsKind(SyntaxKind.EndOfLineTrivia)) {
                lastEolIndex = i;
            }
        }
        var listTrivia = new System.Collections.Generic.List<SyntaxTrivia>();
        if (lastEolIndex >= 0) {
            for (int i = 0; i <= lastEolIndex; i++) {
                listTrivia.Add(leading[i]);
            }
            int j = lastEolIndex + 1;
            while (j < leading.Count && leading[j].IsKind(SyntaxKind.WhitespaceTrivia)) {
                j++;
            }
            listTrivia.Add(SyntaxFactory.Whitespace(expectedIndent));
            for (; j < leading.Count; j++) {
                listTrivia.Add(leading[j]);
            }
        } else {
            int k = 0;
            while (k < leading.Count && leading[k].IsKind(SyntaxKind.WhitespaceTrivia)) {
                k++;
            }
            listTrivia.Add(SyntaxFactory.Whitespace(expectedIndent));
            for (; k < leading.Count; k++) {
                listTrivia.Add(leading[k]);
            }
        }
        var newToken = firstToken.WithLeadingTrivia(SyntaxFactory.TriviaList(listTrivia));
        var newList = list.ReplaceToken(firstToken, newToken);
        var newRoot = root.ReplaceNode(list, newList);
        return document.WithSyntaxRoot(newRoot);
    }
}
