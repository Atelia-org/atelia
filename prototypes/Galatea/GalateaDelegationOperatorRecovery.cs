using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Atelia.Galatea.Server.Mailbox;

namespace Atelia.Galatea.Server;

/// <summary>
/// Offline, Galatea-owned recovery for one exact Codex completion whose
/// accepted turn is absent from the official persistent projection. The
/// command intentionally neither starts the web host/provider stack nor reads
/// Codex rollout files or private Codex databases.
/// </summary>
internal static class GalateaDelegationOperatorRecovery {
    internal const string CommandName = "recover-codex-completed";
    internal const string EvidenceKind = "codex-turn-completed";
    internal const int EvidenceVersion = 1;
    internal const int MaximumEvidenceUtf8Bytes = 2 * 1024 * 1024;
    private const string RequiredReconcileCode =
        GalateaDelegateDispatchInspection.AcceptedTurnNotVisible.FailureCode;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    internal static bool IsOperatorInvocation(string[] args) =>
        args.Length > 0
        && string.Equals(args[0], "operator", StringComparison.Ordinal);

    internal static int Run(
        string[] args,
        TextWriter standardOutput,
        TextWriter standardError
    ) {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);
        try {
            Invocation invocation = ParseInvocation(args);
            byte[] evidenceBytes = GalateaStrictConfigReader
                .ReadBoundedRegularFile(
                    invocation.EvidencePath,
                    MaximumEvidenceUtf8Bytes,
                    "Galatea Codex completion recovery evidence"
                );
            GalateaCodexCompletionRecoveryEvidence evidence =
                DecodeEvidence(evidenceBytes);
            GalateaConfig config = GalateaConfigLoader.Load(
                invocation.ConfigPath
            );
            GalateaUserConfig user = config.Users.SingleOrDefault(value =>
                    string.Equals(
                        value.UserId,
                        evidence.UserId,
                        StringComparison.Ordinal
                    ))
                ?? throw new InvalidDataException(
                    "Recovery evidence userId does not name exactly one "
                    + "configured Galatea user."
                );
            GalateaCodexCompletionRecoveryResult result = Execute(
                user,
                config.Delegates.CodexRoute,
                evidence,
                invocation.Apply
            );
            standardOutput.WriteLine(
                "Galatea Codex completion recovery: "
                + $"outcome={result.Outcome}, "
                + $"userId={GalateaMailboxText.SummarizeForLog(evidence.UserId)}, "
                + $"dispatchId={GalateaMailboxText.SummarizeForLog(evidence.DispatchId)}, "
                + $"threadId={GalateaMailboxText.SummarizeForLog(evidence.ThreadId)}, "
                + $"turnId={GalateaMailboxText.SummarizeForLog(evidence.TurnId)}, "
                + $"storeRevision={result.StoreRevision}."
            );
            return 0;
        }
        catch (Exception exception) when (
            GalateaExceptionClassifier.IsNonFatal(exception)) {
            standardError.WriteLine(
                "Galatea Codex completion recovery failed: "
                + exception.Message
            );
            return 2;
        }
    }

    internal static GalateaCodexCompletionRecoveryResult Execute(
        GalateaUserConfig user,
        GalateaDelegateRouteConfig route,
        GalateaCodexCompletionRecoveryEvidence evidence,
        bool apply
    ) {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(route);
        ValidateEvidence(evidence);
        if (!string.Equals(
                user.UserId,
                evidence.UserId,
                StringComparison.Ordinal)) {
            throw new InvalidDataException(
                "Recovery evidence userId does not match the selected user."
            );
        }

        GalateaDelegationStoreOwner owner = CreateOwner(user, route);
        GalateaDelegationStoreLimits limits =
            GalateaDelegationSupervisor.CreateLimits(route);
        GalateaDelegationStateSnapshot inspected;
        RecoveryClassification classification;
        using (GalateaDelegationSqliteStore readOnly =
               GalateaDelegationSqliteStore.OpenExistingReadOnly(
                   user.DelegationStateDir,
                   owner,
                   limits
               )) {
            inspected = readOnly.ReadSnapshot();
            classification = Classify(inspected, evidence);
        }

        if (classification == RecoveryClassification.AlreadyApplied) {
            return new(
                GalateaCodexCompletionRecoveryOutcome.AlreadyApplied,
                inspected.StoreRevision
            );
        }
        if (!apply) {
            return new(
                GalateaCodexCompletionRecoveryOutcome.DryRunReady,
                inspected.StoreRevision
            );
        }

        using GalateaDelegationSqliteStore writable =
            GalateaDelegationSqliteStore.OpenExisting(
                user.DelegationStateDir,
                owner,
                limits
            );
        GalateaDelegationStateSnapshot before = writable.ReadSnapshot();
        classification = Classify(before, evidence);
        if (classification == RecoveryClassification.AlreadyApplied) {
            return new(
                GalateaCodexCompletionRecoveryOutcome.AlreadyApplied,
                before.StoreRevision
            );
        }

        GalateaOutboundMailSnapshot mail = ReadEvidenceMail(
            before,
            evidence.DispatchId
        );
        _ = writable.RecordCompletedMail(
            evidence.DispatchId,
            mail.Revision,
            evidence.ThreadId,
            evidence.TurnId,
            evidence.Final
        );
        GalateaDelegationStateSnapshot after = writable.ReadSnapshot();
        RequireExactPostState(before, after, evidence);
        return new(
            GalateaCodexCompletionRecoveryOutcome.Applied,
            after.StoreRevision
        );
    }

    internal static GalateaCodexCompletionRecoveryEvidence DecodeEvidence(
        ReadOnlySpan<byte> utf8
    ) {
        if (utf8.IsEmpty || utf8.Length > MaximumEvidenceUtf8Bytes) {
            throw new InvalidDataException(
                "Recovery evidence bytes are empty or exceed the code-owned cap."
            );
        }
        try {
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 2
            });
            RequireToken(ref reader, JsonTokenType.StartObject);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int? version = null;
            string? kind = null;
            string? userId = null;
            string? dispatchId = null;
            string? threadId = null;
            string? turnId = null;
            int? taskUtf8Bytes = null;
            string? taskSha256 = null;
            int? finalUtf8Bytes = null;
            string? finalSha256 = null;
            string? finalUtf8Base64 = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject) {
                if (reader.TokenType != JsonTokenType.PropertyName) {
                    throw InvalidEvidenceShape();
                }
                string property = reader.GetString()
                    ?? throw InvalidEvidenceShape();
                if (!seen.Add(property) || !reader.Read()) {
                    throw InvalidEvidenceShape();
                }
                switch (property) {
                    case "v":
                        version = ReadInt32(ref reader);
                        break;
                    case "kind":
                        kind = ReadString(ref reader);
                        break;
                    case "userId":
                        userId = ReadString(ref reader);
                        break;
                    case "dispatchId":
                        dispatchId = ReadString(ref reader);
                        break;
                    case "threadId":
                        threadId = ReadString(ref reader);
                        break;
                    case "turnId":
                        turnId = ReadString(ref reader);
                        break;
                    case "taskUtf8Bytes":
                        taskUtf8Bytes = ReadInt32(ref reader);
                        break;
                    case "taskSha256":
                        taskSha256 = ReadString(ref reader);
                        break;
                    case "finalUtf8Bytes":
                        finalUtf8Bytes = ReadInt32(ref reader);
                        break;
                    case "finalSha256":
                        finalSha256 = ReadString(ref reader);
                        break;
                    case "finalUtf8Base64":
                        finalUtf8Base64 = ReadString(ref reader);
                        break;
                    default:
                        throw InvalidEvidenceShape();
                }
            }
            if (reader.TokenType != JsonTokenType.EndObject || reader.Read()) {
                throw InvalidEvidenceShape();
            }
            string encodedFinal = finalUtf8Base64
                ?? throw InvalidEvidenceShape();
            byte[] finalBytes;
            try {
                finalBytes = Convert.FromBase64String(encodedFinal);
            }
            catch (FormatException exception) {
                throw new InvalidDataException(
                    "Recovery evidence finalUtf8Base64 is not canonical base64.",
                    exception
                );
            }
            if (!string.Equals(
                    Convert.ToBase64String(finalBytes),
                    encodedFinal,
                    StringComparison.Ordinal)) {
                throw new InvalidDataException(
                    "Recovery evidence finalUtf8Base64 is not canonical base64."
                );
            }
            string final;
            try {
                final = StrictUtf8.GetString(finalBytes);
            }
            catch (DecoderFallbackException exception) {
                throw new InvalidDataException(
                    "Recovery evidence final bytes are not strict UTF-8.",
                    exception
                );
            }
            var evidence = new GalateaCodexCompletionRecoveryEvidence(
                version ?? throw InvalidEvidenceShape(),
                kind ?? throw InvalidEvidenceShape(),
                userId ?? throw InvalidEvidenceShape(),
                dispatchId ?? throw InvalidEvidenceShape(),
                threadId ?? throw InvalidEvidenceShape(),
                turnId ?? throw InvalidEvidenceShape(),
                taskUtf8Bytes ?? throw InvalidEvidenceShape(),
                taskSha256 ?? throw InvalidEvidenceShape(),
                finalUtf8Bytes ?? throw InvalidEvidenceShape(),
                finalSha256 ?? throw InvalidEvidenceShape(),
                final
            );
            ValidateEvidence(evidence);
            return evidence;
        }
        catch (JsonException exception) {
            throw new InvalidDataException(
                "Recovery evidence is not strict valid UTF-8 JSON.",
                exception
            );
        }
    }

    private static Invocation ParseInvocation(string[] args) {
        if (args.Length < 2
            || !string.Equals(args[0], "operator", StringComparison.Ordinal)
            || !string.Equals(args[1], CommandName, StringComparison.Ordinal)) {
            throw Usage();
        }
        string? configPath = null;
        string? evidencePath = null;
        bool apply = false;
        for (int index = 2; index < args.Length; index++) {
            switch (args[index]) {
                case "--config" when configPath is null
                    && index + 1 < args.Length:
                    configPath = RequireAbsoluteNoFollowFile(
                        args[++index],
                        "Galatea config"
                    );
                    break;
                case "--evidence" when evidencePath is null
                    && index + 1 < args.Length:
                    evidencePath = RequireEvidenceFilePath(args[++index]);
                    break;
                case "--apply" when !apply:
                    apply = true;
                    break;
                default:
                    throw Usage();
            }
        }
        if (configPath is null || evidencePath is null) {
            throw Usage();
        }
        return new(configPath, evidencePath, apply);
    }

    private static string RequireAbsoluteNoFollowFile(
        string path,
        string kind
    ) {
        if (string.IsNullOrWhiteSpace(path)
            || !Path.IsPathFullyQualified(path)) {
            throw new InvalidDataException(
                $"{kind} path must be absolute."
            );
        }
        string canonical = Path.GetFullPath(path);
        GalateaStrictConfigReader.RequireExistingRegularFileNoFollow(
            canonical,
            kind
        );
        return canonical;
    }

    internal static string RequireEvidenceFilePath(string path) {
        if (!OperatingSystem.IsLinux()) {
            throw new PlatformNotSupportedException(
                "Galatea Codex completion recovery requires Linux "
                + "no-follow file and Unix permission semantics."
            );
        }
        const UnixFileMode allowed = UnixFileMode.UserRead
            | UnixFileMode.UserWrite;
        string canonical = RequireAbsoluteNoFollowFile(
            path,
            "Galatea Codex completion recovery evidence"
        );
        UnixFileMode mode = File.GetUnixFileMode(canonical);
        if ((mode & UnixFileMode.UserRead) == 0 || (mode & ~allowed) != 0) {
            throw new InvalidDataException(
                "Galatea Codex completion recovery evidence permissions "
                + "must be exactly owner-readable, with optional owner-write "
                + "(0400 or 0600)."
            );
        }
        return canonical;
    }

    private static RecoveryClassification Classify(
        GalateaDelegationStateSnapshot snapshot,
        GalateaCodexCompletionRecoveryEvidence evidence
    ) {
        GalateaOutboundMailSnapshot mail = ReadEvidenceMail(
            snapshot,
            evidence.DispatchId
        );
        if (mail.State is GalateaDurableMailState.TerminalCompleted
            or GalateaDurableMailState.TerminalFailed) {
            GalateaReplyNoticeSnapshot? notice = snapshot.Notices
                .SingleOrDefault(value => string.Equals(
                    value.DispatchId,
                    evidence.DispatchId,
                    StringComparison.Ordinal
                ));
            bool exact = mail.State
                    == GalateaDurableMailState.TerminalCompleted
                && notice is not null
                && notice.Kind == GalateaReplyNoticeKind.Reply
                && string.Equals(
                    notice.Body,
                    evidence.Final,
                    StringComparison.Ordinal
                )
                && notice.Stage is null
                && notice.Code is null
                && string.Equals(
                    mail.AcceptedThreadId,
                    evidence.ThreadId,
                    StringComparison.Ordinal
                )
                && string.Equals(
                    mail.AcceptedTurnId,
                    evidence.TurnId,
                    StringComparison.Ordinal
                )
                && string.Equals(
                    mail.TerminalFinalSha256,
                    evidence.FinalSha256,
                    StringComparison.Ordinal
                )
                && snapshot.Route.State
                    == GalateaDelegationRouteState.Bound
                && string.Equals(
                    snapshot.Route.ThreadId,
                    evidence.ThreadId,
                    StringComparison.Ordinal
                )
                && !string.Equals(
                    snapshot.Route.ActiveDispatchId,
                    evidence.DispatchId,
                    StringComparison.Ordinal
                );
            if (exact) { return RecoveryClassification.AlreadyApplied; }
            throw new InvalidDataException(
                "Recovery evidence conflicts with an existing terminal state."
            );
        }

        if (snapshot.Route.State != GalateaDelegationRouteState.Bound
            || !string.Equals(
                snapshot.Route.ActiveDispatchId,
                evidence.DispatchId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                snapshot.Route.ThreadId,
                evidence.ThreadId,
                StringComparison.Ordinal
            )
            || mail.State != GalateaDurableMailState.Accepted
            || !mail.IsCodexRouted
            || !string.Equals(
                mail.RequestedThreadId,
                evidence.ThreadId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                mail.AcceptedThreadId,
                evidence.ThreadId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                mail.AcceptedTurnId,
                evidence.TurnId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                mail.ReconcileLastCode,
                RequiredReconcileCode,
                StringComparison.Ordinal
            )
            || mail.ReconcileAttemptCount < 1
            || snapshot.Notices.Any(value => string.Equals(
                value.DispatchId,
                evidence.DispatchId,
                StringComparison.Ordinal
            ))) {
            throw new InvalidDataException(
                "Recovery requires the exact active Accepted dispatch with "
                + "ACCEPTED_TURN_NOT_VISIBLE and no existing notice."
            );
        }
        string task = mail.Body
            ?? throw new InvalidDataException(
                "The active Accepted dispatch has no durable task body."
            );
        byte[] taskBytes;
        try {
            taskBytes = StrictUtf8.GetBytes(task);
        }
        catch (EncoderFallbackException exception) {
            throw new InvalidDataException(
                "The active Accepted dispatch task is not strict Unicode.",
                exception
            );
        }
        if (taskBytes.Length != evidence.TaskUtf8Bytes
            || !string.Equals(
                Sha256(taskBytes),
                evidence.TaskSha256,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                "Recovery evidence task identity does not match durable mail."
            );
        }
        if (evidence.FinalUtf8Bytes > snapshot.Limits.MaximumReplyUtf8Bytes) {
            throw new InvalidDataException(
                "Recovery evidence final exceeds the durable reply limit."
            );
        }
        return RecoveryClassification.Ready;
    }

    private static void RequireExactPostState(
        GalateaDelegationStateSnapshot before,
        GalateaDelegationStateSnapshot after,
        GalateaCodexCompletionRecoveryEvidence evidence
    ) {
        GalateaOutboundMailSnapshot beforeMail = ReadEvidenceMail(
            before,
            evidence.DispatchId
        );
        GalateaOutboundMailSnapshot afterMail = ReadEvidenceMail(
            after,
            evidence.DispatchId
        );
        GalateaOutboundMailSnapshot expectedMail = beforeMail with {
            Body = null,
            EvidenceQuote = null,
            State = GalateaDurableMailState.TerminalCompleted,
            AcceptedThreadId = evidence.ThreadId,
            AcceptedTurnId = evidence.TurnId,
            TerminalFinalSha256 = evidence.FinalSha256,
            TerminalStage = null,
            TerminalCode = null,
            ReconcileAttemptCount = 0,
            ReconcileLastCode = null,
            NextReconcileAtUnixTimeMilliseconds = null,
            Revision = checked(beforeMail.Revision + 1)
        };
        var expectedNotice = new GalateaReplyNoticeSnapshot(
            evidence.DispatchId,
            evidence.DispatchId,
            GalateaReplyNoticeKind.Reply,
            evidence.Final,
            Stage: null,
            Code: null,
            before.NextCompletionSequence,
            GalateaReplyNoticeState.Ready,
            ConsumedActionAddress: null,
            Revision: 0
        );
        GalateaRouteBindingSnapshot expectedRoute = before.Route with {
            ActiveDispatchId = null,
            Revision = checked(before.Route.Revision + 1)
        };
        bool exact = after.Owner == before.Owner
            && after.Baseline == before.Baseline
            && after.Limits == before.Limits
            && after.StoreRevision == checked(before.StoreRevision + 1)
            && after.NextCompletionSequence
                == checked(before.NextCompletionSequence + 1)
            && after.Route == expectedRoute
            && after.Captures.SequenceEqual(before.Captures)
            && ActiveLeasesEqual(after.ActiveLease, before.ActiveLease)
            && afterMail == expectedMail
            && after.Mails.Where(value => !string.Equals(
                    value.DispatchId,
                    evidence.DispatchId,
                    StringComparison.Ordinal
                )).SequenceEqual(before.Mails.Where(value => !string.Equals(
                    value.DispatchId,
                    evidence.DispatchId,
                    StringComparison.Ordinal
                )))
            && after.Notices.Count == before.Notices.Count + 1
            && after.Notices.Take(before.Notices.Count)
                .SequenceEqual(before.Notices)
            && after.Notices[^1] == expectedNotice;
        if (!exact) {
            throw new InvalidDataException(
                "Recovery transaction post-readback was not the exact "
                + "Accepted-to-completed transition."
            );
        }
    }

    private static bool ActiveLeasesEqual(
        GalateaReplyLeaseSnapshot? left,
        GalateaReplyLeaseSnapshot? right
    ) {
        if (left is null || right is null) { return left is null && right is null; }
        return string.Equals(left.LeaseId, right.LeaseId,
                StringComparison.Ordinal)
            && left.State == right.State
            && string.Equals(left.PlayerText, right.PlayerText,
                StringComparison.Ordinal)
            && string.Equals(left.ExpectedSessionHead,
                right.ExpectedSessionHead, StringComparison.Ordinal)
            && string.Equals(left.RenderedObservation,
                right.RenderedObservation, StringComparison.Ordinal)
            && left.ObservationUtf8Bytes == right.ObservationUtf8Bytes
            && string.Equals(left.ObservationSha256,
                right.ObservationSha256, StringComparison.Ordinal)
            && left.CompletionFrontier == right.CompletionFrontier
            && string.Equals(left.ObservationAddress,
                right.ObservationAddress, StringComparison.Ordinal)
            && left.Revision == right.Revision
            && left.NoticeIds.SequenceEqual(right.NoticeIds);
    }

    private static GalateaDelegationStoreOwner CreateOwner(
        GalateaUserConfig user,
        GalateaDelegateRouteConfig route
    ) => new(
        user.UserId,
        GalateaDelegationSupervisor.CreateSessionRepositoryId(user.SessionDir),
        GalateaDelegationDurableContract.CreateRoutePolicyFingerprint(route)
    );

    private static void ValidateEvidence(
        GalateaCodexCompletionRecoveryEvidence evidence
    ) {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.Version != EvidenceVersion
            || !string.Equals(
                evidence.Kind,
                EvidenceKind,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                "Recovery evidence requires exact v=1 and "
                + "kind=codex-turn-completed."
            );
        }
        RequireBoundedIdentity(evidence.UserId, "userId");
        RequireBoundedIdentity(evidence.DispatchId, "dispatchId");
        RequireBoundedIdentity(evidence.ThreadId, "threadId");
        RequireBoundedIdentity(evidence.TurnId, "turnId");
        if (evidence.DispatchId.Length != 68
            || !evidence.DispatchId.StartsWith("gd1-", StringComparison.Ordinal)
            || evidence.DispatchId[4..].Any(static character => character is not (
                >= '0' and <= '9' or >= 'a' and <= 'f'))) {
            throw new InvalidDataException(
                "Recovery evidence dispatchId is not canonical."
            );
        }
        if (evidence.TaskUtf8Bytes is < 1
                or > GalateaDelegationStateBounds.MaximumTaskUtf8Bytes
            || evidence.FinalUtf8Bytes is < 1
                or > GalateaDelegationStateBounds.MaximumTaskUtf8Bytes
            || string.IsNullOrWhiteSpace(evidence.Final)) {
            throw new InvalidDataException(
                "Recovery evidence byte counts or final body are invalid."
            );
        }
        RequireSha256(evidence.TaskSha256, "taskSha256");
        RequireSha256(evidence.FinalSha256, "finalSha256");
        byte[] finalBytes;
        try {
            finalBytes = StrictUtf8.GetBytes(evidence.Final);
        }
        catch (EncoderFallbackException exception) {
            throw new InvalidDataException(
                "Recovery evidence final is not strict Unicode.",
                exception
            );
        }
        if (finalBytes.Length != evidence.FinalUtf8Bytes
            || !string.Equals(
                Sha256(finalBytes),
                evidence.FinalSha256,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                "Recovery evidence final byte count or SHA-256 is invalid."
            );
        }
    }

    private static void RequireBoundedIdentity(string? value, string field) {
        if (string.IsNullOrWhiteSpace(value)
            || GalateaMailboxText.ContainsHeaderLineBreak(value)) {
            throw new InvalidDataException(
                $"Recovery evidence {field} is blank or contains a line break."
            );
        }
        try {
            if (StrictUtf8.GetByteCount(value)
                    > GalateaDelegationStateBounds.MaximumIdentityUtf8Bytes) {
                throw new InvalidDataException(
                    $"Recovery evidence {field} exceeds its byte limit."
                );
            }
        }
        catch (EncoderFallbackException exception) {
            throw new InvalidDataException(
                $"Recovery evidence {field} is not strict Unicode.",
                exception
            );
        }
    }

    private static void RequireSha256(string? value, string field) {
        if (value is not { Length: 64 }
            || value.Any(static character => character is not (
                >= '0' and <= '9' or >= 'a' and <= 'f'))) {
            throw new InvalidDataException(
                $"Recovery evidence {field} must be canonical lowercase SHA-256."
            );
        }
    }

    private static GalateaOutboundMailSnapshot ReadEvidenceMail(
        GalateaDelegationStateSnapshot snapshot,
        string dispatchId
    ) => snapshot.Mails.SingleOrDefault(value => string.Equals(
            value.DispatchId,
            dispatchId,
            StringComparison.Ordinal
        ))
        ?? throw new InvalidDataException(
            "Recovery evidence dispatchId was not found in the durable store."
        );

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void RequireToken(
        ref Utf8JsonReader reader,
        JsonTokenType token
    ) {
        if (!reader.Read() || reader.TokenType != token) {
            throw InvalidEvidenceShape();
        }
    }

    private static int ReadInt32(ref Utf8JsonReader reader) =>
        reader.TokenType == JsonTokenType.Number
            && reader.TryGetInt32(out int value)
                ? value
                : throw InvalidEvidenceShape();

    private static string ReadString(ref Utf8JsonReader reader) =>
        reader.TokenType == JsonTokenType.String
            ? reader.GetString() ?? throw InvalidEvidenceShape()
            : throw InvalidEvidenceShape();

    private static InvalidDataException InvalidEvidenceShape() => new(
        "Recovery evidence must be one exact closed V1 JSON object."
    );

    private static InvalidDataException Usage() => new(
        "Usage: Galatea.Server operator recover-codex-completed "
        + "--config <absolute-path> --evidence <absolute-path> [--apply]. "
        + "The default is dry-run."
    );

    private sealed record Invocation(
        string ConfigPath,
        string EvidencePath,
        bool Apply
    );

    private enum RecoveryClassification {
        Ready,
        AlreadyApplied
    }
}

internal sealed record GalateaCodexCompletionRecoveryEvidence(
    int Version,
    string Kind,
    string UserId,
    string DispatchId,
    string ThreadId,
    string TurnId,
    int TaskUtf8Bytes,
    string TaskSha256,
    int FinalUtf8Bytes,
    string FinalSha256,
    string Final
);

internal enum GalateaCodexCompletionRecoveryOutcome {
    DryRunReady,
    Applied,
    AlreadyApplied
}

internal sealed record GalateaCodexCompletionRecoveryResult(
    GalateaCodexCompletionRecoveryOutcome Outcome,
    long StoreRevision
);
