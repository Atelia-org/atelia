using Atelia.ChatSession;
using Atelia.Completion.Abstractions;

namespace Atelia.ChatSession.Memory;

public sealed class AutobiographicalRecordingMemoryMaintainer : IMemoryBlockMaintainer {
    public const string DefaultId = "roleplay.first-person-autobiography.recording";

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

    private static readonly MemoryDocumentFinishToolProfile s_finishProfile = new(
        MemoryDocumentTools.RecordingFinishToolName
    );

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
        var loopResult = await MemoryDocumentAgentLoop.RunAsync(
            new MemoryDocumentAgentLoopRequest(
                Id,
                _completionClient,
                _modelId,
                _systemPrompt,
                "Final Recording Instruction",
                _userPrompt,
                "Current Autobiography Editing View",
                request,
                editingSession,
                s_finishProfile
            ),
            ct
        ).ConfigureAwait(false);

        return new MemoryBlockMaintenanceResult(
            MaintainerId: Id,
            Target: Target,
            NewBlock: new MemoryPackBlock(editingSession.RenderDocumentText()),
            Notices: [
                new MemoryMaintenanceNotice(
                    "recording-completion",
                    editingSession.CompletionStatus is MemoryDocumentCompletionStatus.Changed
                        ? $"Autobiographical recording completed with {editingSession.EditCount} edits."
                        : "Autobiographical recording completed with no changes."
                )
            ],
            Diagnostics: [
                "stage=recording",
                $"completionStatus={FormatCompletionStatus(editingSession.CompletionStatus)}",
                $"editCount={editingSession.EditCount}",
                $"blockCount={editingSession.WorkingDocument.Blocks.Count}"
            ],
            Invocation: loopResult.Invocation,
            Errors: loopResult.Errors,
            ToolCallsExecuted: loopResult.ToolCallsExecuted
        );
    }

    private static string FormatCompletionStatus(MemoryDocumentCompletionStatus? status)
        => status switch {
            MemoryDocumentCompletionStatus.Changed => "changed",
            MemoryDocumentCompletionStatus.NoChange => "no-change",
            _ => "unfinished"
        };
}
