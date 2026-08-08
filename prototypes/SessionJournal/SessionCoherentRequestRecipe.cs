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
    private const int MinimumRecapFenceLength = 4;
    private const string RecapFenceInfoString = "recap-block";

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

    /// <summary>
    /// Renders one raw derived block through the core-owned request recipe.
    /// Providers supply only the block body; routing keys are not presentation
    /// titles, and content-owned titles remain the Maintainer's responsibility.
    /// </summary>
    public static SessionRequestArtifactContextSnapshot CreateOneHotSnapshot(
        ContextHeaderBlockPath target,
        string exactText
    ) {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(exactText);
        string rendered = RenderRecapBlock(exactText);
        return target.Carrier switch {
            ContextHeaderCarrier.System => new SessionRequestArtifactContextSnapshot(
                rendered, "", ""
            ),
            ContextHeaderCarrier.Observation => new SessionRequestArtifactContextSnapshot(
                "", rendered, ""
            ),
            ContextHeaderCarrier.Action => new SessionRequestArtifactContextSnapshot(
                "", "", rendered
            ),
            _ => throw new InvalidDataException(
                $"Unsupported coherent request carrier '{target.Carrier}'."
            )
        };
    }

    private static string RenderRecapBlock(string exactText) {
        int fenceLength = Math.Max(
            MinimumRecapFenceLength,
            GetLongestTildeRun(exactText) + 1
        );
        string fence = new('~', fenceLength);
        var builder = new StringBuilder();
        builder.Append(fence)
            .Append(RecapFenceInfoString)
            .Append('\n')
            .Append(exactText);
        if (!exactText.EndsWith('\n')) { builder.Append('\n'); }
        builder.Append(fence);
        return builder.ToString();
    }

    private static int GetLongestTildeRun(string text) {
        int longestRun = 0;
        int currentRun = 0;
        foreach (char character in text) {
            if (character == '~') {
                currentRun++;
                longestRun = Math.Max(longestRun, currentRun);
            }
            else {
                currentRun = 0;
            }
        }
        return longestRun;
    }

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
