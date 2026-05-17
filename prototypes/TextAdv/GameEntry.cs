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
        root.Add(BuildInteractCommand());
        root.Add(BuildExploreCommand());
        root.Add(BuildRestAWhileCommand());
        root.Add(BuildDevGoCommand());
        root.Add(BuildDevAddLlmPlayerCommand());
        root.Add(BuildDevLookActorCommand());
        root.Add(BuildDevTurnStatusCommand());
        root.Add(BuildDevSubmitLargeActionCommand());

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

    private static Command BuildDevAddLlmPlayerCommand() {
        var locationOption = new Option<string?>("--location") {
            Description = "可选：新 LLM player 的初始 LocationId；默认使用终端玩家当前位置。"
        };
        var actorIdArg = new Argument<string>("actor-id");
        var nameArg = new Argument<string>("name");
        var profileNoteArg = new Argument<string>("profile-note");
        var cmd = new Command("dev-add-llm-player", "开发者调试：创建一个 active llm-player actor（不驱动行动）")
        {
            locationOption,
            actorIdArg,
            nameArg,
            profileNoteArg,
        };
        cmd.SetAction(
            ctx => {
                var output = ctx.InvocationConfiguration.Output;
                var actorId = ctx.GetValue(actorIdArg)!;
                var name = ctx.GetValue(nameArg)!;
                var profileNote = ctx.GetValue(profileNoteArg)!;
                var locationId = ctx.GetValue(locationOption);

                var state = GetState();
                if (state is null) {
                    output.WriteLine("❌ 还没有游戏存档。请先运行 new 命令创建新世界。");
                    return;
                }

                var (repo, root) = state.Value;
                var result = GameSimulation.CreateLlmPlayerActor(root, actorId, name, profileNote, locationId);
                if (!result.TryGetValue(out var createdActorId) || string.IsNullOrWhiteSpace(createdActorId)) {
                    output.WriteLine("❌ 创建 LLM player actor 失败。");
                    WriteAteliaError(output, result.Error);
                    return;
                }

                _ = repo.Commit(root).Value;
                output.WriteLine($"✅ 已创建 llm-player actor: {createdActorId}");
                output.WriteLine();
                output.Write(GamePresenter.RenderPerception(GameSimulation.DescribePerceptionForActor(root, createdActorId)));
            }
        );
        return cmd;
    }

    private static Command BuildDevLookActorCommand() {
        var actorIdArg = new Argument<string>("actor-id");
        var cmd = new Command("dev-look-actor", "开发者调试：查看指定 actor 的 Perception-Bundle") { actorIdArg };
        cmd.SetAction(
            ctx => {
                var output = ctx.InvocationConfiguration.Output;
                var actorId = ctx.GetValue(actorIdArg)!;

                var state = GetState();
                if (state is null) {
                    output.WriteLine("❌ 还没有游戏存档。请先运行 new 命令创建新世界。");
                    return;
                }

                try {
                    var (_, root) = state.Value;
                    output.Write(GamePresenter.RenderPerception(GameSimulation.DescribePerceptionForActor(root, actorId)));
                }
                catch (Exception ex) {
                    output.WriteLine($"❌ 无法查看 actor '{actorId}'：{ex.Message}");
                }
            }
        );
        return cmd;
    }

    private static Command BuildDevTurnStatusCommand() {
        var cmd = new Command("dev-turn-status", "开发者调试：查看当前回合 barrier 与各 active actor 的 Large-Action 提交状态");
        cmd.SetAction(
            ctx => {
                var output = ctx.InvocationConfiguration.Output;

                var state = GetState();
                if (state is null) {
                    output.WriteLine("❌ 还没有游戏存档。请先运行 new 命令创建新世界。");
                    return;
                }

                var (_, root) = state.Value;
                output.Write(GamePresenter.RenderTurnCollectionStatus(GameSimulation.DescribeCurrentTurnStatus(root)));
            }
        );
        return cmd;
    }

    private static Command BuildDevSubmitLargeActionCommand() {
        var payloadOption = new Option<string?>("--payload") {
            Description = "可选：动作 payload，按行保存到 actionPayload。"
        };
        var actorIdArg = new Argument<string>("actor-id");
        var actionKindArg = new Argument<string>("action-kind");
        var summaryArg = new Argument<string>("summary");
        var reasonArg = new Argument<string>("reason");
        var cmd = new Command("dev-submit-large-action", "开发者调试：为任意 active actor 提交一个 Large-Action（绕过 validator，不触发 GM）")
        {
            payloadOption,
            actorIdArg,
            actionKindArg,
            summaryArg,
            reasonArg,
        };
        cmd.SetAction(
            ctx => {
                var output = ctx.InvocationConfiguration.Output;
                var actorId = ctx.GetValue(actorIdArg)!;
                var actionKind = ctx.GetValue(actionKindArg)!;
                var summary = ctx.GetValue(summaryArg)!;
                var reason = ctx.GetValue(reasonArg)!;
                var payload = ctx.GetValue(payloadOption);

                var state = GetState();
                if (state is null) {
                    output.WriteLine("❌ 还没有游戏存档。请先运行 new 命令创建新世界。");
                    return;
                }

                var (repo, root) = state.Value;
                var result = GameSimulation.SubmitDevLargeActionForActor(root, actorId, actionKind, summary, payload, reason);
                if (!result.TryGetValue(out var status) || status is null) {
                    output.WriteLine("❌ dev Large-Action 提交失败。");
                    WriteAteliaError(output, result.Error);
                    return;
                }

                _ = repo.Commit(root).Value;
                output.WriteLine($"✅ dev Large-Action 已提交：[{actorId}] {actionKind} — {summary}");
                output.WriteLine();
                output.Write(GamePresenter.RenderTurnCollectionStatus(status));
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

    private static bool TryCollectTerminalLargeActionInsteadOfResolving(
        TextWriter output,
        Repository repo,
        DurableDict<string> root,
        string actionKind,
        string actionSummary,
        string? actionPayload,
        string preActionReason,
        string validatorFeedback
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

        _ = repo.Commit(root).Value;
        output.WriteLine($"✅ Large-Action 已接受并进入多主体回合收集：{actionSummary}");
        output.WriteLine($"🧪 validator: {validatorFeedback}");
        output.WriteLine("⏳ 仍需等待其他 active actor 提交 Large-Action；当前 MVP 暂不自动驱动 LLM Player。");
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

                if (TryCollectTerminalLargeActionInsteadOfResolving(
                    output,
                    repo,
                    root,
                    actionKind: "large/explore",
                    actionSummary,
                    actionPayload,
                    preActionReason,
                    validation.Feedback
                )) {
                    return;
                }

                var resolutionResult = await GameSimulation.ApplyExploreAsync(root, direction, focus, preActionReason, validation.Feedback, ct);
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

                var state = GetState();
                if (state is null) {
                    output.WriteLine("❌ 还没有游戏存档。请先运行 new 命令创建新世界。");
                    return;
                }

                var (repo, root) = state.Value;
                var perception = GameSimulation.DescribeCurrentPerception(root);
                var interactionResult = GameSimulation.TryGetVisibleInteraction(perception, interactionId);
                if (!interactionResult.TryGetValue(out var interaction) || interaction is null) {
                    output.WriteLine("❌ 当前看不到这个 interaction，不能执行。");
                    WriteAteliaError(output, interactionResult.Error);
                    return;
                }

                var actionSummary = $"{interaction.VisibleLabel} ({interaction.ActionKind})";
                var actionPayload = GameSimulation.BuildInteractionPayload(interaction);

                GameActionValidator.ValidationResult validation;
                try {
                    validation = await GameActionValidator.ValidateActionAsync(
                        perception,
                        actionKind: "large/interact",
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

                if (TryCollectTerminalLargeActionInsteadOfResolving(
                    output,
                    repo,
                    root,
                    actionKind: "large/interact",
                    actionSummary,
                    actionPayload,
                    preActionReason,
                    validation.Feedback
                )) {
                    return;
                }

                var resolutionResult = await GameSimulation.ApplyInteractionAsync(
                    root,
                    interaction.InteractionId,
                    preActionReason,
                    validation.Feedback,
                    ct
                );
                if (!resolutionResult.TryGetValue(out var resolution) || resolution is null) {
                    output.WriteLine("❌ GM 交互结算失败。");
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

                if (TryCollectTerminalLargeActionInsteadOfResolving(
                    output,
                    repo,
                    root,
                    actionKind: "large/rest-a-while",
                    actionSummary,
                    actionPayload: null,
                    preActionReason,
                    validation.Feedback
                )) {
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
