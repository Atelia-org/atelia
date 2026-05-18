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
public static partial class GameEntry {
    private static Repository? _repo;
    private static DurableDict<string>? _root;

    private static (Repository repo, DurableDict<string> root)? GetState(out string? errorMessage) {
        errorMessage = null;
        if (_repo is not null && _root is not null) { return (_repo, _root); }

        var repoDir = TextAdvRuntimeEnvironment.GetRepoDir();
        if (!Directory.Exists(repoDir)) { return null; }

        _repo = null;
        _root = null;

        var openResult = Repository.Open(repoDir);
        if (!openResult.IsSuccess) {
            errorMessage = $"无法打开游戏存档目录 '{repoDir}'：{openResult.Error?.Message ?? openResult.Error?.ToString() ?? "未知错误"}";
            return null;
        }

        _repo = openResult.Value!;
        var revResult = _repo.GetOrCreateBranch("main");
        if (!revResult.IsSuccess) {
            _repo = null;
            _root = null;
            errorMessage = $"无法打开游戏分支 'main'：{revResult.Error?.Message ?? revResult.Error?.ToString() ?? "未知错误"}";
            return null;
        }

        var rev = revResult.Value!;

        _root = rev.GraphRoot as DurableDict<string>;
        if (_root is null) {
            _repo = null;
            _root = null;
            errorMessage = $"游戏存档目录 '{repoDir}' 中的根状态不是 DurableDict<string>。";
            return null;
        }

        return (_repo, _root);
    }

    private static bool TryGetState(TextWriter output, out (Repository repo, DurableDict<string> root) state) {
        var loadedState = GetState(out var errorMessage);
        if (loadedState is not null) {
            state = loadedState.Value;
            return true;
        }

        state = default;
        if (errorMessage is not null) {
            output.WriteLine($"❌ {errorMessage}");
            return false;
        }

        output.WriteLine("❌ 还没有游戏存档。请先运行 new 命令创建新世界。");
        output.WriteLine($"💡 当前存档目录：{TextAdvRuntimeEnvironment.GetRepoDir()}");
        return false;
    }

    public static RootCommand BuildGame() {
        var root = new RootCommand("荒岛求生 — 最小回合流程原型");

        root.Add(BuildNewCommand());
        root.Add(BuildLookAroundCommand());
        root.Add(BuildEditMemoryNotebookCommand());
        root.Add(BuildInteractCommand());
        root.Add(BuildExploreCommand());
        root.Add(BuildRestAWhileCommand());
        root.Add(BuildDevGoCommand());
        root.Add(BuildDevAddLlmPlayerCommand());
        root.Add(BuildDevLookActorCommand());
        root.Add(BuildDevTurnStatusCommand());
        root.Add(BuildDevSubmitLargeActionCommand());
        root.Add(BuildDevShowActorJournalCommand());
        root.Add(BuildDevExportActorJournalsCommand());
        root.Add(BuildDevRunAutonomousRoundsCommand());

        return root;
    }

