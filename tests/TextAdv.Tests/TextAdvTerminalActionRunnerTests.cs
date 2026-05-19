using Atelia.StateJournal;
using Xunit;

namespace Atelia.TextAdv.Tests;

public sealed class TextAdvTerminalActionRunnerTests : IDisposable {
    private readonly string _repoDir = Path.Combine(
        Path.GetTempPath(),
        "textadv-runner-tests",
        Guid.NewGuid().ToString("N")
    );

    public void Dispose() {
        try {
            if (Directory.Exists(_repoDir)) {
                Directory.Delete(_repoDir, recursive: true);
            }
        }
        catch {
        }
    }

    [Fact]
    public async Task RunAsync_WhenValidatorRejects_ShouldNotExecuteOrCommit() {
        var session = AssertSuccess(TextAdvSession.CreateNew(_repoDir));
        var initialHead = session.HeadAddress;
        var executeCalled = false;
        var runner = new TextAdvTerminalActionRunner(
            static (_, _, _, _, _, _) => Task.FromResult(new GameActionValidator.ValidationResult(false, "证据不足。")),
            (_, _, _, _) => {
                executeCalled = true;
                return Task.FromResult(
                    AsyncAteliaResult<ActionResolution>.Success(
                        new ActionResolution("不应走到这里。", GameSimulation.DescribeCurrentPerception(session.Root))
                    )
                );
            }
        );

        var result = await runner.RunAsync(
            session,
            new TerminalActionExecutionPlan.RestAWhile("先试试看。"),
            CancellationToken.None
        );

        var rejected = Assert.IsType<TerminalActionRunResult.ValidationRejected>(result);
        Assert.Equal("证据不足。", rejected.Feedback);
        Assert.False(executeCalled);
        Assert.Equal(initialHead, session.HeadAddress);
        session.Repo.Dispose();
    }

    [Fact]
    public async Task RunAsync_WhenImmediatePlanSucceeds_ShouldCommitAndRenderSessionPerception() {
        var session = AssertSuccess(TextAdvSession.CreateNew(_repoDir));
        var initialHead = session.HeadAddress;
        var runner = new TextAdvTerminalActionRunner(
            static (_, _, _, _, _, _) => Task.FromResult(new GameActionValidator.ValidationResult(true, "通过。")),
            (root, _, _, _) => {
                root.Upsert("runnerTestFlag", true);
                return Task.FromResult(
                    AsyncAteliaResult<ActionResolution>.Success(
                        new ActionResolution("已经记下。", GameSimulation.DescribeCurrentPerception(root))
                    )
                );
            }
        );

        var result = await runner.RunAsync(
            session,
            new TerminalActionExecutionPlan.Interaction(
                "test-interaction",
                "测试顺手动作",
                "test",
                "interactionId=test-interaction",
                InteractionExecutionKind.ImmediateSelf,
                "先记一笔。"
            ),
            CancellationToken.None
        );

        var success = Assert.IsType<TerminalActionRunResult.Success>(result);
        Assert.Equal("✅ 你顺手做了：测试顺手动作 (test)", success.Message);
        Assert.Contains("🗓️", success.BodyText);
        Assert.NotEqual(initialHead, session.HeadAddress);
        Assert.Equal(GetIssue.None, session.Root.Get("runnerTestFlag", out bool flag));
        Assert.True(flag);
        session.Repo.Dispose();
    }

    [Fact]
    public async Task RunAsync_WhenImmediatePlanFails_ShouldReturnFailureWithoutCommit() {
        var session = AssertSuccess(TextAdvSession.CreateNew(_repoDir));
        var initialHead = session.HeadAddress;
        var runner = new TextAdvTerminalActionRunner(
            static (_, _, _, _, _, _) => Task.FromResult(new GameActionValidator.ValidationResult(true, "通过。")),
            (_, _, _, _) => Task.FromResult(
                AsyncAteliaResult<ActionResolution>.Failure(
                    new TextAdvError("TextAdv.RunnerTestFailure", "模拟失败")
                )
            )
        );

        var result = await runner.RunAsync(
            session,
            new TerminalActionExecutionPlan.Interaction(
                "test-interaction",
                "测试顺手动作",
                "test",
                "interactionId=test-interaction",
                InteractionExecutionKind.ImmediateSelf,
                "先记一笔。"
            ),
            CancellationToken.None
        );

        var failure = Assert.IsType<TerminalActionRunResult.Failure>(result);
        Assert.Equal("❌ 小动作结算失败：测试顺手动作 (test)", failure.Message);
        Assert.Equal("TextAdv.RunnerTestFailure", failure.Error?.ErrorCode);
        Assert.Equal(initialHead, session.HeadAddress);
        session.Repo.Dispose();
    }

    [Fact]
    public async Task RunAsync_WhenLargeInteractionPlanSucceeds_ShouldUseLargeSuccessMessage() {
        var session = AssertSuccess(TextAdvSession.CreateNew(_repoDir));
        var initialHead = session.HeadAddress;
        var runner = new TextAdvTerminalActionRunner(
            static (_, _, _, _, _, _) => Task.FromResult(new GameActionValidator.ValidationResult(true, "通过。")),
            (root, _, _, _) => {
                root.Upsert("runnerLargeInteractionFlag", true);
                return Task.FromResult(
                    AsyncAteliaResult<ActionResolution>.Success(
                        new ActionResolution("已经忙完。", GameSimulation.DescribeCurrentPerception(root))
                    )
                );
            }
        );

        var result = await runner.RunAsync(
            session,
            new TerminalActionExecutionPlan.Interaction(
                "test-interaction",
                "测试整回合交互",
                "move",
                "interactionId=test-interaction",
                InteractionExecutionKind.TurnEnding,
                "先把这一步做完。"
            ),
            CancellationToken.None
        );

        var success = Assert.IsType<TerminalActionRunResult.Success>(result);
        Assert.Equal("✅ 你决定了：测试整回合交互 (move)", success.Message);
        Assert.NotEqual(initialHead, session.HeadAddress);
        Assert.Equal(GetIssue.None, session.Root.Get("runnerLargeInteractionFlag", out bool flag));
        Assert.True(flag);
        session.Repo.Dispose();
    }

    [Fact]
    public async Task RunAsync_WhenValidationIsCanceled_ShouldPropagateCancellation() {
        var session = AssertSuccess(TextAdvSession.CreateNew(_repoDir));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var runner = new TextAdvTerminalActionRunner(
            static (_, _, _, _, _, token) => Task.FromCanceled<GameActionValidator.ValidationResult>(token),
            (_, _, _, _) => Task.FromResult(
                AsyncAteliaResult<ActionResolution>.Success(
                    new ActionResolution("不应走到这里。", GameSimulation.DescribeCurrentPerception(session.Root))
                )
            )
        );

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunAsync(
                session,
                new TerminalActionExecutionPlan.RestAWhile("先试试看。"),
                cts.Token
            )
        );

        session.Repo.Dispose();
    }

    private static T AssertSuccess<T>(AteliaResult<T> result) where T : notnull {
        Assert.True(result.IsSuccess, $"Expected success but got error: {result.Error}");
        return result.Value!;
    }
}
