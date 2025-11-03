using Atelia.LlmProviders;
namespace Atelia.Agent.Core;

/// <summary>
/// 用于模型切换功能的内层对象。
/// 原型阶段回避次要复杂性，先不做配置的序列化和文件读写，仅在初始化阶段用代码构造。
/// </summary>
/// <param name="Name">用于在UI中显示，以及区分不同的LlmProfile实例。</param>
public sealed record LlmProfile(
    IProviderClient Client,
    string ModelId,
    string Name
);
