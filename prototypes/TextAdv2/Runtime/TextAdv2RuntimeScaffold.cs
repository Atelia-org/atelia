namespace Atelia.TextAdv2.Runtime;

/// <summary>
/// 过渡期 runtime 脚手架信息。
///
/// 它不是正式的 authoritative runtime，只用于让外部 host 项目先稳定接上 TextAdv2 本体，
/// 后续再逐步把真正的 repo/world 生命周期与命令执行逻辑迁移进来。
/// </summary>
public static class TextAdv2RuntimeScaffold {
    public static TextAdv2RuntimeScaffoldInfo DescribeCurrentState() => new(
        EngineAssemblyName: typeof(TextAdv2RuntimeScaffold).Assembly.GetName().Name ?? "Atelia.TextAdv2",
        RuntimeExtracted: true,
        CurrentExecutableOwner: "TextAdv2.E2eCli",
        PlannedHosts: ["TextAdv2.E2eCli", "TextAdv2.GameServer"],
        Notes: [
            "TextAdv2Runtime 已承载 repo/world 生命周期与现有 CLI 命令编排。",
            "TextAdv2 本体现已回退为 Library；一次性 CLI 入口已迁移到 TextAdv2.E2eCli。",
            "GameServer 与 E2E CLI 都已直接调用 TextAdv2Runtime；下一阶段应继续收口 host 专属契约与集成测试。"
        ]
    );
}

public sealed record TextAdv2RuntimeScaffoldInfo(
    string EngineAssemblyName,
    bool RuntimeExtracted,
    string CurrentExecutableOwner,
    string[] PlannedHosts,
    string[] Notes
);
