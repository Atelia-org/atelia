using System;
using System.Collections.Generic;

namespace MemoFileProto.Tools;

internal sealed class LiteralRegionLocator : IRegionLocator {
    private readonly string _oldText;

    public LiteralRegionLocator(string oldText) {
        _oldText = oldText;
    }

    public RegionLocateResult Locate(string memory, ReplacementRequest request, AnchorResolution anchor) {
        if (string.IsNullOrEmpty(_oldText)) { return RegionLocateResult.Failure("Error: old_text 不能为空"); }

        if (!anchor.IsRequested) { return LocateUnique(memory, request); }

        return LocateAfterAnchor(memory, request, anchor);
    }

    private RegionLocateResult LocateUnique(string memory, ReplacementRequest request) {
        var matches = FindAllMatches(memory, _oldText);
        if (matches.Count == 0) { return RegionLocateResult.Failure("Error: 找不到要替换的文本。请确认 old_text 精确匹配[Memory Notebook]内容。"); }

        if (matches.Count > 1) {
            var contextInfo = TextToolUtilities.FormatMatchesForError(matches, memory, _oldText.Length, 80);
            var message = $"Error: 找到 {matches.Count} 个匹配项。\n\n{contextInfo}\n\n请使用 search_after 参数来定位目标匹配项。";
            return RegionLocateResult.Failure(message);
        }

        return RegionLocateResult.SuccessAt(matches[0], _oldText.Length);
    }

    private RegionLocateResult LocateAfterAnchor(string memory, ReplacementRequest request, AnchorResolution anchor) {
        var searchStart = anchor.SearchStart;
        var targetIndex = memory.IndexOf(_oldText, searchStart, StringComparison.Ordinal);
        if (targetIndex < 0) {
            var anchorLabel = request.SearchAfter is null
                ? "search_after"
                : request.SearchAfter.Length == 0
                    ? "search_after=''"
                    : $"search_after '{request.SearchAfter}'";

            var context = TextToolUtilities.GetContext(memory, Math.Min(searchStart, memory.Length - 1), 0, 80);
            var message = $"Error: 在 {anchorLabel} 之后找不到要替换的文本。\n\n搜索起始位置: {searchStart}\n附近内容：\n{context}";
            return RegionLocateResult.Failure(message);
        }

        return RegionLocateResult.SuccessAt(targetIndex, _oldText.Length);
    }

    private static List<int> FindAllMatches(string text, string pattern) {
        var matches = new List<int>();
        var index = 0;

        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0) {
            matches.Add(index);
            index += Math.Max(pattern.Length, 1);
        }

        return matches;
    }
}
