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
            "GameServer 的 sample-world dev bootstrap / reset / repo lock retry 已回到显式 host policy，runtime service 只保留当前 runtime handle 与单写者替换。",
            "logical time、route acceleration、location observation、actor observation、navigation observation、actor movement、route trace、route plan 已建立 public typed DTO seam；JSON 或 plain-text 渲染都在宿主边界完成。",
            "compact movement text 与 route trace text 已从 runtime 主对象移出，改由显式 dev-support renderer 供宿主按需调用。",
            "sample-world seed 与默认 landmark profile 已从 runtime public seam 下沉到显式 dev support 层。",
            "canonical navigation graph seam 已收口为显式 read model，并被 planner / heuristic / stale-signature 共享复用。",
            "world root schema gate 已补齐；Passage 高频写操作已收回到 WorldState.SetPassage* seam，主读链路也已切到只读 facade。",
            "修订后的近程路线是：先显式化 E2eCli dev-mode，再决定 DumpWorld/DumpLocation 这类残余 dev text surface 的最终归属。"
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
