using System.Collections.Immutable;
using System.Text;
using Atelia.Completion.Abstractions;

namespace Atelia.SessionJournal;

/// <summary>
/// Version-owned coherent artifact contribution ordering and expansion.
/// ArtifactSet membership, lineage, and latest-selection policy remain outside this recipe.
/// </summary>
internal static class SessionCoherentRequestRecipe {
    public static int GetCarrierRank(MemoryPackCarrier carrier)
        => carrier switch {
            MemoryPackCarrier.System => 0,
            MemoryPackCarrier.Observation => 1,
            MemoryPackCarrier.Action => 2,
            _ => throw new InvalidDataException(
                $"Unsupported coherent request carrier '{carrier}'."
            )
        };

    public static SessionRequestArtifactContextSnapshot ValidateAndAggregate(
        IReadOnlyList<SessionRequestArtifactInput> inputs,
        ArtifactSetCommittedBody activation
    ) {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(activation);
        SessionArtifactSetMember[] canonicalMembers = [
            .. activation.Members
                .OrderBy(static member => GetCarrierRank(member.Target.Carrier))
                .ThenBy(static member => member.Target.BlockKey, StringComparer.Ordinal)
        ];
        if (inputs.Count != canonicalMembers.Length) {
            throw new InvalidDataException(
                "Prepared artifact inputs do not exactly match the referenced activation."
            );
        }

        for (int i = 0; i < canonicalMembers.Length; i++) {
            SessionRequestArtifactInput input = inputs[i];
            SessionArtifactSetMember member = canonicalMembers[i];
            if (!string.Equals(input.ArtifactId, member.ArtifactId, StringComparison.Ordinal)
                || !string.Equals(input.ArtifactKind, member.ArtifactKind, StringComparison.Ordinal)
                || !string.Equals(input.ContentSha256, member.ContentSha256, StringComparison.Ordinal)
                || GetSnapshotCarrier(input.ContextSnapshot) != member.Target.Carrier) {
                throw new InvalidDataException(
                    "Prepared artifact inputs do not match activation target order, identity, kind, hash, or carrier."
                );
            }
        }

        return Aggregate(inputs);
    }

    public static SessionRequestArtifactContextSnapshot Aggregate(
        IReadOnlyList<SessionRequestArtifactInput> inputs
    ) => Aggregate([
        .. inputs.Select(static input => input.ContextSnapshot)
    ]);

    public static SessionRequestArtifactContextSnapshot Aggregate(
        IReadOnlyList<SessionRequestArtifactContextSnapshot> snapshots
    ) => new(
        JoinSnapshotField(snapshots, static snapshot => snapshot.SystemPromptFragment),
        JoinSnapshotField(snapshots, static snapshot => snapshot.ObservationMessage),
        JoinSnapshotField(snapshots, static snapshot => snapshot.ActionMessage)
    );

    /// <summary>
    /// Renders one raw derived block through the core-owned MemoryPack recipe. Providers never supply
    /// request-format decoration such as block headings.
    /// </summary>
    public static SessionRequestArtifactContextSnapshot CreateOneHotSnapshot(
        MemoryPackBlockPath target,
        string exactText
    ) {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(exactText);
        var singleton = new MemoryPack();
        singleton.GetCarrier(target.Carrier).Add(
            target.BlockKey,
            new MemoryPackBlock(exactText)
        );
        RenderedMemoryPack rendered = singleton.Render();
        return target.Carrier switch {
            MemoryPackCarrier.System => new SessionRequestArtifactContextSnapshot(
                rendered.SystemPromptFragment, "", ""
            ),
            MemoryPackCarrier.Observation => new SessionRequestArtifactContextSnapshot(
                "", rendered.ObservationMessage, ""
            ),
            MemoryPackCarrier.Action => new SessionRequestArtifactContextSnapshot(
                "", "", rendered.ActionMessage
            ),
            _ => throw new InvalidDataException(
                $"Unsupported coherent request carrier '{target.Carrier}'."
            )
        };
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

    private static MemoryPackCarrier GetSnapshotCarrier(
        SessionRequestArtifactContextSnapshot snapshot
    ) {
        bool hasSystem = !string.IsNullOrWhiteSpace(snapshot.SystemPromptFragment);
        bool hasObservation = !string.IsNullOrWhiteSpace(snapshot.ObservationMessage);
        bool hasAction = !string.IsNullOrWhiteSpace(snapshot.ActionMessage);
        if ((hasSystem ? 1 : 0) + (hasObservation ? 1 : 0) + (hasAction ? 1 : 0) != 1) {
            throw new InvalidDataException(
                "A coherent artifact snapshot must populate exactly one carrier."
            );
        }
        if (hasSystem) { return MemoryPackCarrier.System; }
        if (hasObservation) { return MemoryPackCarrier.Observation; }
        return MemoryPackCarrier.Action;
    }
}
