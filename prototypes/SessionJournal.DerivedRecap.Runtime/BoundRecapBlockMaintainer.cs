using System.Runtime.CompilerServices;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.DerivedRecap.Abstractions;
using Atelia.SessionJournal.DerivedRecap.Maintainers;

namespace Atelia.SessionJournal.DerivedRecap.Runtime;

public sealed class RecapRuntimeGroup {
    internal RecapRuntimeGroup(
        RecapMaintainerFamilyDefinition family,
        RecapExecutionLane lane
    ) {
        Family = family ?? throw new ArgumentNullException(nameof(family));
        Lane = lane ?? throw new ArgumentNullException(nameof(lane));
    }

    public RecapMaintainerFamilyDefinition Family { get; }

    public RecapExecutionLane Lane { get; }

    public BoundRecapBlockMaintainer Bind(
        RecapMaintainerDefinition definition
    ) => new(definition, this);
}

/// <summary>
/// Interns runtime groups by the exact lane and family references.
/// </summary>
public sealed class RecapRuntimeGroupInterner {
    private readonly object _gate = new();
    private readonly Dictionary<Key, RecapRuntimeGroup> _groups =
        new(KeyComparer.Instance);

    public RecapRuntimeGroup GetOrAdd(
        RecapExecutionLane lane,
        RecapMaintainerFamilyDefinition family
    ) {
        ArgumentNullException.ThrowIfNull(lane);
        ArgumentNullException.ThrowIfNull(family);
        var key = new Key(lane, family);
        lock (_gate) {
            if (_groups.TryGetValue(
                    key,
                    out RecapRuntimeGroup? existing
                )) {
                return existing;
            }
            var created = new RecapRuntimeGroup(family, lane);
            _groups.Add(key, created);
            return created;
        }
    }

    private readonly record struct Key(
        RecapExecutionLane Lane,
        RecapMaintainerFamilyDefinition Family
    );

    private sealed class KeyComparer : IEqualityComparer<Key> {
        internal static KeyComparer Instance { get; } = new();

        public bool Equals(Key x, Key y) =>
            ReferenceEquals(x.Lane, y.Lane)
            && ReferenceEquals(x.Family, y.Family);

        public int GetHashCode(Key value) => HashCode.Combine(
            RuntimeHelpers.GetHashCode(value.Lane),
            RuntimeHelpers.GetHashCode(value.Family)
        );
    }
}

/// <summary>
/// The only executable completion-backed recap Maintainer binding.
/// </summary>
public sealed class BoundRecapBlockMaintainer
    : IRecapBlockMaintainer {
    internal BoundRecapBlockMaintainer(
        RecapMaintainerDefinition definition,
        RecapRuntimeGroup runtimeGroup
    ) {
        Definition = definition
            ?? throw new ArgumentNullException(nameof(definition));
        RuntimeGroup = runtimeGroup
            ?? throw new ArgumentNullException(nameof(runtimeGroup));
        if (!ReferenceEquals(Definition.Family, RuntimeGroup.Family)) {
            throw new ArgumentException(
                "Maintainer definition and runtime group must reference the exact same family instance.",
                nameof(runtimeGroup)
            );
        }
        if (!string.Equals(
                Definition.ImplementationId,
                RecapMaintainerImplementationIds.StructuredRewrite,
                StringComparison.Ordinal
            )) {
            throw new ArgumentException(
                "Bound recap Maintainer does not support implementation id "
                    + $"'{Definition.ImplementationId}'.",
                nameof(definition)
            );
        }
    }

    public RecapMaintainerDefinition Definition { get; }

    public RecapRuntimeGroup RuntimeGroup { get; }

    public string Id => Definition.MaintainerId;

    public ContextHeaderBlockPath Target => Definition.Target;

    public string CapabilityFingerprint =>
        Definition.CapabilityFingerprint;

    public object RuntimeGroupAffinity => RuntimeGroup;

    public async ValueTask<RecapMaintenanceSuccess> MaintainAsync(
        RecapMaintenanceEpochInput input,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(input);
        RecapMaintainerFamilyDefinition family = RuntimeGroup.Family;
        CompletionResult result = await RuntimeGroup.Lane.SendAsync(
            family.CreatePromptPrefix(input),
            Definition.CreateTaskTailMessages(),
            new RecapCallContext(Id, Target, input.SourceId),
            cancellationToken
        ).ConfigureAwait(false);

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
