using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;

namespace Atelia.ChatSession;

public sealed partial class ChatSessionEngine {
    private const int MaxToolLoopIterations = 16;
    private const string OpenAIChatApiSpecId = "openai-chat-v1";

    public Task<ChatSessionTurnResult> SendMessageAsync(string message, CancellationToken ct = default)
        => SendMessageAsync(message, observer: null, ct);

    public async Task<ChatSessionTurnResult> SendMessageAsync(
        string message,
        CompletionStreamObserver? observer,
        CancellationToken ct = default
    ) {
        ThrowIfDisposed();
        ValidateRuntimeSurfaceIdentity();

        var toolExecutor = new ToolExecutor(_runtime.ToolRegistry, _runtime.ToolSessionState);
        var tools = toolExecutor.VisibleToolDefinitions;

        MessageRecord.AppendObservation(_messages, message);
        Commit();

        var totalToolCallsExecuted = 0;
        ActionMessage finalMessage = null!;
        CompletionDescriptor? invocation = null;
        List<string>? errors = null;

        for (int iteration = 0; iteration < MaxToolLoopIterations; iteration++) {
            ct.ThrowIfCancellationRequested();

            var context = MessageRecord.ToHistoryMessages(_messages);
            var request = new CompletionRequest(
                ModelId: _modelId,
                SystemPrompt: _systemPrompt,
                Context: context,
                Tools: tools
            );

            var result = await _runtime.CompletionClient.StreamCompletionAsync(request, observer, ct)
                .ConfigureAwait(false);

            invocation = result.Invocation;
            finalMessage = SanitizeForPersistence(result.Message);

            if (result.Errors is { Count: > 0 }) {
                errors ??= new List<string>();
                errors.AddRange(result.Errors);
            }

            MessageRecord.AppendAction(_messages, finalMessage);
            Commit();

            var toolCalls = finalMessage.ToolCalls;
            if (toolCalls.Count == 0) { break; }

            var toolResults = new ToolResult[toolCalls.Count];
            int executed = 0;
            for (int i = 0; i < toolCalls.Count; i++) {
                ct.ThrowIfCancellationRequested();
                var callResult = await toolExecutor.ExecuteAsync(toolCalls[i], ct).ConfigureAwait(false);
                toolResults[i] = callResult.ToToolResult();
                executed++;
            }

            totalToolCallsExecuted += executed;

            var toolResultsMessage = new ToolResultsMessage(
                content: null,
                results: toolResults
            );
            MessageRecord.AppendToolResults(_messages, toolResultsMessage);
            Commit();
        }

        if (finalMessage is not null && finalMessage.ToolCalls.Count > 0) {
            throw new InvalidOperationException(
                $"Tool loop exceeded the hard limit of {MaxToolLoopIterations} iterations."
            );
        }

        finalMessage ??= new ActionMessage(Array.Empty<ActionBlock>());

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
        bool allowPlaintextReasoning = string.Equals(
            _runtime.CompletionClient.ApiSpecId,
            OpenAIChatApiSpecId,
            StringComparison.Ordinal
        );

        for (int i = 0; i < message.Blocks.Count; i++) {
            var block = message.Blocks[i];
            switch (block) {
                case ActionBlock.Text:
                case ActionBlock.ToolCall:
                    persistedBlocks.Add(block);
                    break;
                case ActionBlock.TextReasoningBlock when allowPlaintextReasoning:
                    persistedBlocks.Add(block);
                    break;
            }
        }

        return new ActionMessage(persistedBlocks);
    }
}
