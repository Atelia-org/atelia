using System.Text.Json;
using Atelia.Completion.Tools;
using Atelia.SessionJournal.HistoryTimeline;

namespace Atelia.SessionJournal.RecapGrid.AgentControl;

public abstract record RecapGridAgentControlOpenResult {
    private RecapGridAgentControlOpenResult() { }

    public sealed record Opened(RecapGridAgentControlHandle Handle)
        : RecapGridAgentControlOpenResult;
    public sealed record ControlAbsent : RecapGridAgentControlOpenResult;
    public sealed record TimelineAbsent : RecapGridAgentControlOpenResult;
    public sealed record ProfileAbsent(string ProfileId)
        : RecapGridAgentControlOpenResult;
    public sealed record Busy(string Component)
        : RecapGridAgentControlOpenResult;
    public sealed record UnsupportedSchema(string Component, int SchemaVersion)
        : RecapGridAgentControlOpenResult;
    public sealed record Invalid(string Component, string Code, string Detail)
        : RecapGridAgentControlOpenResult;
}

public sealed class RecapGridAgentControlHandle : IDisposable {
    private readonly RecapGridAgentControlLifetime _lifetime;

    internal RecapGridAgentControlHandle(
        ToolSession toolSession,
        SessionToolRuntimeIdentity runtimeIdentity,
        RecapGridAgentControlLifetime lifetime
    ) {
        ToolSession = toolSession;
        RuntimeIdentity = runtimeIdentity;
        _lifetime = lifetime;
    }

    public ToolSession ToolSession { get; }
    public SessionToolRuntimeIdentity RuntimeIdentity { get; }

    public void Dispose() => _lifetime.Dispose();
}

internal sealed class RecapGridAgentControlLifetime : IDisposable {
    private readonly object _gate = new();
    private readonly IDisposable[] _owned;
    private readonly AsyncLocal<int> _operationDepth = new();
    private bool _closing;
    private bool _disposeClaimed;
    private bool _complete;
    private int _operations;

    internal RecapGridAgentControlLifetime(params IDisposable[] owned) {
        _owned = owned;
    }

    internal Operation? TryEnter() {
        lock (_gate) {
            if (_closing) {
                return null;
            }
            _operations = checked(_operations + 1);
            _operationDepth.Value = checked(_operationDepth.Value + 1);
            return new Operation(this);
        }
    }

    public void Dispose() {
        bool disposeOwned = false;
        lock (_gate) {
            if (_complete) {
                return;
            }
            _closing = true;
            if (_operationDepth.Value > 0) {
                return;
            }
            while (_operations != 0 || _disposeClaimed) {
                Monitor.Wait(_gate);
                if (_complete) {
                    return;
                }
            }
            _disposeClaimed = true;
            disposeOwned = true;
        }
        if (disposeOwned) {
            DisposeOwned();
        }
    }

    private void Exit() {
        bool disposeOwned = false;
        if (_operationDepth.Value <= 0) {
            throw new InvalidOperationException(
                "Agent Control operation ownership is unbalanced."
            );
        }
        _operationDepth.Value--;
        lock (_gate) {
            _operations--;
            if (_operations == 0) {
                Monitor.PulseAll(_gate);
                if (_closing && !_disposeClaimed) {
                    _disposeClaimed = true;
                    disposeOwned = true;
                }
            }
        }
        if (disposeOwned) {
            DisposeOwned();
        }
    }

    private void DisposeOwned() {
        List<Exception>? failures = null;
        try {
            foreach (IDisposable value in _owned.Reverse()) {
                try {
                    value.Dispose();
                }
                catch (Exception exception) when (!IsFatal(exception)) {
                    (failures ??= []).Add(exception);
                }
            }
            if (failures is { Count: > 0 }) {
                throw failures.Count == 1
                    ? failures[0]
                    : new AggregateException(failures);
            }
        }
        finally {
            lock (_gate) {
                _complete = true;
                Monitor.PulseAll(_gate);
            }
        }
    }

    private static bool IsFatal(Exception exception)
        => exception is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException;

    internal sealed class Operation : IDisposable {
        private RecapGridAgentControlLifetime? _owner;

        internal Operation(RecapGridAgentControlLifetime owner)
            => _owner = owner;

        public void Dispose() => Interlocked.Exchange(
            ref _owner,
            null
        )?.Exit();
    }
}

public static class RecapGridAgentControlBuiltIns {
    public const string MysteryInvestigationV1 =
        "mystery-investigation-v1";

    public static IReadOnlyList<string> AssetIds { get; } =
        Array.AsReadOnly([MysteryInvestigationV1]);

    public static bool TryCreateRegistrationBundle(
        string assetId,
        out Control.RecapGridControlRegistrationBundle? bundle
    ) {
        if (!string.Equals(
                assetId,
                MysteryInvestigationV1,
                StringComparison.Ordinal)) {
            bundle = null;
            return false;
        }
        FamilyDefinition family = CreateMysteryFamily();
        var capability = new MaintainerCapabilitySpec(
            RecapRewriterProtocolV1.RuntimeProtocolId,
            MaintainerReadableScope
                .FullPriorBuildTargetAndCurrentHistorySegmentV1
        );
        MaintainerDefinitionRevision culprit =
            MaintainerDefinitionRevision.Create(
                new LogicalColumnId("case.culprit-hypothesis"),
                family.Digest,
                new ContextHeaderBlockPath(
                    ContextHeaderCarrier.System,
                    "culprit-hypothesis"
                ),
                capability,
                new MaintainerDeclarativeSpec(
                    "Who is the culprit?",
                    "Maintain the current culprit hypothesis and reconcile new clues."
                ),
                16 * 1024
            );
        MaintainerDefinitionRevision suspicion =
            MaintainerDefinitionRevision.Create(
                new LogicalColumnId("case.x-suspicion"),
                family.Digest,
                new ContextHeaderBlockPath(
                    ContextHeaderCarrier.System,
                    "x-suspicion"
                ),
                capability,
                new MaintainerDeclarativeSpec(
                    "Is X's behavior suspicious?",
                    "Maintain exact evidence for and against X being suspicious."
                ),
                16 * 1024
            );
        bundle = new Control.RecapGridControlRegistrationBundle(
            [family],
            [culprit, suspicion],
            []
        );
        return true;
    }

    internal static byte[] CanonicalCatalogBytes() =>
        JsonSerializer.SerializeToUtf8Bytes(
            AssetIds
                .Order(StringComparer.Ordinal)
                .Select(static assetId => {
                    if (!TryCreateRegistrationBundle(
                            assetId,
                            out Control.RecapGridControlRegistrationBundle?
                                bundle)
                        || bundle is null) {
                        throw new InvalidOperationException(
                            "A listed Agent Control built-in cannot be materialized."
                        );
                    }
                    return new BuiltInCatalogEntry(
                        assetId,
                        bundle.CanonicalCommandDigest,
                        Convert.ToBase64String(
                            bundle.ToCanonicalCommandBytes()
                        )
                    );
                })
                .ToArray(),
            AgentControlJson.Options
        );

    private sealed record BuiltInCatalogEntry(
        string AssetId,
        string RegistrationCommandDigest,
        string RegistrationCommandBase64
    );

    private static FamilyDefinition CreateMysteryFamily() =>
        FamilyDefinition.Create(
            "Maintain one bounded investigation thought.",
            [RecapRewriterProtocolV1.CreateTerminalTool(
                "Submit the updated thought."
            )],
            RecapRewriterProtocolV1.CreateOutputProtocol(),
            RecapRewriterProtocolV1.CreateInputRenderingProtocol()
        );
}
