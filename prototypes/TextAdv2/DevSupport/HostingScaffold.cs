namespace Atelia.TextAdv2.DevSupport;

/// <summary>
/// 跨宿主稳定暴露的最小运行时描述。
///
/// 它不承载迁移历史、路线图或 host-specific 说明，
/// 只保留当前多个宿主都可稳定共享的 engine/runtime 事实。
/// </summary>
public static class HostingScaffold {
    public static HostingRuntimeInfo DescribeCurrentState() => new(
        EngineAssemblyName: typeof(HostingScaffold).Assembly.GetName().Name ?? "Atelia.TextAdv2"
    );
}

public sealed record HostingRuntimeInfo(
    string EngineAssemblyName
);
