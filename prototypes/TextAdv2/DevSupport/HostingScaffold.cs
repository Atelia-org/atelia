using Atelia.TextAdv2.Session;

namespace Atelia.TextAdv2.DevSupport;

/// <summary>
/// 过渡期 session 脚手架信息。
///
/// 它不是正式的 authoritative session，只用于让外部 host 项目先稳定接上 TextAdv2 本体，
/// 当前 session 已承载 repo/world 生命周期并直接暴露 typed methods；
/// 各宿主仍自行负责把自己的 CLI/HTTP 入口分发到这些方法上。
/// </summary>
public static class HostingScaffold {
    public static HostingScaffoldInfo DescribeCurrentState() => new(
        EngineAssemblyName: typeof(HostingScaffold).Assembly.GetName().Name ?? "Atelia.TextAdv2",
        SessionExtracted: true,
        CurrentExecutableOwner: "TextAdv2.E2eCli",
        PlannedHosts: ["TextAdv2.E2eCli", "TextAdv2.GameServer"],
        Notes: [
            "WorldSession 已承载 repo/world 生命周期，并直接暴露 typed session methods。",
            "TextAdv2 本体现已回退为 Library；一次性 CLI 入口已迁移到 TextAdv2.E2eCli。",
            "GameServer 与 E2E CLI 都已直接调用WorldSession；宿主仍自行负责 CLI/HTTP 请求到 session method 的分发。",
            "GameServer 的 sample-world dev bootstrap / reset / repo lock retry 已回到显式 host policy，session service 只保留当前 session handle 与单写者替换。",
            "logical time、route acceleration、location observation、actor observation、navigation observation、actor movement、route trace、route plan 已建立 public typed DTO seam；JSON 或 plain-text 渲染都在宿主边界完成。",
            "world dump/location dump、compact movement text 与 route trace text 已从 session 主对象移出，改由显式 dev-support renderer 供宿主按需调用。",
            "sample-world seed 与默认 landmark profile 已从 session public seam 下沉到显式 dev support 层。",
            "canonical navigation graph seam 已收口为显式 read model，并被 planner / heuristic / stale-signature 共享复用。",
            "world root schema gate 已补齐；Passage 高频写操作已收回到 WorldState.SetPassage* seam，主读链路也已切到只读 facade。",
            "E2eCli dev-mode 已显式化：session 命令现在必须显式选择 --repo-dir 或 --dev-sample-world。",
            "WorldSession public surface 已不再暴露调试文本输出或 WorldSessionCommandResult；world/location dump 走显式 dev-support helper。"
        ]
    );
}

public sealed record HostingScaffoldInfo(
    string EngineAssemblyName,
    bool SessionExtracted,
    string CurrentExecutableOwner,
    string[] PlannedHosts,
    string[] Notes
);
