using System.CommandLine;
using Atelia.StateJournal;

namespace Atelia.TextAdv;

public static partial class GameEntry {
    private static Command BuildDevGoCommand() {
        var directionArg = new Argument<string>("direction");
        var cmd = new Command("dev-go", "开发者调试：直接移动到相邻区域（不参与回合结算）") { directionArg };
        cmd.SetAction(
            ctx => {
                var output = ctx.InvocationConfiguration.Output;
                var direction = ctx.GetValue(directionArg)!;

                if (!TryGetSession(output, out var session)) { return; }

                var repo = session.Repo;
                var root = session.Root;
                var moveResult = GameSimulation.MovePlayer(root, direction);

                if (!moveResult.IsSuccess) {
                    output.WriteLine($"❌ {moveResult.Error!.Message}");
                    return;
                }

                _ = repo.Commit(root).Value;

                output.WriteLine("⚠️ 这是 dev-go 开发者调试移动，不会记录为回合步骤，也不会触发 validator。");
                output.WriteLine($"🚶 你向 {direction} 方向走去…");
                output.WriteLine();
                output.Write(session.RenderPerception(moveResult.Value!));
            }
        );
        return cmd;
    }

    private static Command BuildDevAddLlmPlayerCommand() {
        var locationOption = new Option<string?>("--location") {
            Description = "可选：新内部玩家的初始 LocationId；默认使用终端玩家当前位置。"
        };
        var actorIdArg = new Argument<string>("actor-id");
        var nameArg = new Argument<string>("name");
        var profileNoteArg = new Argument<string>("profile-note");
        var cmd = new Command("dev-add-llm-player", "开发者调试：创建一个 active 内部玩家 actor（默认由 internal LLM 管线驱动）")
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

                if (!TryGetSession(output, out var session)) { return; }

                var repo = session.Repo;
                var root = session.Root;
                var result = GameSimulation.CreateLlmPlayerActor(root, actorId, name, profileNote, locationId);
                if (!result.TryGetValue(out var createdActorId) || string.IsNullOrWhiteSpace(createdActorId)) {
                    output.WriteLine("❌ 创建内部玩家 actor 失败。");
                    WriteAteliaError(output, result.Error);
                    return;
                }

                _ = repo.Commit(root).Value;
                output.WriteLine($"✅ 已创建内部玩家 actor: {createdActorId}");
                output.WriteLine();
                output.Write(
                    session.RenderPerception(GameSimulation.DescribePerceptionForActor(root, createdActorId))
                );
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

                try {
                    if (!TryGetSession(output, out var session)) { return; }

                    var root = session.Root;
                    output.Write(
                        session.RenderPerception(GameSimulation.DescribePerceptionForActor(root, actorId))
                    );
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

                if (!TryGetSession(output, out var session)) { return; }

                var root = session.Root;
                output.Write(GamePresenter.RenderTurnCollectionStatus(GameSimulation.DescribeCurrentTurnStatus(root)));
            }
        );
        return cmd;
    }

    private static Command BuildDevShowActorJournalCommand() {
        var actorIdArg = new Argument<string>("actor-id");
        var cmd = new Command("dev-show-actor-journal", "开发者调试：显示指定 actor 的第一人称日志") { actorIdArg };
        cmd.SetAction(
            ctx => {
                var output = ctx.InvocationConfiguration.Output;
                var actorId = ctx.GetValue(actorIdArg)!;

                try {
                    if (!TryGetSession(output, out var session)) { return; }

                    var root = session.Root;
                    var journal = GameSimulation.BuildActorJournalExport(root, actorId);
                    output.WriteLine($"# {journal.ActorName} [{journal.ActorId}, {journal.ActorKind}]");
                    output.WriteLine();
                    output.WriteLine(journal.Content);
                }
                catch (Exception ex) {
                    output.WriteLine($"❌ 无法显示 actor journal '{actorId}'：{ex.Message}");
                }
            }
        );
        return cmd;
    }

    private static Command BuildDevExportActorJournalsCommand() {
        var outputDirOption = new Option<string?>("--output-dir") {
            Description = $"可选：导出目录；默认 {TextAdvRuntimeEnvironment.ActorJournalDirEnv} 或存档目录下 actor-journals。"
        };
        var cmd = new Command("dev-export-actor-journals", "开发者调试：导出所有 actor 的第一人称日志 Markdown 文件")
        {
            outputDirOption
        };
        cmd.SetAction(
            ctx => {
                var output = ctx.InvocationConfiguration.Output;
                var outputDir = ctx.GetValue(outputDirOption);
                outputDir = string.IsNullOrWhiteSpace(outputDir)
                    ? TextAdvRuntimeEnvironment.GetActorJournalDir()
                    : outputDir.Trim();

                if (!TryGetSession(output, out var session)) { return; }

                var root = session.Root;
                ExportActorJournals(output, root, outputDir);
            }
        );
        return cmd;
    }

    private static Command BuildDevRunAutonomousRoundsCommand() {
        var ensureLlmPlayersOption = new Option<int>("--ensure-llm-players") {
            Description = "运行前至少补足多少个 diagnostic 内部玩家；默认 2，传 0 可只托管终端玩家。",
            DefaultValueFactory = _ => 2
        };
        var skipExportOption = new Option<bool>("--skip-export") {
            Description = "只推进世界与账本，不在结束后导出 actor journal 文件。"
        };
        var realAgentsOption = new Option<bool>("--real-agents") {
            Description = "为其他 diagnostic 内部玩家启用真实 LLM Player 管线；GM 结算始终要求真实 GM Agent。默认关闭时，其它 actor 会提交保守的诊断动作。"
        };
        var outputDirOption = new Option<string?>("--output-dir") {
            Description = $"actor journal 导出目录；默认 {TextAdvRuntimeEnvironment.ActorJournalDirEnv} 或存档目录下 actor-journals。"
        };
        var roundsArg = new Argument<int>("rounds") {
            Description = "要自动推进的诊断回合数。"
        };
        var cmd = new Command("dev-run-autonomous-rounds", "开发者调试：托管终端玩家并自动推进若干多主体回合")
        {
            ensureLlmPlayersOption,
            skipExportOption,
            realAgentsOption,
            outputDirOption,
            roundsArg
        };
        cmd.SetAction(
            async (ctx, ct) => {
                var output = ctx.InvocationConfiguration.Output;
                var rounds = ctx.GetValue(roundsArg);
                var ensureLlmPlayers = ctx.GetValue(ensureLlmPlayersOption);
                var skipExport = ctx.GetValue(skipExportOption);
                var realAgents = ctx.GetValue(realAgentsOption);
                var outputDir = ctx.GetValue(outputDirOption);

                if (rounds <= 0) {
                    output.WriteLine("❌ rounds 必须大于 0。");
                    return;
                }

                if (!TryGetSession(output, out var session)) { return; }

                var repo = session.Repo;
                var root = session.Root;
                var ensureResult = GameSimulation.EnsureDiagnosticLlmPlayers(root, ensureLlmPlayers);
                if (!ensureResult.TryGetValue(out var ensuredActorIds) || ensuredActorIds is null) {
                    output.WriteLine("❌ diagnostic 内部玩家准备失败。");
                    WriteAteliaError(output, ensureResult.Error);
                    return;
                }

                _ = repo.Commit(root).Value;
                if (ensuredActorIds.Count > 0) {
                    output.WriteLine($"✅ 已补充 diagnostic 内部玩家: {string.Join(", ", ensuredActorIds)}");
                }
                else {
                    output.WriteLine("✅ diagnostic 内部玩家已满足目标数量。");
                }

                var completedRounds = 0;
                for (var index = 1; index <= rounds; index++) {
                    var roundResult = await GameSimulation.RunDiagnosticAutonomousRoundAsync(root, index, realAgents, ct)
                        .ConfigureAwait(false);
                    if (!roundResult.TryGetValue(out var report) || report is null) {
                        output.WriteLine($"❌ 自动诊断第 {index} 回合失败。");
                        WriteAteliaError(output, roundResult.Error);
                        return;
                    }

                    _ = repo.Commit(root).Value;
                    completedRounds++;
                    output.WriteLine($"✅ Round {report.RoundNumber:D4}: {report.TerminalActionSummary}");
                    output.WriteLine($"📣 {report.ResolutionSummary}");
                }

                output.WriteLine();
                output.WriteLine($"🏁 自动诊断完成：{completedRounds}/{rounds} 回合。");

                if (!skipExport) {
                    outputDir = string.IsNullOrWhiteSpace(outputDir)
                        ? TextAdvRuntimeEnvironment.GetActorJournalDir()
                        : outputDir.Trim();
                    ExportActorJournals(output, root, outputDir);
                }
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

                if (!TryGetSession(output, out var session)) { return; }

                var repo = session.Repo;
                var root = session.Root;
                var result = GameSimulation.SubmitDevLargeActionForActor(
                    root,
                    actorId,
                    new ActionDescriptor(actionKind, summary, payload, reason)
                );
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

    private static string BuildActorJournalFileContent(ActorJournalExport journal) {
        return $"""
# {journal.ActorName} [{journal.ActorId}, {journal.ActorKind}]

{journal.Content}
""";
    }

    private static void ExportActorJournals(TextWriter output, DurableDict<string> root, string outputDir) {
        Directory.CreateDirectory(outputDir);
        var journals = GameSimulation.BuildActorJournalExports(root);
        foreach (var journal in journals) {
            var path = Path.Combine(outputDir, journal.FileName);
            File.WriteAllText(path, BuildActorJournalFileContent(journal));
        }

        output.WriteLine($"✅ 已导出 {journals.Count} 个 actor journal。");
        output.WriteLine($"📁 {outputDir}");
        foreach (var journal in journals) {
            output.WriteLine($"- {journal.ActorName} [{journal.ActorId}] -> {Path.Combine(outputDir, journal.FileName)}");
        }
    }
}
