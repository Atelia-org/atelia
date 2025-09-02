using Microsoft.CodeAnalysis;
using CodeCortex.Core.Models;

namespace CodeCortex.Core.Hashing;

/// <summary>
/// Computes a set of stable hash digests for a type symbol, partitioned into semantic layers
/// (structure, public implementation, internal implementation, XML doc, cosmetic, composite impl).
/// These hashes will be used in later增量构建与变化检测阶段。
/// </summary>
public interface ITypeHasher {
    /// <summary>
    /// 计算指定类型的多维度哈希。
    /// </summary>
    /// <param name="symbol">目标类型符号（命名类型，包含语义信息）。</param>
    /// <param name="partialFilePaths">参与该类型定义的（可能被裁剪/拆分后的）源码文件相对路径集合。</param>
    /// <param name="config">控制结构哈希包含范围与重写策略的配置。</param>
    /// <returns><see cref="TypeHashes"/> 包含各分类哈希值。</returns>
    TypeHashes Compute(INamedTypeSymbol symbol, IReadOnlyList<string> partialFilePaths, HashConfig config);
}

/// <summary>
/// 类型哈希计算的配置选项。
/// </summary>
/// <param name="IncludeInternalInStructureHash">若为 true，则结构哈希把 internal 成员也视为“结构”组成部分。</param>
/// <param name="StructureHashIncludesXmlDoc">若为 true，结构哈希会把 XML 文档首行/摘要并入（用于文档敏感变更）。</param>
/// <param name="SkipCosmeticRewrite">若为 true，跳过对源码进行格式/空白等“外观”归一化重写（调试或性能考虑）。</param>
public sealed record HashConfig(
    bool IncludeInternalInStructureHash = false,
    bool StructureHashIncludesXmlDoc = false,
    bool SkipCosmeticRewrite = false
);
