using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Atelia.Analyzers.Style {
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class MT0101XmlDocEscapeAnalyzer : DiagnosticAnalyzer {
        public const string DiagnosticId = "MT0101";
        private static readonly LocalizableString Title = "Unescaped angle bracket in XML doc text";
        private static readonly LocalizableString Message = "XML documentation text contains unescaped '<' or '>' (use &lt; and &gt;)";
        private const string Category = "Documentation";

        private static readonly DiagnosticDescriptor Rule = new(
            DiagnosticId,
            Title,
            Message,
            Category,
            DiagnosticSeverity.Info,
            isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context) {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(AnalyzeDocTrivia,
                SyntaxKind.SingleLineDocumentationCommentTrivia,
                SyntaxKind.MultiLineDocumentationCommentTrivia);
        }

        private static void AnalyzeDocTrivia(SyntaxNodeAnalysisContext ctx) {
            if (ctx.Node is not DocumentationCommentTriviaSyntax doc) {
                return;
            }

            var original = doc.ToFullString();
            // Fast check: if no angle brackets at all, skip
            if (original.IndexOf('<') < 0 && original.IndexOf('>') < 0) {
                return;
            }

            var fixedText = EscapeAnglesKeepingKnownTags(original);
            if (!string.Equals(fixedText, original, StringComparison.Ordinal)) {
                ctx.ReportDiagnostic(Diagnostic.Create(Rule, doc.GetLocation()));
            }
        }

        private static readonly HashSet<string> KnownTags = new(StringComparer.OrdinalIgnoreCase) {
            // Core XML doc tags (keep in sync with MT0101XmlDocEscapeCodeFix)
            "summary","remarks","para","c","code","see","seealso",
            // list & structure
            "list","listheader","item","term","description",
            // blocks & members
            "example","exception","include","param","paramref","typeparam","typeparamref","permission","value","returns",
            // others
            "inheritdoc","note"
        };

        // Escape '<' and '>' which are not part of known documentation tags.
        internal static string EscapeAnglesKeepingKnownTags(string s) {
            if (string.IsNullOrEmpty(s)) {
                return s;
            }

            var sb = new StringBuilder(s.Length + 16);
            bool inTag = false;
            int i = 0;
            while (i < s.Length) {
                char ch = s[i];
                if (!inTag) {
                    if (ch == '<') {
                        // Peek tag name
                        int j = i + 1;
                        bool isEnd = (j < s.Length && s[j] == '/');
                        if (isEnd) {
                            j++;
                        }
                        // Skip XML declaration/processing/comment/CDATA
                        if (j < s.Length && (s[j] == '!' || s[j] == '?')) {
                            // treat as tag-like; copy through until next '>'
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

                        var name = s.Substring(nameStart, Math.Max(0, j - nameStart));
                        if (name.Length > 0 && KnownTags.Contains(name)) {
                            // keep as real tag
                            int k = s.IndexOf('>', j);
                            if (k < 0) { sb.Append("&lt;"); i++; continue; }
                            sb.Append(s, i, k - i + 1);
                            i = k + 1;
                            continue;
                        }
                        // Not a known tag -> escape '<'
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
                else {
                    // not used in this simple state machine; kept for clarity
                    sb.Append(ch);
                    i++;
                }
            }
            return sb.ToString();
        }
    }
}

