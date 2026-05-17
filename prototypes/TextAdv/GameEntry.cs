using System.CommandLine;
using Atelia;
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
public static class GameEntry {
    private const string RepoDir = "/tmp/atelia-textadv-game";

    private static Repository? _repo;
    private static DurableDict<string>? _root;

    private static (Repository repo, DurableDict<string> root)? GetState() {
        if (_repo is not null && _root is not null) { return (_repo, _root); }

        if (!Directory.Exists(RepoDir)) { return null; }

        var openResult = Repository.Open(RepoDir);
        if (!openResult.IsSuccess) { return null; }

        _repo = openResult.Value!;
        var revResult = _repo.GetOrCreateBranch("main");
        var rev = revResult.Value!;

        _root = rev.GraphRoot as DurableDict<string>;
        if (_root is null) { return null; }

        return (_repo, _root);
    }

    public static RootCommand BuildGame() {
        var root = new RootCommand("荒岛求生 — 最小回合流程原型");

        root.Add(BuildNewCommand());
        root.Add(BuildLookAroundCommand());
        root.Add(BuildEditMemoryNotebookCommand());
        root.Add(BuildExploreCommand());
        root.Add(BuildRestAWhileCommand());
        root.Add(BuildDevGoCommand());

        return root;
    }

    private static Command BuildNewCommand() {
        var cmd = new Command("new", "开始新游戏（会覆盖旧存档）");
        cmd.SetAction(
            ctx => {
                var output = ctx.InvocationConfiguration.Output;

                _repo = null;
                _root = null;

                if (Directory.Exists(RepoDir)) {
                    Directory.Delete(RepoDir, recursive: true);
                }

                var createResult = Repository.Create(RepoDir);
                if (!createResult.IsSuccess) {
                    output.WriteLine($"❌ 创建游戏世界失败：{createResult.Error}");
                    return;
                }

                _repo = createResult.Value!;
                _root = GameSimulation.CreateNewWorld(_repo);

                output.WriteLine("✅ 新世界已创建！");
                output.WriteLine();
                output.Write(
                    GamePresenter.RenderPerception(
                        GameSimulation.DescribeCurrentPerception(_root)
                    )
                );
            }
        );
        return cmd;
    }

    private static Command BuildDevGoCommand() {
        var directionArg = new Argument<string>("direction");
        var cmd = new Command("dev-go", "开发者调试：直接移动到相邻区域（不参与回合结算）") { directionArg };
        cmd.SetAction(
            ctx => {
                var output = ctx.InvocationConfiguration.Output;
                var direction = ctx.GetValue(directionArg)!;

                var state = GetState();
                if (state is null) {
                    output.WriteLine("❌ 还没有游戏存档。请先运行 new 命令创建新世界。");
                    return;
                }

                var (repo, root) = state.Value;
                var moveResult = GameSimulation.MovePlayer(root, direction);

                if (!moveResult.IsSuccess) {
                    output.WriteLine($"❌ {moveResult.Error!.Message}");
                    return;
                }

                _ = repo.Commit(root).Value;

                output.WriteLine("⚠️ 这是 dev-go 开发者调试移动，不会记录为回合步骤，也不会触发 validator。");
                output.WriteLine($"🚶 你向 {direction} 方向走去…");
                output.WriteLine();
                output.Write(GamePresenter.RenderPerception(moveResult.Value!));
            }
        );
        return cmd;
    }

    private static Command BuildLookAroundCommand() {
        var cmd = new Command("look-around", "重新显示当前局面、笔记本和操作速查");
        cmd.SetAction(
            ctx => {
                var output = ctx.InvocationConfiguration.Output;

                var state = GetState();
                if (state is null) {
                    output.WriteLine("❌ 还没有游戏存档。请先运行 new 命令创建新世界。");
                    return;
                }

                var (_, root) = state.Value;
                output.Write(
                    GamePresenter.RenderPerception(
                        GameSimulation.DescribeCurrentPerception(root)
                    )
                );
            }
        );
        return cmd;
    }

