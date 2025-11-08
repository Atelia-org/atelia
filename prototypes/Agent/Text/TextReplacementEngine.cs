using System;
using System.Text;

namespace Atelia.Agent.Text;

internal interface IRegionLocator {
    RegionLocateResult Locate(string memory, ReplacementRequest request);
}

internal readonly record struct RegionLocateResult(
    bool Success,
    int StartIndex,
    int Length,
    string? ErrorMessage
) {
    public static RegionLocateResult Failure(string error) => new(false, -1, 0, error);
    public static RegionLocateResult SuccessAt(int startIndex, int length) => new(true, startIndex, length, null);
}

internal readonly record struct ReplacementRequest(
    string OldText,
    string NewText,
    bool IsAppend,
    string OperationName
);

internal readonly record struct ReplacementOutcome(
    bool Success,
    string Message
) {
    public static ReplacementOutcome Fail(string message) => new(false, message);
    public static ReplacementOutcome Succeed(string message) => new(true, message);
}

internal sealed class TextReplacementEngine {
    private readonly Func<string> _getMemory;
    private readonly Action<string> _setMemory;
    private readonly string _notebookLabel;

    public TextReplacementEngine(Func<string> getMemory, Action<string> setMemory, string notebookLabel) {
        _getMemory = getMemory;
        _setMemory = setMemory;
        _notebookLabel = notebookLabel.Trim();
    }

    public ReplacementOutcome Execute(ReplacementRequest request, IRegionLocator? locator) {
        try {
            var currentMemory = TextToolUtilities.NormalizeLineEndings(_getMemory() ?? string.Empty);
            var newText = TextToolUtilities.NormalizeLineEndings(request.NewText ?? string.Empty);

            if (request.IsAppend) { return Append(currentMemory, newText); }

            if (locator is null) { return ReplacementOutcome.Fail("未提供定位策略"); }

            var oldText = TextToolUtilities.NormalizeLineEndings(request.OldText ?? string.Empty);
            if (string.IsNullOrEmpty(oldText)) { return ReplacementOutcome.Fail("old_text 不能为空"); }

            var locateResult = locator.Locate(currentMemory, request with { OldText = oldText });
            if (!locateResult.Success) { return ReplacementOutcome.Fail(locateResult.ErrorMessage ?? "未能定位到替换区域"); }

            var updatedMemory = ReplaceSegment(currentMemory, locateResult.StartIndex, locateResult.Length, newText);
            _setMemory(updatedMemory);

            var delta = updatedMemory.Length - currentMemory.Length;
            return ReplacementOutcome.Succeed(
                ToolMessages.FormatReplaceSuccess(delta, updatedMemory.Length, _notebookLabel)
            );
        }
        catch (Exception ex) {
            return ReplacementOutcome.Fail(ex.Message);
        }
    }

    private ReplacementOutcome Append(string currentMemory, string newText) {
        var hasExistingMemory = !string.IsNullOrEmpty(currentMemory);
        var hasNewContent = !string.IsNullOrEmpty(newText);

        if (!hasNewContent) { return ReplacementOutcome.Succeed(ToolMessages.FormatNoContentToAppend(currentMemory.Length, _notebookLabel)); }

        var needsSeparator = hasExistingMemory
            && !currentMemory.EndsWith("\n", StringComparison.Ordinal)
            && !newText.StartsWith("\n", StringComparison.Ordinal);

        var appendedMemory = needsSeparator
            ? currentMemory + "\n" + newText
            : currentMemory + newText;

        _setMemory(appendedMemory);
        var delta = appendedMemory.Length - currentMemory.Length;
        return ReplacementOutcome.Succeed(ToolMessages.FormatAppendSuccess(delta, appendedMemory.Length, _notebookLabel));
    }

    private static string ReplaceSegment(string memory, int start, int length, string replacement) {
        var builder = new StringBuilder();
        builder.Append(memory, 0, start);
        builder.Append(replacement);
        builder.Append(memory, start + length, memory.Length - (start + length));
        return builder.ToString();
    }
}
