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
    public async Task RunLargeActionAsync_WhenValidatorRejects_ShouldNotResolveOrCommit() {
        var session = AssertSuccess(TextAdvSession.CreateNew(_repoDir));
        var initialHead = session.HeadAddress;
        var resolveCalled = false;
        var runner = new TextAdvTerminalActionRunner(
            static (_, _, _, _, _, _) => Task.FromResult(new GameActionValidator.ValidationResult(false, "证据不足。"))
        );

        var result = await runner.RunLargeActionAsync(
            session,
            new TerminalActionRequest("large/test", "测试大动作", null, "先试试看。"),
            (_, _, _) => {
                resolveCalled = true;
                return Task.FromResult(AsyncAteliaResult<TurnResolution>.Success(
                    new TurnResolution("不应走到这里。", GameSimulation.DescribeCurrentPerception(session.Root))
                ));
            },
            CancellationToken.None
        );

        var rejected = Assert.IsType<TerminalActionRunResult.ValidationRejected>(result);
        Assert.Equal("证据不足。", rejected.Feedback);
        Assert.False(resolveCalled);
        Assert.Equal(initialHead, session.HeadAddress);
        session.Repo.Dispose();
    }

    [Fact]
    public async Task RunImmediateActionAsync_WhenResolveSucceeds_ShouldCommitAndRenderSessionPerception() {
        var session = AssertSuccess(TextAdvSession.CreateNew(_repoDir));
        var initialHead = session.HeadAddress;
        var runner = new TextAdvTerminalActionRunner(
            static (_, _, _, _, _, _) => Task.FromResult(new GameActionValidator.ValidationResult(true, "通过。"))
        );

        var result = await runner.RunImmediateActionAsync(
            session,
            new TerminalActionRequest("small/test", "测试顺手动作", null, "先记一笔。"),
            (root, _, _) => {
                root.Upsert("runnerTestFlag", true);
                return Task.FromResult(AsyncAteliaResult<SmallActionResolution>.Success(
                    new SmallActionResolution("已经记下。", GameSimulation.DescribeCurrentPerception(root))
                ));
            },
            CancellationToken.None
        );

        var success = Assert.IsType<TerminalActionRunResult.Success>(result);
        Assert.Equal("✅ 你顺手做了：测试顺手动作", success.Message);
        Assert.Contains("🗓️", success.BodyText);
        Assert.NotEqual(initialHead, session.HeadAddress);
        Assert.Equal(GetIssue.None, session.Root.Get("runnerTestFlag", out bool flag));
        Assert.True(flag);
        session.Repo.Dispose();
    }

    [Fact]
    public async Task RunImmediateActionAsync_WhenResolveFails_ShouldReturnFailureWithoutCommit() {
        var session = AssertSuccess(TextAdvSession.CreateNew(_repoDir));
        var initialHead = session.HeadAddress;
        var runner = new TextAdvTerminalActionRunner(
            static (_, _, _, _, _, _) => Task.FromResult(new GameActionValidator.ValidationResult(true, "通过。"))
        );

        var result = await runner.RunImmediateActionAsync(
            session,
            new TerminalActionRequest("small/test", "测试顺手动作", null, "先记一笔。"),
            (_, _, _) => Task.FromResult(
                AsyncAteliaResult<SmallActionResolution>.Failure(
                    new TextAdvError("TextAdv.RunnerTestFailure", "模拟失败")
                )
            ),
            CancellationToken.None
        );

        var failure = Assert.IsType<TerminalActionRunResult.Failure>(result);
        Assert.Equal("❌ 小动作结算失败：测试顺手动作", failure.Message);
        Assert.Equal("TextAdv.RunnerTestFailure", failure.Error?.ErrorCode);
        Assert.Equal(initialHead, session.HeadAddress);
        session.Repo.Dispose();
    }

    [Fact]
    public async Task RunLargeActionAsync_WhenValidationIsCanceled_ShouldPropagateCancellation() {
        var session = AssertSuccess(TextAdvSession.CreateNew(_repoDir));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var runner = new TextAdvTerminalActionRunner(
            static (_, _, _, _, _, token) => Task.FromCanceled<GameActionValidator.ValidationResult>(token)
        );

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunLargeActionAsync(
                session,
                new TerminalActionRequest("large/test", "测试大动作", null, "先试试看。"),
                (_, _, _) => Task.FromResult(AsyncAteliaResult<TurnResolution>.Success(
                    new TurnResolution("不应走到这里。", GameSimulation.DescribeCurrentPerception(session.Root))
                )),
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
