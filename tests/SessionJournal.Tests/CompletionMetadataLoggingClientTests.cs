using System.Text.Json;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class CompletionMetadataLoggingClientTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), "completion-metadata-tests", Guid.NewGuid().ToString("N"));
    private const string Secret = "PRIVATE-PROMPT-正文-DO-NOT-LOG";

    [Fact]
    public async Task LogsOnlyMetadataAndPreservesInvocationOverloadAndResult() {
        var inner = new StubClient();
        var client = Create(inner);
        var options = new CompletionInvocationOptions { PromptCacheReuseHint = PromptCacheReuseHint.NoReuseExpected };
        var observer = new CompletionStreamObserver();
        using var cancellation = new CancellationTokenSource();
        CompletionRequest request = Request();

        CompletionResult result = await client.StreamCompletionAsync(request, options, observer, cancellation.Token);

        Assert.Same(inner.Result, result);
        Assert.Same(request, inner.Request);
        Assert.Same(options, inner.Options);
        Assert.Same(observer, inner.Observer);
        Assert.Equal(cancellation.Token, inner.Token);
        Assert.Equal(inner.Name, client.Name);
        Assert.Equal(inner.ApiSpecId, client.ApiSpecId);
        using JsonDocument log = ReadLog();
        JsonElement root = log.RootElement;
        Assert.Equal("Completed", root.GetProperty("status").GetString());
        Assert.Equal("test-source", root.GetProperty("context").GetProperty("command").GetString());
        Assert.Equal("test-connection", root.GetProperty("connection").GetProperty("id").GetString());
        Assert.Equal("NoReuseExpected", root.GetProperty("promptCacheReuseHint").GetString());
        JsonElement content = root.GetProperty("request").GetProperty("content");
        byte[] expected = CompletionMetadataLoggingClient.CanonicalizeRequest(request);
        Assert.Equal(SessionRequestCanonicalizer.Sha256Hex(expected), content.GetProperty("sha256").GetString());
        Assert.True(content.GetProperty("utf8Bytes").GetInt32() > 0);
        Assert.Equal(1, root.GetProperty("response").GetProperty("errorCount").GetInt32());
        Assert.NotEmpty(root.GetProperty("response").GetProperty("content").GetProperty("sha256").GetString()!);
        Assert.Equal(32, root.GetProperty("callId").GetString()!.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExceptionsAndCancellationAreRethrownWithoutLoggingTheirText(bool cancelled) {
        using var cancellation = new CancellationTokenSource();
        Exception failure = cancelled
            ? new OperationCanceledException(Secret, cancellation.Token)
            : new InvalidOperationException(Secret);
        var inner = new StubClient { Failure = failure };

        Exception? actual = await Record.ExceptionAsync(() => Create(inner).StreamCompletionAsync(Request(), null, cancellation.Token));

        Assert.Same(failure, actual);
        Assert.Null(inner.Options);
        using JsonDocument log = ReadLog();
        Assert.Equal(cancelled ? "cancelled" : "exception", log.RootElement.GetProperty("status").GetString());
        Assert.Equal(failure.GetType().FullName, log.RootElement.GetProperty("exceptionType").GetString());
    }

    [Fact]
    public async Task TypedTailIsIncludedInDiagnosticDigestWithoutTextFallback() {
        var inner = new StubClient();
        CompletionRequest request = Request(tail: true);

        Assert.Same(inner.Result, await Create(inner).StreamCompletionAsync(request, null));

        Assert.Same(request, inner.Request);
        using JsonDocument log = ReadLog();
        JsonElement content = log.RootElement.GetProperty("request").GetProperty("content");
        Assert.NotEmpty(content.GetProperty("sha256").GetString()!);
        Assert.Equal(JsonValueKind.Null, content.GetProperty("unavailableReason").ValueKind);
        Assert.NotEqual(
            SessionRequestCanonicalizer.Sha256Hex(CompletionMetadataLoggingClient.CanonicalizeRequest(Request())),
            content.GetProperty("sha256").GetString()
        );
    }

    [Fact]
    public async Task UnwritableLogLocationDoesNotReplaceResultOrProviderFailure() {
        Directory.CreateDirectory(_root);
        string file = Path.Combine(_root, "file");
        File.WriteAllText(file, "occupied");
        var inner = new StubClient();
        var client = new CompletionMetadataLoggingClient(inner, "connection", file, "test");

        Assert.Same(inner.Result, await client.StreamCompletionAsync(Request(), null));
        inner.Failure = new InvalidOperationException(Secret);
        Assert.Same(inner.Failure, await Record.ExceptionAsync(() => client.StreamCompletionAsync(Request(), null)));
        Assert.Equal("occupied", File.ReadAllText(file));
    }

    private CompletionMetadataLoggingClient Create(ICompletionClient inner)
        => new(inner, "test-connection", _root, "test-source");

    private static CompletionRequest Request(bool tail = false) => new(
        "test-model",
        new CompletionPromptPrefix(
            Secret + "-system",
            CompletionOutputContract.ProviderDefault([new ToolDefinition("tool", Secret + "-tool-description", new ToolSchema.Object())]),
            [new ObservationMessage(Secret + "-user")]
        ),
        tail ? [new ObservationMessage(Secret)] : []
    );

    private JsonDocument ReadLog() {
        string path = Assert.Single(Directory.GetFiles(_root));
        string text = File.ReadAllText(path);
        Assert.DoesNotContain("PRIVATE-PROMPT", text);
        Assert.DoesNotContain("DO-NOT-LOG", text);
        Assert.DoesNotContain("stackTrace", text);
        return JsonDocument.Parse(text);
    }

    public void Dispose() {
        if (Directory.Exists(_root)) { Directory.Delete(_root, true); }
    }

    private sealed class StubClient : ICompletionClient {
        public string Name => "test-provider";
        public string ApiSpecId => "test-api";
        public CompletionRequest? Request { get; private set; }
        public CompletionInvocationOptions? Options { get; private set; }
        public CancellationToken Token { get; private set; }
        public CompletionStreamObserver? Observer { get; private set; }
        public Exception? Failure { get; set; }
        public CompletionResult Result { get; } = new(
            new ActionMessage([new ActionBlock.Text(Secret)]),
            new CompletionDescriptor("test-provider", "test-api", "test-model"),
            errors: [Secret],
            termination: CompletionTermination.Completed(providerReason: Secret, detail: Secret)
        );

        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer, CancellationToken cancellationToken = default)
            => Invoke(request, null, observer, cancellationToken);

        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionInvocationOptions invocationOptions, CompletionStreamObserver? observer, CancellationToken cancellationToken = default)
            => Invoke(request, invocationOptions, observer, cancellationToken);

        private Task<CompletionResult> Invoke(CompletionRequest request, CompletionInvocationOptions? options, CompletionStreamObserver? observer, CancellationToken token) {
            Request = request;
            Options = options;
            Observer = observer;
            Token = token;
            return Failure is null ? Task.FromResult(Result) : Task.FromException<CompletionResult>(Failure);
        }
    }
}
