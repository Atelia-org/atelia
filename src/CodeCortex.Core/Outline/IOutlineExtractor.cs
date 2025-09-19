using Microsoft.CodeAnalysis;
using CodeCortex.Core.Models;

namespace CodeCortex.Core.Outline;

/// <summary>
/// 负责根据类型符号和已生成哈希构造结构化 Outline（用于提示窗口或上下文裁剪）。
/// </summary>
public interface IOutlineExtractor {
    /// <summary>
    /// 构建类型 Outline 文本/结构（格式尚未最终定稿，可为多行可解析文本）。
    /// </summary>
    /// <param name="symbol">Roslyn 类型符号。</param>
    /// <param name="hashes">该类型已计算的哈希集合。</param>
    /// <param name="options">控制包含内容的选项。</param>
    /// <returns>Outline 表示（临时以字符串形式）。</returns>
    string BuildOutline(INamedTypeSymbol symbol, TypeHashes hashes, OutlineOptions options);
}

/// <summary>
/// Outline 生成选项（表格前空行已固定开启以符合块级元素规范）。
/// </summary>
/// <param name="IncludeXmlDocFirstLine">是否在 Outline 顶部包含 XML 文档首行摘要。</param>
public sealed record OutlineOptions(bool IncludeXmlDocFirstLine = true);
