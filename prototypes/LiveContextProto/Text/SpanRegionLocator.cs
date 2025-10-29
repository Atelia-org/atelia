using System;
using System.Collections.Generic;

namespace Atelia.LiveContextProto.Text;

internal sealed class SpanRegionLocator : IRegionLocator {
    private readonly string _regionStart;
    private readonly string _regionEnd;

    public SpanRegionLocator(string regionStart, string regionEnd) {
        _regionStart = regionStart;
        _regionEnd = regionEnd;
    }

    public RegionLocateResult Locate(string memory, ReplacementRequest request, AnchorResolution anchor) {
        if (string.IsNullOrEmpty(_regionStart) || string.IsNullOrEmpty(_regionEnd)) { return RegionLocateResult.Failure("Error: old_span_start 和 old_span_end 不能为空"); }

        var searchStart = anchor.SearchStart;
        var startMatches = FindAllMatchesFrom(memory, _regionStart, searchStart);

        if (startMatches.Count == 0) {
            var anchorLabel = anchor.IsRequested
                ? request.SearchAfter is null
                    ? "search_after"
                    : request.SearchAfter.Length == 0
                        ? "search_after=''"
                        : $"search_after '{request.SearchAfter}'"
                : null;

            var message = anchorLabel is null
                ? $"Error: 找不到 old_span_start: '{_regionStart}'"
                : $"Error: 找不到 old_span_start: '{_regionStart}' (在 {anchorLabel} 之后)";
            return RegionLocateResult.Failure(message);
        }

        if (startMatches.Count > 1) {
            var contextInfo = TextToolUtilities.FormatMatchesForError(startMatches, memory, _regionStart.Length, 80);
            var message = $"Error: 找到 {startMatches.Count} 个 old_span_start 匹配。\n\n{contextInfo}\n\n请设置 search_after 锚点或提供更精确的标记。";
            return RegionLocateResult.Failure(message);
        }

        var startIndex = startMatches[0];
        var endIndex = memory.IndexOf(_regionEnd, startIndex + _regionStart.Length, StringComparison.Ordinal);
        if (endIndex < 0) {
            var context = TextToolUtilities.GetContext(memory, startIndex, _regionStart.Length, 80);
            var message = $"Error: 找不到 old_span_end: '{_regionEnd}' (在 old_span_start 之后)\n\nold_span_start 位置附近内容：\n{context}";
            return RegionLocateResult.Failure(message);
        }

        var length = (endIndex + _regionEnd.Length) - startIndex;
        return RegionLocateResult.SuccessAt(startIndex, length);
    }

    private static List<int> FindAllMatchesFrom(string text, string pattern, int searchStart) {
        var matches = new List<int>();
        var index = searchStart;

        while (index <= text.Length) {
            var found = text.IndexOf(pattern, index, StringComparison.Ordinal);
            if (found < 0) { break; }
            matches.Add(found);
            index = found + Math.Max(pattern.Length, 1);
        }

        return matches;
    }
}
