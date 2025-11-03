namespace Atelia.Agent.Text;

/// <summary>
/// 工具输出消息的常量定义
/// 集中管理所有工具返回给 LLM 的消息文本，确保实现与测试的一致性
/// </summary>
public static class ToolMessages {
    public const string NoNewContent = "无新内容追加";

    // 格式化成功消息
    public static string FormatUpdate(int startIndex, int replaceLength, int newMemoryLength, string notebookLabel) {
        var label = FormatNotebookLabel(notebookLabel);
        return $"{label}已更新: 起始位置 {startIndex}, 替换长度 {replaceLength}, 新{label}长度 {newMemoryLength}";
    }

    public static string FormatAppendSuccess(int newMemoryLength, string notebookLabel) {
        var label = FormatNotebookLabel(notebookLabel);
        return $"已追加内容到{label}末尾。当前{label}长度: {newMemoryLength}";
    }

    public static string FormatNoContentToAppend(int currentMemoryLength, string notebookLabel) {
        var label = FormatNotebookLabel(notebookLabel);
        return $"{NoNewContent}。当前{label}长度: {currentMemoryLength}";
    }

    private static string FormatNotebookLabel(string notebookLabel) {
        var label = notebookLabel.Trim();
        return $"[{label}]";
    }
}
