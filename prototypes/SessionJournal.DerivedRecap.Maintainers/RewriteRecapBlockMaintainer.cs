using System.Text;
using Atelia.Completion.Abstractions;

namespace Atelia.SessionJournal.DerivedRecap.Maintainers;

public sealed record RecapRewriteProfile(
    string Id,
    ContextHeaderBlockPath Target,
    string SystemPrompt,
    string UserPrompt
) {
    public string Id { get; init; } = string.IsNullOrWhiteSpace(Id)
        ? throw new ArgumentException("Recap rewrite profile id cannot be empty.", nameof(Id))
        : Id;
    public ContextHeaderBlockPath Target { get; init; } =
        Target ?? throw new ArgumentNullException(nameof(Target));
    public string SystemPrompt { get; init; } =
        SystemPrompt ?? throw new ArgumentNullException(nameof(SystemPrompt));
    public string UserPrompt { get; init; } =
        UserPrompt ?? throw new ArgumentNullException(nameof(UserPrompt));
}

public sealed class RewriteRecapBlockMaintainer : IRecapBlockMaintainer {
    public const string ImplementationId =
        "atelia.session-journal.recap-maintainer.rewrite.v1";

    private static readonly CompletionInvocationOptions InvocationOptions =
        new() {
            PromptCacheReuseHint = PromptCacheReuseHint.NoReuseExpected
        };

    private readonly RecapRewriteProfile _profile;

    public RewriteRecapBlockMaintainer(
        RecapRewriteProfile profile,
        ICompletionClient completionClient,
        string modelId
    ) {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        CapabilityFingerprint =
            RecapMaintainerCapabilityFingerprint.Compute(
                ImplementationId,
                _profile.Id,
                _profile.Target,
                RecapMaintainerCapabilityFingerprint.ComputePrompt(
                    RecapMaintainerProfileDescriptor
                        .PromptFingerprintSchema,
                    _profile.SystemPrompt,
                    _profile.UserPrompt
                )
            );
        CompletionClient = completionClient
            ?? throw new ArgumentNullException(nameof(completionClient));
        ModelId = string.IsNullOrWhiteSpace(modelId)
            ? throw new ArgumentException("Model id cannot be empty.", nameof(modelId))
            : modelId;
    }

    public string Id => _profile.Id;
    public ContextHeaderBlockPath Target => _profile.Target;
    public string CapabilityFingerprint { get; }
    public ICompletionClient CompletionClient { get; }
    public string ModelId { get; }
    public PromptCacheReuseHint PromptCacheReuseHint
        => InvocationOptions.PromptCacheReuseHint;

    public async ValueTask<RecapBlockMaintenanceResult> MaintainAsync(
        RecapBlockMaintenanceRequest request,
        CancellationToken ct
    ) {
        ArgumentNullException.ThrowIfNull(request);
        var workingContext = BuildWorkingContext(request);
        var result = await CompletionClient.StreamCompletionAsync(
            new CompletionRequest(
                ModelId: ModelId,
                SystemPrompt: _profile.SystemPrompt,
                Context: workingContext,
                Tools: []
            ),
            InvocationOptions,
            observer: null,
            ct
        ).ConfigureAwait(false);

        if (!result.Termination.IsSuccess) {
            throw new SessionJournalTurnAbortedException(
                BuildTurnAbortMessage(result.Termination),
                result.Termination,
                result.Errors
            );
        }

        var finalMessage = StripReasoningBlocks(result.Message);
        if (finalMessage.ToolCalls.Count > 0) {
            throw new InvalidOperationException(
                $"Rewrite maintainer '{Id}' returned unexpected tool calls."
            );
        }

        var updatedText = NormalizeBlockText(finalMessage.GetFlattenedText());
        return new RecapBlockMaintenanceResult(
            MaintainerId: Id,
            Target: Target,
            NewBlock: new ContextHeaderBlock(updatedText),
            Invocation: result.Invocation,
            Errors: result.Errors
        );
    }

