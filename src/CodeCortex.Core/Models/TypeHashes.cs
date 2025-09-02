namespace CodeCortex.Core.Models;

/// <summary>
/// 表示针对单个类型生成的一组分层哈希值。
/// </summary>
/// <param name="Structure">公开(及可选 internal)的“结构”签名（命名空间/类型/成员签名，不含实现体）哈希。</param>
/// <param name="PublicImpl">公共可见成员实现体（包含语句/表达式）归约后哈希。</param>
/// <param name="InternalImpl">internal 成员实现体归约后哈希。</param>
/// <param name="XmlDoc">XML 文档（可按策略截取或规范化）哈希。</param>
/// <param name="Cosmetic">仅代表“外观”(格式/缩进/空白/注释等)提取出的哈希，用于检测纯格式改动。</param>
/// <param name="Impl">综合实现（PublicImpl + InternalImpl + 其它实现相关层）聚合哈希。</param>
public sealed record TypeHashes(
    string Structure,
    string PublicImpl,
    string InternalImpl,
    string XmlDoc,
    string Cosmetic,
    string Impl
) {
    /// <summary>
    /// 空哈希占位（所有字段为空字符串）。
    /// </summary>
    public static readonly TypeHashes Empty = new("", "", "", "", "", "");
}
