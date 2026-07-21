using System.Text;
using Atelia.ChatSession;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;

namespace Atelia.ChatSession.Memory;

internal sealed record MemoryDocumentAgentLoopRequest(
    string MaintainerId,
    string Stage,
    ICompletionClient CompletionClient,
    string ModelId,
    string SystemPrompt,
    string FinalInstructionTitle,
    string FinalInstruction,
    string DocumentViewTitle,
    MemoryBlockMaintenanceRequest MaintenanceRequest,
    MemoryDocumentEditingSession EditingSession,
    MemoryDocumentFinishToolProfile FinishProfile,
    int? TargetTokens = null,
    int MissingFinishRetryCount = 0,
    int MaxIterations = 128
);

internal sealed record MemoryDocumentAgentLoopResult(
    MemoryDocumentEditingSession EditingSession,
    CompletionDescriptor? Invocation,
    IReadOnlyList<string>? Errors,
    int ToolCallsExecuted
);

public sealed class MemoryDocumentAgentLoopException : InvalidOperationException {
    public MemoryDocumentAgentLoopException(
        string message,
        string stage,
        int beforeTokens,
        int workingTokens,
        int? targetTokens,
        CompletionDescriptor? invocation,
        IReadOnlyList<string>? errors,
        int toolCallsExecuted,
        Exception? innerException = null
    ) : base(message, innerException) {
        Stage = stage;
        BeforeTokens = beforeTokens;
        WorkingTokens = workingTokens;
        TargetTokens = targetTokens;
        Invocation = invocation;
        Errors = errors;
        ToolCallsExecuted = toolCallsExecuted;
    }

    public string Stage { get; }
    public int BeforeTokens { get; }
    public int WorkingTokens { get; }
    public int? TargetTokens { get; }
    public CompletionDescriptor? Invocation { get; }
    public IReadOnlyList<string>? Errors { get; }
    public int ToolCallsExecuted { get; }
}

internal static class MemoryDocumentAgentLoop {
    internal const string EmptyLeadingObservationPlaceholder = "<empty>";

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
            CompletionResult result;
            try {
                result = await request.CompletionClient.StreamCompletionAsync(completionRequest, null, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) {
                throw CreateFailure($"Memory document maintenance completion failed: {ex.Message}", ex);
            }

            invocation = result.Invocation;
            if (result.Errors is { Count: > 0 }) {
                errors ??= [];
                errors.AddRange(result.Errors);
            }

            if (!result.Termination.IsSuccess) {
                var aborted = new ChatSessionTurnAbortedException(
                    $"Memory document maintenance completion aborted: {result.Termination.ProviderReason ?? result.Termination.Detail ?? "unknown reason"}.",
                    result.Termination,
                    result.Errors
                );
                throw CreateFailure(aborted.Message, aborted);
            }

            var action = StripReasoningBlocks(result.Message);
            workingContext.Add(action);
            var toolCalls = action.ToolCalls;
            if (toolCalls.Count == 0) {
                if (request.EditingSession.EditCount > 0) {
                    var implicitFinish = MemoryDocumentTools.FinishSession(
                        request.EditingSession,
                        new FinishMemoryDocumentArtifact { Status = "changed" },
                        request.FinishProfile
                    );
                    if (implicitFinish.IsValid) {
                        return new MemoryDocumentAgentLoopResult(
                            request.EditingSession,
                            invocation,
                            errors?.AsReadOnly(),
                            totalToolCallsExecuted
                        );
                    }

                    workingContext.Add(
                        new ObservationMessage(
                            $"Document maintenance cannot finish yet: {implicitFinish.message ?? "validation failed"} "
                            + "Continue editing, then finish again."
                        )
                    );
                    continue;
                }

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

                throw CreateFailure(
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
                throw CreateFailure(
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
                if (!request.EditingSession.IsFinished) { throw CreateFailure("Finish tool reported success without finishing the editing session."); }

                return new MemoryDocumentAgentLoopResult(
                    request.EditingSession,
                    invocation,
                    errors?.AsReadOnly(),
                    totalToolCallsExecuted
                );
            }

            workingContext.Add(new ToolResultsMessage(content: null, results: toolResults));
        }

        throw CreateFailure(
            $"Memory maintainer '{request.MaintainerId}' tool loop exceeded the hard limit of {maxIterations} iterations."
        );

        MemoryDocumentAgentLoopException CreateFailure(string message, Exception? innerException = null)
            => new(
                message,
                request.Stage,
                MemoryDocumentTokenEstimator.Estimate(request.EditingSession.BaseText),
                MemoryDocumentTokenEstimator.Estimate(request.EditingSession.RenderDocumentText()),
                request.TargetTokens,
                invocation,
                errors?.AsReadOnly(),
                totalToolCallsExecuted,
                innerException
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
        EnsureObservationFirst(context);
        return context;
    }

    private static void EnsureObservationFirst(List<IHistoryMessage> context) {
        if (context.Count > 0 && context[0].Kind is HistoryMessageKind.Action) {
            context.Insert(0, new ObservationMessage(EmptyLeadingObservationPlaceholder));
        }
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
