using System.Text;
using Atelia.ChatSession;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;

namespace Atelia.ChatSession.Memory;

public sealed class AutobiographicalRecordingMemoryMaintainer : IMemoryBlockMaintainer {
    public const string DefaultId = "roleplay.first-person-autobiography.recording";

    private const int MaxToolLoopIterations = 16;
    private const string AgentToolInstructions = """

        ## Editing Protocol

        The current autobiography is shown in the final instruction as blocks marked `[block:N]` and `[/block]`.
        These markers are editing metadata and are not part of the memoir.

        Modify the document only through the `memory_document_*` tools. Do not output replacement memoir text in an assistant message.
        Block IDs are stable for this recording invocation. Tool results report new IDs and recoverable edit failures.

        When all necessary edits are complete, call `memory_document_finish_recording` as the only tool call in that turn:
        - status `changed` after one or more successful edits;
        - status `no-change` when this experience has no lasting autobiographical consequence and no edits were made.

        The recording is not accepted without a successful finish tool call.
        """;

    private readonly ICompletionClient _completionClient;
    private readonly string _modelId;
    private readonly string _systemPrompt;
    private readonly string _userPrompt;

    public AutobiographicalRecordingMemoryMaintainer(
        ICompletionClient completionClient,
        string modelId,
        string? systemPrompt = null,
        string? userPrompt = null
    ) {
        _completionClient = completionClient ?? throw new ArgumentNullException(nameof(completionClient));
        _modelId = string.IsNullOrWhiteSpace(modelId)
            ? throw new ArgumentException("Model id cannot be empty.", nameof(modelId))
            : modelId;
        _systemPrompt = string.Concat(
            systemPrompt ?? AutobiographicalRecordingPrompts.SystemPrompt,
            AgentToolInstructions
        );
        _userPrompt = userPrompt ?? AutobiographicalRecordingPrompts.UserPrompt;
    }

    public string Id => DefaultId;

    public MemoryPackBlockPath Target => RolePlayMemoryBlockPaths.FirstPersonAutobiography;

    public async ValueTask<MemoryBlockMaintenanceResult> MaintainAsync(
        MemoryBlockMaintenanceRequest request,
        CancellationToken ct
    ) {
        ArgumentNullException.ThrowIfNull(request);
        if (!Equals(Target, request.Target)) { throw new ArgumentException("Maintenance request target does not match autobiographical recording target.", nameof(request)); }

        var editingSession = new MemoryDocumentEditingSession(request.OldBlock.Text);
        var toolSession = MemoryDocumentTools.CreateSession(editingSession);
        var workingContext = BuildWorkingContext(request, editingSession);
        CompletionDescriptor? invocation = null;
        List<string>? errors = null;
        int totalToolCallsExecuted = 0;

        for (int iteration = 0; iteration < MaxToolLoopIterations; iteration++) {
            ct.ThrowIfCancellationRequested();

            var completionRequest = new CompletionRequest(
                ModelId: _modelId,
                SystemPrompt: _systemPrompt,
                Context: workingContext,
                Tools: toolSession.VisibleDefinitions
            );
            var result = await _completionClient.StreamCompletionAsync(completionRequest, null, ct)
                .ConfigureAwait(false);

            invocation = result.Invocation;
            if (result.Errors is { Count: > 0 }) {
                errors ??= [];
                errors.AddRange(result.Errors);
            }

            if (!result.Termination.IsSuccess) {
                throw new ChatSessionTurnAbortedException(
                    $"Autobiographical recording completion aborted: {result.Termination.ProviderReason ?? result.Termination.Detail ?? "unknown reason"}.",
                    result.Termination,
                    result.Errors
                );
            }

            var action = StripReasoningBlocks(result.Message);
            workingContext.Add(action);
            var toolCalls = action.ToolCalls;
            if (toolCalls.Count == 0) {
                throw new InvalidOperationException(
                    $"Memory maintainer '{Id}' stopped without calling {MemoryDocumentTools.FinishToolName}."
                );
            }

            bool containsFinish = toolCalls.Any(
                static call => call.ToolName.Equals(
                    MemoryDocumentTools.FinishToolName,
                    StringComparison.OrdinalIgnoreCase
                )
            );
            if (containsFinish && toolCalls.Count != 1) {
                throw new InvalidOperationException(
                    $"{MemoryDocumentTools.FinishToolName} must be the only tool call in its assistant turn."
                );
            }

            var toolResults = new ToolResult[toolCalls.Count];
            for (int i = 0; i < toolCalls.Count; i++) {
                var execution = await toolSession.ExecuteAsync(toolCalls[i], ct).ConfigureAwait(false);
                toolResults[i] = execution.ToToolResult();
                totalToolCallsExecuted++;
            }

            if (containsFinish && toolResults[0].Status is ToolExecutionStatus.Success) {
                if (!editingSession.IsFinished) { throw new InvalidOperationException("Finish tool reported success without finishing the editing session."); }

                return new MemoryBlockMaintenanceResult(
                    MaintainerId: Id,
                    Target: Target,
                    NewBlock: new MemoryPackBlock(editingSession.RenderDocumentText()),
                    Notices: [
                        new MemoryMaintenanceNotice(
                            "recording-completion",
                            editingSession.CompletionStatus is MemoryDocumentRecordingCompletionStatus.Changed
                                ? $"Autobiographical recording completed with {editingSession.EditCount} edits."
                                : "Autobiographical recording completed with no changes."
                        )
                    ],
                    Diagnostics: [
                        $"stage=recording",
                        $"completionStatus={FormatCompletionStatus(editingSession.CompletionStatus)}",
                        $"editCount={editingSession.EditCount}",
                        $"blockCount={editingSession.WorkingDocument.Blocks.Count}"
                    ],
                    Invocation: invocation,
                    Errors: errors?.AsReadOnly(),
                    ToolCallsExecuted: totalToolCallsExecuted
                );
            }

            workingContext.Add(new ToolResultsMessage(content: null, results: toolResults));
        }

        throw new InvalidOperationException(
            $"Memory maintainer '{Id}' tool loop exceeded the hard limit of {MaxToolLoopIterations} iterations."
        );
    }

    private List<IHistoryMessage> BuildWorkingContext(
        MemoryBlockMaintenanceRequest request,
        MemoryDocumentEditingSession editingSession
    ) {
        var recentHistory = request.RecentHistory;
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
        context.Add(new ObservationMessage(BuildFinalInstruction(editingSession)));
        return context;
    }

    private string BuildFinalInstruction(MemoryDocumentEditingSession editingSession) {
        var builder = new StringBuilder();
        builder.AppendLine("## Current Autobiography Editing View");
        builder.AppendLine();
        builder.AppendLine("Block markers are stable editing metadata for this invocation and are not memoir text.");
        builder.AppendLine();
        builder.AppendLine(
            editingSession.WorkingDocument.Blocks.Count == 0
            ? "(empty document)"
            : editingSession.RenderBlockView()
        );
        builder.AppendLine();
        builder.AppendLine("## Final Recording Instruction");
        builder.AppendLine();
        builder.Append(_userPrompt);
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

    private static string FormatCompletionStatus(MemoryDocumentRecordingCompletionStatus? status)
        => status switch {
            MemoryDocumentRecordingCompletionStatus.Changed => "changed",
            MemoryDocumentRecordingCompletionStatus.NoChange => "no-change",
            _ => "unfinished"
        };
}