    private static Command BuildEditMemoryNotebookCommand() {
        var dryRunOption = new Option<bool>("--dry-run") {
            Description = "只做 parse、after-view 预演和 validator 校验，不写入 notebook，也不记录 accepted step。"
        };
        var reasonArg = new Argument<string>("reason") {
            Description = "这一步动作的事前推理。应先根据当前证据说明你为什么准备这么做，而不是事后合理化。"
        };
        var scriptArg = new Argument<string>("edit-script") {
            Description = "notebook 编辑片段或完整文档。可直接传 <insert side=\"after\" anchor=\"tail\">…</insert>；系统会自动补 text-edit-script 根节点。"
        };
        var cmd = new Command("edit-memory-notebook", "Small-Action：编辑私人 Memory-Notebook")
        {
            dryRunOption,
            reasonArg,
            scriptArg,
        };
        cmd.SetAction(
            async (ctx, ct) => {
                var output = ctx.InvocationConfiguration.Output;
                var dryRun = ctx.GetValue(dryRunOption);
                var scriptXml = ctx.GetValue(scriptArg)!;
                var preActionReason = ctx.GetValue(reasonArg)!;

                var state = GetState();
                if (state is null) {
                    output.WriteLine("❌ 还没有游戏存档。请先运行 new 命令创建新世界。");
                    return;
                }

                var (repo, root) = state.Value;
                var perception = GameSimulation.DescribeCurrentPerception(root);
                var notebookEditResult = GameNotebookEditService.Prepare(perception.NotebookBlocks, scriptXml);
                if (!notebookEditResult.TryGetValue(out var notebookEdit) || notebookEdit is null) {
                    output.WriteLine("❌ notebook 编辑片段无效。");
                    WriteAteliaError(output, notebookEditResult.Error);
                    return;
                }

                GameActionValidator.ValidationResult validation;
                try {
                    validation = await GameActionValidator.ValidateActionAsync(
                        perception,
                        actionKind: "small/edit-memory-notebook",
                        notebookEdit.ActionSummary,
                        preActionReason,
                        actionPayload: notebookEdit.ValidatorPayload,
                        cancellationToken: ct
                    );
                }
                catch (Exception ex) {
                    output.WriteLine($"❌ validator 调用失败：{ex.Message}");
                    return;
                }

                if (dryRun) {
                    output.Write(GamePresenter.RenderNotebookEditDryRun(perception, notebookEdit, preActionReason, validation));
                    return;
                }

                if (!validation.Accepted) {
                    output.WriteLine("❌ validator 未通过这一步 Small-Action。");
                    output.WriteLine(validation.Feedback);
                    return;
                }

                var updatedPerception = GameSimulation.ApplyNotebookEdit(root, notebookEdit, preActionReason, validation.Feedback);
                _ = repo.Commit(root).Value;

                output.WriteLine("✅ Small-Action 已接受：edit-memory-notebook");
                output.WriteLine($"🧪 validator: {validation.Feedback}");
                output.WriteLine();
                output.Write(GamePresenter.RenderPerception(updatedPerception));
            }
        );
        return cmd;
    }

    private static void WriteAteliaError(TextWriter output, AteliaError? error) {
        if (error is null) {
            output.WriteLine("缺少更具体的错误信息。");
            return;
        }

        output.WriteLine(error.Message);
        if (!string.IsNullOrWhiteSpace(error.RecoveryHint)) {
            output.WriteLine($"💡 {error.RecoveryHint}");
        }
    }

