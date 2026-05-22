using System;
using System.Collections.Generic;

namespace Atelia.Agent.Text;

internal sealed class LiteralRegionLocator : IRegionLocator {
    private readonly string _oldText;

    public LiteralRegionLocator(string oldText) {
        _oldText = oldText;
    }

    public RegionLocateResult Locate(string memory, ReplacementRequest request) {
        if (string.IsNullOrEmpty(_oldText)) { return RegionLocateResult.Failure("old_text 不能为空"); }

        var matches = FindAllMatches(memory, _oldText);
        if (matches.Count == 0) { return RegionLocateResult.Failure("未找到要替换的文本"); }

        if (matches.Count > 1) { return RegionLocateResult.Failure($"找到 {matches.Count} 处匹配，请提供更精确的旧文本"); }

        return RegionLocateResult.SuccessAt(matches[0], _oldText.Length);
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
