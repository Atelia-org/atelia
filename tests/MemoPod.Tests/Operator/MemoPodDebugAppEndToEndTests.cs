using System.Text;
using System.Text.Json;

namespace Atelia.MemoPod.Tests.Operator;

public sealed class MemoPodDebugAppEndToEndTests {
    [Fact]
    public async Task CreateInspectRecallGetEditAndReopenFormOneVertical() {
        using var host = new MemoPodDebugAppTestHost();
        const string topic = "customer-specific correspondence";
        const string oldText = "  old-order-caf\u00e9\nline-2";
        const string newText = "replacement-e\u0301-detail";
        const string query = "find-order-private-query";

        OperatorRunResult created = await host.CreateAsync(topic, oldText);
        AssertSuccessWithoutCanaries(created, topic, oldText);
        using (JsonDocument report = JsonDocument.Parse(
            created.StandardOutput
        )) {
            AssertContentFreeReportShape(report, "create");
            Assert.Equal("create", String(report, "command"));
            Assert.Equal("Frozen", String(report, "phase"));
            Assert.Equal(1, Int32(report, "activeCount"));
            Assert.Equal(["m1:00000001"], Strings(report, "activeIds"));
            Assert.Equal(
                ["m1:00000001"],
                Strings(report, "committedIds")
            );
        }

        OperatorRunResult inspected = await host.RunAsync([
            "inspect",
            "--root", host.StoreRoot,
            "--pod", MemoPodDebugAppTestHost.PodIdText
        ]);
        AssertSuccessWithoutCanaries(inspected, topic, oldText);
        using (JsonDocument report = JsonDocument.Parse(
            inspected.StandardOutput
        )) {
            AssertContentFreeReportShape(report, "inspect");
            Assert.Equal(1, Int32(report, "activeCount"));
            Assert.Equal(["m1:00000001"], Strings(report, "activeIds"));
        }

        OperatorRunResult recalled = await host.RunAsync([
            "recall",
            "--root", host.StoreRoot,
            "--pod", MemoPodDebugAppTestHost.PodIdText,
            "--query-file", host.WriteText(query),
            "--fake-return-id", "m1:00000001"
        ]);
        AssertSuccessWithoutCanaries(recalled, topic, oldText, query);
        using (JsonDocument report = JsonDocument.Parse(
            recalled.StandardOutput
        )) {
            AssertContentFreeReportShape(report, "recall");
            Assert.Equal(1, Int32(report, "selectedCount"));
            Assert.Equal(["m1:00000001"], Strings(report, "selectedIds"));
        }

        byte[] queryBytes = Encoding.UTF8.GetBytes(query);
        Assert.All(
            EnumerateStoreFileBytes(host.StoreRoot),
            bytes => Assert.False(Contains(bytes, queryBytes))
        );

        OperatorRunResult got = await host.RunAsync([
            "get",
            "--root", host.StoreRoot,
            "--pod", MemoPodDebugAppTestHost.PodIdText,
            "--memo", "m1:00000001"
        ]);
        Assert.Equal(0, got.ExitCode);
        Assert.Equal(oldText, got.StandardOutput);
        Assert.Empty(got.StandardError);

        OperatorRunResult edited = await host.RunAsync([
            "edit",
            "--root", host.StoreRoot,
            "--pod", MemoPodDebugAppTestHost.PodIdText,
            "--remove", "m1:00000001",
            "--memo-file", host.WriteText(newText)
        ]);
        AssertSuccessWithoutCanaries(edited, topic, oldText, newText);
        using (JsonDocument report = JsonDocument.Parse(
            edited.StandardOutput
        )) {
            AssertContentFreeReportShape(report, "edit");
            Assert.Equal(1, Int32(report, "activeCount"));
            Assert.Equal(["m1:00000002"], Strings(report, "activeIds"));
            Assert.Equal(
                ["m1:00000002"],
                Strings(report, "committedIds")
            );
        }

        MemoPod reopened = MemoPod.Open(
            host.StoreRoot,
            MemoPodId.Parse(MemoPodDebugAppTestHost.PodIdText)
        );
        Assert.Equal(MemoPodPhase.Frozen, reopened.Phase);
        Assert.False(reopened.TryGet(
            MemoId.Parse("m1:00000001"),
            out _
        ));
        Memo onlyMemo = Assert.Single(reopened.List());
        Assert.Equal(MemoId.Parse("m1:00000002"), onlyMemo.Id);
        Assert.Equal(newText, onlyMemo.ExactText);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task RecallReturnsNoneOneOrManyIdsInFakeOrder(
        int selectionCount
    ) {
        using var host = new MemoPodDebugAppTestHost();
        OperatorRunResult created = await host.CreateAsync(
            "orders",
            "alpha",
            "beta",
            "gamma"
        );
        Assert.Equal(0, created.ExitCode);
        string[] allIds = [
            "m1:00000003",
            "m1:00000001",
            "m1:00000002"
        ];
        string[] selectedIds = allIds[..selectionCount];
        var args = new List<string> {
            "recall",
            "--root", host.StoreRoot,
            "--pod", MemoPodDebugAppTestHost.PodIdText,
            "--query-file", host.WriteText("order lookup")
        };
        foreach (string selectedId in selectedIds) {
            args.Add("--fake-return-id");
            args.Add(selectedId);
        }

        OperatorRunResult recalled = await host.RunAsync(args.ToArray());

        Assert.Equal(0, recalled.ExitCode);
        Assert.Empty(recalled.StandardError);
        using JsonDocument report = JsonDocument.Parse(
            recalled.StandardOutput
        );
        AssertContentFreeReportShape(report, "recall");
        Assert.Equal(selectionCount, Int32(report, "selectedCount"));
        Assert.Equal(selectedIds, Strings(report, "selectedIds"));
    }

    [Fact]
    public async Task EmptyPodAndDefaultFakeProduceNoMatch() {
        using var host = new MemoPodDebugAppTestHost();
        OperatorRunResult created = await host.CreateAsync("empty topic");
        Assert.Equal(0, created.ExitCode);

        OperatorRunResult recalled = await host.RunAsync([
            "recall",
            "--root", host.StoreRoot,
            "--pod", MemoPodDebugAppTestHost.PodIdText,
            "--query-file", host.WriteText("nothing here")
        ]);

        Assert.Equal(0, recalled.ExitCode);
        using JsonDocument report = JsonDocument.Parse(
            recalled.StandardOutput
        );
        AssertContentFreeReportShape(report, "recall");
        Assert.Equal(0, Int32(report, "selectedCount"));
        Assert.Empty(Strings(report, "selectedIds"));
    }

    private static void AssertSuccessWithoutCanaries(
        OperatorRunResult result,
        params string[] canaries
    ) {
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        foreach (string canary in canaries) {
            Assert.DoesNotContain(canary, result.StandardOutput);
            Assert.DoesNotContain(canary, result.StandardError);
        }
    }

    private static void AssertContentFreeReportShape(
        JsonDocument report,
        string command
    ) {
        string[] expectedProperties = command switch {
            "create" or "edit" => [
                "schema",
                "command",
                "podId",
                "phase",
                "activeCount",
                "activeIds",
                "committedIds"
            ],
            "inspect" => [
                "schema",
                "command",
                "podId",
                "phase",
                "activeCount",
                "activeIds"
            ],
            "recall" => [
                "schema",
                "command",
                "podId",
                "phase",
                "selectedCount",
                "selectedIds"
            ],
            _ => throw new ArgumentOutOfRangeException(
                nameof(command),
                command,
                "Unknown content-free operator report."
            )
        };

        JsonElement root = report.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal(
            expectedProperties,
            root.EnumerateObject()
                .Select(static property => property.Name)
                .ToArray()
        );
        AssertStringProperty(
            root,
            "schema",
            "atelia.memo-pod.debug-app.v1"
        );
        AssertStringProperty(root, "command", command);
        AssertStringProperty(
            root,
            "podId",
            MemoPodDebugAppTestHost.PodIdText
        );
        AssertStringProperty(root, "phase", "Frozen");

        string countProperty = command == "recall"
            ? "selectedCount"
            : "activeCount";
        string idsProperty = command == "recall"
            ? "selectedIds"
            : "activeIds";
        Assert.Equal(
            JsonValueKind.Number,
            root.GetProperty(countProperty).ValueKind
        );
        AssertStringArray(root, idsProperty);
        Assert.Equal(
            root.GetProperty(idsProperty).GetArrayLength(),
            root.GetProperty(countProperty).GetInt32()
        );
        if (command is "create" or "edit") {
            AssertStringArray(root, "committedIds");
        }
    }

    private static void AssertStringProperty(
        JsonElement root,
        string name,
        string expectedValue
    ) {
        JsonElement property = root.GetProperty(name);
        Assert.Equal(JsonValueKind.String, property.ValueKind);
        Assert.Equal(expectedValue, property.GetString());
    }

    private static void AssertStringArray(JsonElement root, string name) {
        JsonElement property = root.GetProperty(name);
        Assert.Equal(JsonValueKind.Array, property.ValueKind);
        Assert.All(
            property.EnumerateArray(),
            static item => Assert.Equal(
                JsonValueKind.String,
                item.ValueKind
            )
        );
    }

    private static string String(JsonDocument report, string name)
        => report.RootElement.GetProperty(name).GetString()!;

    private static int Int32(JsonDocument report, string name)
        => report.RootElement.GetProperty(name).GetInt32();

    private static string[] Strings(JsonDocument report, string name)
        => report.RootElement.GetProperty(name)
            .EnumerateArray()
            .Select(static item => item.GetString()!)
            .ToArray();

    private static IEnumerable<byte[]> EnumerateStoreFileBytes(
        string rootPath
    ) => Directory.EnumerateFiles(
        rootPath,
        "*",
        SearchOption.AllDirectories
    ).Select(File.ReadAllBytes);

    private static bool Contains(byte[] haystack, byte[] needle)
        => haystack.AsSpan().IndexOf(needle) >= 0;
}
