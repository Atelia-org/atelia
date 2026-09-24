using Atelia.Diagnostics;
using Atelia.EventJournal;
using Atelia.SessionJournal;

namespace Atelia.Galatea.Server;

public sealed partial class GalateaHostService {
    private sealed record RuntimeConnectionSelection(
        string ConnectionId, GalateaConnectionStateChange? LastChange);

    internal TimeSpan? ConnectionStateExtractionDeadlineForTest { get; set; }

    // One immutable read keeps the override and its explanation coherent.
    internal GalateaConnectionStateSnapshot CaptureConnectionState(string characterId, string turnConnectionId) {
        GalateaCharacterConfig character = _characters[characterId];
        if (!IsSelectableFor(character, turnConnectionId)) {
            throw new ArgumentException("Turn connection must be selectable for the character.", nameof(turnConnectionId));
        }
        RuntimeConnectionSelection? selection = _runtimeConnectionOverrides.GetValueOrDefault(characterId);
        return new(selection?.ConnectionId, selection?.ConnectionId ?? character.DefaultConnectionId,
            turnConnectionId, selection?.LastChange);
    }

    private GalateaTurnOptions FreezeConnectionState(CharacterSessionHost host, GalateaTurnOptions options) =>
        options with { ConnectionState = CaptureConnectionState(host.Character.CharacterId, options.ConnectionId) };

    /// <summary>
    /// Best-effort derived selection, before releasing TurnLock. No admission-time
    /// replay or durable plan: a later completed Action may repair a missed match.
    /// </summary>
    private async ValueTask ExtractConnectionStateAfterCompletionAsync(
        CharacterSessionHost host, EventAddress completedHead, CancellationToken cancellationToken
    ) {
        if (_maintenanceMode
            || !_characterConnectionStateExtractors.TryGetValue(host.Character.CharacterId, out var extractor)
            || extractor is DisabledCharacterConnectionStateExtractor) {
            return;
        }
        using var deadline = new CancellationTokenSource(
            ConnectionStateExtractionDeadlineForTest ?? TimeSpan.FromSeconds(30), _timeProvider);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try {
            var read = GalateaTerminalActionExtractionTargetReader.ReadAt(host.Engine, completedHead, lifetime.Token);
            if (read is not GalateaTerminalActionExtractionReadResult.Available available) {
                throw new InvalidDataException("Completed connection-state extraction target is unavailable.");
            }
            CharacterConnectionStateMatch? match = await extractor
                .ExtractAsync(available.Target.VisibleText, lifetime.Token).ConfigureAwait(false);
            // Also protects against a client returning successfully after cancellation.
            lifetime.Token.ThrowIfCancellationRequested();
            if (match is null) { return; }
            GalateaCharacterConnectionOption option = host.Character.ConnectionOptions.Single(candidate =>
                string.Equals(candidate.ConnectionId, match.ConnectionId, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(candidate.Trigger));
            if (string.IsNullOrWhiteSpace(match.Evidence)
                || TextExtractorUtf8.GetByteCount(match.Evidence) > CharacterConnectionStateExtractor.MaximumEvidenceUtf8Bytes
                || !available.Target.VisibleText.Contains(match.Evidence, StringComparison.Ordinal)) {
                throw new InvalidDataException("Connection state evidence must be a bounded exact source substring.");
            }

            string characterId = host.Character.CharacterId;
            RuntimeConnectionSelection? previous = _runtimeConnectionOverrides.GetValueOrDefault(characterId);
            string previousId = previous?.ConnectionId ?? host.Character.DefaultConnectionId;
            GalateaConnectionStateChange? change = string.Equals(previousId, match.ConnectionId, StringComparison.Ordinal)
                ? previous?.LastChange
                : new(EventAddressTextCodec.Format(available.Target.SourceAction), previousId,
                    match.ConnectionId, option.Name, match.Evidence);
            // All mutations and turn admission are serialized by this Character's TurnLock.
            _runtimeConnectionOverrides[characterId] = new(match.ConnectionId, change);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        }
        catch (Exception exception) when (GalateaExceptionClassifier.IsNonFatal(exception)) {
            DebugUtil.Warning("Galatea.ConnectionState",
                $"Connection state extraction skipped: timeout={deadline.IsCancellationRequested}; exceptionType={exception.GetType().FullName}");
        }
    }
}
