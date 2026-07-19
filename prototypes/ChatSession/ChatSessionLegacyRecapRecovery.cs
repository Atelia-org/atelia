using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.StateJournal;

namespace Atelia.ChatSession;

public enum ChatSessionRecapRecoveryConfidence {
    High,
    Medium,
    Low,
    Unresolved
}

public enum ChatSessionCommitAttributionKind {
    InitialState,
    ModelTurn,
    Compaction,
    RevertTurn,
    UpdateSystemPrompt,
    UpdateContextHeader,
    RedundantSave,
    Other
}

public enum ChatSessionCommitAttributionSource {
    LegacyInferred,
    Explicit
}

public sealed record ChatSessionCommitAttribution(
    ChatSessionCommitAttributionKind Kind,
    string Reason,
    ChatSessionCommitAttributionSource Source = ChatSessionCommitAttributionSource.LegacyInferred
);

public sealed record ChatSessionLegacyCommitTimelineEntry(
    int Ordinal,
    string Commit,
    BranchHistoryAddressSource Source,
    int MessageCount,
    int? MessageCountDeltaFromPrevious,
    ChatSessionCommitAttribution Attribution,
    IReadOnlyList<string> Oldest3,
    IReadOnlyList<string> Newest3
);

public sealed record ChatSessionLegacyRecapRecoveryFinding(
    string OldHead,
    string NewHead,
    int? RecapIndex,
    int SourceStartIndex,
    int SourceEndExclusive,
    int SourceMessageCountBefore,
    int SuffixMatchCount,
    ChatSessionRecapRecoveryConfidence Confidence,
    string Reason
);

public sealed record ChatSessionLegacyRecapRecoveryReport(
    IReadOnlyList<ChatSessionLegacyCommitTimelineEntry> Timeline,
    IReadOnlyList<ChatSessionLegacyRecapRecoveryFinding> Findings,
    IReadOnlyList<string> Warnings
);

public static class ChatSessionLegacyRecapRecovery {
    private const int SignaturePreviewEdgeLength = 48;
    private const int SignatureHashBytes = 6;

    public static ChatSessionLegacyRecapRecoveryReport Analyze(string repoDir, string branchName = "main") {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);

        using var repo = Repository.Open(repoDir).Unwrap();
        var history = RepositoryHistoryReader.EnumerateBranchEffectiveCommitAddresses(repo, branchName);
        var warnings = new List<string>(history.Warnings);
        var snapshots = new List<CommitSnapshot>(history.Addresses.Count);

        for (int addressIndex = history.Addresses.Count - 1; addressIndex >= 0; addressIndex--) {
            var historyAddress = history.Addresses[addressIndex];
            var rootResult = repo.LoadRootAtCommit(historyAddress.Address);
            if (rootResult.IsFailure) {
                warnings.Add($"Skipped commit {historyAddress.Address}: {rootResult.Error!.Message}");
                continue;
            }

            if (rootResult.Value is not DurableDict<string> root) {
                warnings.Add($"Skipped commit {historyAddress.Address}: graph root is not a ChatSession root dict.");
                continue;
            }

            try {
                ChatSessionStorageSchema.ValidateRoot(root);
                var records = ReadRecords(root);
                var systemPrompt = ReadSystemPrompt(root);
                snapshots.Add(new CommitSnapshot(historyAddress, records, systemPrompt));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException) {
                warnings.Add($"Skipped commit {historyAddress.Address}: {ex.Message}");
            }
        }

