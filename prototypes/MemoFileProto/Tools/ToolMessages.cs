namespace MemoFileProto.Tools;

/// <summary>
/// 工具输出消息的常量定义
/// 集中管理所有工具返回给 LLM 的消息文本，确保实现与测试的一致性
/// </summary>
public static class ToolMessages {
    // 成功消息前缀
    public const string Updated = "记忆已更新";
    public const string ContentAppended = "已追加内容到记忆末尾";
    public const string NoNewContent = "无新内容追加";

    // 格式化成功消息
    public static string FormatUpdate(int startIndex, int replaceLength, int newMemoryLength) {
        return $"{Updated}: 起始位置 {startIndex}, 替换长度 {replaceLength}, 新记忆长度 {newMemoryLength}";
    }

    public static string FormatAppendSuccess(int newMemoryLength) {
        return $"{ContentAppended}。当前记忆长度: {newMemoryLength}";
    }

    public static string FormatNoContentToAppend(int currentMemoryLength) {
        return $"{NoNewContent}。当前记忆长度: {currentMemoryLength}";
    }
}
