using Atelia.Completion.Abstractions;
using Atelia.MemoPod.DebugApp;

namespace Atelia.MemoPod.Tests.Operator;

public sealed class MemoPodDebugAppRecallBehaviorTests {
    [Fact]
    public async Task SameFrozenPodKeepsPrefixStableAndVariesOnlyTail() {
        using var host = new MemoPodDebugAppTestHost();
        OperatorRunResult created = await host.CreateAsync(
            "customer details",
            "memo body"
        );
        Assert.Equal(0, created.ExitCode);
        var client = new ScriptedOperatorCompletionClient();
        Func<IReadOnlyList<string>, ICompletionClient> factory = _ => client;

        OperatorRunResult first = await RecallAsync(
            host,
            "first query",
            factory
        );
        OperatorRunResult second = await RecallAsync(
            host,
            "second query",
            factory
        );

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
        Assert.Equal(2, client.InvocationCount);
        Assert.Equal(0, client.LegacyInvocationCount);
        CompletionRequest firstRequest = client.Requests[0];
        CompletionRequest secondRequest = client.Requests[1];
        Assert.Equal(
            firstRequest.PromptPrefix.SystemPrompt,
            secondRequest.PromptPrefix.SystemPrompt
        );
        Assert.Same(
            firstRequest.PromptPrefix.OutputContract,
            secondRequest.PromptPrefix.OutputContract
        );
        Assert.Equal(
            SharedContents(firstRequest),
            SharedContents(secondRequest)
        );
        Assert.NotEqual(
            TailContent(firstRequest),
            TailContent(secondRequest)
        );
        Assert.DoesNotContain(
            "first query",
            SharedContents(firstRequest).Single()
        );
        Assert.DoesNotContain(
            "second query",
            SharedContents(secondRequest).Single()
        );
        Assert.All(
            client.InvocationOptions,
            static options => Assert.Equal(
                PromptCacheReuseHint.ReuseExpectedSoon,
                options.PromptCacheReuseHint
            )
        );
    }

    [Fact]
    public async Task InvalidFakeOutputIsRejectedOnceAndPodRemainsReopenable() {
        using var host = new MemoPodDebugAppTestHost();
        Assert.Equal(
            0,
            (await host.CreateAsync("topic", "body")).ExitCode
        );
        DeterministicMemoRecallClient? client = null;

        OperatorRunResult result = await RecallAsync(
            host,
            "query",
            rawIds => client = new DeterministicMemoRecallClient(rawIds),
            "not-a-memo-id"
        );

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal("error=recall-invalid-output\n", result.StandardError);
        DeterministicMemoRecallClient invokedClient = Assert.IsType<
            DeterministicMemoRecallClient
        >(client);
        Assert.Equal(1, invokedClient.InvocationCount);
        Assert.Equal(0, invokedClient.LegacyInvocationCount);
        AssertReopenable(host);
    }

    [Fact]
    public async Task ProviderFailureIsReportedOnceAndPodRemainsReopenable() {
        using var host = new MemoPodDebugAppTestHost();
        Assert.Equal(
            0,
            (await host.CreateAsync("topic", "body")).ExitCode
        );
        var client = new ScriptedOperatorCompletionClient {
            Handler = static (_, _, _) => Task.FromException<CompletionResult>(
                new HttpRequestException("provider-secret-detail")
            )
        };

        OperatorRunResult result = await RecallAsync(
            host,
            "query-secret-detail",
            _ => client
        );

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal("error=recall-provider\n", result.StandardError);
        Assert.DoesNotContain("secret-detail", result.StandardError);
        Assert.Equal(1, client.InvocationCount);
        AssertReopenable(host);
    }

    [Fact]
    public async Task CallerCancellationIsReportedOnceAndPodRemainsReopenable() {
        using var host = new MemoPodDebugAppTestHost();
        Assert.Equal(
            0,
            (await host.CreateAsync("topic", "body")).ExitCode
        );
        using var cancellation = new CancellationTokenSource();
        var client = new ScriptedOperatorCompletionClient {
            Handler = (_, _, token) => {
                cancellation.Cancel();
                return Task.FromCanceled<CompletionResult>(token);
            }
        };

        OperatorRunResult result = await RecallAsync(
            host,
            "query",
            _ => client,
            cancellationToken: cancellation.Token
        );

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal("error=cancelled\n", result.StandardError);
        Assert.Equal(1, client.InvocationCount);
        AssertReopenable(host);
    }

    private static Task<OperatorRunResult> RecallAsync(
        MemoPodDebugAppTestHost host,
        string query,
        Func<IReadOnlyList<string>, ICompletionClient> factory,
        string? rawId = null,
        CancellationToken cancellationToken = default
    ) {
        var args = new List<string> {
            "recall",
            "--root", host.StoreRoot,
            "--pod", MemoPodDebugAppTestHost.PodIdText,
            "--query-file", host.WriteText(query)
        };
        if (rawId is not null) {
            args.Add("--fake-return-id");
            args.Add(rawId);
        }
        return host.RunAsync(args.ToArray(), factory, cancellationToken);
    }

    private static string[] SharedContents(CompletionRequest request)
        => request.PromptPrefix.SharedContextMessages
            .Cast<ObservationMessage>()
            .Select(static message => message.Content!)
            .ToArray();

    private static string TailContent(CompletionRequest request)
        => Assert.Single(request.TailMessages.Cast<ObservationMessage>())
            .Content!;

    private static void AssertReopenable(MemoPodDebugAppTestHost host) {
        MemoPod reopened = MemoPod.Open(
            host.StoreRoot,
            MemoPodId.Parse(MemoPodDebugAppTestHost.PodIdText)
        );
        Assert.Equal(MemoPodPhase.Frozen, reopened.Phase);
        Assert.Single(reopened.List());
    }
}
