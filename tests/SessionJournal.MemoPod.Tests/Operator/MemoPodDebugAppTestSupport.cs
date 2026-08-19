using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.MemoPod.DebugApp;

namespace Atelia.SessionJournal.MemoPod.Tests.Operator;

internal sealed class MemoPodDebugAppTestHost : IDisposable {
    internal const string PodIdText = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static readonly UTF8Encoding Utf8WithoutBom = new(
        encoderShouldEmitUTF8Identifier: false
    );

    private int _nextInputNumber;

    internal MemoPodDebugAppTestHost() {
        ContainerPath = Path.Combine(
            Path.GetTempPath(),
            "atelia-memo-pod-debug-app-tests",
            Guid.NewGuid().ToString("N")
        );
        StoreRoot = Path.Combine(ContainerPath, "store");
        InputRoot = Path.Combine(ContainerPath, "input");
        Directory.CreateDirectory(StoreRoot);
        Directory.CreateDirectory(InputRoot);
    }

    internal string ContainerPath { get; }
    internal string StoreRoot { get; }
    internal string InputRoot { get; }

    internal string WriteText(string text, string extension = ".txt") {
        string path = NextInputPath(extension);
        File.WriteAllText(path, text, Utf8WithoutBom);
        return path;
    }

    internal string WriteBytes(byte[] bytes, string extension = ".bin") {
        string path = NextInputPath(extension);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    internal Task<OperatorRunResult> RunAsync(
        string[] args,
        Func<IReadOnlyList<string>, ICompletionClient>?
            fakeClientFactory = null,
        CancellationToken cancellationToken = default
    ) => RunCoreAsync(
        args,
        fakeClientFactory,
        cancellationToken
    );

    internal async Task<OperatorRunResult> CreateAsync(
        string topic,
        params string[] exactTexts
    ) {
        var args = new List<string> {
            "create",
            "--root",
            StoreRoot,
            "--pod",
            PodIdText,
            "--topic-file",
            WriteText(topic)
        };
        foreach (string exactText in exactTexts) {
            args.Add("--memo-file");
            args.Add(WriteText(exactText));
        }
        return await RunAsync(args.ToArray()).ConfigureAwait(false);
    }

    public void Dispose() {
        if (Directory.Exists(ContainerPath)) {
            Directory.Delete(ContainerPath, recursive: true);
        }
    }

    private string NextInputPath(string extension) {
        int number = Interlocked.Increment(ref _nextInputNumber);
        return Path.Combine(InputRoot, $"input-{number:D4}{extension}");
    }

    private static async Task<OperatorRunResult> RunCoreAsync(
        string[] args,
        Func<IReadOnlyList<string>, ICompletionClient>?
            fakeClientFactory,
        CancellationToken cancellationToken
    ) {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int exitCode = await Program.MainCoreAsync(
            args,
            output,
            error,
            fakeClientFactory,
            cancellationToken
        ).ConfigureAwait(false);
        return new OperatorRunResult(
            exitCode,
            output.ToString(),
            error.ToString()
        );
    }
}

internal sealed record OperatorRunResult(
    int ExitCode,
    string StandardOutput,
    string StandardError
);

internal sealed class ScriptedOperatorCompletionClient : ICompletionClient {
    private Func<
        ScriptedOperatorCompletionClient,
        CompletionRequest,
        CancellationToken,
        Task<CompletionResult>
    > _handler;

    internal ScriptedOperatorCompletionClient() {
        _handler = static (client, request, _) => Task.FromResult(
            client.ValidResult(request)
        );
    }

    public string Name => "memo-pod-operator-scripted";
    public string ApiSpecId => "memo-pod-operator-scripted-v1";

    internal int InvocationCount { get; private set; }
    internal int LegacyInvocationCount { get; private set; }
    internal List<CompletionRequest> Requests { get; } = [];
    internal List<CompletionInvocationOptions> InvocationOptions { get; }
        = [];

    internal Func<
        ScriptedOperatorCompletionClient,
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
        _ = request;
        _ = observer;
        _ = cancellationToken;
        LegacyInvocationCount++;
        throw new InvalidOperationException(
            "Operator recall must use the four-parameter overload."
        );
    }

    public Task<CompletionResult> StreamCompletionAsync(
        CompletionRequest request,
        CompletionInvocationOptions invocationOptions,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) {
        _ = observer;
        InvocationCount++;
        Requests.Add(request);
        InvocationOptions.Add(invocationOptions);
        return _handler(this, request, cancellationToken);
    }

    internal CompletionResult ValidResult(
        CompletionRequest request,
        params string[] memoIds
    ) {
        string ids = string.Join(",", memoIds.Select(
            static id => $"\"{id}\""
        ));
        return new CompletionResult(
            new ActionMessage([
                new ActionBlock.ToolCall(new RawToolCall(
                    "recall_memos",
                    "operator-call-1",
                    $"{{\"memoIds\":[{ids}]}}"
                ))
            ]),
            CompletionDescriptor.From(this, request)
        );
    }
}
