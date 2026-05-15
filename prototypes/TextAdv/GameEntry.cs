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
        var root = new RootCommand("荒岛求生 — 最小回合流程原型");

        root.Add(BuildNewCommand());
        root.Add(BuildLookAroundCommand());
        root.Add(BuildEditMemoryNotebookCommand());
        root.Add(BuildRestAWhileCommand());
        root.Add(BuildGoCommand());

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
                GameSimulation.DescribeCurrentPerception(_root)));
        });
        return cmd;
    }

    private static Command BuildGoCommand()
    {
        var directionArg = new Argument<string>("direction");
        var cmd = new Command("go", "调试：直接移动到相邻区域（不参与回合结算）") { directionArg };
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

            output.WriteLine("⚠️ 这是调试移动，不会记录为回合步骤，也不会触发 validator。");
            output.WriteLine($"🚶 你向 {direction} 方向走去…");
            output.WriteLine();
            output.Write(GamePresenter.RenderPerception(moveResult.Value!));
        });
        return cmd;
    }

    private static Command BuildLookAroundCommand()
    {
        var cmd = new Command("look-around", "查看当前最小 Perception-Bundle");
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
                GameSimulation.DescribeCurrentPerception(root)));
        });
        return cmd;
    }

    private static Command BuildEditMemoryNotebookCommand()
    {
        var contentArg = new Argument<string>("content")
        {
            Description = "替换后的 Memory-Notebook 全文"
        };
        var reasonArg = new Argument<string>("reason")
        {
            Description = "支撑这一步 notebook 编辑的 grounded Reason-Trace"
        };
        var cmd = new Command("edit-memory-notebook", "Small-Action：编辑私人 Memory-Notebook")
        {
            contentArg,
            reasonArg,
        };
        cmd.SetAction(async (ctx, ct) =>
        {
            var output = ctx.InvocationConfiguration.Output;
            var content = ctx.GetValue(contentArg)!;
            var reason = ctx.GetValue(reasonArg)!;

            var state = GetState();
            if (state is null)
            {
                output.WriteLine("❌ 还没有游戏存档。请先运行 new 命令创建新世界。");
                return;
            }

            var (repo, root) = state.Value;
            var perception = GameSimulation.DescribeCurrentPerception(root);
            var actionSummary = $"replace notebook ({perception.NotebookContent.Length} -> {content.Length} chars)";

            GameActionValidator.ValidationResult validation;
            try
            {
                validation = await GameActionValidator.ValidateActionAsync(
                    perception,
                    actionKind: "small/edit-memory-notebook",
                    actionSummary,
                    reasonTrace: reason,
                    actionPayload: content,
                    cancellationToken: ct);
            }
            catch (Exception ex)
            {
                output.WriteLine($"❌ validator 调用失败：{ex.Message}");
                return;
            }

            if (!validation.Accepted)
            {
                output.WriteLine("❌ validator 未通过这一步 Small-Action。");
                output.WriteLine(validation.Feedback);
                return;
            }

            var updatedPerception = GameSimulation.ApplyNotebookEdit(root, content, reason, validation.Feedback);
            _ = repo.Commit(root).Value;

            output.WriteLine("✅ Small-Action 已接受：edit-memory-notebook");
            output.WriteLine($"🧪 validator: {validation.Feedback}");
            output.WriteLine();
            output.Write(GamePresenter.RenderPerception(updatedPerception));
        });
        return cmd;
    }

    private static Command BuildRestAWhileCommand()
    {
        var reasonArg = new Argument<string>("reason")
        {
            Description = "支撑这一步“原地休息一会”的 grounded Reason-Trace"
        };
        var cmd = new Command("rest-a-while", "Large-Action：原地休息一会，并结束当前回合")
        {
            reasonArg,
        };
        cmd.SetAction(async (ctx, ct) =>
        {
            var output = ctx.InvocationConfiguration.Output;
            var reason = ctx.GetValue(reasonArg)!;
            const string actionSummary = "原地休息一会";

            var state = GetState();
            if (state is null)
            {
                output.WriteLine("❌ 还没有游戏存档。请先运行 new 命令创建新世界。");
                return;
            }

            var (repo, root) = state.Value;
            var perception = GameSimulation.DescribeCurrentPerception(root);

            GameActionValidator.ValidationResult validation;
            try
            {
                validation = await GameActionValidator.ValidateActionAsync(
                    perception,
                    actionKind: "large/rest-a-while",
                    actionSummary,
                    reasonTrace: reason,
                    actionPayload: null,
                    cancellationToken: ct);
            }
            catch (Exception ex)
            {
                output.WriteLine($"❌ validator 调用失败：{ex.Message}");
                return;
            }

            if (!validation.Accepted)
            {
                output.WriteLine("❌ validator 未通过这一步 Large-Action。");
                output.WriteLine(validation.Feedback);
                return;
            }

            var resolution = GameSimulation.ApplyRestAWhile(root, reason, validation.Feedback);
            _ = repo.Commit(root).Value;

            output.WriteLine("✅ Large-Action 已接受：原地休息一会。当前回合已结束。");
            output.WriteLine($"🧪 validator: {validation.Feedback}");
            output.WriteLine($"📣 结算: {resolution.Summary}");
            output.WriteLine();
            output.Write(GamePresenter.RenderPerception(resolution.NextPerception));
        });
        return cmd;
    }
}
