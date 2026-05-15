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

    private static Repository? _repo;
    private static DurableDict<string>? _root;

    private static (Repository repo, DurableDict<string> root)? GetState()
    {
        if (_repo is not null && _root is not null)
        {
            return (_repo, _root);
        }

        if (!Directory.Exists(RepoDir))
        {
            return null;
        }

        var openResult = Repository.Open(RepoDir);
        if (!openResult.IsSuccess)
        {
            return null;
        }

        _repo = openResult.Value!;
        var revResult = _repo.GetOrCreateBranch("main");
        var rev = revResult.Value!;

        _root = rev.GraphRoot as DurableDict<string>;
        if (_root is null)
        {
            return null;
        }

        return (_repo, _root);
    }

    public static RootCommand BuildGame()
    {
        var root = new RootCommand("荒岛求生 — 文本冒险原型");

        root.Add(BuildNewCommand());
        root.Add(BuildGoCommand());
        root.Add(BuildLookAroundCommand());

        return root;
    }

    private static Command BuildNewCommand()
    {
        var cmd = new Command("new", "开始新游戏（会覆盖旧存档）");
        cmd.SetAction(ctx =>
        {
            var output = ctx.InvocationConfiguration.Output;

            _repo = null;
            _root = null;

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
            _root = GameSimulation.CreateNewWorld(_repo);

            output.WriteLine("✅ 新世界已创建！");
            output.WriteLine();
            output.Write(GamePresenter.RenderPerception(
                GameSimulation.DescribeCurrentLocation(_root)));
        });
        return cmd;
    }

    private static Command BuildGoCommand()
    {
        var directionArg = new Argument<string>("direction");
        var cmd = new Command("go", "移动到相邻区域") { directionArg };
        cmd.SetAction(ctx =>
        {
            var output = ctx.InvocationConfiguration.Output;
            var direction = ctx.GetValue(directionArg)!;

            var state = GetState();
            if (state is null)
            {
                output.WriteLine("❌ 还没有游戏存档。请先运行 new 命令创建新世界。");
                return;
            }

            var (repo, root) = state.Value;
            var moveResult = GameSimulation.MovePlayer(root, direction);

            if (!moveResult.IsSuccess)
            {
                output.WriteLine($"❌ {moveResult.Error!.Message}");
                return;
            }

            _ = repo.Commit(root).Value;

            output.WriteLine($"🚶 你向 {direction} 方向走去…");
            output.WriteLine();
            output.Write(GamePresenter.RenderPerception(moveResult.Value!));
        });
        return cmd;
    }

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
            output.Write(GamePresenter.RenderPerception(
                GameSimulation.DescribeCurrentLocation(root)));
        });
        return cmd;
    }
}
