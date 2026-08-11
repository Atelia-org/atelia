using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Manager;

namespace Atelia.SessionJournal.RecapGrid.AgentControl;

public static class RecapGridAgentControlFactory {
    public static RecapGridAgentControlOpenResult Open(
        SessionJournalReadView selectedRef,
        RecapGridControlAdmission admission,
        params IHistoryUnitLoadEstimator[] estimators
    ) => BindCore(selectedRef, admission, null, estimators);

    /// <summary>
    /// Creates an exact tool binding without opening Timeline, Control, Store,
    /// or Online state. The first real tool execution opens its owner-bound
    /// dependencies once and retains them until the returned handle is disposed.
    /// </summary>
    public static RecapGridAgentControlOpenResult Bind(
        SessionJournalReadView selectedRef,
        RecapGridAgentControlProfile profile,
        params IHistoryUnitLoadEstimator[] estimators
    ) {
        ArgumentNullException.ThrowIfNull(profile);
        RecapGridAgentControlOpenResult result = BindCore(
            selectedRef,
            profile.Admission,
            null,
            estimators
        );
        if (result is RecapGridAgentControlOpenResult.Opened opened
            && opened.Handle.RuntimeIdentity != profile.RuntimeIdentity) {
            opened.Handle.Dispose();
            return new RecapGridAgentControlOpenResult.Invalid(
                "agent-control",
                "ProfileRuntimeIdentityMismatch",
                "The bound tool runtime differs from the frozen profile."
            );
        }
        return result;
    }

    internal static RecapGridAgentControlOpenResult BindForTest(
        SessionJournalReadView selectedRef,
        RecapGridAgentControlProfile profile,
        AgentControlDependencyTestHooks hooks,
        params IHistoryUnitLoadEstimator[] estimators
    ) {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(hooks);
        return BindCore(selectedRef, profile.Admission, hooks, estimators);
    }

    private static RecapGridAgentControlOpenResult BindCore(
        SessionJournalReadView selectedRef,
        RecapGridControlAdmission admission,
        AgentControlDependencyTestHooks? hooks,
        params IHistoryUnitLoadEstimator[] estimators
    ) {
        ArgumentNullException.ThrowIfNull(selectedRef);
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(estimators);
        try {
            IHistoryUnitLoadEstimator[] frozenEstimators = estimators
                .Take(257)
                .ToArray();
            if (frozenEstimators.Length is < 1 or > 256
                || frozenEstimators.Any(static value => value is null)) {
                return new RecapGridAgentControlOpenResult.Invalid(
                    "agent-control",
                    "EstimatorSetInvalid",
                    "One to 256 exact HistoryLoad estimators are required."
                );
            }
            var dependencies = new AgentControlDependencySource(
                selectedRef,
                admission,
                hooks
            );
            var lifetime = new RecapGridAgentControlLifetime(
                dependencies
            );
            var handler = new RecapGridAgentControlTool(
                selectedRef,
                admission,
                frozenEstimators,
                dependencies,
                lifetime,
                hooks
            );
            MethodToolWrapper tool = MethodToolWrapper.FromDelegate<
                AgentControlMethodInput>(handler.ExecuteToolAsync);
            if (!string.Equals(
                    SessionVisibleToolSetFingerprint.ComputeSha256(
                        [tool.Definition]
                    ),
                    SessionVisibleToolSetFingerprint.ComputeSha256(
                        [RecapGridAgentControlTool.CanonicalDefinition]
                    ),
                    StringComparison.Ordinal)) {
                throw new InvalidOperationException(
                    "Agent Control reflected definition is not canonical."
                );
            }
            var registry = new ToolRegistry([tool]);
            SessionToolRuntimeIdentity identity = Identity(admission);
            ToolSession session = registry.CreateSession(items:
                new Dictionary<string, object?>(StringComparer.Ordinal) {
                    [RecapGridAgentControlFactoryIdentity
                        .RuntimeIdentityItemKey] = identity
                });
            return new RecapGridAgentControlOpenResult.Opened(
                new RecapGridAgentControlHandle(
                    session,
                    identity,
                    lifetime
                )
            );
        }
        catch (Exception exception) when (!IsFatal(exception)) {
            return new RecapGridAgentControlOpenResult.Invalid(
                "agent-control",
                "AgentControlOpenInvalid",
                Bound(exception.Message)
            );
        }
    }

    internal static string RuntimeIdentityDigest(
        SessionToolRuntimeIdentity identity
    ) {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            new RuntimeIdentityDto(
                identity.HostId,
                identity.ImplementationSetFingerprint,
                identity.CapabilitySetFingerprint
            ),
            AgentControlJson.Options
        );
        return DomainHash(
            "atelia.recap-grid.agent-control-runtime-identity.v1",
            bytes
        );
    }

    internal static SessionToolRuntimeIdentity Identity(
        RecapGridControlAdmission admission
    ) {
        string visible = SessionVisibleToolSetFingerprint.ComputeSha256(
            [RecapGridAgentControlTool.CanonicalDefinition]
        );
        string implementation = DomainHash(
            "atelia.recap-grid.agent-control-implementation.v2",
            JsonSerializer.SerializeToUtf8Bytes(
                new ImplementationIdentityDto(
                    visible,
                    Convert.ToBase64String(
                        RecapGridAgentControlBuiltIns.CanonicalCatalogBytes()
                    )
                ),
                AgentControlJson.Options
            )
        );
        string capability = DomainHash(
            "atelia.recap-grid.agent-control-capability.v1",
            admission.ToCanonicalBytes()
        );
        return new SessionToolRuntimeIdentity(
            "atelia.recap-grid.agent-control.v1",
            implementation,
            capability
        );
    }

    private static RecapGridAgentControlOpenResult.Invalid Invalid(
        string component,
        string code
    ) => new(
        component,
        code,
        "A dependency returned an unknown outcome."
    );

    internal static string Bound(string? value) {
        const int maximumBytes = 4 * 1024;
        if (string.IsNullOrEmpty(value)) {
            return "No detail was provided.";
        }
        var encoding = new UTF8Encoding(false, true);
        try {
            if (encoding.GetByteCount(value) <= maximumBytes) {
                return value;
            }
        }
        catch (EncoderFallbackException) {
            return "The detail was not strict UTF-8 text.";
        }
        StringBuilder builder = new(Math.Min(value.Length, maximumBytes));
        int bytes = 0;
        foreach (Rune rune in value.EnumerateRunes()) {
            int count = rune.Utf8SequenceLength;
            if (bytes + count > maximumBytes) {
                break;
            }
            builder.Append(rune);
            bytes += count;
        }
        return builder.ToString();
    }

    private static string DomainHash(
        string domain,
        ReadOnlySpan<byte> value
    ) {
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256
        );
        Append(Encoding.UTF8.GetBytes(domain));
        Append(value);
        return Convert.ToHexStringLower(hash.GetHashAndReset());

        void Append(ReadOnlySpan<byte> bytes) {
            Span<byte> length = stackalloc byte[sizeof(int)];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(
                length,
                bytes.Length
            );
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
    }

    private sealed record RuntimeIdentityDto(
        string HostId,
        string ImplementationSetFingerprint,
        string CapabilitySetFingerprint
    );

    private sealed record ImplementationIdentityDto(
        string VisibleToolSetFingerprint,
        string BuiltInCatalogCanonicalBase64
    );

    private static bool IsFatal(Exception exception)
        => exception is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException;
}

internal static class AgentControlJson {
    internal static JsonSerializerOptions Options { get; } = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
}
