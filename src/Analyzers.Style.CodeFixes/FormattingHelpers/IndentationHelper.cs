using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Atelia.Analyzers.Style;

internal static class IndentationHelper {
    // Try get indent_size from analyzer config; fallback 4.
    public static int GetIndentSize(Document doc, SyntaxTree tree) {
        try {
            var provider = doc.Project.AnalyzerOptions.AnalyzerConfigOptionsProvider;
            var opts = provider.GetOptions(tree);
            if (opts.TryGetValue("indent_size", out var raw) && int.TryParse(raw, out var v) && v > 0 && v < 16) {
                return v; // guard upper bound
            }
        } catch { }
        return 4;
    }

    public static SyntaxTrivia ComputeIndentTrivia(SourceText text, int position, int additionalLevels, int indentSize) {
        var line = text.Lines.GetLineFromPosition(position);
        var lineText = line.ToString();
        int baseIndent = 0;
        while (baseIndent < lineText.Length && char.IsWhiteSpace(lineText[baseIndent])) {
            baseIndent++;
        }

        int total = baseIndent + Math.Max(0, additionalLevels) * indentSize;
        return Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Whitespace(new string(' ', total));
    }

    public static async Task<SyntaxTrivia> ComputeIndentTriviaAsync(Document doc, SyntaxToken anchorToken, int additionalLevels, CancellationToken ct) {
        var tree = anchorToken.SyntaxTree;
        var text = await doc.GetTextAsync(ct).ConfigureAwait(false);
        var size = tree != null ? GetIndentSize(doc, tree) : 4;
        return ComputeIndentTrivia(text, anchorToken.SpanStart, additionalLevels, size);
    }

    // For aligning a closing parenthesis to the column of its opening construct's anchor (no extra indent levels).
    public static async Task<SyntaxTrivia> ComputeAlignedCloseParenIndentAsync(Document doc, SyntaxToken anchorToken, CancellationToken ct) {
        return await ComputeIndentTriviaAsync(doc, anchorToken, 0, ct).ConfigureAwait(false);
    }
}
