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
    private static TextAdvSession? _session;
    private static readonly TextAdvTerminalActionRunner s_actionRunner = TextAdvTerminalActionRunner.Default;

    private static bool TryLoadSession(out TextAdvSession? session, out AteliaError? error) {
        if (_session is not null) {
            session = _session;
            error = null;
            return true;
        }

        var repoDir = TextAdvRuntimeEnvironment.GetRepoDir();
        if (!Directory.Exists(repoDir)) {
            session = null;
            error = null;
            return false;
        }

        var sessionResult = TextAdvSession.Load(repoDir);
        if (sessionResult.TryGetValue(out var loadedSession) && loadedSession is not null) {
            _session = loadedSession;
            session = loadedSession;
            error = null;
            return true;
        }

        error = sessionResult.Error;
        session = null;
        return false;
    }

    private static bool TryGetSession(TextWriter output, out TextAdvSession session) {
        if (TryLoadSession(out var loadedSession, out var error) && loadedSession is not null) {
            session = loadedSession;
            return true;
        }

        session = null!;
        if (error is not null) {
            output.WriteLine("❌ 无法打开当前游戏会话。");
            WriteAteliaError(output, error);
            return false;
        }

        output.WriteLine("❌ 还没有游戏存档。请先运行 new 命令创建新世界。");
        output.WriteLine($"💡 当前存档目录：{TextAdvRuntimeEnvironment.GetRepoDir()}");
        return false;
    }

    public static RootCommand BuildGame() {
        var root = new RootCommand("荒岛求生 — 最小回合流程原型");

        root.Add(BuildNewCommand());
        root.Add(BuildLoadVersionCommand());
        root.Add(BuildHelpCommand());
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

                _session = null;

                if (Directory.Exists(repoDir)) {
                    Directory.Delete(repoDir, recursive: true);
                }

                var createResult = TextAdvSession.CreateNew(repoDir);
                if (!createResult.TryGetValue(out var session) || session is null) {
                    output.WriteLine("❌ 创建游戏世界失败。");
                    WriteAteliaError(output, createResult.Error);
                    return;
                }

                _session = session;

                output.WriteLine("✅ 新世界已创建！");
                output.WriteLine($"💾 存档目录：{repoDir}");
                output.WriteLine();
                output.Write(session.RenderCurrentPerception(forceShowFullHelp: true));
            }
        );
        return cmd;
    }

    private static Command BuildLoadVersionCommand() {
        var versionArg = new Argument<string>("version-address") {
            Description = "要加载的历史版本地址，例如 seg:1:0123abcd4567ef89。"
        };
        var cmd = new Command("load-version", "从指定历史版本派生一条新的存档线，并切换过去继续游玩") {
            versionArg
        };
        cmd.SetAction(
            ctx => {
                var output = ctx.InvocationConfiguration.Output;
                var versionText = ctx.GetValue(versionArg)!;

                if (!TryGetSession(output, out var session)) { return; }

                var parsedAddress = CommitAddress.TryParse(versionText);
                if (parsedAddress is null) {
                    output.WriteLine($"❌ version-address 格式无效：{versionText}");
                    output.WriteLine("💡 期待格式：seg:<segmentNumber>:<ticketHex16>");
                    return;
                }

                var loadResult = session.LoadVersionAsNewBranch(parsedAddress.Value);
                if (!loadResult.TryGetValue(out var loadedSession) || loadedSession is null) {
                    output.WriteLine("❌ 无法加载指定版本。");
                    WriteAteliaError(output, loadResult.Error);
                    return;
                }

                var persistResult = loadedSession.Persist(TextAdvRuntimeEnvironment.GetRepoDir());
                if (!persistResult.TryGetValue(out loadedSession) || loadedSession is null) {
                    output.WriteLine("❌ 已创建新的存档线，但无法保存当前会话指针。");
                    WriteAteliaError(output, persistResult.Error);
                    return;
                }

                _session = loadedSession;
                var perception = GameSimulation.DescribeCurrentPerception(loadedSession.Root);

                output.WriteLine($"✅ 已从版本 {parsedAddress.Value} 加载新的存档线。");
                output.WriteLine($"🌿 当前 branch: {loadedSession.BranchName}");
                output.WriteLine("⚠️ 原来的 main 不会被改写；之后的操作会继续写入这条新 branch。");
                output.WriteLine();
                output.Write(loadedSession.RenderPerception(perception));
            }
        );
        return cmd;
    }

    private static Command BuildHelpCommand() {
        var modeArg = new Argument<string?>("mode") {
            Description = "可选：on=以后每次都显示完整速查，off=以后只保留最简帮助提示，status=查看当前设置。省略时只显示一次完整帮助。",
            Arity = ArgumentArity.ZeroOrOne
        };
        var cmd = new Command("help", "显示或切换终端玩家帮助模式") { modeArg };
        cmd.SetAction(
            ctx => {
                var output = ctx.InvocationConfiguration.Output;
                var mode = ctx.GetValue(modeArg);

                if (string.IsNullOrWhiteSpace(mode)) {
                    if (!TryLoadSession(out var currentSession, out _) || currentSession is null) {
                        output.Write(GamePresenter.RenderStandaloneHelp(perception: null, TerminalHelpMode.Off));
                        return;
                    }

                    output.Write(
                        GamePresenter.RenderStandaloneHelp(
                            GameSimulation.DescribeCurrentPerception(currentSession.Root),
                            currentSession.HelpMode
                        )
                    );
                    return;
                }

                if (!TryGetSession(output, out var session)) { return; }

                switch (mode.Trim().ToLowerInvariant()) {
                    case "on":
                        var helpOnResult = session.WithHelpMode(TerminalHelpMode.On).Persist(TextAdvRuntimeEnvironment.GetRepoDir());
                        if (!helpOnResult.TryGetValue(out session) || session is null) {
                            output.WriteLine("❌ 无法保存帮助模式设置。");
                            WriteAteliaError(output, helpOnResult.Error);
                            return;
                        }
                        _session = session;
                        output.WriteLine("✅ 以后每次局面渲染后都会附带完整操作速查。");
                        output.WriteLine();
                        output.Write(
                            GamePresenter.RenderStandaloneHelp(
                                GameSimulation.DescribeCurrentPerception(session.Root),
                                session.HelpMode
                            )
                        );
                        return;
                    case "off":
                        var helpOffResult = session.WithHelpMode(TerminalHelpMode.Off).Persist(TextAdvRuntimeEnvironment.GetRepoDir());
                        if (!helpOffResult.TryGetValue(out session) || session is null) {
                            output.WriteLine("❌ 无法保存帮助模式设置。");
                            WriteAteliaError(output, helpOffResult.Error);
                            return;
                        }
                        _session = session;
                        output.WriteLine("✅ 以后默认只保留最简帮助提示；完整速查可随时用 `pmux game help` 查看。");
                        output.WriteLine();
                        output.WriteLine(GamePresenter.RenderStandaloneHelpStatus(TerminalHelpMode.Off));
                        output.WriteLine();
                        output.Write(PlayerActionGuideCatalog.RenderTerminalMinimalHelpHint());
                        return;
                    case "status":
                        output.WriteLine(GamePresenter.RenderStandaloneHelpStatus(session.HelpMode));
                        return;
                    default:
                        output.WriteLine("❌ help mode 只支持 on / off / status。");
                        return;
                }
            }
        );
        return cmd;
    }

    private static Command BuildLookAroundCommand() {
        var cmd = new Command("look-around", "重新显示当前局面、笔记本和帮助提示");
        cmd.SetAction(
            ctx => {
                var output = ctx.InvocationConfiguration.Output;

                if (!TryGetSession(output, out var session)) { return; }

                output.Write(session.RenderCurrentPerception());
            }
        );
        return cmd;
    }

    private static Command BuildEditMemoryNotebookCommand() {
        var dryRunOption = new Option<bool>("--dry-run") {
            Description = "只做 parse、after-view 预演和 validator 校验，不写入 notebook，也不记录 accepted step。"
        };
        var reasonArg = new Argument<string>("reason") {
            Description = PlayerActionGuideCatalog.GetEditMemoryNotebookReasonArgumentDescription()
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

                if (!TryGetSession(output, out var session)) { return; }

                var repo = session.Repo;
                var root = session.Root;
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
                        actionKind: TerminalActionKinds.SmallEditMemoryNotebook,
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
                output.Write(session.RenderPerception(updatedPerception));
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

    private static void WriteTerminalActionRunResult(TextWriter output, TerminalActionRunResult result) {
        switch (result) {
            case TerminalActionRunResult.Success success:
                output.WriteLine(success.Message);
                output.WriteLine();
                output.Write(success.BodyText);
                return;
            case TerminalActionRunResult.ValidationRejected rejected:
                output.WriteLine(rejected.Message);
                output.WriteLine(rejected.Feedback);
                return;
            case TerminalActionRunResult.Failure failure:
                output.WriteLine(failure.Message);
                if (failure.Error is not null) {
                    WriteAteliaError(output, failure.Error);
                }
                return;
            default:
                throw new InvalidOperationException($"Unknown terminal action run result type: {result.GetType().FullName}");
        }
    }

    private static Command BuildExploreCommand() {
        var focusOption = new Option<string?>("--focus") {
            Description = "可选：你希望重点寻找或确认的对象，例如“山洞入口”“淡水痕迹”。"
        };
        var reasonArg = new Argument<string>("reason") {
            Description = PlayerActionGuideCatalog.GetExploreReasonArgumentDescription()
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
                await RunPlanCommandAsync(
                    output,
                    static (_, state) => GameSimulation.BuildExploreTerminalPlan(
                        state.Direction,
                        state.Focus,
                        state.PreActionReason
                    ),
                    (Direction: direction, Focus: focus, PreActionReason: preActionReason),
                    "❌ 当前不能构造这个 explore 动作。",
                    ct
                );
            }
        );
        return cmd;
    }

    private static Command BuildInteractCommand() {
        var reasonArg = new Argument<string>("reason") {
            Description = PlayerActionGuideCatalog.GetInteractReasonArgumentDescription()
        };
        var interactionIdArg = new Argument<string>("interaction-id") {
            Description = "当前 Perception-Bundle 中可见的 InteractionId，例如 inspect-drag-marks。"
        };
        var cmd = new Command("interact", "执行一个当前可见的交互 affordance；有些只是顺手动作，有些会结束本回合")
        {
            reasonArg,
            interactionIdArg,
        };
        cmd.SetAction(
            async (ctx, ct) => {
                var output = ctx.InvocationConfiguration.Output;
                var preActionReason = ctx.GetValue(reasonArg)!;
                var interactionId = ctx.GetValue(interactionIdArg)!;

                await RunPlanCommandAsync(
                    output,
                    static (session, state) => GameSimulation.BuildTerminalInteractionPlan(
                        GameSimulation.DescribeCurrentPerception(session.Root),
                        state.InteractionId,
                        state.PreActionReason
                    ),
                    (InteractionId: interactionId, PreActionReason: preActionReason),
                    "❌ 当前不能执行这个 interaction。",
                    ct
                );
            }
        );
        return cmd;
    }

    private static Command BuildRestAWhileCommand() {
        var reasonArg = new Argument<string>("reason") {
            Description = PlayerActionGuideCatalog.GetRestReasonArgumentDescription()
        };
        var cmd = new Command("rest-a-while", "Large-Action：原地休息一会，并结束当前回合")
        {
            reasonArg,
        };
        cmd.SetAction(
            async (ctx, ct) => {
                var output = ctx.InvocationConfiguration.Output;
                var preActionReason = ctx.GetValue(reasonArg)!;
                await RunPlanCommandAsync(
                    output,
                    static (_, reason) => GameSimulation.BuildRestAWhileTerminalPlan(reason),
                    preActionReason,
                    "❌ 当前不能构造这个 rest-a-while 动作。",
                    ct
                );
            }
        );
        return cmd;
    }

    private static async Task RunPlanCommandAsync<TState>(
        TextWriter output,
        Func<TextAdvSession, TState, AteliaResult<TerminalActionExecutionPlan>> buildPlan,
        TState state,
        string buildFailureMessage,
        CancellationToken cancellationToken
    ) {
        if (!TryGetSession(output, out var session)) { return; }

        var planResult = buildPlan(session, state);
        if (!planResult.TryGetValue(out var plan) || plan is null) {
            output.WriteLine(buildFailureMessage);
            WriteAteliaError(output, planResult.Error);
            return;
        }

        WriteTerminalActionRunResult(output, await s_actionRunner.RunAsync(session, plan, cancellationToken));
    }
}
