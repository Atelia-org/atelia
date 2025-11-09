namespace Atelia.Agent.Core;

/// <summary>
/// 分析后还是认为LOD概念比Verbosity概念更适合本项目。
/// Basic级别与Detail级别是互相全量替代关系，而非差值增量关系。
/// </summary>
public enum LevelOfDetail {
    Basic,
    Detail
}

/// <summary>
/// 表示一个包含Basic和Detail两级内容的不可变数据结构，用于在不同细节级别下提供相应的文本内容。
/// Basic级别与Detail级别是互相全量替代关系，而非差值增量关系。
/// 此类型保证 <see cref="Basic"/> 和 <see cref="Detail"/> 属性永不为 <c>null</c>，使用方无需进行空值检查。
/// </summary>
public sealed record LevelOfDetailContent {
    private readonly string _basic;
    private readonly string _detail;

    /// <summary>
    /// 初始化 <see cref="LevelOfDetailContent"/> 的新实例。
    /// </summary>
    /// <param name="basic">Basic级别的文本内容，不能为 <c>null</c>。</param>
    /// <param name="detail">Detail级别的文本内容，不能为 <c>null</c>。</param>
    /// <exception cref="ArgumentNullException">当 <paramref name="basic"/> 或 <paramref name="detail"/> 为 <c>null</c> 时抛出。</exception>
    public LevelOfDetailContent(string basic, string detail) {
        _basic = basic ?? throw new ArgumentNullException(nameof(basic));
        _detail = detail ?? throw new ArgumentNullException(nameof(detail));
    }

    /// <summary>
    /// 初始化 <see cref="LevelOfDetailContent"/> 的新实例，其中Detail内容与Basic内容相同。
    /// </summary>
    /// <param name="basic">Basic级别的文本内容，不能为 <c>null</c>。此内容也将用作Detail级别。</param>
    /// <exception cref="ArgumentNullException">当 <paramref name="basic"/> 为 <c>null</c> 时抛出。</exception>
    public LevelOfDetailContent(string basic) {
        _basic = basic ?? throw new ArgumentNullException(nameof(basic));
        _detail = basic;
    }

    /// <summary>
    /// 获取Basic级别的文本内容。此属性永不为 <c>null</c>。
    /// </summary>
    public string Basic => _basic;

    /// <summary>
    /// 获取Detail级别的文本内容。此属性永不为 <c>null</c>。
    /// </summary>
    public string Detail => _detail;

    /// <summary>
    /// 根据指定的细节级别获取相应的文本内容。
    /// </summary>
    /// <param name="detailLevel">要获取内容的细节级别。</param>
    /// <returns>对应级别的文本内容，永不为 <c>null</c>。</returns>
    public string GetContent(LevelOfDetail detailLevel)
        => detailLevel switch {
            LevelOfDetail.Basic => _basic,
            LevelOfDetail.Detail => Detail,
            _ => _basic
        };

    // Note: params IEnumerable<T> is a valid parameter type in C# 12+
    /// <summary>
    /// 将多个 <see cref="LevelOfDetailContent"/> 实例的内容使用指定的分隔符连接起来，形成新的实例。
    /// </summary>
    /// <param name="separator">用于连接内容的字符串分隔符，可以为 <c>null</c>。</param>
    /// <param name="items">要连接的 <see cref="LevelOfDetailContent"/> 实例集合。</param>
    /// <returns>连接后的新 <see cref="LevelOfDetailContent"/> 实例，其 <see cref="Basic"/> 和 <see cref="Detail"/> 内容分别由输入实例的对应内容连接而成。</returns>
    public static LevelOfDetailContent Join(string? separator, params IEnumerable<LevelOfDetailContent> items) {
        string basic = string.Join(separator, items.Select(x => x.Basic));
        string detail = string.Join(separator, items.Select(x => x.Detail));
        return new LevelOfDetailContent(basic, detail);
    }

    /// <summary>
    /// 将两个 <see cref="LevelOfDetailContent"/> 实例的内容使用指定的分隔符连接起来，形成新的实例。
    /// </summary>
    /// <param name="separator">用于连接内容的字符串分隔符，可以为 <c>null</c>。</param>
    /// <returns>连接后的新 <see cref="LevelOfDetailContent"/> 实例。</returns>
    public static LevelOfDetailContent Join(string? separator, LevelOfDetailContent item0, LevelOfDetailContent item1) {
        string basic = string.Join(separator, item0.Basic, item1.Basic);
        string detail = string.Join(separator, item0.Detail, item1.Detail);
        return new LevelOfDetailContent(basic, detail);
    }

    /// <summary>
    /// 将三个 <see cref="LevelOfDetailContent"/> 实例的内容使用指定的分隔符连接起来，形成新的实例。
    /// </summary>
    /// <param name="separator">用于连接内容的字符串分隔符，可以为 <c>null</c>。</param>
    /// <returns>连接后的新 <see cref="LevelOfDetailContent"/> 实例。</returns>
    public static LevelOfDetailContent Join(string? separator, LevelOfDetailContent item0, LevelOfDetailContent item1, LevelOfDetailContent item2) {
        string basic = string.Join(separator, item0.Basic, item1.Basic, item2.Basic);
        string detail = string.Join(separator, item0.Detail, item1.Detail, item2.Detail);
        return new LevelOfDetailContent(basic, detail);
    }

    /// <summary>
    /// 将四个 <see cref="LevelOfDetailContent"/> 实例的内容使用指定的分隔符连接起来，形成新的实例。
    /// </summary>
    /// <param name="separator">用于连接内容的字符串分隔符，可以为 <c>null</c>。</param>
    /// <returns>连接后的新 <see cref="LevelOfDetailContent"/> 实例。</returns>
    public static LevelOfDetailContent Join(string? separator, LevelOfDetailContent item0, LevelOfDetailContent item1, LevelOfDetailContent item2, LevelOfDetailContent item3) {
        string basic = string.Join(separator, item0.Basic, item1.Basic, item2.Basic, item3.Basic);
        string detail = string.Join(separator, item0.Detail, item1.Detail, item2.Detail, item3.Detail);
        return new LevelOfDetailContent(basic, detail);
    }

    /// <summary>
    /// 将五个 <see cref="LevelOfDetailContent"/> 实例的内容使用指定的分隔符连接起来，形成新的实例。
    /// </summary>
    /// <param name="separator">用于连接内容的字符串分隔符，可以为 <c>null</c>。</param>
    /// <returns>连接后的新 <see cref="LevelOfDetailContent"/> 实例。</returns>
    public static LevelOfDetailContent Join(string? separator, LevelOfDetailContent item0, LevelOfDetailContent item1, LevelOfDetailContent item2, LevelOfDetailContent item3, LevelOfDetailContent item4) {
        string basic = string.Join(separator, item0.Basic, item1.Basic, item2.Basic, item3.Basic, item4.Basic);
        string detail = string.Join(separator, item0.Detail, item1.Detail, item2.Detail, item3.Detail, item4.Detail);
        return new LevelOfDetailContent(basic, detail);
    }
}