    private static Command BuildExploreCommand() {
        var focusOption = new Option<string?>("--focus") {
            Description = "可选：你希望重点寻找或确认的对象，例如“山洞入口”“淡水痕迹”。"
        };
        var reasonArg = new Argument<string>("reason") {
            Description = "这一步动作的事前推理。应先根据当前证据说明你为什么准备探索这个方向，而不是事后合理化。"
        };
        var directionArg = new Argument<string>("direction") {
            Description = "探索方向，例如 north/south/east/west/inside。"
        };
        var cmd = new Command("explore", "Large-Action：向指定方向探索；必要时由 GM 账本创建新地点")
        {
            focusOption,
            reasonArg,
            directionArg,
        };
        cmd.SetAction(
            async (ctx, ct) => {
                var output = ctx.InvocationConfiguration.Output;
                var preActionReason = ctx.GetValue(reasonArg)!;
                var direction = ctx.GetValue(directionArg)!;
                var focus = ctx.GetValue(focusOption);
                var actionSummary = string.IsNullOrWhiteSpace(focus)
                    ? $"向 {direction} 探索"
                    : $"向 {direction} 探索：{focus}";
                var actionPayload = string.IsNullOrWhiteSpace(focus)
                    ? $"direction={direction}"
                    : $"direction={direction}\nfocus={focus!.Trim()}";

                var state = GetState();
                if (state is null) {
                    output.WriteLine("❌ 还没有游戏存档。请先运行 new 命令创建新世界。");
                    return;
                }

                var (repo, root) = state.Value;
                var perception = GameSimulation.DescribeCurrentPerception(root);

                GameActionValidator.ValidationResult validation;
                try {
                    validation = await GameActionValidator.ValidateActionAsync(
                        perception,
                        actionKind: "large/explore",
                        actionSummary,
                        preActionReason,
                        actionPayload,
                        cancellationToken: ct
                    );
                }
                catch (Exception ex) {
                    output.WriteLine($"❌ validator 调用失败：{ex.Message}");
                    return;
                }

                if (!validation.Accepted) {
                    output.WriteLine("❌ validator 未通过这一步 Large-Action。");
                    output.WriteLine(validation.Feedback);
                    return;
                }

                var resolutionResult = GameSimulation.ApplyExplore(root, direction, focus, preActionReason, validation.Feedback);
                if (!resolutionResult.TryGetValue(out var resolution) || resolution is null) {
                    output.WriteLine("❌ GM 探索结算失败。");
                    WriteAteliaError(output, resolutionResult.Error);
                    return;
                }

                _ = repo.Commit(root).Value;

                output.WriteLine($"✅ Large-Action 已接受：{actionSummary}。当前回合已结束。");
                output.WriteLine($"🧪 validator: {validation.Feedback}");
                output.WriteLine($"📣 结算: {resolution.Summary}");
                output.WriteLine();
                output.Write(GamePresenter.RenderPerception(resolution.NextPerception));
            }
        );
        return cmd;
    }

    private static Command BuildRestAWhileCommand() {
        var reasonArg = new Argument<string>("reason") {
            Description = "这一步动作的事前推理。应先根据当前证据说明你为什么准备原地休息，而不是事后合理化。"
        };
        var cmd = new Command("rest-a-while", "Large-Action：原地休息一会，并结束当前回合")
        {
            reasonArg,
        };
        cmd.SetAction(
            async (ctx, ct) => {
                var output = ctx.InvocationConfiguration.Output;
                var preActionReason = ctx.GetValue(reasonArg)!;
                const string actionSummary = "原地休息一会";

                var state = GetState();
                if (state is null) {
                    output.WriteLine("❌ 还没有游戏存档。请先运行 new 命令创建新世界。");
                    return;
                }

                var (repo, root) = state.Value;
                var perception = GameSimulation.DescribeCurrentPerception(root);

                GameActionValidator.ValidationResult validation;
                try {
                    validation = await GameActionValidator.ValidateActionAsync(
                        perception,
                        actionKind: "large/rest-a-while",
                        actionSummary,
                        preActionReason,
                        actionPayload: null,
                        cancellationToken: ct
                    );
                }
                catch (Exception ex) {
                    output.WriteLine($"❌ validator 调用失败：{ex.Message}");
                    return;
                }

                if (!validation.Accepted) {
                    output.WriteLine("❌ validator 未通过这一步 Large-Action。");
                    output.WriteLine(validation.Feedback);
                    return;
                }

                var resolution = GameSimulation.ApplyRestAWhile(root, preActionReason, validation.Feedback);
                _ = repo.Commit(root).Value;

                output.WriteLine("✅ Large-Action 已接受：原地休息一会。当前回合已结束。");
                output.WriteLine($"🧪 validator: {validation.Feedback}");
                output.WriteLine($"📣 结算: {resolution.Summary}");
                output.WriteLine();
                output.Write(GamePresenter.RenderPerception(resolution.NextPerception));
            }
        );
        return cmd;
    }
}
