using System.Collections.Immutable;
using System.Text;
using Atelia.Completion.Abstractions;

namespace Atelia.SessionJournal;

/// <summary>
/// Version-owned exact request-context aggregation and expansion.
/// Candidate selection, artifact membership, lineage, and latest-selection policy remain outside
/// Prepared v5 execution recovery. An empty exact-input list represents the explicit bounded
/// empty-memory bootstrap recipe.
/// </summary>
internal static class SessionCoherentRequestRecipe {
    public static SessionRequestArtifactContextSnapshot AggregateExactInputs(
        IReadOnlyList<SessionRequestContextInput> inputs
    ) => Aggregate(
        [
        .. inputs.Select(static input => input.ContextSnapshot)
    ]
    );

    public static SessionRequestArtifactContextSnapshot Aggregate(
        IReadOnlyList<SessionRequestArtifactContextSnapshot> snapshots
    ) => new(
        JoinSnapshotField(snapshots, static snapshot => snapshot.SystemPromptFragment),
        JoinSnapshotField(snapshots, static snapshot => snapshot.ObservationMessage),
        JoinSnapshotField(snapshots, static snapshot => snapshot.ActionMessage)
    );

    public static (
        string SystemPrompt,
        ImmutableArray<IHistoryMessage> Context
    ) Expand(
        string baseSystemPrompt,
        SessionRequestArtifactContextSnapshot snapshot
    ) {
        ArgumentNullException.ThrowIfNull(baseSystemPrompt);
        ArgumentNullException.ThrowIfNull(snapshot);
        var systemPrompt = new StringBuilder(baseSystemPrompt);
        var context = ImmutableArray.CreateBuilder<IHistoryMessage>(2);
        if (!string.IsNullOrWhiteSpace(snapshot.SystemPromptFragment)) {
            if (systemPrompt.Length > 0) { systemPrompt.Append("\n\n"); }
            systemPrompt.Append(snapshot.SystemPromptFragment.Trim());
        }
        if (!string.IsNullOrWhiteSpace(snapshot.ObservationMessage)) {
            context.Add(new ObservationMessage(snapshot.ObservationMessage));
        }
        if (!string.IsNullOrEmpty(snapshot.ActionMessage)) {
            context.Add(new ActionMessage([new ActionBlock.Text(snapshot.ActionMessage)]));
        }
        return (systemPrompt.ToString(), context.ToImmutable());
    }

    private static string JoinSnapshotField(
        IReadOnlyList<SessionRequestArtifactContextSnapshot> snapshots,
        Func<SessionRequestArtifactContextSnapshot, string> selector
    ) => string.Join(
        "\n\n",
        snapshots.Select(selector)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
    );

}
