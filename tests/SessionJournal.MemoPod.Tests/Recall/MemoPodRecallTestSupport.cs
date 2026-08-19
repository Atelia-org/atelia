using Atelia.Completion.Abstractions;

namespace Atelia.SessionJournal.MemoPod.Tests.Recall;

internal sealed class FakeMemoRecallCompletionClient : ICompletionClient {
    private Func<
        FakeMemoRecallCompletionClient,
        CompletionRequest,
        CancellationToken,
        Task<CompletionResult>
    > _handler;

    internal FakeMemoRecallCompletionClient() {
        _handler = static (client, request, _) => Task.FromResult(
            client.Result(
                request,
                [client.ToolCall("{\"memoIds\":[]}")]
            )
        );
    }

    public string Name { get; set; } = "memo-recall-fake";
    public string ApiSpecId { get; set; } = "memo-recall-fake-v1";

    internal int InvocationCount { get; private set; }
    internal int LegacyInvocationCount { get; private set; }
    internal List<CompletionRequest> Requests { get; } = [];
    internal List<CompletionInvocationOptions> InvocationOptions { get; }
        = [];
    internal List<CompletionStreamObserver?> Observers { get; } = [];

    internal Func<
        FakeMemoRecallCompletionClient,
        CompletionRequest,
        CancellationToken,
        Task<CompletionResult>
    > Handler {
        get => _handler;
        set => _handler = value
            ?? throw new ArgumentNullException(nameof(value));
    }

    public Task<CompletionResult> StreamCompletionAsync(
        CompletionRequest request,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) {
        LegacyInvocationCount++;
        throw new InvalidOperationException(
            "Recall must use the four-parameter completion overload."
        );
    }

    public Task<CompletionResult> StreamCompletionAsync(
        CompletionRequest request,
        CompletionInvocationOptions invocationOptions,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) {
        InvocationCount++;
        Requests.Add(request);
        InvocationOptions.Add(invocationOptions);
        Observers.Add(observer);
        return _handler(this, request, cancellationToken);
    }

    internal ActionBlock.ToolCall ToolCall(
        string? rawArgumentsJson,
        string toolName = MemoPodRecallProtocol.ToolName,
        string? toolCallId = "call-1"
    ) => new(new RawToolCall(
        toolName,
        toolCallId!,
        rawArgumentsJson!
    ));

    internal CompletionResult Result(
        CompletionRequest request,
        IReadOnlyList<ActionBlock> blocks,
        IReadOnlyList<string>? errors = null,
        CompletionTermination? termination = null,
        CompletionUsage? usage = null,
        CompletionDescriptor? invocation = null
    ) => new(
        new ActionMessage(blocks),
        invocation ?? CompletionDescriptor.From(this, request),
        errors,
        termination,
        usage
    );
}

internal sealed class MemoPodRecallFixture : IDisposable {
    internal static readonly MemoPodId PodId = MemoPodId.Parse(
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
    );

    private MemoPodRecallFixture(
        string root,
        MemoPod pod,
        MemoId[] ids
    ) {
        Root = root;
        Pod = pod;
        Ids = ids;
    }

    internal string Root { get; }
    internal MemoPod Pod { get; }
    internal MemoId[] Ids { get; }

    internal static async Task<MemoPodRecallFixture> CreateAsync(
        string topic = "customer details",
        params string[] exactTexts
    ) {
        string root = Path.Combine(
            Path.GetTempPath(),
            "atelia-memo-pod-recall-tests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(root);
        MemoPod pod = MemoPod.Create(root, PodId, topic);
        MemoId[] ids = exactTexts.Select(pod.Append).ToArray();
        await pod.FreezeAsync();
        return new MemoPodRecallFixture(root, pod, ids);
    }

    internal static MemoRecallOptions Options(
        int maxResults = 8,
        int maxTokens = 256,
        int maximumFrozenPromptUtf8Bytes =
            MemoPodLimits.MaximumRenderedPromptUtf8Bytes,
        int maximumHydratedExactTextUtf8Bytes =
            MemoPodLimits.MaximumActiveExactTextUtf8Bytes
    ) => new(
        maxResults,
        maxTokens,
        maximumFrozenPromptUtf8Bytes,
        maximumHydratedExactTextUtf8Bytes
    );

    public void Dispose() {
        if (Directory.Exists(Root)) {
            Directory.Delete(Root, recursive: true);
        }
    }
}
