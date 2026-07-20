using System.Text;
using Atelia.ChatSession;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;

namespace Atelia.ChatSession.Memory;

internal sealed record MemoryDocumentAgentLoopRequest(
    string MaintainerId,
    ICompletionClient CompletionClient,
    string ModelId,
    string SystemPrompt,
    string FinalInstructionTitle,
    string FinalInstruction,
    string DocumentViewTitle,
    MemoryBlockMaintenanceRequest MaintenanceRequest,
    MemoryDocumentEditingSession EditingSession,
    MemoryDocumentFinishToolProfile FinishProfile,
    int MissingFinishRetryCount = 0,
    int MaxIterations = 16
);

internal sealed record MemoryDocumentAgentLoopResult(
    MemoryDocumentEditingSession EditingSession,
    CompletionDescriptor? Invocation,
    IReadOnlyList<string>? Errors,
    int ToolCallsExecuted
);

internal static class MemoryDocumentAgentLoop {
    public static async ValueTask<MemoryDocumentAgentLoopResult> RunAsync(
        MemoryDocumentAgentLoopRequest request,
        CancellationToken ct
    ) {
        ArgumentNullException.ThrowIfNull(request);

        var toolSession = MemoryDocumentTools.CreateSession(request.EditingSession, request.FinishProfile);
        var workingContext = BuildWorkingContext(request);
        CompletionDescriptor? invocation = null;
        List<string>? errors = null;
        int totalToolCallsExecuted = 0;
        int missingFinishRetries = 0;

        int maxIterations = request.MaxIterations > 0
            ? request.MaxIterations
            : throw new ArgumentOutOfRangeException(nameof(request), "MaxIterations must be positive.");
        for (int iteration = 0; iteration < maxIterations; iteration++) {
            ct.ThrowIfCancellationRequested();

            var completionRequest = new CompletionRequest(
                ModelId: request.ModelId,
                SystemPrompt: request.SystemPrompt,
                Context: workingContext,
                Tools: toolSession.VisibleDefinitions
            );
            var result = await request.CompletionClient.StreamCompletionAsync(completionRequest, null, ct)
                .ConfigureAwait(false);

            invocation = result.Invocation;
            if (result.Errors is { Count: > 0 }) {
                errors ??= [];
                errors.AddRange(result.Errors);
            }

            if (!result.Termination.IsSuccess) {
                throw new ChatSessionTurnAbortedException(
                    $"Memory document maintenance completion aborted: {result.Termination.ProviderReason ?? result.Termination.Detail ?? "unknown reason"}.",
                    result.Termination,
                    result.Errors
                );
            }

            var action = StripReasoningBlocks(result.Message);
            workingContext.Add(action);
            var toolCalls = action.ToolCalls;
            if (toolCalls.Count == 0) {
                if (missingFinishRetries < request.MissingFinishRetryCount) {
                    missingFinishRetries++;
                    workingContext.Add(
                        new ObservationMessage(
                            $"Document maintenance is not complete until you call `{request.FinishProfile.ToolName}`. "
                        + "Call that finish tool now as the only tool call in your response."
                        )
                    );
                    continue;
                }

                throw new InvalidOperationException(
                    $"Memory maintainer '{request.MaintainerId}' stopped without calling {request.FinishProfile.ToolName}."
                );
            }

            bool containsFinish = toolCalls.Any(
                call => call.ToolName.Equals(
                    request.FinishProfile.ToolName,
                    StringComparison.OrdinalIgnoreCase
                )
            );
            if (containsFinish && toolCalls.Count != 1) {
                throw new InvalidOperationException(
                    $"{request.FinishProfile.ToolName} must be the only tool call in its assistant turn."
                );
            }

            var toolResults = new ToolResult[toolCalls.Count];
            for (int i = 0; i < toolCalls.Count; i++) {
                var execution = await toolSession.ExecuteAsync(toolCalls[i], ct).ConfigureAwait(false);
                toolResults[i] = execution.ToToolResult();
                totalToolCallsExecuted++;
            }

            if (containsFinish && toolResults[0].Status is ToolExecutionStatus.Success) {
                if (!request.EditingSession.IsFinished) { throw new InvalidOperationException("Finish tool reported success without finishing the editing session."); }

                return new MemoryDocumentAgentLoopResult(
                    request.EditingSession,
                    invocation,
                    errors?.AsReadOnly(),
                    totalToolCallsExecuted
                );
            }

            workingContext.Add(new ToolResultsMessage(content: null, results: toolResults));
        }

        throw new InvalidOperationException(
            $"Memory maintainer '{request.MaintainerId}' tool loop exceeded the hard limit of {maxIterations} iterations."
        );
    }