    private static string BuildTurnAbortMessage(
        CompletionTermination termination
    ) {
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

    private List<IHistoryMessage> BuildWorkingContext(
        RecapBlockMaintenanceRequest request
    ) {
        var recentHistory = request.RecentHistory;
        var workingContext =
            new List<IHistoryMessage>(recentHistory.Messages.Count + 8);

        if (!string.IsNullOrWhiteSpace(
                recentHistory.PriorContext.SystemPromptFragment
            )) {
            workingContext.Add(
                new ObservationMessage(
                    recentHistory.PriorContext.SystemPromptFragment
                )
            );
        }
        if (!string.IsNullOrWhiteSpace(
                recentHistory.PriorContext.ObservationMessage
            )) {
            workingContext.Add(
                new ObservationMessage(
                    recentHistory.PriorContext.ObservationMessage
                )
            );
        }
        if (!string.IsNullOrWhiteSpace(
                recentHistory.PriorContext.ActionMessage
            )) {
            workingContext.Add(
                new ActionMessage([
                    new ActionBlock.Text(
                        recentHistory.PriorContext.ActionMessage
                    )
                ])
            );
        }

        AddProjectedMessages(workingContext, recentHistory.Messages);
        workingContext.Add(new ObservationMessage(BuildMaintenancePrompt()));
        return workingContext;
    }

    private static void AddProjectedMessages(
        List<IHistoryMessage> destination,
        IReadOnlyList<IHistoryMessage> messages
    ) {
        for (int i = 0; i < messages.Count; i++) {
            var original = messages[i];
            switch (original.Kind) {
                case HistoryMessageKind.ContextHeader:
                    var header = original as SessionContextHeader
                        ?? throw new InvalidOperationException(
                            $"SessionJournal recap maintainer received unsupported context header type '{original.GetType().FullName}'."
                        );
                    if (!string.IsNullOrWhiteSpace(
                            header.SystemPromptFragment
                        )) {
                        destination.Add(
                            new ObservationMessage(
                                header.SystemPromptFragment
                            )
                        );
                    }
                    if (!string.IsNullOrWhiteSpace(
                            header.ObservationMessage
                        )) {
                        destination.Add(
                            new ObservationMessage(
                                header.ObservationMessage
                            )
                        );
                    }
                    if (header.ActionMessage is not null) {
                        destination.Add(
                            StripReasoningBlocks(header.ActionMessage)
                        );
                    }
                    break;
                case HistoryMessageKind.Action:
                    destination.Add(
                        StripReasoningBlocks((ActionMessage)original)
                    );
                    break;
                case HistoryMessageKind.Observation:
                case HistoryMessageKind.ToolResults:
                    destination.Add(original);
                    break;
            }
        }
    }

    private string BuildMaintenancePrompt() {
        var builder = new StringBuilder();
        builder.AppendLine("Maintain this recap block.");
        builder.Append("Target: ")
            .Append(
                ContextHeaderCarrierTokens.ToStorageToken(Target.Carrier)
            )
            .Append('/')
            .AppendLine(Target.BlockKey);
        builder.AppendLine();
        builder.Append("Instruction:").AppendLine();
        builder.Append(_profile.UserPrompt);
        return builder.ToString();
    }

    private static string NormalizeBlockText(string? text) {
        var trimmed = InlineThinkTextFilter
            .StripInlineThinkBlocks(text ?? string.Empty)
            .Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) {
            return trimmed;
        }

        int firstLineEnd = trimmed.IndexOf('\n', StringComparison.Ordinal);
        if (firstLineEnd < 0) { return trimmed; }

        int closingFenceStart = trimmed.LastIndexOf(
            "```",
            StringComparison.Ordinal
        );
        if (closingFenceStart <= firstLineEnd) { return trimmed; }

        string trailing = trimmed[(closingFenceStart + 3)..].Trim();
        return trailing.Length == 0
            ? trimmed[(firstLineEnd + 1)..closingFenceStart].Trim()
            : trimmed;
    }

    private static ActionMessage StripReasoningBlocks(
        ActionMessage action
    ) {
        var filtered = new List<ActionBlock>(action.Blocks.Count);
        for (int i = 0; i < action.Blocks.Count; i++) {
            switch (action.Blocks[i]) {
                case ActionBlock.Text text:
                    var visibleText = InlineThinkTextFilter
                        .StripInlineThinkBlocks(text.Content);
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
