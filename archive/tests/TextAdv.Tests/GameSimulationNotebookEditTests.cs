using Atelia.StateJournal;
using Xunit;

namespace Atelia.TextAdv.Tests;

public sealed class GameSimulationNotebookEditTests : IDisposable {
    private readonly string _repoDir = Path.Combine(
        Path.GetTempPath(),
        "textadv-tests",
        Guid.NewGuid().ToString("N")
    );

    public void Dispose() {
        if (Directory.Exists(_repoDir)) {
            Directory.Delete(_repoDir, recursive: true);
        }
    }

    [Fact]
    public void PreparePreviewAndActualApply_ShouldRenderSameAfterView_ForEmptyNotebookInsert() {
        using var repo = CreateRepository();
        var root = GameSimulation.CreateNewWorld(repo);
        var perception = GameSimulation.DescribeCurrentPerception(root);
        var before = perception.NotebookBlocks;

        var proposal = AssertSuccess(
            GameNotebookEditService.Prepare(
                before,
                "<insert side=\"after\" anchor=\"tail\">记住：这里是沙滩。</insert>"
            )
        );

        var previewAfter = NotebookBlockViewRenderer.RenderPreviewBlockView(before, proposal.PredictedAfterSnapshot);
        var actualAfter = GameSimulation.ApplyNotebookEdit(root, proposal, "先记住最直接可见的信息。", "通过");
        var actualAfterView = NotebookBlockViewRenderer.RenderPreviewBlockView(before, actualAfter.NotebookBlocks);

        Assert.Equal(previewAfter, actualAfterView);
    }

    [Fact]
    public void PreparePreviewAndActualApply_ShouldRenderSameAfterView_ForSequentialHeadTailOperations() {
        using var repo = CreateRepository();
        var root = GameSimulation.CreateNewWorld(repo);

        ApplyAcceptedNotebookEdit(root, "<insert side=\"after\" anchor=\"tail\">A</insert>");
        ApplyAcceptedNotebookEdit(root, "<insert side=\"after\" anchor=\"tail\">B</insert>");

        var perception = GameSimulation.DescribeCurrentPerception(root);
        var before = perception.NotebookBlocks;
        var proposal = AssertSuccess(
            GameNotebookEditService.Prepare(
                before,
                """
            <insert side="before" anchor="head">X</insert>
            <delete anchor="head" />
            <replace anchor="tail">B2</replace>
            """
            )
        );

        var previewAfter = NotebookBlockViewRenderer.RenderPreviewBlockView(before, proposal.PredictedAfterSnapshot);
        var actualAfter = GameSimulation.ApplyNotebookEdit(root, proposal, "先测试一串 head/tail 序列动作。", "通过");
        var actualAfterView = NotebookBlockViewRenderer.RenderPreviewBlockView(before, actualAfter.NotebookBlocks);

        Assert.Equal(previewAfter, actualAfterView);
    }

    [Fact]
    public void PrepareNotebookEdit_ShouldRejectNumericAnchorThatIsNotLiveInStartingSnapshot() {
        using var repo = CreateRepository();
        var root = GameSimulation.CreateNewWorld(repo);
        var perception = GameSimulation.DescribeCurrentPerception(root);

        var result = GameNotebookEditService.Prepare(
            perception.NotebookBlocks,
            "<replace anchor=\"2\">无效测试</replace>"
        );

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Equal("TextAdv.NotebookEdit.AnchorMustTargetLiveBlock", result.Error.ErrorCode);
    }

    private Repository CreateRepository() {
        var createResult = Repository.Create(_repoDir);
        return AssertSuccess(createResult);
    }

    private static void ApplyAcceptedNotebookEdit(DurableDict<string> root, string scriptXml) {
        var perception = GameSimulation.DescribeCurrentPerception(root);
        var proposal = AssertSuccess(GameNotebookEditService.Prepare(perception.NotebookBlocks, scriptXml));
        _ = GameSimulation.ApplyNotebookEdit(root, proposal, "测试初始化用事前推理。", "通过");
    }

    private static T AssertSuccess<T>(AteliaResult<T> result)
        where T : notnull {
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);
        return result.Value!;
    }
}
