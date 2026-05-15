using System.CommandLine;
using Atelia.StateJournal;

namespace Atelia.TextAdv;

/// <summary>
/// PipeMux 主入口：荒岛求生文本冒险游戏。
///
/// 注册方法：
/// <code>
/// pmux :register game /path/to/Atelia.TextAdv.dll Atelia.TextAdv.GameEntry.BuildGame
/// </code>
///
/// 状态全部持久化在 StateJournal 中，进程重启不丢失。
/// </summary>
public static class GameEntry
{
    private const string RepoDir = "/tmp/atelia-textadv-game";

    /// <summary>游戏层错误类型。</summary>
    private sealed record GameError(string ErrorCode, string Message)
        : AteliaError(ErrorCode, Message);

    // ── 延迟加载的游戏状态 ──
    private static Repository? _repo;
    private static DurableDict<string>? _root;

    /// <summary>
    /// 加载或初始化游戏状态。若仓库不存在则返回 null（需要先 <c>new</c>）。
    /// </summary>
    private static (Repository repo, DurableDict<string> root)? GetState()
    {
        if (_repo is not null && _root is not null)
            return (_repo, _root);

        if (!Directory.Exists(RepoDir))
            return null;

        var openResult = Repository.Open(RepoDir);
        if (!openResult.IsSuccess)
            return null;

        _repo = openResult.Value!;
        var revResult = _repo.GetOrCreateBranch("main");
        var rev = revResult.Value!;

        _root = rev.GraphRoot as DurableDict<string>;
        if (_root is null)
            return null;

        return (_repo, _root);
    }

    // ── Location 构建辅助 ──

    /// <summary>
    /// 创建一个地点对象，包含 name / description / exits（空）三个字段。
    /// </summary>
    private static DurableDict<string> CreateLocation(Revision rev, string name, string description)
    {
        var loc = rev.CreateDict<string>();
        loc.Upsert("name", name);
        loc.Upsert("description", description);
        loc.Upsert("exits", rev.CreateDict<string>());
        return loc;
    }

    /// <summary>
    /// 为地点添加一条单向出口。目标以对象引用存储（非字符串 key），消除隐式外键约束。
    /// </summary>
    private static void AddExit(DurableDict<string> from, string direction, DurableDict<string> to)
    {
        var exits = from.GetOrThrow<DurableDict<string>>("exits")!;
        exits.Upsert(direction, to);
    }

    // ═══════════════════════════════════════════
    // Model — 游戏状态操作（纯逻辑，不依赖 System.CommandLine）
    // ═══════════════════════════════════════════

    /// <summary>
    /// 获取玩家当前所在的地点对象。
    /// </summary>
    private static DurableDict<string> GetPlayerLocation(DurableDict<string> root)
    {
        var player = root.GetOrThrow<DurableDict<string>>("player")!;
        return player.GetOrThrow<DurableDict<string>>("location")!;
    }

    /// <summary>
    /// 尝试将玩家向指定方向移动。成功时返回新地点对象，失败时返回错误。
    /// 调用方负责 Commit。
    /// </summary>
    private static AteliaResult<DurableDict<string>> MovePlayer(DurableDict<string> root, string direction)
    {
        var player = root.GetOrThrow<DurableDict<string>>("player")!;
        var currentLocation = player.GetOrThrow<DurableDict<string>>("location")!;
        var exits = currentLocation.GetOrThrow<DurableDict<string>>("exits")!;

        if (!exits.TryGet(direction, out DurableDict<string>? targetLocation) || targetLocation is null)
        {
            var available = string.Join(", ", exits.Keys.Select(k =>
            {
                var t = exits.GetOrThrow<DurableDict<string>>(k)!;
                return $"{k} → {t.GetOrThrow<string>("name")!}";
            }));
            var currentName = currentLocation.GetOrThrow<string>("name")!;
            return AteliaResult<DurableDict<string>>.Failure(
                new GameError("TextAdv.InvalidDirection",
                    $"「{currentName}」没有通往「{direction}」的出口。可用的出口: {available}"));
        }

        player.Upsert("location", targetLocation);
        return targetLocation;
    }

    /// <summary>
    /// 创建全新游戏世界，返回根节点（已 Commit）。
    /// </summary>
    private static DurableDict<string> CreateNewWorld(Repository repo)
    {
        var revResult = repo.GetOrCreateBranch("main");
        var rev = revResult.Value!;

        var root = rev.CreateDict<string>();
        var world = rev.CreateDict<string>();
        var locations = rev.CreateDict<string>();
        var player = rev.CreateDict<string>();

        var beach = CreateLocation(rev, "沙滩",
            "一片开阔的沙滩，海浪轻拍着海岸线。"
            + "细白的沙子在阳光下闪闪发光。远处可以看到茂密的树林。");
        var forest = CreateLocation(rev, "密林",
            "茂密的树林遮天蔽日，空气中弥漫着泥土和树叶的气味。"
            + "树影间隐约能听到鸟鸣声。南边透过树缝可以看到沙滩的亮光。");

        AddExit(beach, "north", forest);
        AddExit(forest, "south", beach);

        locations.Upsert("beach", beach);
        locations.Upsert("forest", forest);

        world.Upsert("locations", locations);
        world.Upsert("initialLocation", "beach");

        player.Upsert("location", beach);

        root.Upsert("world", world);
        root.Upsert("player", player);

        _ = repo.Commit(root);
        return root;
    }

