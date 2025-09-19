using System;
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
using Microsoft.CodeAnalysis.Text;

namespace Atelia.Analyzers.Style {
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(MT0101XmlDocEscapeCodeFix)), Shared]
    public sealed class MT0101XmlDocEscapeCodeFix : CodeFixProvider {
        public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(MT0101XmlDocEscapeAnalyzer.DiagnosticId);
        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context) {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            if (root == null) {
                return;
            }

            var diag = context.Diagnostics.First();
            context.RegisterCodeFix(
                CodeAction.Create(
                    title: "Escape angle brackets in XML doc text",
                    createChangedDocument: ct => FixAsync(context.Document, diag.Location.SourceSpan, ct),
                    equivalenceKey: "EscapeXmlDocAngles"),
                diag);
        }

        private static async Task<Document> FixAsync(Document doc, TextSpan span, CancellationToken ct) {
            var text = await doc.GetTextAsync(ct).ConfigureAwait(false);
            var original = text.ToString(span);
            var fixedText = EscapeAnglesKeepingKnownTags(original);
            if (string.Equals(fixedText, original, StringComparison.Ordinal)) {
                return doc;
            }

            var newText = text.Replace(span, fixedText);
            return doc.WithText(newText);
        }

        // Duplicated minimal escaper (kept local to avoid cross-assembly coupling)
        private static string EscapeAnglesKeepingKnownTags(string s) {
            if (string.IsNullOrEmpty(s)) {
                return s;
            }

            var known = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase) {
                // Core XML doc tags (keep in sync with analyzer)
                "summary","remarks","para","c","code","see","seealso",
                // list & structure
                "list","listheader","item","term","description",
                // blocks & members
                "example","exception","include","param","paramref","typeparam","typeparamref","permission","value","returns",
                // others
                "inheritdoc","note"
            };
            var sb = new System.Text.StringBuilder(s.Length + 16);
            int i = 0;
            while (i < s.Length) {
                char ch = s[i];
                if (ch == '<') {
                    int j = i + 1;
                    bool isEnd = (j < s.Length && s[j] == '/');
                    if (isEnd) {
                        j++;
                    }

                    if (j < s.Length && (s[j] == '!' || s[j] == '?')) {
                        int k = s.IndexOf('>', j);
                        if (k < 0) { sb.Append("&lt;"); i++; continue; }
                        sb.Append(s, i, k - i + 1);
                        i = k + 1;
                        continue;
                    }
                    int nameStart = j;
                    while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == ':' || s[j] == '_')) {
                        j++;
                    }

                    var name = s.Substring(nameStart, System.Math.Max(0, j - nameStart));
                    if (name.Length > 0 && known.Contains(name)) {
                        int k = s.IndexOf('>', j);
                        if (k < 0) { sb.Append("&lt;"); i++; continue; }
                        sb.Append(s, i, k - i + 1);
                        i = k + 1;
                        continue;
                    }
                    sb.Append("&lt;");
                    i++;
                    continue;
                }
                else if (ch == '>') {
                    sb.Append("&gt;");
                    i++;
                    continue;
                }
                else {
                    sb.Append(ch);
                    i++;
                    continue;
                }
            }
            return sb.ToString();
        }
    }
}