    private static Command BuildNewCommand() {
        var cmd = new Command("new", "开始新游戏（会覆盖旧存档）");
        cmd.SetAction(
            ctx => {
                var output = ctx.InvocationConfiguration.Output;
                var repoDir = TextAdvRuntimeEnvironment.GetRepoDir();

                _repo = null;
                _root = null;

                if (Directory.Exists(repoDir)) {
                    Directory.Delete(repoDir, recursive: true);
                }

                var createResult = Repository.Create(repoDir);
                if (!createResult.IsSuccess) {
                    output.WriteLine($"❌ 创建游戏世界失败：{createResult.Error}");
                    return;
                }

                _repo = createResult.Value!;
                _root = GameSimulation.CreateNewWorld(_repo);

                output.WriteLine("✅ 新世界已创建！");
                output.WriteLine($"💾 存档目录：{repoDir}");
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

    private static Command BuildLookAroundCommand() {
        var cmd = new Command("look-around", "重新显示当前局面、笔记本和操作速查");
        cmd.SetAction(
            ctx => {
                var output = ctx.InvocationConfiguration.Output;

                if (!TryGetState(output, out var state)) { return; }

                var (_, root) = state;
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

                if (!TryGetState(output, out var state)) { return; }

                var (repo, root) = state;
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
                    output.WriteLine($"❌ 动作检查失败：{ex.Message}");
                    return;
                }

                if (dryRun) {
                    output.Write(GamePresenter.RenderNotebookEditDryRun(perception, notebookEdit, preActionReason, validation));
                    return;
                }

                if (!validation.Accepted) {
                    output.WriteLine("❌ 这次改动没有通过检查。");
                    output.WriteLine(validation.Feedback);
                    return;
                }

                var updatedPerception = GameSimulation.ApplyNotebookEdit(root, notebookEdit, preActionReason, validation.Feedback);
                _ = repo.Commit(root).Value;

                output.WriteLine("✅ 记事本已更新。");
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

    private static async Task<GameActionValidator.ValidationResult?> TryValidateLargeActionAsync(
        TextWriter output,
        PerceptionBundle perception,
        string actionKind,
        string actionSummary,
        string preActionReason,
        string? actionPayload,
        CancellationToken cancellationToken
    ) {
        try {
            return await GameActionValidator.ValidateActionAsync(
                perception,
                actionKind,
                actionSummary,
                preActionReason,
                actionPayload,
                cancellationToken
            );
        }
        catch (Exception ex) {
            output.WriteLine($"❌ 动作检查失败：{ex.Message}");
            return null;
        }
    }

    private static async Task ExecuteTerminalLargeActionAsync(
        TextWriter output,
        string actionKind,
        string actionSummary,
        string? actionPayload,
        string preActionReason,
        Func<DurableDict<string>, GameActionValidator.ValidationResult, CancellationToken, Task<AsyncAteliaResult<TurnResolution>>> resolveAsync,
        CancellationToken cancellationToken
    ) {
        if (!TryGetState(output, out var state)) { return; }

        var (repo, root) = state;
        var perception = GameSimulation.DescribeCurrentPerception(root);
        var validation = await TryValidateLargeActionAsync(
            output,
            perception,
            actionKind,
            actionSummary,
            preActionReason,
            actionPayload,
            cancellationToken
        );
        if (validation is null) { return; }

        if (!validation.Accepted) {
            output.WriteLine("❌ 这一步没有通过检查。");
            output.WriteLine(validation.Feedback);
            return;
        }

        if (await TryCollectTerminalLargeActionInsteadOfResolvingAsync(
            output,
            repo,
            root,
            actionKind,
            actionSummary,
            actionPayload,
            preActionReason,
            validation.Feedback,
            cancellationToken
        )) { return; }

        var resolutionResult = await resolveAsync(root, validation, cancellationToken);
        if (!resolutionResult.TryGetValue(out var resolution) || resolution is null) {
            output.WriteLine($"❌ Large-Action 结算失败：{actionSummary}");
            WriteAteliaError(output, resolutionResult.Error);
            return;
        }

        _ = repo.Commit(root).Value;
        output.WriteLine($"✅ 你决定了：{actionSummary}");
        output.WriteLine($"📣 {resolution.Summary}");
        output.WriteLine();
        output.Write(GamePresenter.RenderPerception(resolution.NextPerception));
    }

    private static async Task<bool> TryCollectTerminalLargeActionInsteadOfResolvingAsync(
        TextWriter output,
        Repository repo,
        DurableDict<string> root,
        string actionKind,
        string actionSummary,
        string? actionPayload,
        string preActionReason,
        string validatorFeedback,
        CancellationToken cancellationToken
    ) {
        if (!GameSimulation.RequiresMultiActorCollection(root)) { return false; }

        var result = GameSimulation.SubmitLargeActionForActor(
            root,
            actorId: "player",
            actionKind,
            actionSummary,
            actionPayload,
            preActionReason,
            validatorFeedback
        );
        if (!result.TryGetValue(out var status) || status is null) {
            output.WriteLine("❌ 多主体回合收集失败。");
            WriteAteliaError(output, result.Error);
            return true;
        }

        var fallbackResult = await GameSimulation.SubmitLargeActionsForPendingLlmPlayersAsync(root, cancellationToken);
        if (!fallbackResult.TryGetValue(out status) || status is null) {
            output.WriteLine("❌ LLM Player 行动提交失败。");
            WriteAteliaError(output, fallbackResult.Error);
            return true;
        }

        if (status.AllActiveActorsSubmittedLargeAction) {
            var resolutionResult = await GameSimulation.ApplyReadyCollectedTurnAsync(root, cancellationToken);
            if (!resolutionResult.TryGetValue(out var resolution) || resolution is null) {
                output.WriteLine("❌ 多主体统一结算失败。");
                WriteAteliaError(output, resolutionResult.Error);
                return true;
            }

            _ = repo.Commit(root).Value;
            output.WriteLine($"✅ 你决定了：{actionSummary}");
            output.WriteLine($"📣 {resolution.Summary}");
            output.WriteLine();
            output.Write(GamePresenter.RenderPerception(resolution.NextPerception));
            return true;
        }

        _ = repo.Commit(root).Value;
        output.WriteLine($"✅ 你决定了：{actionSummary}");
        output.WriteLine("⏳ 其他同行还在行动，这一回合暂时还没完全结束。");
        output.WriteLine();
        output.Write(GamePresenter.RenderTurnCollectionStatus(status));
        return true;
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
                await ExecuteTerminalLargeActionAsync(
                    output,
                    actionKind: "large/explore",
                    actionSummary,
                    actionPayload,
                    preActionReason,
                    (root, validation, token) => GameSimulation.ApplyExploreAsync(
                        root,
                        direction,
                        focus,
                        preActionReason,
                        validation.Feedback,
                        token
                    ),
                    ct
                );
            }
        );
        return cmd;
    }

    private static Command BuildInteractCommand() {
        var reasonArg = new Argument<string>("reason") {
            Description = "这一步动作的事前推理。应先根据当前可见交互说明你为什么准备执行它。"
        };
        var interactionIdArg = new Argument<string>("interaction-id") {
            Description = "当前 Perception-Bundle 中可见的 InteractionId，例如 inspect-drag-marks。"
        };
        var cmd = new Command("interact", "Large-Action：执行一个当前可见的交互 affordance")
        {
            reasonArg,
            interactionIdArg,
        };
        cmd.SetAction(
            async (ctx, ct) => {
                var output = ctx.InvocationConfiguration.Output;
                var preActionReason = ctx.GetValue(reasonArg)!;
                var interactionId = ctx.GetValue(interactionIdArg)!;

                if (!TryGetState(output, out var state)) { return; }

                var (repo, root) = state;
                var perception = GameSimulation.DescribeCurrentPerception(root);
                var interactionResult = GameSimulation.TryGetVisibleInteraction(perception, interactionId);
                if (!interactionResult.TryGetValue(out var interaction) || interaction is null) {
                    output.WriteLine("❌ 当前看不到这个 interaction，不能执行。");
                    WriteAteliaError(output, interactionResult.Error);
                    return;
                }

                var actionSummary = $"{interaction.VisibleLabel} ({interaction.ActionKind})";
                var actionPayload = GameSimulation.BuildInteractionPayload(interaction);
                await ExecuteTerminalLargeActionAsync(
                    output,
                    actionKind: "large/interact",
                    actionSummary,
                    actionPayload,
                    preActionReason,
                    (resolvedRoot, validation, token) => GameSimulation.ApplyInteractionAsync(
                        resolvedRoot,
                        interaction.InteractionId,
                        preActionReason,
                        validation.Feedback,
                        token
                    ),
                    ct
                );
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
                await ExecuteTerminalLargeActionAsync(
                    output,
                    actionKind: "large/rest-a-while",
                    actionSummary,
                    actionPayload: null,
                    preActionReason,
                    (root, validation, _) => Task.FromResult(
                        AsyncAteliaResult<TurnResolution>.Success(
                            GameSimulation.ApplyRestAWhile(root, preActionReason, validation.Feedback)
                        )
                    ),
                    ct
                );
            }
        );
        return cmd;
    }
}
