using System.Net;
using System.Text;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.MemoPod.DebugApp;
using MemoPodAggregate = Atelia.MemoPod.MemoPod;

namespace Atelia.MemoPod.Tests.Live;

internal sealed class LiveMemoRecallTestHost : IDisposable {
    internal const string PodIdText =
        "cccccccccccccccccccccccccccccccc";

    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private int _nextInput;

    internal LiveMemoRecallTestHost() {
        Root = Path.Combine(
            Path.GetTempPath(),
            "atelia-memo-pod-live-tests",
            Guid.NewGuid().ToString("N")
        );
        StoreRoot = Path.Combine(Root, "store");
        InputRoot = Path.Combine(Root, "input");
        Directory.CreateDirectory(StoreRoot);
        Directory.CreateDirectory(InputRoot);
    }

    internal string Root { get; }
    internal string StoreRoot { get; }
    internal string InputRoot { get; }

    internal string WriteText(string text, string extension = ".txt") {
        string path = Path.Combine(
            InputRoot,
            $"input-{Interlocked.Increment(ref _nextInput):D4}{extension}"
        );
        File.WriteAllText(path, text, Utf8WithoutBom);
        return path;
    }

    internal string WriteConnections(
        string apiKeyEnvironmentVariable,
        string connectionId = "candidate",
        string defaultConnectionId = "candidate",
        string baseAddress = "https://api.deepseek.com/"
    ) {
        string json =
            $$"""
            {"v":1,"connections":[{"id":"{{connectionId}}","kind":"openai-chat","modelId":"deepseek-v4-flash","completionSurfaceId":"openai-chat/deepseek-v4","baseAddress":"{{baseAddress}}","apiKeyEnv":"{{apiKeyEnvironmentVariable}}","reasoningEffort":"disabled"}],"defaultConnectionId":"{{defaultConnectionId}}"}
            """;
        return WriteText(json, ".json");
    }

    internal async Task CreateFrozenPodAsync(
        string topic = "synthetic-topic",
        params string[] memos
    ) {
        MemoPodAggregate pod = MemoPodAggregate.Create(
            StoreRoot,
            MemoPodId.Parse(PodIdText),
            topic
        );
        foreach (string memo in memos) {
            pod.Append(memo);
        }
        await pod.FreezeAsync();
    }

    internal async Task<LiveOperatorResult> RunAsync(
        string[] args,
        LiveMemoRecallServices? services = null,
        Func<IReadOnlyList<string>, ICompletionClient>?
            fakeClientFactory = null,
        CancellationToken cancellationToken = default
    ) {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int exitCode = await Program.MainCoreAsync(
            args,
            output,
            error,
            fakeClientFactory,
            cancellationToken,
            services
        );
        return new LiveOperatorResult(
            exitCode,
            output.ToString(),
            error.ToString()
        );
    }

    public void Dispose() {
        if (Directory.Exists(Root)) {
            Directory.Delete(Root, recursive: true);
        }
    }
}

internal sealed record LiveOperatorResult(
    int ExitCode,
    string StandardOutput,
    string StandardError
);

internal sealed class CountingCompletionClientFactory(
    Func<CompletionConnectionConfig, ICompletionClient> create
) : ICompletionClientFactory {
    private readonly Func<CompletionConnectionConfig, ICompletionClient>
        _create = create ?? throw new ArgumentNullException(nameof(create));

    internal int CreateCount { get; private set; }
    internal CompletionConnectionConfig? LastConnection { get; private set; }

    public ICompletionClient Create(CompletionConnectionConfig connection) {
        CreateCount++;
        LastConnection = connection;
        return _create(connection);
    }
}

internal sealed class ScriptedLiveCompletionClient : ICompletionClient {
    private readonly Func<CompletionRequest, CompletionResult> _result;
    private readonly Exception? _failure;

    internal ScriptedLiveCompletionClient(
        Func<CompletionRequest, CompletionResult>? result = null,
        Exception? failure = null
    ) {
        _result = result ?? ValidEmptyResult;
        _failure = failure;
    }

    public string Name => "provider-free-live";
    public string ApiSpecId => "provider-free-live-v1";

    internal int InvocationCount { get; private set; }

    public Task<CompletionResult> StreamCompletionAsync(
        CompletionRequest request,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) => throw new InvalidOperationException(
        "Live test client requires invocation options."
    );

    public Task<CompletionResult> StreamCompletionAsync(
        CompletionRequest request,
        CompletionInvocationOptions invocationOptions,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) {
        _ = observer;
        cancellationToken.ThrowIfCancellationRequested();
        InvocationCount++;
        if (_failure is not null) {
            return Task.FromException<CompletionResult>(_failure);
        }
        return Task.FromResult(_result(request));
    }

    private CompletionResult ValidEmptyResult(CompletionRequest request)
        => ToolResult(request, []);

    internal CompletionResult ToolResult(
        CompletionRequest request,
        IReadOnlyList<string> memoIds,
        CompletionUsage? usage = null
    ) {
        string arguments = System.Text.Json.JsonSerializer.Serialize(new {
            memoIds
        });
        return new CompletionResult(
            new ActionMessage([
                new ActionBlock.ToolCall(new RawToolCall(
                    "recall_memos",
                    "provider-free-call-1",
                    arguments
                ))
            ]),
            CompletionDescriptor.From(this, request),
            usage: usage
        );
    }
}

internal sealed class CapturingSseHandler(string sse) : HttpMessageHandler {
    private readonly string _sse = sse;

    internal List<string> RequestBodies { get; } = [];
    internal List<HttpMethod> RequestMethods { get; } = [];
    internal List<Uri?> RequestUris { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    ) {
        RequestMethods.Add(request.Method);
        RequestUris.Add(request.RequestUri);
        RequestBodies.Add(
            request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken)
        );
        return new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent(
                _sse,
                Encoding.UTF8,
                "text/event-stream"
            )
        };
    }
}

internal sealed class OwnedTestCompletionClient(
    ICompletionClient inner,
    HttpClient httpClient
) : ICompletionClient, IDisposable {
    public string Name => inner.Name;
    public string ApiSpecId => inner.ApiSpecId;

    public Task<CompletionResult> StreamCompletionAsync(
        CompletionRequest request,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) => inner.StreamCompletionAsync(
        request,
        observer,
        cancellationToken
    );

    public Task<CompletionResult> StreamCompletionAsync(
        CompletionRequest request,
        CompletionInvocationOptions invocationOptions,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) => inner.StreamCompletionAsync(
        request,
        invocationOptions,
        observer,
        cancellationToken
    );

    public void Dispose() => httpClient.Dispose();
}
