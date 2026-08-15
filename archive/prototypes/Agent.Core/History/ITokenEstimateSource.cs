namespace Atelia.Agent.Core.History;

/// <summary>
/// 提供 token 估算值的统一访问接口。
/// </summary>
public interface ITokenEstimateSource {
    /// <summary>
    /// 获取该对象当前记录的 token 估算值。
    /// 約定为非负值，并使用 <see cref="uint"/> 表示。
    /// </summary>
    uint TokenEstimate { get; }
}
