using System;
using System.Collections.Generic;

namespace Atelia.Agent.Text;

internal sealed class SpanRegionLocator : IRegionLocator {
    private readonly string _regionStart;
    private readonly string _regionEnd;

    public SpanRegionLocator(string regionStart, string regionEnd) {
        _regionStart = regionStart;
        _regionEnd = regionEnd;
    }

    public RegionLocateResult Locate(string memory, ReplacementRequest request) {
        if (string.IsNullOrEmpty(_regionStart) || string.IsNullOrEmpty(_regionEnd)) { return RegionLocateResult.Failure("old_span_start 和 old_span_end 不能为空"); }

        var startMatches = FindAllMatchesFrom(memory, _regionStart, 0);

        if (startMatches.Count == 0) { return RegionLocateResult.Failure($"未找到 old_span_start: '{_regionStart}'"); }

        if (startMatches.Count > 1) { return RegionLocateResult.Failure($"找到 {startMatches.Count} 处 old_span_start，请提供更精确的标记"); }

        var startIndex = startMatches[0];
        var endIndex = memory.IndexOf(_regionEnd, startIndex + _regionStart.Length, StringComparison.Ordinal);
        if (endIndex < 0) { return RegionLocateResult.Failure($"未找到 old_span_end: '{_regionEnd}'"); }

        var length = endIndex + _regionEnd.Length - startIndex;
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
