using System.Text;

namespace Atelia.MemoPod.Tests.Operator;

public sealed class MemoPodDebugAppInputAndFailureTests {
    [Fact]
    public async Task FailedCreatePublishesNoProvisionalIds() {
        using var host = new MemoPodDebugAppTestHost();
        OperatorRunResult original = await host.CreateAsync(
            "first topic",
            "committed body"
        );
        Assert.Equal(0, original.ExitCode);

        OperatorRunResult duplicate = await host.CreateAsync(
            "second secret topic",
            "uncommitted secret body"
        );

        Assert.Equal(2, duplicate.ExitCode);
        Assert.Empty(duplicate.StandardOutput);
        Assert.Equal("error=persistence\n", duplicate.StandardError);
        Assert.DoesNotContain("m1:", duplicate.StandardError);
        MemoPod reopened = MemoPod.Open(
            host.StoreRoot,
            MemoPodId.Parse(MemoPodDebugAppTestHost.PodIdText)
        );
        Assert.Equal("committed body", Assert.Single(reopened.List()).ExactText);
    }

    [Fact]
    public async Task PreCancelledCreatePublishesNoDocumentOrProvisionalId() {
        using var host = new MemoPodDebugAppTestHost();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        OperatorRunResult result = await host.RunAsync([
            "create",
            "--root", host.StoreRoot,
            "--pod", MemoPodDebugAppTestHost.PodIdText,
            "--topic-file", host.WriteText("cancelled topic"),
            "--memo-file", host.WriteText("cancelled provisional body")
        ], cancellationToken: cancellation.Token);

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal("error=cancelled\n", result.StandardError);
        Assert.DoesNotContain("m1:", result.StandardError);
        MemoPodPersistenceException exception = Assert.Throws<
            MemoPodPersistenceException
        >(() => MemoPod.Open(
            host.StoreRoot,
            MemoPodId.Parse(MemoPodDebugAppTestHost.PodIdText)
        ));
        Assert.Equal(
            MemoPodPersistenceFailureKind.NotFound,
            exception.FailureKind
        );
    }

    [Fact]
    public async Task PreCancelledEditKeepsOldAuthorityAndAllocator() {
        using var host = new MemoPodDebugAppTestHost();
        Assert.Equal(
            0,
            (await host.CreateAsync("topic", "old authority")).ExitCode
        );
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        OperatorRunResult result = await host.RunAsync([
            "edit",
            "--root", host.StoreRoot,
            "--pod", MemoPodDebugAppTestHost.PodIdText,
            "--remove", "m1:00000001",
            "--memo-file", host.WriteText("cancelled replacement")
        ], cancellationToken: cancellation.Token);

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal("error=cancelled\n", result.StandardError);
        Assert.DoesNotContain("m1:", result.StandardError);

        MemoPod reopened = MemoPod.Open(
            host.StoreRoot,
            MemoPodId.Parse(MemoPodDebugAppTestHost.PodIdText)
        );
        Memo oldMemo = Assert.Single(reopened.List());
        Assert.Equal(MemoId.Parse("m1:00000001"), oldMemo.Id);
        Assert.Equal("old authority", oldMemo.ExactText);

        reopened.ResumeEditing();
        Assert.Equal(
            MemoId.Parse("m1:00000002"),
            reopened.Append("next committed candidate")
        );
    }

    [Theory]
    [MemberData(nameof(InvalidSyntaxCases))]
    public async Task ManualParserRejectsInvalidOrLiveShapes(string[] args) {
        using var host = new MemoPodDebugAppTestHost();
        string[] expanded = args.Select(value => value switch {
            "{root}" => host.StoreRoot,
            "{pod}" => MemoPodDebugAppTestHost.PodIdText,
            "{topic}" => host.WriteText("topic"),
            "{query}" => host.WriteText("query"),
            _ => value
        }).ToArray();

        OperatorRunResult result = await host.RunAsync(expanded);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal("error=syntax\n", result.StandardError);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BomAndInvalidUtf8AreRejectedWithoutCreatingPod(
        bool useBom
    ) {
        using var host = new MemoPodDebugAppTestHost();
        byte[] bytes = useBom
            ? [0xEF, 0xBB, 0xBF, (byte)'x']
            : [0xC3, 0x28];
        string topicPath = host.WriteBytes(bytes);

        OperatorRunResult result = await host.RunAsync([
            "create",
            "--root", host.StoreRoot,
            "--pod", MemoPodDebugAppTestHost.PodIdText,
            "--topic-file", topicPath
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal("error=input\n", result.StandardError);
        Assert.Empty(Directory.EnumerateFiles(
            host.StoreRoot,
            "*",
            SearchOption.AllDirectories
        ));
    }

    [Fact]
    public async Task ExactTextIsNotTrimmedNormalizedOrGivenANewline() {
        using var host = new MemoPodDebugAppTestHost();
        const string exactText = "  decomposed-e\u0301\nlast-line";
        Assert.Equal(
            0,
            (await host.CreateAsync("topic", exactText)).ExitCode
        );

        OperatorRunResult result = await host.RunAsync([
            "get",
            "--root", host.StoreRoot,
            "--pod", MemoPodDebugAppTestHost.PodIdText,
            "--memo", "m1:00000001"
        ]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(exactText, result.StandardOutput);
        Assert.False(result.StandardOutput.EndsWith("\n", StringComparison.Ordinal));
        Assert.Empty(result.StandardError);
        Assert.Equal(
            Encoding.UTF8.GetBytes(exactText),
            Encoding.UTF8.GetBytes(result.StandardOutput)
        );
    }

    public static TheoryData<string[]> InvalidSyntaxCases => new() {
        new[] { "unknown" },
        new[] {
            "create", "--root", "{root}", "--pod", "{pod}",
            "--topic-file", "{topic}", "--live", "true"
        },
        new[] {
            "create", "--root", "{root}", "--root", "{root}",
            "--pod", "{pod}", "--topic-file", "{topic}"
        },
        new[] { "create", "--root" },
        new[] { "inspect", "--root", "{root}", "--pod" },
        new[] {
            "recall", "--root", "{root}", "--pod", "{pod}",
            "--query-file", "{query}", "--connections", "poison"
        },
        new[] { "edit", "--root", "{root}", "--pod", "{pod}" }
    };
}
