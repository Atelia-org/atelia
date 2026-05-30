namespace Atelia.TextAdv2.Runtime;

/// <summary>
/// 过渡期 runtime 脚手架信息。
///
/// 它不是正式的 authoritative runtime，只用于让外部 host 项目先稳定接上 TextAdv2 本体，
/// 当前 runtime 已承载 repo/world 生命周期并直接暴露 typed methods；
/// 各宿主仍自行负责把自己的 CLI/HTTP 入口分发到这些方法上。
/// </summary>
public static class TextAdv2RuntimeScaffold {
    public static TextAdv2RuntimeScaffoldInfo DescribeCurrentState() => new(
        EngineAssemblyName: typeof(TextAdv2RuntimeScaffold).Assembly.GetName().Name ?? "Atelia.TextAdv2",
        RuntimeExtracted: true,
        CurrentExecutableOwner: "TextAdv2.E2eCli",
        PlannedHosts: ["TextAdv2.E2eCli", "TextAdv2.GameServer"],
        Notes: [
            "TextAdv2Runtime 已承载 repo/world 生命周期，并直接暴露 typed runtime methods。",
            "TextAdv2 本体现已回退为 Library；一次性 CLI 入口已迁移到 TextAdv2.E2eCli。",
            "GameServer 与 E2E CLI 都已直接调用 TextAdv2Runtime；宿主仍自行负责 CLI/HTTP 请求到 runtime method 的分发。",
            "当前阶段还没有建立 public typed DTO seam；下一阶段应继续收口 host 专属契约与集成测试。"
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
