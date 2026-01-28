using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Atelia.Analyzers.Style {
    public static class XmlDocAngleEscaper {
        public static string EscapeAngles(DocumentationCommentTriviaSyntax doc, string original) {
            if (string.IsNullOrEmpty(original)) {
                return original;
            }

            var preserved = CollectPreservedSpans(doc, original.Length);
            if (preserved.Count == 0) {
                return EscapeAnglesNoPreservedSpans(original);
            }

            var builder = new StringBuilder(original.Length + 16);
            int spanIndex = 0;
            int index = 0;
            while (index < original.Length) {
                if (spanIndex < preserved.Count && index == preserved[spanIndex].Start) {
                    var span = preserved[spanIndex];
                    builder.Append(original, span.Start, span.Length);
                    index += span.Length;
                    spanIndex++;
                    continue;
                }

                char current = original[index];
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
            var sb = new StringBuilder(s.Length + 16);
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

        private static List<TagSpan> CollectPreservedSpans(DocumentationCommentTriviaSyntax doc, int originalLength) {
            var spans = new List<TagSpan>(capacity: 8);
            var tags = new List<TagToken>(capacity: 8);
            int baseOffset = doc.FullSpan.Start;

            bool TryGetRelativeSpan(TextSpan span, out TagSpan result) {
                int start = span.Start - baseOffset;
                if (start < 0 || start >= originalLength) {
                    result = default;
                    return false;
                }
                int length = Math.Min(span.Length, originalLength - start);
                if (length <= 0) {
                    result = default;
                    return false;
                }
                result = new TagSpan(start, length);
                return true;
            }

            void AddSpan(TextSpan span) {
                if (TryGetRelativeSpan(span, out var relative)) {
                    spans.Add(relative);
                }
            }

            foreach (var node in doc.DescendantNodes()) {
                switch (node) {
                    case XmlElementSyntax element:
                        if (TryCreateStartTagToken(element.StartTag, baseOffset, originalLength, out var startTag)) {
                            tags.Add(startTag);
                        }
                        if (TryCreateEndTagToken(element.EndTag, baseOffset, originalLength, out var endTag)) {
                            tags.Add(endTag);
                        }
                        break;
                    case XmlEmptyElementSyntax empty:
                        if (TryGetRelativeSpan(empty.Span, out var emptySpan) && IsWellFormedEmptyElement(empty)) {
                            spans.Add(emptySpan);
                        }
                        break;
                    case XmlCommentSyntax comment:
                        AddSpan(comment.Span);
                        break;
                    case XmlCDataSectionSyntax cdata:
                        AddSpan(cdata.Span);
                        break;
                    case XmlProcessingInstructionSyntax pi:
                        AddSpan(pi.Span);
                        break;
                }
            }

            AddMatchedTagSpans(tags, spans);
            return NormalizeSpans(spans);
        }

        private static void AddMatchedTagSpans(List<TagToken> tags, List<TagSpan> spans) {
            if (tags.Count == 0) {
                return;
            }

            tags.Sort((a, b) => a.Start.CompareTo(b.Start));
            var openTags = new List<TagToken>(capacity: 4);
            foreach (var tag in tags) {
                if (!tag.IsEnd) {
                    openTags.Add(tag);
                    continue;
                }

                int matchIndex = FindMatchingOpenTagIndex(openTags, tag.Name);
                if (matchIndex < 0) {
                    continue;
                }

                spans.Add(new TagSpan(openTags[matchIndex].Start, openTags[matchIndex].Length));
                spans.Add(new TagSpan(tag.Start, tag.Length));
                openTags.RemoveRange(matchIndex, openTags.Count - matchIndex);
            }
        }

        private static int FindMatchingOpenTagIndex(List<TagToken> openTags, string name) {
            for (int i = openTags.Count - 1; i >= 0; i--) {
                if (string.Equals(openTags[i].Name, name, StringComparison.OrdinalIgnoreCase)) {
                    return i;
                }
            }
            return -1;
        }

        private static bool TryCreateStartTagToken(XmlElementStartTagSyntax? tag, int baseOffset, int originalLength, out TagToken token) {
            token = default;
            if (tag == null || tag.Name == null || tag.Name.IsMissing) {
                return false;
            }
            if (tag.LessThanToken.IsMissing || tag.GreaterThanToken.IsMissing) {
                return false;
            }
            if (!TryCreateRelativeSpan(tag.Span, baseOffset, originalLength, out var span)) {
                return false;
            }

            token = new TagToken(span.Start, span.Length, tag.Name.ToString(), isEnd: false);
            return true;
        }

        private static bool TryCreateEndTagToken(XmlElementEndTagSyntax? tag, int baseOffset, int originalLength, out TagToken token) {
            token = default;
            if (tag == null || tag.Name == null || tag.Name.IsMissing) {
                return false;
            }
            if (tag.LessThanSlashToken.IsMissing || tag.GreaterThanToken.IsMissing) {
                return false;
            }
            if (!TryCreateRelativeSpan(tag.Span, baseOffset, originalLength, out var span)) {
                return false;
            }

            token = new TagToken(span.Start, span.Length, tag.Name.ToString(), isEnd: true);
            return true;
        }

        private static bool TryCreateRelativeSpan(TextSpan span, int baseOffset, int originalLength, out TagSpan result) {
            int start = span.Start - baseOffset;
            if (start < 0 || start >= originalLength) {
                result = default;
                return false;
            }
            int length = Math.Min(span.Length, originalLength - start);
            if (length <= 0) {
                result = default;
                return false;
            }
            result = new TagSpan(start, length);
            return true;
        }

        private static List<TagSpan> NormalizeSpans(List<TagSpan> spans) {
            if (spans.Count == 0) {
                return spans;
            }

            spans.Sort((a, b) => a.Start.CompareTo(b.Start));
            var normalized = new List<TagSpan>(spans.Count);
            foreach (var span in spans) {
                if (normalized.Count == 0) {
                    normalized.Add(span);
                    continue;
                }

                var last = normalized[normalized.Count - 1];
                int lastEnd = last.Start + last.Length;
                if (span.Start > lastEnd) {
                    normalized.Add(span);
                    continue;
                }

                int newEnd = Math.Max(lastEnd, span.Start + span.Length);
                normalized[normalized.Count - 1] = new TagSpan(last.Start, newEnd - last.Start);
            }

            return normalized;
        }

        private static bool IsWellFormedEmptyElement(XmlEmptyElementSyntax element) {
            if (element.Name == null || element.Name.IsMissing) {
                return false;
            }

            return !element.LessThanToken.IsMissing && !element.SlashGreaterThanToken.IsMissing;
        }

        private readonly struct TagSpan {
            public TagSpan(int start, int length) {
                Start = start;
                Length = length;
            }

            public int Start { get; }
            public int Length { get; }
        }

        private readonly struct TagToken {
            public TagToken(int start, int length, string name, bool isEnd) {
                Start = start;
                Length = length;
                Name = name;
                IsEnd = isEnd;
            }

            public int Start { get; }
            public int Length { get; }
            public string Name { get; }
            public bool IsEnd { get; }
        }
    }
}
