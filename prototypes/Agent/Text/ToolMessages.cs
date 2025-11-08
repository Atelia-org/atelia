using System.Globalization;

namespace Atelia.Agent.Text;

/// <summary>
/// 工具输出消息的常量定义
/// 集中管理所有工具返回给 LLM 的消息文本，确保实现与测试的一致性
/// </summary>
public static class ToolMessages {
    public const string NoNewContent = "无新内容追加";

    // 格式化成功消息
    public static string FormatReplaceSuccess(int delta, int newMemoryLength, string notebookLabel) {
        var label = FormatNotebookLabel(notebookLabel);
        return $"{label}已更新。Δ {FormatDelta(delta)}，新长度 {newMemoryLength}";
    }

    public static string FormatAppendSuccess(int delta, int newMemoryLength, string notebookLabel) {
        var label = FormatNotebookLabel(notebookLabel);
        return $"{label}已追加内容。Δ {FormatDelta(delta)}，新长度 {newMemoryLength}";
    }

    public static string FormatNoContentToAppend(int currentMemoryLength, string notebookLabel) {
        var label = FormatNotebookLabel(notebookLabel);
        return $"{label}未追加新内容。当前长度 {currentMemoryLength}";
    }

    private static string FormatNotebookLabel(string notebookLabel) {
        var label = notebookLabel.Trim();
        return $"[{label}]";
    }

    private static string FormatDelta(int delta) {
        return delta >= 0
            ? "+" + delta.ToString(CultureInfo.InvariantCulture)
            : delta.ToString(CultureInfo.InvariantCulture);
    }
}
