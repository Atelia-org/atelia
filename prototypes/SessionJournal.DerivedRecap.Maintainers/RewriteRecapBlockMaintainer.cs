using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.DerivedRecap.Abstractions;

namespace Atelia.SessionJournal.DerivedRecap.Maintainers;

public sealed class RewriteRecapBlockMaintainer
    : IRecapBlockMaintainer {
    public const string ImplementationId =
        "atelia.session-journal.recap-maintainer.rewrite.v2";

    private static readonly CompletionInvocationOptions InvocationOptions =
        new() {
            PromptCacheReuseHint = PromptCacheReuseHint.NoReuseExpected
        };

    public RewriteRecapBlockMaintainer(
        RecapMaintainerDefinition definition,
        ICompletionClient completionClient,
        string modelId
    ) {
        Definition = definition
            ?? throw new ArgumentNullException(nameof(definition));
        if (!string.Equals(
                Definition.ImplementationId,
                ImplementationId,
                StringComparison.Ordinal
            )) {
            throw new ArgumentException(
                $"Rewrite Maintainer requires implementation id '{ImplementationId}'.",
                nameof(definition)
            );
        }
        CompletionClient = completionClient
            ?? throw new ArgumentNullException(nameof(completionClient));
        ModelId = string.IsNullOrWhiteSpace(modelId)
            ? throw new ArgumentException(
                "Model id cannot be empty.",
                nameof(modelId)
            )
            : modelId;
    }

    public RecapMaintainerDefinition Definition { get; }

    public string Id => Definition.MaintainerId;

    public ContextHeaderBlockPath Target => Definition.Target;

    public string CapabilityFingerprint =>
        Definition.CapabilityFingerprint;

    public ICompletionClient CompletionClient { get; }

    public string ModelId { get; }

    public PromptCacheReuseHint PromptCacheReuseHint =>
        InvocationOptions.PromptCacheReuseHint;

    public async ValueTask<RecapMaintenanceSuccess> MaintainAsync(
        RecapMaintenanceEpochInput input,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(input);
        RecapMaintainerFamilyDefinition family = Definition.Family;
        CompletionResult result = await CompletionClient
            .StreamCompletionAsync(
                new CompletionRequest(
                    ModelId,
                    family.CreatePromptPrefix(input),
                    tailMessages: [
                        new ObservationMessage(BuildTaskPrompt())
                    ]
                ),
                InvocationOptions,
                observer: null,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (!result.Termination.IsSuccess) {
            throw new SessionJournalTurnAbortedException(
                BuildTurnAbortMessage(result.Termination),
                result.Termination,
                result.Errors
            );
        }
        if (result.Errors is { Count: > 0 }) {
            throw new InvalidOperationException(
                "Completion reported errors: "
                    + string.Join("; ", result.Errors)
            );
        }

        return family.OutputProtocol.ParseAndValidate(result);
    }

    private string BuildTaskPrompt()
        => "Maintain this recap block.\n"
            + "Target: "
            + ContextHeaderCarrierTokens.ToStorageToken(Target.Carrier)
            + "/"
            + Target.BlockKey
            + "\n\nInstruction:\n"
            + Definition.TaskInstruction;

    private static string BuildTurnAbortMessage(
        CompletionTermination termination
    ) => termination.Kind switch {
        CompletionTerminationKind.Incomplete =>
            $"Completion ended incompletely and was not persisted. reason={termination.ProviderReason ?? "<none>"}, detail={termination.Detail ?? "<none>"}",
        CompletionTerminationKind.Failed =>
            $"Completion failed and was not persisted. reason={termination.ProviderReason ?? "<none>"}, detail={termination.Detail ?? "<none>"}",
        _ =>
            $"Completion was aborted and was not persisted. reason={termination.ProviderReason ?? "<none>"}, detail={termination.Detail ?? "<none>"}"
    };
}
