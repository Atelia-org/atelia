using System.Text.Json;
using Atelia.Completion.Abstractions;

namespace Atelia.SessionJournal.MemoPod.DebugApp;

internal sealed class DeterministicMemoRecallClient : ICompletionClient {
    internal const string ToolName = "recall_memos";

    private readonly string[] _rawMemoIds;

    internal DeterministicMemoRecallClient(
        IReadOnlyList<string> rawMemoIds
    ) {
        ArgumentNullException.ThrowIfNull(rawMemoIds);
        _rawMemoIds = rawMemoIds.ToArray();
    }

    public string Name => "memo-pod-debug-fake";
    public string ApiSpecId => "memo-pod-debug-fake-v1";

    internal int InvocationCount { get; private set; }
    internal int LegacyInvocationCount { get; private set; }
    internal List<CompletionRequest> Requests { get; } = [];
    internal List<CompletionInvocationOptions> InvocationOptions { get; }
        = [];

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
            "MemoPod recall must use the four-parameter completion overload."
        );
    }

    public Task<CompletionResult> StreamCompletionAsync(
        CompletionRequest request,
        CompletionInvocationOptions invocationOptions,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(invocationOptions);
        _ = observer;
        InvocationCount++;
        Requests.Add(request);
        InvocationOptions.Add(invocationOptions);
        cancellationToken.ThrowIfCancellationRequested();

        string arguments = JsonSerializer.Serialize(new {
            memoIds = _rawMemoIds
        });
        var message = new ActionMessage([
            new ActionBlock.ToolCall(new RawToolCall(
                ToolName,
                "memo-pod-debug-call-1",
                arguments
            ))
        ]);
        return Task.FromResult(new CompletionResult(
            message,
            CompletionDescriptor.From(this, request)
        ));
    }
}
