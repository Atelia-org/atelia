using Atelia.Completion.Abstractions;

namespace Atelia.SessionJournal.DerivedRecap.Abstractions;

/// <summary>
/// One immutable, set-level input shared by every Maintainer in a recap
/// maintenance epoch.
/// </summary>
public sealed class RecapMaintenanceEpochInput {
    public RecapMaintenanceEpochInput(
        ContextHeaderSnapshot priorContext,
        IReadOnlyList<IHistoryMessage> historyMessages,
        string? sourceId = null,
        ulong? estimatedTokens = null
    ) {
        PriorContext = priorContext
            ?? throw new ArgumentNullException(nameof(priorContext));
        ArgumentNullException.ThrowIfNull(historyMessages);
        if (historyMessages.Any(static message => message is null)) {
            throw new ArgumentException(
                "History messages cannot contain null elements.",
                nameof(historyMessages)
            );
        }
        HistoryMessages = Array.AsReadOnly([
            .. historyMessages
        ]);
        SourceId = sourceId;
        EstimatedTokens = estimatedTokens;
    }

    public ContextHeaderSnapshot PriorContext { get; }

    public IReadOnlyList<IHistoryMessage> HistoryMessages { get; }

    public string? SourceId { get; }

    public ulong? EstimatedTokens { get; }
}

/// <summary>
/// Closed set of successful Maintainer outcomes. Failures are represented by
/// exceptions and the outer execution result, never as a third success case.
/// </summary>
public abstract record RecapMaintenanceSuccess {
    private RecapMaintenanceSuccess() {
    }

    public sealed record Updated : RecapMaintenanceSuccess {
        public Updated(string content) {
            Content = content
                ?? throw new ArgumentNullException(nameof(content));
        }

        public string Content { get; }
    }

    public sealed record KeepUnchanged : RecapMaintenanceSuccess {
        private KeepUnchanged() {
        }

        public static KeepUnchanged Instance { get; } = new();
    }
}

public interface IRecapBlockMaintainer {
    string Id { get; }

    ContextHeaderBlockPath Target { get; }

    /// <summary>
    /// Opaque semantic capability identity frozen by derived-recap plans.
    /// </summary>
    string CapabilityFingerprint { get; }

    /// <summary>
    /// Opaque process-local scheduling affinity. Consumers must compare this
    /// object by reference, never by value or durable identifier.
    /// </summary>
    object RuntimeGroupAffinity { get; }

    ValueTask<RecapMaintenanceSuccess> MaintainAsync(
        RecapMaintenanceEpochInput input,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Resolves one executable Maintainer by the exact durable identity frozen in
/// a recap plan. Runtime family, lane, and provider details remain opaque to
/// Planner callers.
/// </summary>
public interface IRecapBlockMaintainerRegistry {
    bool TryResolve(
        string maintainerId,
        ContextHeaderBlockPath target,
        string maintainerCapabilityFingerprint,
        out IRecapBlockMaintainer maintainer
    );
}

/// <summary>
/// Defers construction of one complete Maintainer registry until the first
/// real binding lookup. Initialization is thread-safe and attempted at most
/// once; factory exceptions are cached and propagated without retry.
/// </summary>
public sealed class DeferredRecapBlockMaintainerRegistry
    : IRecapBlockMaintainerRegistry {
    private readonly Lazy<IRecapBlockMaintainerRegistry> _inner;

    public DeferredRecapBlockMaintainerRegistry(
        Func<IRecapBlockMaintainerRegistry> factory
    ) {
        ArgumentNullException.ThrowIfNull(factory);
        _inner = new Lazy<IRecapBlockMaintainerRegistry>(
            () => factory()
                ?? throw new InvalidOperationException(
                    "Deferred Maintainer registry factory returned null."
                ),
            LazyThreadSafetyMode.ExecutionAndPublication
        );
    }

    public bool TryResolve(
        string maintainerId,
        ContextHeaderBlockPath target,
        string maintainerCapabilityFingerprint,
        out IRecapBlockMaintainer maintainer
    ) => _inner.Value.TryResolve(
        maintainerId,
        target,
        maintainerCapabilityFingerprint,
        out maintainer
    );
}

public sealed class RecapBlockMaintainerRegistry
    : IRecapBlockMaintainerRegistry {
    private readonly IReadOnlyDictionary<
        (string Id, ContextHeaderBlockPath Target,
            string CapabilityFingerprint),
        IRecapBlockMaintainer
    > _maintainers;

    public RecapBlockMaintainerRegistry(
        IReadOnlyList<IRecapBlockMaintainer> maintainers
    ) {
        ArgumentNullException.ThrowIfNull(maintainers);
        var index = new Dictionary<
            (string Id, ContextHeaderBlockPath Target,
                string CapabilityFingerprint),
            IRecapBlockMaintainer
        >();
        foreach (IRecapBlockMaintainer? maintainer in maintainers) {
            ArgumentNullException.ThrowIfNull(maintainer);
            if (string.IsNullOrWhiteSpace(maintainer.Id)
                || maintainer.Target is null
                || maintainer.RuntimeGroupAffinity is null) {
                throw new ArgumentException(
                    "Maintainer Id, Target, runtime group affinity, and "
                    + "capability fingerprint must be present.",
                    nameof(maintainers)
                );
            }
            try {
                RecapMaintainerCapabilityFingerprintSyntax.Require(
                    maintainer.CapabilityFingerprint,
                    nameof(maintainers)
                );
            }
            catch (ArgumentException error) {
                throw new ArgumentException(
                    "Maintainer capability fingerprint is invalid.",
                    nameof(maintainers),
                    error
                );
            }
            if (!index.TryAdd(
                    (
                        maintainer.Id,
                        maintainer.Target,
                        maintainer.CapabilityFingerprint
                    ),
                    maintainer
                )) {
                throw new ArgumentException(
                    "Maintainer registry contains a duplicate "
                    + $"('{maintainer.Id}', '{maintainer.Target}', "
                    + $"'{maintainer.CapabilityFingerprint}').",
                    nameof(maintainers)
                );
            }
        }
        _maintainers = index;
    }

    public bool TryResolve(
        string maintainerId,
        ContextHeaderBlockPath target,
        string maintainerCapabilityFingerprint,
        out IRecapBlockMaintainer maintainer
    ) => _maintainers.TryGetValue(
        (maintainerId, target, maintainerCapabilityFingerprint),
        out maintainer!
    );
}

public static class RecapMaintainerCapabilityFingerprintSyntax {
    public static string Require(
        string value,
        string parameterName
    ) {
        const string Prefix = "sha256:";
        if (value is null
            || !value.StartsWith(Prefix, StringComparison.Ordinal)
            || value.Length != Prefix.Length + 64
            || value.AsSpan(Prefix.Length).ContainsAnyExcept(
                "0123456789abcdef"
            )) {
            throw new ArgumentException(
                "Maintainer capability fingerprint must be sha256: "
                + "followed by lowercase SHA-256 hex.",
                parameterName
            );
        }
        return value;
    }
}
