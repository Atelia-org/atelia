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

            var preserved = new System.Collections.Generic.List<TagSpan>(capacity: 8);
            var openTags = new System.Collections.Generic.List<OpenTagInfo>(capacity: 4);

            int i = 0;
            while (i < s.Length) {
                char ch = s[i];
                if (ch != '<') {
                    i++;
                    continue;
                }

                int tagStart = i;
                int j = i + 1;
                if (j >= s.Length) {
                    i++;
                    continue;
                }

                if (s[j] == '!' || s[j] == '?') {
                    int specialEnd = s.IndexOf('>', j);
                    if (specialEnd < 0) {
                        i = tagStart + 1;
                        continue;
                    }
                    preserved.Add(new TagSpan(tagStart, specialEnd - tagStart + 1));
                    i = specialEnd + 1;
                    continue;
                }

                bool isEnd = false;
                if (s[j] == '/') {
                    isEnd = true;
                    j++;
                }

                int nameStart = j;
                while (j < s.Length && IsNameChar(s[j])) {
                    j++;
                }
                int nameLength = j - nameStart;
                if (nameLength == 0) {
                    i = tagStart + 1;
                    continue;
                }

                bool closed = false;
                bool selfClosing = false;
                bool invalidClosingTag = false;
                int scan = j;
                char quote = '\0';
                while (scan < s.Length) {
                    char current = s[scan];
                    if (quote != '\0') {
                        if (current == quote) {
                            quote = '\0';
                        }
                        scan++;
                        continue;
                    }

                    if (current == '\"' || current == '\'') {
                        quote = current;
                        scan++;
                        continue;
                    }

                    if (current == '>') {
                        closed = true;
                        int back = scan - 1;
                        while (back >= tagStart && char.IsWhiteSpace(s[back])) {
                            back--;
                        }
                        if (!isEnd && back >= tagStart && s[back] == '/') {
                            selfClosing = true;
                        }

                        if (isEnd) {
                            for (int t = nameStart + nameLength; t < scan; t++) {
                                if (!char.IsWhiteSpace(s[t])) {
                                    invalidClosingTag = true;
                                    break;
                                }
                            }
                        }

                        scan++;
                        break;
                    }

                    scan++;
                }

                if (!closed || invalidClosingTag) {
                    i = tagStart + 1;
                    continue;
                }

                int tagEnd = scan;
                int spanLength = tagEnd - tagStart;

                if (isEnd) {
                    if (openTags.Count == 0) {
                        i = tagStart + 1;
                        continue;
                    }

                    var open = openTags[openTags.Count - 1];
                    if (!NamesEqual(s, open.NameStart, open.NameLength, nameStart, nameLength)) {
                        i = tagStart + 1;
                        continue;
                    }

                    openTags.RemoveAt(openTags.Count - 1);
                    preserved.Add(new TagSpan(open.SpanStart, open.SpanLength));
                    preserved.Add(new TagSpan(tagStart, spanLength));
                    i = tagEnd;
                    continue;
                }

                if (selfClosing) {
                    preserved.Add(new TagSpan(tagStart, spanLength));
                    i = tagEnd;
                    continue;
                }

                openTags.Add(new OpenTagInfo(tagStart, spanLength, nameStart, nameLength));
                i = tagEnd;
            }

            if (preserved.Count == 0) {
                return EscapeAnglesNoPreservedSpans(s);
            }

            preserved.Sort((a, b) => a.Start.CompareTo(b.Start));

            var builder = new System.Text.StringBuilder(s.Length + 16);
            int spanIndex = 0;
            int index = 0;
            while (index < s.Length) {
                if (spanIndex < preserved.Count && index == preserved[spanIndex].Start) {
                    var span = preserved[spanIndex];
                    builder.Append(s, span.Start, span.Length);
                    index += span.Length;
                    spanIndex++;
                    continue;
                }

                char current = s[index];
                if (current == '<') {
                    builder.Append("&lt;");
                }
                else if (current == '>') {
                    builder.Append("&gt;");
                }
                else {
                    builder.Append(current);
                }

                index++;
            }

            return builder.ToString();
        }

        private static string EscapeAnglesNoPreservedSpans(string s) {
            var sb = new System.Text.StringBuilder(s.Length + 16);
            for (int i = 0; i < s.Length; i++) {
                char ch = s[i];
                if (ch == '<') {
                    sb.Append("&lt;");
                }
                else if (ch == '>') {
                    sb.Append("&gt;");
                }
                else {
                    sb.Append(ch);
                }
            }
            return sb.ToString();
        }

        private static bool IsNameChar(char ch) {
            return char.IsLetterOrDigit(ch) || ch == ':' || ch == '_' || ch == '-';
        }

        private static bool NamesEqual(string text, int startA, int lengthA, int startB, int lengthB) {
            if (lengthA != lengthB) {
                return false;
            }

            return string.Compare(text, startA, text, startB, lengthA, System.StringComparison.OrdinalIgnoreCase) == 0;
        }

        private readonly struct TagSpan {
            public TagSpan(int start, int length) {
                Start = start;
                Length = length;
            }

            public int Start { get; }
            public int Length { get; }
        }

        private readonly struct OpenTagInfo {
            public OpenTagInfo(int spanStart, int spanLength, int nameStart, int nameLength) {
                SpanStart = spanStart;
                SpanLength = spanLength;
                NameStart = nameStart;
                NameLength = nameLength;
            }

            public int SpanStart { get; }
            public int SpanLength { get; }
            public int NameStart { get; }
            public int NameLength { get; }
        }
    }
}

