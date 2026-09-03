using Atelia.EventJournal;

namespace Atelia.SessionJournal;

/// <summary>
/// Resolves the governing setup pair by walking the actual Parent lineage until direct setup
/// events or a controlled-writer Prepared checkpoint are reached. A checkpoint hit revalidates
/// each referenced payload's kind, schema, and hash, but deliberately does not perform an O(N)
/// ancestry proof that those references are the latest setup events on the checkpoint lineage.
/// This bounded online trust is safe because the writer appends Prepared only after exact request
/// reconstruction/canonical validation, a bound setup cursor check, and head CAS. Journals from
/// untrusted import paths must pass full offline validation before this resolver is used online.
/// </summary>
internal static class SessionAuthoritativeGoverningSetupResolver {
    internal sealed record Result(
        SessionGoverningSetup Setup,
        GoverningSetupResolutionDiagnostics Diagnostics
    );

    public static Result Resolve(
        SessionJournalEventReader reader,
        EventAddress head,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(reader);
        EventAddress? cursor = head;
        EventAddress? runtimeAddress = null;
        EventAddress? promptAddress = null;
        SessionRuntimeConfiguration? runtime = null;
        string? prompt = null;
        int headerVisits = 0;
        int payloadReads = 0;
        int checkpointPayloadReads = 0;

        while (cursor is { } address && (runtimeAddress is null || promptAddress is null)) {
            cancellationToken.ThrowIfCancellationRequested();
            EventFrameHeader header = reader.ReadEventHeaderPreview(address).Unwrap();
            headerVisits++;
            ValidateHeader(address, header);
            var kind = (SessionEventKind)header.OpaqueEventKind;
            if (kind == SessionEventKind.RuntimeConfigSetup && runtimeAddress is null) {
                runtimeAddress = address;
            }
            else if (kind == SessionEventKind.SystemPromptSetup && promptAddress is null) {
                promptAddress = address;
            }
            else if (kind == SessionEventKind.CompletionRequestPrepared) {
                using SessionJournalEventFrame frame = reader.ReadEvent(address).Unwrap();
                payloadReads++;
                checkpointPayloadReads++;
                object body = SessionEventCodec.Decode(
                    kind,
                    frame.Payload,
                    out int bodySchemaVersion
                );
                SessionGoverningSetupReferences references =
                    SessionPreparedManifestView.FromDecoded(
                        bodySchemaVersion,
                        body
                    ).Setups;
                if (runtimeAddress is null) {
                    runtime = ReadSetup<SessionRuntimeConfiguration>(
                        reader, references.RuntimeConfig, SessionEventKind.RuntimeConfigSetup, ref payloadReads
                    );
                    runtimeAddress = references.RuntimeConfig.Address;
                }
                if (promptAddress is null) {
                    prompt = ReadSetup<SystemPromptSetupBody>(
                        reader, references.SystemPrompt, SessionEventKind.SystemPromptSetup, ref payloadReads
                    ).Content;
                    promptAddress = references.SystemPrompt.Address;
                }
            }
            cursor = header.Parent;
        }

        if (runtimeAddress is null) {
            throw new InvalidDataException(
                $"SessionJournal governing setup for head {head} is missing runtime-config-setup on its Parent chain."
            );
        }
        if (promptAddress is null) {
            throw new InvalidDataException(
                $"SessionJournal governing setup for head {head} is missing system-prompt-setup on its Parent chain."
            );
        }
        if (runtime is null) {
            runtime = ReadDirectSetup<SessionRuntimeConfiguration>(
                reader, runtimeAddress.Value, SessionEventKind.RuntimeConfigSetup, ref payloadReads
            );
        }
        if (prompt is null) {
            prompt = ReadDirectSetup<SystemPromptSetupBody>(
                reader, promptAddress.Value, SessionEventKind.SystemPromptSetup, ref payloadReads
            ).Content;
        }
        return new Result(
            new SessionGoverningSetup(head, runtimeAddress.Value, runtime, promptAddress.Value, prompt),
            new GoverningSetupResolutionDiagnostics(headerVisits, payloadReads, checkpointPayloadReads)
        );
    }

    private static T ReadSetup<T>(
        SessionJournalEventReader reader,
        SessionSetupReference reference,
        SessionEventKind expectedKind,
        ref int payloadReads
    ) where T : class {
        using SessionJournalEventFrame frame = reader.ReadEvent(reference.Address).Unwrap();
        payloadReads++;
        ValidateHeader(reference.Address, frame.Header);
        var actualKind = (SessionEventKind)frame.Header.OpaqueEventKind;
        if (actualKind != expectedKind) {
            throw new InvalidDataException($"Setup checkpoint expected '{expectedKind}' at {reference.Address}, got '{actualKind}'.");
        }
        object body = SessionEventCodec.Decode(actualKind, frame.Payload, out int version);
        if (version != reference.BodySchemaVersion
            || !string.Equals(SessionRequestCanonicalizer.Sha256Hex(frame.Payload), reference.PayloadSha256, StringComparison.Ordinal)) {
            throw new InvalidDataException($"Setup checkpoint provenance mismatch at {reference.Address}.");
        }
        return body as T ?? throw new InvalidDataException(
            $"Setup checkpoint at {reference.Address} decoded to '{body.GetType().Name}', expected '{typeof(T).Name}'."
        );
    }

    private static T ReadDirectSetup<T>(
        SessionJournalEventReader reader,
        EventAddress address,
        SessionEventKind expectedKind,
        ref int payloadReads
    ) where T : class {
        using SessionJournalEventFrame frame = reader.ReadEvent(address).Unwrap();
        payloadReads++;
        ValidateHeader(address, frame.Header);
        var actualKind = (SessionEventKind)frame.Header.OpaqueEventKind;
        if (actualKind != expectedKind) {
            throw new InvalidDataException($"Expected '{expectedKind}' at {address}, got '{actualKind}'.");
        }
        return SessionEventCodec.Decode(actualKind, frame.Payload, out _) as T
            ?? throw new InvalidDataException($"Setup at {address} decoded to an unexpected body.");
    }

    private static void ValidateHeader(EventAddress address, EventFrameHeader header) {
        if (!Enum.IsDefined(typeof(SessionEventKind), header.OpaqueEventKind)) {
            throw new InvalidDataException($"Unknown SessionJournal event kind '{header.OpaqueEventKind}' at {address}.");
        }
        if (header.Hint != default(AddressHint)) {
            throw new InvalidDataException($"SessionJournal trunk requires EventAddress hint 0, got '{header.Hint}' at {address}.");
        }
    }
}
