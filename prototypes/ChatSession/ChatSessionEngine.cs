using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;

namespace Atelia.ChatSession;

public sealed partial class ChatSessionEngine {
    private const int MaxToolLoopIterations = 16;

    public Task<ChatSessionTurnResult> SendMessageAsync(string message, CancellationToken ct = default)
        => SendMessageAsync(message, observer: null, ct);

    public async Task<ChatSessionTurnResult> SendMessageAsync(
        string message,
        CompletionStreamObserver? observer,
        CancellationToken ct = default
    ) {
        ThrowIfDisposed();
        ValidateRuntimeSurfaceIdentity();

        var session = _runtime.ToolSession;
        var tools = session.VisibleDefinitions;
        var persistedContext = MessageRecord.ToHistoryMessages(_messages);
        var workingContext = new List<IHistoryMessage>(persistedContext.Count + 8);
        workingContext.AddRange(persistedContext);

        var turnMessages = new List<IHistoryMessage>(capacity: 4);
        var userObservation = new ObservationMessage(message);
        turnMessages.Add(userObservation);
        workingContext.Add(userObservation);

        var totalToolCallsExecuted = 0;
        ActionMessage finalMessage = null!;
        CompletionDescriptor? invocation = null;
        List<string>? errors = null;

        for (int iteration = 0; iteration < MaxToolLoopIterations; iteration++) {
            ct.ThrowIfCancellationRequested();

            var request = new CompletionRequest(
                ModelId: _modelId,
                SystemPrompt: _systemPrompt,
                Context: workingContext,
                Tools: tools
            );

            var result = await _runtime.CompletionClient.StreamCompletionAsync(request, observer, ct)
                .ConfigureAwait(false);

            invocation = result.Invocation;
            if (result.Errors is { Count: > 0 }) {
                errors ??= new List<string>();
                errors.AddRange(result.Errors);
            }

            if (!result.Termination.IsSuccess) {
                throw new ChatSessionTurnAbortedException(
                    BuildTurnAbortMessage(result.Termination),
                    result.Termination,
                    result.Errors
                );
            }

            finalMessage = SanitizeForPersistence(result.Message);
            turnMessages.Add(finalMessage);
            workingContext.Add(finalMessage);

            var toolCalls = finalMessage.ToolCalls;
            if (toolCalls.Count == 0) { break; }

            var toolResults = new ToolResult[toolCalls.Count];
            int executed = 0;
            for (int i = 0; i < toolCalls.Count; i++) {
                ct.ThrowIfCancellationRequested();
                var callResult = await session.ExecuteAsync(toolCalls[i], ct).ConfigureAwait(false);
                toolResults[i] = callResult.ToToolResult();
                executed++;
            }

            totalToolCallsExecuted += executed;

            var toolResultsMessage = new ToolResultsMessage(
                content: null,
                results: toolResults
            );
            turnMessages.Add(toolResultsMessage);
            workingContext.Add(toolResultsMessage);
        }

        if (finalMessage is not null && finalMessage.ToolCalls.Count > 0) {
            throw new InvalidOperationException(
                $"Tool loop exceeded the hard limit of {MaxToolLoopIterations} iterations."
            );
        }

        finalMessage ??= new ActionMessage(Array.Empty<ActionBlock>());
        PersistTurnMessages(turnMessages);
        Commit();

        return new ChatSessionTurnResult(
            Message: finalMessage,
            Invocation: invocation ?? new CompletionDescriptor("none", "none", _modelId),
            Errors: errors?.AsReadOnly(),
            ToolCallsExecuted: totalToolCallsExecuted
        );
    }

    private void ValidateRuntimeSurfaceIdentity() {
        if (_runtime.CompletionClient.ApiSpecId != _apiSpecId) {
            throw new InvalidOperationException(
                $"Runtime API spec changed: '{_runtime.CompletionClient.ApiSpecId}' vs persisted '{_apiSpecId}'."
            );
        }

        if (_runtime.CompletionSurfaceId != _completionSurfaceId) {
            throw new InvalidOperationException(
                $"Runtime surface changed: '{_runtime.CompletionSurfaceId}' vs persisted '{_completionSurfaceId}'."
            );
        }
    }

    private ActionMessage SanitizeForPersistence(ActionMessage message) {
        var persistedBlocks = new List<ActionBlock>(message.Blocks.Count);

        for (int i = 0; i < message.Blocks.Count; i++) {
            var block = message.Blocks[i];
            switch (block) {
                case ActionBlock.Text text:
                    var visibleText = InlineThinkTextFilter.StripInlineThinkBlocks(text.Content);
                    if (!string.IsNullOrEmpty(visibleText)) {
                        persistedBlocks.Add(new ActionBlock.Text(visibleText));
                    }
                    break;
                case ActionBlock.ToolCall:
                    persistedBlocks.Add(block);
                    break;
            }
        }

        return new ActionMessage(persistedBlocks);
    }

    private void PersistTurnMessages(IReadOnlyList<IHistoryMessage> turnMessages) {
        for (int i = 0; i < turnMessages.Count; i++) {
            switch (turnMessages[i]) {
                case ToolResultsMessage toolResults:
                    MessageRecord.AppendToolResults(_messages, toolResults);
                    break;
                case ObservationMessage observation:
                    MessageRecord.AppendObservation(_messages, observation.Content);
                    break;
                case ActionMessage action:
                    MessageRecord.AppendAction(_messages, action);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported turn message type '{turnMessages[i].GetType()}'.");
            }
        }
    }

    private static string BuildTurnAbortMessage(CompletionTermination termination) {
        ArgumentNullException.ThrowIfNull(termination);

        return termination.Kind switch {
            CompletionTerminationKind.Incomplete =>
                $"Completion ended incompletely and was not persisted. reason={termination.ProviderReason ?? "<none>"}, detail={termination.Detail ?? "<none>"}",
            CompletionTerminationKind.Failed =>
                $"Completion failed and was not persisted. reason={termination.ProviderReason ?? "<none>"}, detail={termination.Detail ?? "<none>"}",
            _ =>
                $"Completion was aborted and was not persisted. reason={termination.ProviderReason ?? "<none>"}, detail={termination.Detail ?? "<none>"}"
        };
    }
}