    private static List<IHistoryMessage> BuildWorkingContext(MemoryDocumentAgentLoopRequest request) {
        var recentHistory = request.MaintenanceRequest.RecentHistory;
        var context = new List<IHistoryMessage>(recentHistory.Messages.Count + 8);

        if (!string.IsNullOrWhiteSpace(recentHistory.PriorContext.SystemPromptFragment)) {
            context.Add(new ObservationMessage(recentHistory.PriorContext.SystemPromptFragment));
        }
        if (!string.IsNullOrWhiteSpace(recentHistory.PriorContext.ObservationMessage)) {
            context.Add(new ObservationMessage(recentHistory.PriorContext.ObservationMessage));
        }
        if (!string.IsNullOrWhiteSpace(recentHistory.PriorContext.ActionMessage)) {
            context.Add(new ActionMessage([new ActionBlock.Text(recentHistory.PriorContext.ActionMessage)]));
        }

        AddProjectedMessages(context, recentHistory.Messages);
        context.Add(new ObservationMessage(BuildFinalInstruction(request)));
        return context;
    }

    private static string BuildFinalInstruction(MemoryDocumentAgentLoopRequest request) {
        var builder = new StringBuilder();
        builder.Append("## ").AppendLine(request.DocumentViewTitle);
        builder.AppendLine();
        builder.AppendLine("Block markers are stable editing metadata for this invocation and are not document text.");
        builder.AppendLine();
        builder.AppendLine(
            request.EditingSession.WorkingDocument.Blocks.Count == 0
            ? "(empty document)"
            : request.EditingSession.RenderBlockView()
        );
        builder.AppendLine();
        builder.Append("## ").AppendLine(request.FinalInstructionTitle);
        builder.AppendLine();
        builder.Append(request.FinalInstruction);
        return builder.ToString();
    }

    private static void AddProjectedMessages(
        List<IHistoryMessage> destination,
        IReadOnlyList<IHistoryMessage> messages
    ) {
        for (int i = 0; i < messages.Count; i++) {
            switch (messages[i]) {
                case ContextHeader header:
                    if (!string.IsNullOrWhiteSpace(header.SystemPromptFragment)) {
                        destination.Add(new ObservationMessage(header.SystemPromptFragment));
                    }
                    if (!string.IsNullOrWhiteSpace(header.ObservationMessage)) {
                        destination.Add(new ObservationMessage(header.ObservationMessage));
                    }
                    if (header.ActionMessage is not null) {
                        destination.Add(StripReasoningBlocks(header.ActionMessage));
                    }
                    break;
                case ActionMessage action:
                    destination.Add(StripReasoningBlocks(action));
                    break;
                case ObservationMessage observation:
                    destination.Add(observation);
                    break;
            }
        }
    }

    private static ActionMessage StripReasoningBlocks(ActionMessage action) {
        var filtered = new List<ActionBlock>(action.Blocks.Count);
        for (int i = 0; i < action.Blocks.Count; i++) {
            switch (action.Blocks[i]) {
                case ActionBlock.Text text:
                    var visibleText = InlineThinkTextFilter.StripInlineThinkBlocks(text.Content);
                    if (!string.IsNullOrEmpty(visibleText)) {
                        filtered.Add(new ActionBlock.Text(visibleText));
                    }
                    break;
                case ActionBlock.ToolCall:
                    filtered.Add(action.Blocks[i]);
                    break;
            }
        }

        return new ActionMessage(filtered);
    }
}
