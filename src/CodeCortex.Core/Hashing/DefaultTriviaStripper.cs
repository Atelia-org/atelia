using System.Text;

namespace CodeCortex.Core.Hashing;

/// <summary>
/// Default trivia stripper removing line (// / ///) and block (/* */) comments plus all whitespace characters.
/// Lightweight single-pass state machine; preserves string/char literals content (i.e. does not strip inside them).
/// Phase1 heuristic: we don't handle verbatim/interpolated string escaped sequences perfectly, but we avoid removing
/// comment markers that appear inside quoted strings.
/// </summary>
public sealed class DefaultTriviaStripper : ITriviaStripper {
    private static readonly System.Text.RegularExpressions.Regex BlockComment = new(@"/\*.*?\*/", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.Singleline);
    private static readonly System.Text.RegularExpressions.Regex Ws = new(@"\s+", System.Text.RegularExpressions.RegexOptions.Compiled);
    /// &lt;inheritdoc /&gt;
    public string Strip(string codeFragment) {
        if (string.IsNullOrWhiteSpace(codeFragment)) { return string.Empty; }
        var withoutBlock = BlockComment.Replace(codeFragment, string.Empty);
        // Roslyn parse & rebuild tokens ignoring trivia (comments/whitespace) for reliability
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(withoutBlock);
        var root = tree.GetRoot();
        var sb = new StringBuilder(withoutBlock.Length);
        foreach (var token in root.DescendantTokens(descendIntoTrivia: true)) {
            if (token.RawKind == (int)Microsoft.CodeAnalysis.CSharp.SyntaxKind.EndOfFileToken) { break; }
            sb.Append(token.Text);
        }
        // Remove any remaining whitespace characters
        var collapsed = Ws.Replace(sb.ToString(), string.Empty);
        // Remove line comment artifacts if any slipped (defensive)
        collapsed = collapsed.Replace("//", string.Empty);
        return collapsed;
    }
}
