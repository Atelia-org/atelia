using Atelia.MemoPod.DebugApp;

namespace Atelia.MemoPod.Tests.Operator;

public sealed class MemoPodDebugAppInputBoundaryTests {
    [Theory]
    [MemberData(nameof(Utf8ByteBoundCases))]
    public async Task TextInputsEnforceExactUtf8ByteBounds(
        string inputKind,
        int byteLength,
        bool expectSuccess
    ) {
        using var host = new MemoPodDebugAppTestHost();
        string inputPath = host.WriteText(new string('x', byteLength));
        var client = new ScriptedOperatorCompletionClient();

        OperatorRunResult result;
        switch (inputKind) {
            case "topic":
                result = await host.RunAsync([
                    "create",
                    "--root", host.StoreRoot,
                    "--pod", MemoPodDebugAppTestHost.PodIdText,
                    "--topic-file", inputPath
                ]);
                break;
            case "memo":
                result = await host.RunAsync([
                    "create",
                    "--root", host.StoreRoot,
                    "--pod", MemoPodDebugAppTestHost.PodIdText,
                    "--topic-file", host.WriteText("topic"),
                    "--memo-file", inputPath
                ]);
                break;
            case "query":
                Assert.Equal(
                    0,
                    (await host.CreateAsync("topic", "body")).ExitCode
                );
                result = await host.RunAsync([
                    "recall",
                    "--root", host.StoreRoot,
                    "--pod", MemoPodDebugAppTestHost.PodIdText,
                    "--query-file", inputPath
                ], _ => client);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(inputKind),
                    inputKind,
                    "Unknown operator text input kind."
                );
        }

        if (expectSuccess) {
            Assert.Equal(0, result.ExitCode);
            Assert.NotEmpty(result.StandardOutput);
            Assert.Empty(result.StandardError);
            Assert.Equal(inputKind == "query" ? 1 : 0, client.InvocationCount);
        }
        else {
            Assert.Equal(1, result.ExitCode);
            Assert.Empty(result.StandardOutput);
            Assert.Equal("error=input\n", result.StandardError);
            Assert.Equal(0, client.InvocationCount);
            AssertAuthorityAfterRejectedInput(host, inputKind);
        }
    }

    [Theory]
    [MemberData(nameof(InvalidUtf8MemoAndQueryCases))]
    public async Task MemoAndQueryRejectBomAndInvalidUtf8(
        string inputKind,
        bool useBom
    ) {
        using var host = new MemoPodDebugAppTestHost();
        byte[] bytes = useBom
            ? [0xEF, 0xBB, 0xBF, (byte)'x']
            : [0xC3, 0x28];
        string inputPath = host.WriteBytes(bytes);
        bool factoryInvoked = false;

        OperatorRunResult result;
        if (inputKind == "memo") {
            result = await host.RunAsync([
                "create",
                "--root", host.StoreRoot,
                "--pod", MemoPodDebugAppTestHost.PodIdText,
                "--topic-file", host.WriteText("topic"),
                "--memo-file", inputPath
            ]);
        }
        else if (inputKind == "query") {
            Assert.Equal(
                0,
                (await host.CreateAsync("topic", "body")).ExitCode
            );
            result = await host.RunAsync([
                "recall",
                "--root", host.StoreRoot,
                "--pod", MemoPodDebugAppTestHost.PodIdText,
                "--query-file", inputPath
            ], ids => {
                factoryInvoked = true;
                return new DeterministicMemoRecallClient(ids);
            });
        }
        else {
            throw new ArgumentOutOfRangeException(
                nameof(inputKind),
                inputKind,
                "Unknown operator text input kind."
            );
        }

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal("error=input\n", result.StandardError);
        Assert.False(factoryInvoked);
        AssertAuthorityAfterRejectedInput(host, inputKind);
    }

    public static TheoryData<string, int, bool> Utf8ByteBoundCases => new() {
        {
            "topic",
            MemoPodLimits.MaximumTopicUtf8Bytes,
            true
        },
        {
            "topic",
            MemoPodLimits.MaximumTopicUtf8Bytes + 1,
            false
        },
        {
            "memo",
            MemoPodLimits.MaximumMemoExactTextUtf8Bytes,
            true
        },
        {
            "memo",
            MemoPodLimits.MaximumMemoExactTextUtf8Bytes + 1,
            false
        },
        {
            "query",
            MemoPodLimits.MaximumRecallQueryUtf8Bytes,
            true
        },
        {
            "query",
            MemoPodLimits.MaximumRecallQueryUtf8Bytes + 1,
            false
        }
    };

    public static TheoryData<string, bool> InvalidUtf8MemoAndQueryCases =>
        new() {
            { "memo", true },
            { "memo", false },
            { "query", true },
            { "query", false }
        };

    private static void AssertAuthorityAfterRejectedInput(
        MemoPodDebugAppTestHost host,
        string inputKind
    ) {
        if (inputKind != "query") {
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
            return;
        }

        MemoPod reopened = MemoPod.Open(
            host.StoreRoot,
            MemoPodId.Parse(MemoPodDebugAppTestHost.PodIdText)
        );
        Memo memo = Assert.Single(reopened.List());
        Assert.Equal(MemoId.Parse("m1:00000001"), memo.Id);
        Assert.Equal("body", memo.ExactText);
    }
}
