using System.Diagnostics.CodeAnalysis;

namespace Atelia.Agent.Core.History;

/// <summary>
/// 定义 token 信息量估算器的统一接口。实现可根据需要压缩或放大信息量指标，
/// 返回的结果应为未偏移的原始估算值，可为零。
/// </summary>
public interface ITokenEstimator {
    /// <summary>
    /// 估算指定文本的 token 数量。
    /// </summary>
    /// <param name="content">需要估算的文本内容；若传入 <c>null</c>，视为无内容并返回 <c>0</c>。</param>
    /// <returns>估算得到的非负 token 数量。</returns>
    uint Estimate(string? content);
}

/// <summary>
/// 一个简单的默认实现，通过字符长度的一半近似 token 数量。
/// </summary>
public sealed class NaiveTokenEstimator : ITokenEstimator {
    /// <inheritdoc />
    public uint Estimate([StringSyntax("Text")] string? content) {
        if (string.IsNullOrEmpty(content)) { return 0; }

        return (uint)(content.Length / 2);
    }
}
