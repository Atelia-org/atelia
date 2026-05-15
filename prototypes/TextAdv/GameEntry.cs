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

    // ── PipeMux 入口 ──

    public static RootCommand BuildGame()
    {
        var root = new RootCommand("荒岛求生 — 文本冒险原型");

        root.Add(BuildNewCommand());
        root.Add(BuildGoCommand());

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
            var revResult = _repo.GetOrCreateBranch("main");
            var rev = revResult.Value!;

            // ── 构建世界 ──
            var root = rev.CreateDict<string>();
            var world = rev.CreateDict<string>();
            var locations = rev.CreateDict<string>();
            var player = rev.CreateDict<string>();

            // 沙滩
            var beach = rev.CreateDict<string>();
            beach.Upsert("name", "沙滩");
            beach.Upsert("description", "一片开阔的沙滩，海浪轻拍着海岸线。"
                + "细白的沙子在阳光下闪闪发光。远处可以看到茂密的树林。");
            var beachExits = rev.CreateDict<string>();
            beachExits.Upsert("north", "forest");
            beach.Upsert("exits", beachExits);
            locations.Upsert("beach", beach);

            // 密林
            var forest = rev.CreateDict<string>();
            forest.Upsert("name", "密林");
            forest.Upsert("description", "茂密的树林遮天蔽日，空气中弥漫着泥土和树叶的气味。"
                + "树影间隐约能听到鸟鸣声。南边透过树缝可以看到沙滩的亮光。");
            var forestExits = rev.CreateDict<string>();
            forestExits.Upsert("south", "beach");
            forest.Upsert("exits", forestExits);
            locations.Upsert("forest", forest);

            world.Upsert("locations", locations);
            world.Upsert("initialLocation", "beach");

            // 玩家
            player.Upsert("location", "beach");

            root.Upsert("world", world);
            root.Upsert("player", player);

            _ = _repo.Commit(root);
            _root = root;

            output.WriteLine("✅ 新世界已创建！");
            output.WriteLine();
            output.Write(ReportPerception(root));
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
            var player = root.GetOrThrow<DurableDict<string>>("player")!;
            var world = root.GetOrThrow<DurableDict<string>>("world")!;
            var locations = world.GetOrThrow<DurableDict<string>>("locations")!;

            var currentLocationId = player.GetOrThrow<string>("location")!;
            var currentLocation = locations.GetOrThrow<DurableDict<string>>(currentLocationId)!;
            var exits = currentLocation.GetOrThrow<DurableDict<string>>("exits")!;

            // 检查方向是否有效
            if (!exits.TryGet(direction, out string? targetLocationId) || targetLocationId is null)
            {
                var available = string.Join(", ", exits.Keys.Select(k =>
                {
                    var targetId = exits.GetOrThrow<string>(k)!;
                    var targetLoc = locations.GetOrThrow<DurableDict<string>>(targetId)!;
                    return $"{k} → {targetLoc.GetOrThrow<string>("name")!}";
                }));
                output.WriteLine($"❌ 「{currentLocation.GetOrThrow<string>("name")}」没有通往「{direction}」的出口。");
                output.WriteLine($"   可用的出口: {available}");
                return;
            }

            // 移动
            player.Upsert("location", targetLocationId);
            _ = repo.Commit(root);

            output.WriteLine($"🚶 你向 {direction} 方向走去…");
            output.WriteLine();
            output.Write(ReportPerception(root));
        });
        return cmd;
    }

    // ═══════════════════════════════════════════
    // 感知报告 — 进入地点后自动输出
    // ═══════════════════════════════════════════

    private static string ReportPerception(DurableDict<string> root)
    {
        var player = root.GetOrThrow<DurableDict<string>>("player")!;
        var world = root.GetOrThrow<DurableDict<string>>("world")!;
        var locations = world.GetOrThrow<DurableDict<string>>("locations")!;

        var locationId = player.GetOrThrow<string>("location")!;
        var location = locations.GetOrThrow<DurableDict<string>>(locationId)!;
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
            var targetId = exits.GetOrThrow<string>(exitKey)!;
            var targetLoc = locations.GetOrThrow<DurableDict<string>>(targetId)!;
            var targetName = targetLoc.GetOrThrow<string>("name")!;
            sb.AppendLine($"   {exitKey} → {targetName}");
        }
        return sb.ToString();
    }
}
