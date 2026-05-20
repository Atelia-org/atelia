using Atelia.StateJournal;
using Xunit;

namespace Atelia.TextAdv.Tests;

public sealed class GameSimulationPlayerEquivalenceTests : IDisposable {
    private readonly string _repoDir = Path.Combine(
        Path.GetTempPath(),
        "textadv-tests",
        Guid.NewGuid().ToString("N")
    );

    public void Dispose() {
        LlmPlayerAgentDriver.ResetForTests();
        if (Directory.Exists(_repoDir)) {
            Directory.Delete(_repoDir, recursive: true);
        }
    }

    [Fact]
    public void TerminalAndInternalPlayers_ShouldSharePlayerKindProjection() {
        using var repo = CreateRepository();
        var root = GameSimulation.CreateNewWorld(repo);

        var playerPerception = GameSimulation.DescribeCurrentPerception(root);
        Assert.Equal(GameSimulation.PlayerActorKind, playerPerception.ActorKind);

        var createResult = GameSimulation.CreateLlmPlayerActor(
            root,
            "ally",
            "同伴",
            "另一个由 internal LLM 驱动的玩家。",
            "beach"
        );
        Assert.True(createResult.IsSuccess, createResult.Error?.Message);

        var allyPerception = GameSimulation.DescribePerceptionForActor(root, "ally");
        Assert.Equal(GameSimulation.PlayerActorKind, allyPerception.ActorKind);
    }

    [Fact]
    public void NewWorld_ShouldNotPersistTerminalHelpModeInsideWorldState() {
        using var repo = CreateRepository();
        var root = GameSimulation.CreateNewWorld(repo);
        var game = root.GetOrThrow<DurableDict<string>>("game")!;

        Assert.False(game.TryGet("terminalHelpMode", out string? _));
    }

    [Fact]
    public async Task SubmitLargeActionsForPendingInternalPlayersAsync_ShouldUseControllerKind_NotActorKind() {
        using var repo = CreateRepository();
        var root = GameSimulation.CreateNewWorld(repo);
        var drivenActorIds = new List<string>();

        Assert.True(
            GameSimulation.CreateLlmPlayerActor(
                root,
                "ally",
                "同伴",
                "另一个由 internal LLM 驱动的玩家。",
                "beach"
            ).IsSuccess
        );

        LlmPlayerAgentDriver.SetStubForTests(
            new LlmPlayerAgentDriver.LlmPlayerStub(
                (stubRoot, actorId, _) => {
                    drivenActorIds.Add(actorId);
                    var submitResult = GameSimulation.SubmitDevLargeActionForActor(
                        stubRoot,
                        actorId,
                        new ActionDescriptor(
                            TerminalActionKinds.LargeRestAWhile,
                            $"{actorId} 谨慎观察并暂不移动",
                            null,
                            "测试桩：为 pending internal player 提交保守动作。"
                        )
                    );
                    return Task.FromResult(
                        submitResult.IsSuccess
                            ? AsyncAteliaResult<TurnCollectionStatus>.Success(submitResult.Value!)
                            : AsyncAteliaResult<TurnCollectionStatus>.Failure(submitResult.Error!)
                    );
                }
            )
        );

        var terminalSubmit = GameSimulation.SubmitDevLargeActionForActor(
            root,
            GameSimulation.TerminalPlayerActorId,
            new ActionDescriptor(
                TerminalActionKinds.LargeRestAWhile,
                "终端玩家先提交本回合动作",
                null,
                "测试桩：先让 external-terminal actor 入 barrier。"
            )
        );
        Assert.True(terminalSubmit.IsSuccess, terminalSubmit.Error?.Message);

        var collectResult = await GameSimulation.SubmitLargeActionsForPendingInternalPlayersAsync(
            root,
            CancellationToken.None
        );
        Assert.True(collectResult.IsSuccess, collectResult.Error?.Message);
        Assert.Equal(["ally"], drivenActorIds);
        Assert.True(collectResult.Value!.AllActiveActorsSubmittedLargeAction);
        Assert.All(collectResult.Value.Actors, actor => Assert.Equal(GameSimulation.PlayerActorKind, actor.Kind));
    }

    private Repository CreateRepository() {
        var createResult = Repository.Create(_repoDir);
        return AssertSuccess(createResult);
    }

    private static T AssertSuccess<T>(AteliaResult<T> result)
        where T : notnull {
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);
        return result.Value!;
    }
}
