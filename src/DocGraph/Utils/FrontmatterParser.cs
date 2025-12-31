// DocGraph v0.1 - Frontmatter 解析器
// 参考：spec.md §2 Frontmatter 解析约束

using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Atelia.DocGraph.Utils;

/// <summary>
/// Frontmatter 解析器。
/// 实现 spec.md [S-FRONTMATTER-001] ~ [S-FRONTMATTER-006] 条款。
/// </summary>
public static class FrontmatterParser
{
    /// <summary>
    /// Frontmatter 最大字节数（64 KB）。
    /// </summary>
    private const int MaxFrontmatterSize = 64 * 1024;

    /// <summary>
    /// YAML 最大嵌套深度。
    /// </summary>
    private const int MaxNestingDepth = 10;

    /// <summary>
    /// Frontmatter 起止标记。
    /// </summary>
    private const string FrontmatterDelimiter = "---";

    /// <summary>
    /// 尝试从文件内容解析 frontmatter。
    /// 遵循 [S-FRONTMATTER-001]：边界检测规则。
    /// </summary>
    /// <param name="content">文件完整内容。</param>
    /// <param name="frontmatter">解析出的 frontmatter 字典。</param>
    /// <param name="error">错误信息（解析失败时）。</param>
    /// <returns>是否成功解析出 frontmatter。</returns>
    public static bool TryParse(
        string content,
        out Dictionary<string, object?>? frontmatter,
        out FrontmatterError? error)
    {
        frontmatter = null;
        error = null;

        if (string.IsNullOrEmpty(content))
        {
            return false; // 无 frontmatter（正常情况）
        }

        // 1. 检测起始标记
        // [S-FRONTMATTER-001]：起始标记前允许空白字符，但不允许其他内容
        var trimmedStart = content.TrimStart();
        if (!trimmedStart.StartsWith(FrontmatterDelimiter))
        {
            return false; // 无 frontmatter
        }

        // 找到起始标记后的位置
        var startIndex = content.IndexOf(FrontmatterDelimiter, StringComparison.Ordinal);
        var afterStart = startIndex + FrontmatterDelimiter.Length;

        // 跳过起始标记后的换行符
        if (afterStart < content.Length && content[afterStart] == '\r')
        {
            afterStart++;
        }
        if (afterStart < content.Length && content[afterStart] == '\n')
        {
            afterStart++;
        }

        // 2. 寻找结束标记
        var endIndex = FindEndDelimiter(content, afterStart);
        if (endIndex < 0)
        {
            // 没有找到结束标记
            return false;
        }

        // 3. 提取 YAML 内容
        var yamlContent = content[afterStart..endIndex];

        // 4. 检查大小限制 [S-FRONTMATTER-003]
        var yamlBytes = System.Text.Encoding.UTF8.GetByteCount(yamlContent);
        if (yamlBytes > MaxFrontmatterSize)
        {
            error = new FrontmatterError(
                "DOCGRAPH_YAML_SIZE_EXCEEDED",
                $"Frontmatter 超过大小限制（{yamlBytes} bytes > {MaxFrontmatterSize} bytes）");
            return false;
        }

        // 5. 解析 YAML
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .Build();

            var parsed = deserializer.Deserialize<Dictionary<string, object?>>(yamlContent);
            if (parsed == null)
            {
                return false; // 空的 frontmatter
            }

            // 6. 检查嵌套深度
            var maxDepth = CalculateMaxDepth(parsed);
            if (maxDepth > MaxNestingDepth)
            {
                error = new FrontmatterError(
                    "DOCGRAPH_YAML_DEPTH_EXCEEDED",
                    $"YAML 嵌套深度超过限制（{maxDepth} > {MaxNestingDepth}）");
                return false;
            }

            // 7. 检查 anchor/alias [S-FRONTMATTER-002]
            if (ContainsAnchorOrAlias(yamlContent))
            {
                error = new FrontmatterError(
                    "DOCGRAPH_YAML_ALIAS_DETECTED",
                    "Frontmatter 中禁止使用 YAML anchor/alias（& 和 *）");
                return false;
            }

            frontmatter = parsed;
            return true;
        }
        catch (YamlException ex)
        {
            error = new FrontmatterError(
                "DOCGRAPH_YAML_SYNTAX_ERROR",
                $"YAML 语法错误: {ex.Message}",
                (int)ex.Start.Line,
                (int)ex.Start.Column);
            return false;
        }
        catch (Exception ex)
        {
            error = new FrontmatterError(
                "DOCGRAPH_YAML_STRUCTURE_ERROR",
                $"YAML 结构错误: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 从文件读取并解析 frontmatter。
    /// </summary>
    /// <param name="filePath">文件绝对路径。</param>
    /// <param name="frontmatter">解析出的 frontmatter 字典。</param>
    /// <param name="error">错误信息（解析失败时）。</param>
    /// <returns>是否成功解析出 frontmatter。</returns>
    public static bool TryParseFile(
        string filePath,
        out Dictionary<string, object?>? frontmatter,
        out FrontmatterError? error)
    {
        frontmatter = null;
        error = null;

        try
        {
            var content = File.ReadAllText(filePath);
            return TryParse(content, out frontmatter, out error);
        }
        catch (IOException ex)
        {
            error = new FrontmatterError(
                "DOCGRAPH_IO_DECODE_FAILED",
                $"文件读取失败: {ex.Message}");
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            error = new FrontmatterError(
                "DOCGRAPH_IO_DECODE_FAILED",
                $"文件访问被拒绝: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 从 frontmatter 中提取字符串字段。
    /// </summary>
    public static string? GetString(IReadOnlyDictionary<string, object?> frontmatter, string key)
    {
        if (!frontmatter.TryGetValue(key, out var value) || value == null)
        {
            return null;
        }

        return value.ToString();
    }

    /// <summary>
    /// 从 frontmatter 中提取字符串数组字段。
    /// </summary>
    public static List<string> GetStringArray(IReadOnlyDictionary<string, object?> frontmatter, string key)
    {
        var result = new List<string>();

        if (!frontmatter.TryGetValue(key, out var value) || value == null)
        {
            return result;
        }

        if (value is IEnumerable<object> list)
        {
            foreach (var item in list)
            {
                if (item != null)
                {
                    result.Add(item.ToString() ?? "");
                }
            }
        }
        else if (value is string singleValue)
        {
            // 支持单个字符串值自动转为数组
            result.Add(singleValue);
        }

        return result;
    }

    /// <summary>
    /// 寻找结束标记位置。
    /// </summary>
    private static int FindEndDelimiter(string content, int startFrom)
    {
        var searchStart = startFrom;

        while (searchStart < content.Length)
        {
            // 查找下一个 ---
            var index = content.IndexOf(FrontmatterDelimiter, searchStart, StringComparison.Ordinal);
            if (index < 0)
            {
                return -1;
            }

            // 检查 --- 是否在行首
            if (index == 0 || content[index - 1] == '\n')
            {
                return index;
            }

            // 继续搜索
            searchStart = index + FrontmatterDelimiter.Length;
        }

        return -1;
    }

    /// <summary>
    /// 计算嵌套深度。
    /// </summary>
    private static int CalculateMaxDepth(object? obj, int currentDepth = 1)
    {
        if (obj == null)
        {
            return currentDepth;
        }

        var maxDepth = currentDepth;

        if (obj is IDictionary<string, object?> dict)
        {
            foreach (var value in dict.Values)
            {
                var childDepth = CalculateMaxDepth(value, currentDepth + 1);
                maxDepth = Math.Max(maxDepth, childDepth);
            }
        }
        else if (obj is IEnumerable<object> list && obj is not string)
        {
            foreach (var item in list)
            {
                var childDepth = CalculateMaxDepth(item, currentDepth + 1);
                maxDepth = Math.Max(maxDepth, childDepth);
            }
        }

        return maxDepth;
    }

    /// <summary>
    /// 检查 YAML 内容是否包含 anchor 或 alias。
    /// 简化实现：检查 & 和 * 字符的使用模式。
    /// </summary>
    private static bool ContainsAnchorOrAlias(string yamlContent)
    {
        // 简化检测：查找可能的 anchor (&name) 或 alias (*name) 模式
        // 注意：这是简化实现，可能会有误报，但 v0.1 优先简化
        var lines = yamlContent.Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            // 跳过注释行
            if (trimmed.StartsWith('#'))
            {
                continue;
            }

            // 检查是否包含 anchor 定义 (&name)
            if (System.Text.RegularExpressions.Regex.IsMatch(line, @"&\w+"))
            {
                return true;
            }

            // 检查是否包含 alias 引用 (*name)
            if (System.Text.RegularExpressions.Regex.IsMatch(line, @"\*\w+"))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Frontmatter 解析错误。
/// </summary>
public class FrontmatterError
{
    /// <summary>
    /// 错误码。
    /// </summary>
    public string ErrorCode { get; }

    /// <summary>
    /// 错误消息。
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// 行号（可选）。
    /// </summary>
    public int? Line { get; }

    /// <summary>
    /// 列号（可选）。
    /// </summary>
    public int? Column { get; }

    public FrontmatterError(string errorCode, string message, int? line = null, int? column = null)
    {
        ErrorCode = errorCode;
        Message = message;
        Line = line;
        Column = column;
    }
}