    // ═══════════════════════════════════════════
    // View — 将游戏状态渲染为文本（纯函数，只读）
    // ═══════════════════════════════════════════

    /// <summary>
    /// 渲染一个地点的感知信息（名称、描述、出口列表）。
    /// 每次调用都从 StateJournal 重新读取，不缓存，保证数据新鲜。
    /// </summary>
    private static string RenderPerception(DurableDict<string> location)
    {
        var name = location.GetOrThrow<string>("name")!;
        var description = location.GetOrThrow<string>("description")!;
        var exits = location.GetOrThrow<DurableDict<string>>("exits")!;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"📍 {name}");
        sb.AppendLine($"   {description}");
        sb.AppendLine();
        sb.AppendLine("🚪 可前往:");
        foreach (var exitKey in exits.Keys)
        {
            var targetLoc = exits.GetOrThrow<DurableDict<string>>(exitKey)!;
            var targetName = targetLoc.GetOrThrow<string>("name")!;
            sb.AppendLine($"   {exitKey} → {targetName}");
        }
        return sb.ToString();
    }

    // ═══════════════════════════════════════════
    // Control — System.CommandLine 命令（薄封装）
    // ═══════════════════════════════════════════

    // ── PipeMux 入口 ──

    public static RootCommand BuildGame()
    {
        var root = new RootCommand("荒岛求生 — 文本冒险原型");

        root.Add(BuildNewCommand());
        root.Add(BuildGoCommand());
        root.Add(BuildLookAroundCommand());

        return root;
    }

    // ═══════════════════════════════════════════
    // new — 创建全新游戏世界
    // ═══════════════════════════════════════════

    private static Command BuildNewCommand()
    {
        var cmd = new Command("new", "开始新游戏（会覆盖旧存档）");
        cmd.SetAction(ctx =>
        {
            var output = ctx.InvocationConfiguration.Output;

            // 覆盖旧数据
            if (Directory.Exists(RepoDir))
            {
                Directory.Delete(RepoDir, recursive: true);
            }

            var createResult = Repository.Create(RepoDir);
            if (!createResult.IsSuccess)
            {
                output.WriteLine($"❌ 创建游戏世界失败：{createResult.Error}");
                return;
            }

            _repo = createResult.Value!;
            _root = CreateNewWorld(_repo);

            output.WriteLine("✅ 新世界已创建！");
            output.WriteLine();
            output.Write(RenderPerception(GetPlayerLocation(_root)));
        });
        return cmd;
    }

    // ═══════════════════════════════════════════
    // go <direction> — 移动
    // ═══════════════════════════════════════════

    private static Command BuildGoCommand()
    {
        var directionArg = new Argument<string>("direction");
        var cmd = new Command("go", "移动到相邻区域") { directionArg };
        cmd.SetAction(ctx =>
        {
            var output = ctx.InvocationConfiguration.Output;
            var direction = ctx.GetValue(directionArg);

            var state = GetState();
            if (state is null)
            {
                output.WriteLine("❌ 还没有游戏存档。请先运行 new 命令创建新世界。");
                return;
            }

            var (repo, root) = state.Value;
            var moveResult = MovePlayer(root, direction);

            if (!moveResult.IsSuccess)
            {
                output.WriteLine($"❌ {moveResult.Error!.Message}");
                return;
            }

            _ = repo.Commit(root);

            output.WriteLine($"🚶 你向 {direction} 方向走去…");
            output.WriteLine();
            output.Write(RenderPerception(moveResult.Value!));
        });
        return cmd;
    }

    // ═══════════════════════════════════════════
    // look-around — 重新查看当前位置（不改变状态）
    // ═══════════════════════════════════════════

    private static Command BuildLookAroundCommand()
    {
        var cmd = new Command("look-around", "重新查看当前位置的感知信息");
        cmd.SetAction(ctx =>
        {
            var output = ctx.InvocationConfiguration.Output;

            var state = GetState();
            if (state is null)
            {
                output.WriteLine("❌ 还没有游戏存档。请先运行 new 命令创建新世界。");
                return;
            }

            var (_, root) = state.Value;
            var location = GetPlayerLocation(root);
            output.Write(RenderPerception(location));
        });
        return cmd;
    }
}