        var findings = BuildFindings(snapshots);
        var timeline = BuildTimeline(snapshots, findings);
        return new ChatSessionLegacyRecapRecoveryReport(timeline, findings, warnings);
    }

    public static string FormatText(ChatSessionLegacyRecapRecoveryReport report) {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("# ChatSession Legacy Recap Recovery Report");
        builder.AppendLine();
        builder.AppendLine("## Timeline");
        for (int entryIndex = 0; entryIndex < report.Timeline.Count; entryIndex++) {
            var entry = report.Timeline[entryIndex];
            builder.Append(CultureInfo.InvariantCulture, $"### {entry.Ordinal:D5} {entry.Commit}");
            builder.AppendLine();
            builder.Append(CultureInfo.InvariantCulture, $"- source: {entry.Source}");
            builder.AppendLine();
            builder.Append(CultureInfo.InvariantCulture, $"- messageCount: {entry.MessageCount}");
            builder.AppendLine();
            builder.Append(CultureInfo.InvariantCulture, $"- messageCountDeltaFromPrevious: {FormatDelta(entry.MessageCountDeltaFromPrevious)}");
            builder.AppendLine();
            builder.Append(CultureInfo.InvariantCulture, $"- attribution: {entry.Attribution.Kind}");
            builder.AppendLine();
            builder.Append(CultureInfo.InvariantCulture, $"- attributionReason: {entry.Attribution.Reason}");
            builder.AppendLine();
            AppendSignatureList(builder, "oldest3", entry.Oldest3);
            AppendSignatureList(builder, "newest3", entry.Newest3);
            builder.AppendLine();
        }

        builder.AppendLine("## Findings");
        if (report.Findings.Count == 0) {
            builder.AppendLine("- none");
        }
        else {
            for (int findingIndex = 0; findingIndex < report.Findings.Count; findingIndex++) {
                var finding = report.Findings[findingIndex];
                builder.Append(CultureInfo.InvariantCulture, $"### finding {findingIndex:D3}");
                builder.AppendLine();
                builder.Append(CultureInfo.InvariantCulture, $"- oldHead: {finding.OldHead}");
                builder.AppendLine();
                builder.Append(CultureInfo.InvariantCulture, $"- newHead: {finding.NewHead}");
                builder.AppendLine();
                builder.Append(CultureInfo.InvariantCulture, $"- recapIndex: {finding.RecapIndex?.ToString(CultureInfo.InvariantCulture) ?? "<none>"}");
                builder.AppendLine();
                builder.Append(CultureInfo.InvariantCulture, $"- sourceRange: [{finding.SourceStartIndex}, {finding.SourceEndExclusive})");
                builder.AppendLine();
                builder.Append(CultureInfo.InvariantCulture, $"- sourceMessageCountBefore: {finding.SourceMessageCountBefore}");
                builder.AppendLine();
                builder.Append(CultureInfo.InvariantCulture, $"- suffixMatchCount: {finding.SuffixMatchCount}");
                builder.AppendLine();
                builder.Append(CultureInfo.InvariantCulture, $"- confidence: {finding.Confidence}");
                builder.AppendLine();
                builder.Append(CultureInfo.InvariantCulture, $"- reason: {finding.Reason}");
                builder.AppendLine();
                builder.AppendLine();
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Warnings");
        if (report.Warnings.Count == 0) {
            builder.AppendLine("- none");
        }
        else {
            for (int warningIndex = 0; warningIndex < report.Warnings.Count; warningIndex++) {
                builder.Append("- ");
                builder.AppendLine(report.Warnings[warningIndex]);
            }
        }

        return builder.ToString();
    }

    private static IReadOnlyList<HistoryRecordSnapshot> ReadRecords(DurableDict<string> root) {
        var messages = ChatSessionStorageSchema.GetMessages(root);
        var records = new List<HistoryRecordSnapshot>(messages.Count);
        for (int messageIndex = 0; messageIndex < messages.Count; messageIndex++) {
            if (!messages.TryGetAt<DurableDict<string>>(messageIndex, out var record) || record is null) { throw new InvalidDataException($"Message record at index {messageIndex} is not a durable message dict."); }

            var kind = MessageRecord.GetKind(record);
            var message = MessageRecord.ToHistoryMessage(record);
            records.Add(new HistoryRecordSnapshot(kind, message, BuildSignature(kind, message), BuildCanonicalText(message)));
        }

        return records;
    }

    private static IReadOnlyList<ChatSessionLegacyCommitTimelineEntry> BuildTimeline(
        IReadOnlyList<CommitSnapshot> snapshots,
        IReadOnlyList<ChatSessionLegacyRecapRecoveryFinding> findings
    ) {
        var findingsByNewHead = findings.ToDictionary(static finding => finding.NewHead, StringComparer.Ordinal);
        var result = new ChatSessionLegacyCommitTimelineEntry[snapshots.Count];
        for (int snapshotIndex = 0; snapshotIndex < snapshots.Count; snapshotIndex++) {
            var snapshot = snapshots[snapshotIndex];
            int? delta = snapshotIndex == 0 ? null : snapshot.Records.Count - snapshots[snapshotIndex - 1].Records.Count;
            var attribution = ClassifyCommit(snapshots, snapshotIndex, findingsByNewHead);
            result[snapshotIndex] = new ChatSessionLegacyCommitTimelineEntry(
                Ordinal: snapshotIndex,
                Commit: snapshot.Address.Address.ToString(),
                Source: snapshot.Address.Source,
                MessageCount: snapshot.Records.Count,
                MessageCountDeltaFromPrevious: delta,
                Attribution: attribution,
                Oldest3: TakeFirstSignatures(snapshot.Records),
                Newest3: TakeLastSignatures(snapshot.Records)
            );
        }

        return result;
    }

    private static ChatSessionCommitAttribution ClassifyCommit(
        IReadOnlyList<CommitSnapshot> snapshots,
        int snapshotIndex,
        IReadOnlyDictionary<string, ChatSessionLegacyRecapRecoveryFinding> findingsByNewHead
    ) {
        var currentSnapshot = snapshots[snapshotIndex];
        if (TryReadExplicitAttribution(currentSnapshot, out var explicitAttribution)) { return explicitAttribution; }

        if (snapshotIndex == 0) { return new ChatSessionCommitAttribution(ChatSessionCommitAttributionKind.InitialState, "oldest effective commit in the analyzed branch history"); }

        var currentHead = currentSnapshot.Address.Address.ToString();
        if (findingsByNewHead.TryGetValue(currentHead, out var finding)) {
            return finding.Confidence == ChatSessionRecapRecoveryConfidence.Unresolved
                ? new ChatSessionCommitAttribution(ChatSessionCommitAttributionKind.Other, finding.Reason)
                : new ChatSessionCommitAttribution(ChatSessionCommitAttributionKind.Compaction, finding.Reason);
        }

        var previousSnapshot = snapshots[snapshotIndex - 1];
        var delta = currentSnapshot.Records.Count - previousSnapshot.Records.Count;
        if (HaveSameMessageSequence(previousSnapshot.Records, currentSnapshot.Records)) {
            return string.Equals(previousSnapshot.SystemPrompt, currentSnapshot.SystemPrompt, StringComparison.Ordinal)
                ? new ChatSessionCommitAttribution(ChatSessionCommitAttributionKind.RedundantSave, "message sequence and system prompt are identical to the previous commit")
                : new ChatSessionCommitAttribution(ChatSessionCommitAttributionKind.UpdateSystemPrompt, "message sequence is unchanged and system prompt changed");
        }

        if (delta > 0 && IsPrefix(previousSnapshot.Records, currentSnapshot.Records)) {
            var added = currentSnapshot.Records.Skip(previousSnapshot.Records.Count).ToArray();
            if (added.Length == 2
                && string.Equals(added[0].Kind, MessageRecord.KindObservation, StringComparison.Ordinal)
                && string.Equals(added[1].Kind, MessageRecord.KindAction, StringComparison.Ordinal)) { return new ChatSessionCommitAttribution(ChatSessionCommitAttributionKind.ModelTurn, "previous messages are preserved and the commit appends one observation/action turn"); }

            return new ChatSessionCommitAttribution(ChatSessionCommitAttributionKind.Other, "previous messages are preserved, but appended records do not match one normal model turn");
        }

        if (delta < 0 && IsPrefix(currentSnapshot.Records, previousSnapshot.Records)) {
            var removedCount = previousSnapshot.Records.Count - currentSnapshot.Records.Count;
            return removedCount >= 2
                ? new ChatSessionCommitAttribution(ChatSessionCommitAttributionKind.RevertTurn, "new snapshot is a prefix of the previous snapshot, indicating a tail turn removal")
                : new ChatSessionCommitAttribution(ChatSessionCommitAttributionKind.Other, "new snapshot removed a tail record, but not enough records for a completed turn");
        }

        return new ChatSessionCommitAttribution(ChatSessionCommitAttributionKind.Other, "message sequence changed in a way that is not classified by the conservative attribution rules");
    }

    private static bool TryReadExplicitAttribution(
        CommitSnapshot snapshot,
        out ChatSessionCommitAttribution attribution
    ) {
        attribution = null!;
        if (!ChatSessionCommitMetadata.TryDecodeNote(snapshot.Address.Note, out var metadata)) { return false; }

        attribution = new ChatSessionCommitAttribution(
            ToAttributionKind(metadata.Kind),
            metadata.Reason ?? "explicit chat session commit metadata",
            ChatSessionCommitAttributionSource.Explicit
        );
        return true;
    }

    private static ChatSessionCommitAttributionKind ToAttributionKind(ChatSessionCommitKind kind)
        => kind switch {
            ChatSessionCommitKind.InitialState => ChatSessionCommitAttributionKind.InitialState,
            ChatSessionCommitKind.ModelTurn => ChatSessionCommitAttributionKind.ModelTurn,
            ChatSessionCommitKind.Compaction => ChatSessionCommitAttributionKind.Compaction,
            ChatSessionCommitKind.RevertTurn => ChatSessionCommitAttributionKind.RevertTurn,
            ChatSessionCommitKind.UpdateSystemPrompt => ChatSessionCommitAttributionKind.UpdateSystemPrompt,
            ChatSessionCommitKind.UpdateContextHeader => ChatSessionCommitAttributionKind.UpdateContextHeader,
            ChatSessionCommitKind.RedundantSave => ChatSessionCommitAttributionKind.RedundantSave,
            _ => ChatSessionCommitAttributionKind.Other
        };

    private static bool IsPrefix(
        IReadOnlyList<HistoryRecordSnapshot> prefix,
        IReadOnlyList<HistoryRecordSnapshot> records
    ) {
        if (prefix.Count > records.Count) { return false; }
        for (int recordIndex = 0; recordIndex < prefix.Count; recordIndex++) {
            if (!string.Equals(prefix[recordIndex].Signature, records[recordIndex].Signature, StringComparison.Ordinal)) { return false; }
        }

        return true;
    }

    private static bool HaveSameMessageSequence(
        IReadOnlyList<HistoryRecordSnapshot> left,
        IReadOnlyList<HistoryRecordSnapshot> right
    )
        => left.Count == right.Count && IsPrefix(left, right);

    private static string ReadSystemPrompt(DurableDict<string> root) {
        if (root.Get<string>(ChatSessionStorageSchema.KeySystemPrompt, out var systemPrompt) != GetIssue.None || systemPrompt is null) { throw new InvalidDataException("Root is missing systemPrompt."); }
        return systemPrompt;
    }

    private static IReadOnlyList<ChatSessionLegacyRecapRecoveryFinding> BuildFindings(IReadOnlyList<CommitSnapshot> snapshots) {
        var findings = new List<ChatSessionLegacyRecapRecoveryFinding>();
        for (int snapshotIndex = 1; snapshotIndex < snapshots.Count; snapshotIndex++) {
            var oldSnapshot = snapshots[snapshotIndex - 1];
            var newSnapshot = snapshots[snapshotIndex];
            var countDelta = newSnapshot.Records.Count - oldSnapshot.Records.Count;
            if (countDelta >= 0) { continue; }
            if (IsPrefix(newSnapshot.Records, oldSnapshot.Records)) { continue; }

            var recapIndex = FindLeadingRecapIndex(newSnapshot.Records);
            if (recapIndex is null) {
                findings.Add(
                    new ChatSessionLegacyRecapRecoveryFinding(
                        oldSnapshot.Address.Address.ToString(),
                        newSnapshot.Address.Address.ToString(),
                        RecapIndex: null,
                        SourceStartIndex: 0,
                        SourceEndExclusive: oldSnapshot.Records.Count,
                        SourceMessageCountBefore: oldSnapshot.Records.Count,
                        SuffixMatchCount: CountMatchingSuffix(oldSnapshot.Records, newSnapshot.Records),
                        Confidence: ChatSessionRecapRecoveryConfidence.Unresolved,
                        Reason: "message count shrank, but the new snapshot has no leading recap candidate"
                    )
                );
                continue;
            }

            var suffixMatchCount = CountMatchingSuffix(oldSnapshot.Records, newSnapshot.Records);
            var sourceEndExclusive = oldSnapshot.Records.Count - suffixMatchCount;
            var newPrefixLength = newSnapshot.Records.Count - suffixMatchCount;
            var confidence = suffixMatchCount > 0 && recapIndex.Value < newPrefixLength
                ? ChatSessionRecapRecoveryConfidence.High
                : ChatSessionRecapRecoveryConfidence.Unresolved;
            var reason = confidence == ChatSessionRecapRecoveryConfidence.High
                ? "new snapshot is shorter, contains leading recap, and preserves a matching suffix"
                : "new snapshot is shorter and contains recap, but suffix match is insufficient";

            findings.Add(
                new ChatSessionLegacyRecapRecoveryFinding(
                    oldSnapshot.Address.Address.ToString(),
                    newSnapshot.Address.Address.ToString(),
                    recapIndex,
                    SourceStartIndex: 0,
                    SourceEndExclusive: sourceEndExclusive,
                    SourceMessageCountBefore: oldSnapshot.Records.Count,
                    SuffixMatchCount: suffixMatchCount,
                    Confidence: confidence,
                    Reason: reason
                )
            );
        }

        return findings;
    }

    private static int? FindLeadingRecapIndex(IReadOnlyList<HistoryRecordSnapshot> records) {
        for (int recordIndex = 0; recordIndex < records.Count; recordIndex++) {
            if (records[recordIndex].Message is RecapMessage) { return recordIndex; }
            if (records[recordIndex].Kind != MessageRecord.KindContextHeader) { return null; }
        }

        return null;
    }

    private static int CountMatchingSuffix(
        IReadOnlyList<HistoryRecordSnapshot> oldRecords,
        IReadOnlyList<HistoryRecordSnapshot> newRecords
    ) {
        var max = Math.Min(oldRecords.Count, newRecords.Count);
        var count = 0;
        while (count < max) {
            var oldRecord = oldRecords[oldRecords.Count - 1 - count];
            var newRecord = newRecords[newRecords.Count - 1 - count];
            if (!string.Equals(oldRecord.Signature, newRecord.Signature, StringComparison.Ordinal)) { break; }
            count++;
        }

        return count;
    }

    private static IReadOnlyList<string> TakeFirstSignatures(IReadOnlyList<HistoryRecordSnapshot> records) {
        var count = Math.Min(3, records.Count);
        var result = new string[count];
        for (int recordIndex = 0; recordIndex < count; recordIndex++) { result[recordIndex] = records[recordIndex].Signature; }
        return result;
    }

    private static IReadOnlyList<string> TakeLastSignatures(IReadOnlyList<HistoryRecordSnapshot> records) {
        var count = Math.Min(3, records.Count);
        var result = new string[count];
        var start = records.Count - count;
        for (int recordIndex = 0; recordIndex < count; recordIndex++) { result[recordIndex] = records[start + recordIndex].Signature; }
        return result;
    }

    private static string BuildSignature(string kind, IHistoryMessage message) {
        var canonical = BuildCanonicalText(message);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)).AsSpan(0, SignatureHashBytes)).ToLowerInvariant();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{kind}:{hash}:{BuildPreview(canonical)}"
        );
    }

    private static string BuildCanonicalText(IHistoryMessage message) {
        return message switch {
            ContextHeader header => BuildContextHeaderText(header),
            ToolResultsMessage toolResults => BuildToolResultsText(toolResults),
            ActionMessage action => BuildActionText(action),
            RecapMessage recap => recap.Content ?? string.Empty,
            ObservationMessage observation => observation.Content ?? string.Empty,
            _ => string.Empty
        };
    }

    private static string BuildContextHeaderText(ContextHeader header) {
        var builder = new StringBuilder();
        AppendLabeled(builder, "systemPromptFragment", header.SystemPromptFragment);
        AppendLabeled(builder, "userMessage", header.ObservationMessage);
        if (header.ActionMessage is not null) { AppendLabeled(builder, "assistantMessage", BuildActionText(header.ActionMessage)); }
        return builder.ToString();
    }

    private static string BuildActionText(ActionMessage action) {
        var builder = new StringBuilder();
        builder.Append(action.GetFlattenedText());
        var toolCalls = action.ToolCalls;
        for (int callIndex = 0; callIndex < toolCalls.Count; callIndex++) {
            var call = toolCalls[callIndex];
            builder.Append(CultureInfo.InvariantCulture, $"\n[toolCall {callIndex}] {call.ToolName} {call.ToolCallId} {call.RawArgumentsJson}");
        }

        return builder.ToString();
    }

    private static string BuildToolResultsText(ToolResultsMessage message) {
        var builder = new StringBuilder();
        AppendLabeled(builder, "content", message.Content);
        for (int resultIndex = 0; resultIndex < message.Results.Count; resultIndex++) {
            var result = message.Results[resultIndex];
            builder.Append(CultureInfo.InvariantCulture, $"[toolResult {resultIndex}] {result.ToolName} {result.ToolCallId} {result.Status} {result.GetFlattenedText()}");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static void AppendLabeled(StringBuilder builder, string label, string? value) {
        if (string.IsNullOrEmpty(value)) { return; }
        builder.Append(label);
        builder.Append(": ");
        builder.AppendLine(value);
    }

    private static string BuildPreview(string text) {
        var escaped = EscapeForSingleLine(text);
        if (escaped.Length <= SignaturePreviewEdgeLength * 2 + 5) { return escaped; }
        return string.Concat(
            escaped.AsSpan(0, SignaturePreviewEdgeLength),
            " ... ",
            escaped.AsSpan(escaped.Length - SignaturePreviewEdgeLength, SignaturePreviewEdgeLength)
        );
    }

    private static string EscapeForSingleLine(string text)
        => text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private static void AppendSignatureList(StringBuilder builder, string label, IReadOnlyList<string> signatures) {
        builder.Append("- ");
        builder.Append(label);
        builder.AppendLine(":");
        if (signatures.Count == 0) {
            builder.AppendLine("  - <none>");
            return;
        }

        for (int signatureIndex = 0; signatureIndex < signatures.Count; signatureIndex++) {
            builder.Append("  - ");
            builder.AppendLine(signatures[signatureIndex]);
        }
    }

    private static string FormatDelta(int? delta)
        => delta is null ? "<none>" : delta.Value.ToString("+0;-0;0", CultureInfo.InvariantCulture);

    private sealed record CommitSnapshot(
        BranchHistoryAddress Address,
        IReadOnlyList<HistoryRecordSnapshot> Records,
        string SystemPrompt
    );

    private sealed record HistoryRecordSnapshot(
        string Kind,
        IHistoryMessage Message,
        string Signature,
        string CanonicalText
    );
}
