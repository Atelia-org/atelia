using System.Security.Cryptography;
using System.Text.Json;
using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;

namespace Atelia.SessionJournal.RecapGrid.Control;

public static class RecapGridControlMaintenance {
    private const string BackupStateFileName = "control.json";
    private const string BackupManifestFileName = "manifest.json";

    public static RecapGridControlInspectResult Inspect(
        string repositoryPath,
        RefId refId
    ) {
        AdminScopeResult scopeResult = OpenScope(repositoryPath, refId);
        switch (scopeResult) {
            case AdminScopeResult.TimelineAbsent:
                return new RecapGridControlInspectResult.TimelineAbsent();
            case AdminScopeResult.Busy:
                return new RecapGridControlInspectResult.Busy();
            case AdminScopeResult.TimelineUnsupportedSchema schema:
                return new RecapGridControlInspectResult
                    .TimelineUnsupportedSchema(schema.SchemaVersion);
            case AdminScopeResult.Invalid invalid:
                return new RecapGridControlInspectResult.Invalid(
                    invalid.Code,
                    invalid.Detail
                );
            default:
                return new RecapGridControlInspectResult.Invalid(
                    "ControlInspectOutcomeInvalid",
                    "The Control scope returned an unknown outcome."
                );
            case AdminScopeResult.Opened scope:
                using (scope.TimelineHandle) {
                    try {
                        if (!ControlDurableFiles.StateExists(scope.Paths)) {
                            return new RecapGridControlInspectResult.Absent();
                        }
                        using FileStream lease = ControlDurableFiles
                            .AcquireSharedLifetime(scope.Paths);
                        ControlState state = ReadState(scope.Paths);
                        return new RecapGridControlInspectResult.Available(
                            state.Snapshot()
                        );
                    }
                    catch (ControlUnsupportedSchemaException schema) {
                        return new RecapGridControlInspectResult
                            .UnsupportedSchema(schema.Version);
                    }
                    catch (ControlBusyException) {
                        return new RecapGridControlInspectResult.Busy();
                    }
                    catch (Exception exception) {
                        (string code, string detail) =
                            ControlError.Invalid(exception);
                        return new RecapGridControlInspectResult.Invalid(
                            code,
                            detail
                        );
                    }
                }
        }
    }

    public static RecapGridControlInspectResult Verify(
        string repositoryPath,
        RefId refId
    ) => Inspect(repositoryPath, refId);

    public static RecapGridControlExportResult Export(
        string repositoryPath,
        RefId refId
    ) {
        AdminScopeResult scopeResult = OpenScope(repositoryPath, refId);
        switch (scopeResult) {
            case AdminScopeResult.TimelineAbsent:
                return new RecapGridControlExportResult.TimelineAbsent();
            case AdminScopeResult.Busy:
                return new RecapGridControlExportResult.Busy();
            case AdminScopeResult.TimelineUnsupportedSchema schema:
                return new RecapGridControlExportResult
                    .TimelineUnsupportedSchema(schema.SchemaVersion);
            case AdminScopeResult.Invalid invalid:
                return new RecapGridControlExportResult.Invalid(
                    invalid.Code,
                    invalid.Detail
                );
            default:
                return new RecapGridControlExportResult.Invalid(
                    "ControlExportOutcomeInvalid",
                    "The Control scope returned an unknown outcome."
                );
            case AdminScopeResult.Opened scope:
                using (scope.TimelineHandle) {
                    try {
                        if (!ControlDurableFiles.StateExists(scope.Paths)) {
                            return new RecapGridControlExportResult.Absent();
                        }
                        using FileStream lease = ControlDurableFiles
                            .AcquireSharedLifetime(scope.Paths);
                        ControlState state = ReadState(scope.Paths);
                        return new RecapGridControlExportResult.Available(
                            state.Snapshot(),
                            (byte[])state.CanonicalBytes.Clone()
                        );
                    }
                    catch (ControlUnsupportedSchemaException schema) {
                        return new RecapGridControlExportResult
                            .UnsupportedSchema(schema.Version);
                    }
                    catch (ControlBusyException) {
                        return new RecapGridControlExportResult.Busy();
                    }
                    catch (Exception exception) {
                        (string code, string detail) =
                            ControlError.Invalid(exception);
                        return new RecapGridControlExportResult.Invalid(
                            code,
                            detail
                        );
                    }
                }
        }
    }

    public static RecapGridControlBackupResult Backup(
        string repositoryPath,
        RefId refId,
        ControlHeadRef expectedWholeHead,
        string backupDirectory
    ) => BackupForTest(
        repositoryPath,
        refId,
        expectedWholeHead,
        backupDirectory,
        ControlPersistenceTestHooks.None
    );

    internal static RecapGridControlBackupResult BackupForTest(
        string repositoryPath,
        RefId refId,
        ControlHeadRef expectedWholeHead,
        string backupDirectory,
        ControlPersistenceTestHooks hooks
    ) {
        ArgumentNullException.ThrowIfNull(expectedWholeHead);
        AdminScopeResult scopeResult = OpenScope(repositoryPath, refId);
        switch (scopeResult) {
            case AdminScopeResult.TimelineAbsent:
                return new RecapGridControlBackupResult.TimelineAbsent();
            case AdminScopeResult.Busy:
                return new RecapGridControlBackupResult.Busy();
            case AdminScopeResult.TimelineUnsupportedSchema schema:
                return new RecapGridControlBackupResult
                    .TimelineUnsupportedSchema(schema.SchemaVersion);
            case AdminScopeResult.Invalid invalid:
                return new RecapGridControlBackupResult.Invalid(
                    invalid.Code,
                    invalid.Detail
                );
            default:
                return new RecapGridControlBackupResult.Invalid(
                    "ControlBackupOutcomeInvalid",
                    "The Control scope returned an unknown outcome."
                );
            case AdminScopeResult.Opened scope:
                using (scope.TimelineHandle) {
                    try {
                        if (!ControlDurableFiles.StateExists(scope.Paths)) {
                            return new RecapGridControlBackupResult.Absent();
                        }
                        using FileStream lifetime = ControlDurableFiles
                            .AcquireExclusiveLifetime(
                                scope.Paths,
                                create: false
                            );
                        using FileStream writer = ControlDurableFiles
                            .AcquireWriter(scope.Paths, create: false);
                        ControlState state = ReadState(scope.Paths);
                        if (state.Head != expectedWholeHead) {
                            return new RecapGridControlBackupResult
                                .StaleControlHead(state.Head);
                        }
                        RecapGridControlBackupManifest manifest =
                            WriteBackup(
                                Path.GetFullPath(backupDirectory),
                                state,
                                hooks
                            );
                        return new RecapGridControlBackupResult.Created(
                            manifest
                        );
                    }
                    catch (ControlBusyException) {
                        return new RecapGridControlBackupResult.Busy();
                    }
                    catch (ControlBackupPublishIndeterminateException indeterminate) {
                        return new RecapGridControlBackupResult
                            .PublishIndeterminate(
                                indeterminate.Intended,
                                indeterminate.Observed
                            );
                    }
                    catch (ControlUnsupportedSchemaException schema) {
                        return new RecapGridControlBackupResult
                            .ControlUnsupportedSchema(schema.Version);
                    }
                    catch (ControlLimitException limit) {
                        return new RecapGridControlBackupResult.LimitExceeded(
                            limit.Limit
                        );
                    }
                    catch (Exception exception) {
                        (string code, string detail) =
                            ControlError.Invalid(exception);
                        return new RecapGridControlBackupResult.Invalid(
                            code,
                            detail
                        );
                    }
                }
        }
    }

    public static RecapGridControlAdminResult Restore(
        string repositoryPath,
        RefId refId,
        ControlHeadRef expectedWholeHead,
        string backupDirectory
    ) => RestoreForTest(
        repositoryPath,
        refId,
        expectedWholeHead,
        backupDirectory,
        ControlPersistenceTestHooks.None
    );

    internal static RecapGridControlAdminResult RestoreForTest(
        string repositoryPath,
        RefId refId,
        ControlHeadRef expectedWholeHead,
        string backupDirectory,
        ControlPersistenceTestHooks hooks
    ) {
        ArgumentNullException.ThrowIfNull(expectedWholeHead);
        BackupPayload payload;
        try {
            payload = ReadBackup(Path.GetFullPath(backupDirectory));
        }
        catch (ControlLimitException limit) {
            return new RecapGridControlAdminResult.LimitExceeded(limit.Limit);
        }
        catch (ControlUnsupportedSchemaException schema) {
            return new RecapGridControlAdminResult
                .ControlUnsupportedSchema(schema.Version);
        }
        catch (Exception exception) {
            (string code, string detail) = ControlError.Invalid(exception);
            return new RecapGridControlAdminResult.Invalid(code, detail);
        }
        return ReplaceWith(
            repositoryPath,
            refId,
            expectedWholeHead,
            payload.State,
            restore: true,
            hooks
        );
    }

    public static RecapGridControlAdminResult Reinitialize(
        string repositoryPath,
        RefId refId,
        ControlHeadRef expectedWholeHead
    ) => ReinitializeForTest(
        repositoryPath,
        refId,
        expectedWholeHead,
        ControlPersistenceTestHooks.None
    );

    internal static RecapGridControlAdminResult ReinitializeForTest(
        string repositoryPath,
        RefId refId,
        ControlHeadRef expectedWholeHead,
        ControlPersistenceTestHooks hooks
    ) => ReplaceWith(
        repositoryPath,
        refId,
        expectedWholeHead,
        replacement: null,
        restore: false,
        hooks
    );

    private static RecapGridControlAdminResult ReplaceWith(
        string repositoryPath,
        RefId refId,
        ControlHeadRef expectedWholeHead,
        ControlState? replacement,
        bool restore,
        ControlPersistenceTestHooks hooks
    ) {
        AdminScopeResult scopeResult = OpenScope(repositoryPath, refId);
        switch (scopeResult) {
            case AdminScopeResult.TimelineAbsent:
                return new RecapGridControlAdminResult.TimelineAbsent();
            case AdminScopeResult.Busy:
                return new RecapGridControlAdminResult.Busy();
            case AdminScopeResult.TimelineUnsupportedSchema schema:
                return new RecapGridControlAdminResult
                    .TimelineUnsupportedSchema(schema.SchemaVersion);
            case AdminScopeResult.Invalid invalid:
                return new RecapGridControlAdminResult.Invalid(
                    invalid.Code,
                    invalid.Detail
                );
            default:
                return new RecapGridControlAdminResult.Invalid(
                    "ControlAdminOutcomeInvalid",
                    "The Control scope returned an unknown outcome."
                );
            case AdminScopeResult.Opened scope:
                using (scope.TimelineHandle) {
                    try {
                        if (!ControlDurableFiles.StateExists(scope.Paths)) {
                            return new RecapGridControlAdminResult.Absent();
                        }
                        using FileStream lifetime = ControlDurableFiles
                            .AcquireExclusiveLifetime(
                                scope.Paths,
                                create: false
                            );
                        using FileStream writer = ControlDurableFiles
                            .AcquireWriter(scope.Paths, create: false);
                        ControlState current = ReadState(scope.Paths);
                        if (current.Head != expectedWholeHead) {
                            return new RecapGridControlAdminResult
                                .StaleControlHead(current.Head);
                        }
                        ControlState next;
                        if (restore) {
                            if (replacement is null
                                || replacement.Head.RefId != refId
                                || replacement.Head.TimelineId
                                    != scope.Paths.TimelineId) {
                                return new RecapGridControlAdminResult.Invalid(
                                    "ControlBackupScopeMismatch",
                                    "The backup belongs to another Ref or Timeline."
                                );
                            }
                            IReadOnlyDictionary<string,
                                ControlOperationReceipt> mergedReceipts =
                                MergeOperationReceipts(
                                    current.OperationReceipts,
                                    replacement.OperationReceipts
                                );
                            next = replacement.WithGenerationAndReceipts(
                                ControlInstanceId.Generate(),
                                checked(current.Head.Generation + 1),
                                mergedReceipts
                            );
                        }
                        else {
                            ControlState empty = ControlState.CreateEmpty(
                                refId,
                                scope.Paths.TimelineId,
                                generation: checked(
                                    current.Head.Generation + 1
                                )
                            );
                            next = empty.WithGenerationAndReceipts(
                                empty.Head.InstanceId,
                                empty.Head.Generation,
                                current.OperationReceipts
                            );
                        }
                        try {
                            ControlDurableFiles.WriteState(
                                scope.Paths,
                                next.CanonicalBytes,
                                createNew: false,
                                hooks
                            );
                        }
                        catch (ControlStatePublishIndeterminateException) {
                            return new RecapGridControlAdminResult
                                .CommitIndeterminate(
                                    next.Head,
                                    RecapGridControlFactory.ObserveHead(
                                        scope.Paths
                                    )
                                );
                        }
                        return new RecapGridControlAdminResult.Applied(
                            next.Head
                        );
                    }
                    catch (ControlBusyException) {
                        return new RecapGridControlAdminResult.Busy();
                    }
                    catch (ControlUnsupportedSchemaException schema) {
                        return new RecapGridControlAdminResult
                            .ControlUnsupportedSchema(schema.Version);
                    }
                    catch (ControlLimitException limit) {
                        return new RecapGridControlAdminResult.LimitExceeded(
                            limit.Limit
                        );
                    }
                    catch (Exception exception) {
                        (string code, string detail) =
                            ControlError.Invalid(exception);
                        return new RecapGridControlAdminResult.Invalid(
                            code,
                            detail
                        );
                    }
                }
        }
    }

    private static IReadOnlyDictionary<string, ControlOperationReceipt>
        MergeOperationReceipts(
        IReadOnlyDictionary<string, ControlOperationReceipt> current,
        IReadOnlyDictionary<string, ControlOperationReceipt> replacement
    ) {
        var merged = new SortedDictionary<string, ControlOperationReceipt>(
            StringComparer.Ordinal
        );
        foreach ((string key, ControlOperationReceipt receipt) in current) {
            merged.Add(key, receipt);
        }
        foreach ((string key, ControlOperationReceipt receipt)
                 in replacement) {
            if (merged.TryGetValue(key, out ControlOperationReceipt? found)) {
                if (found != receipt) {
                    throw new ControlStoreException(
                        "ControlOperationReceiptConflict",
                        "Current and backup Control states contain conflicting operation receipts."
                    );
                }
                continue;
            }
            if (merged.Count
                >= ControlStorageLimits.MaximumOperationReceiptCount) {
                throw new ControlLimitException(
                    "ControlOperationReceiptCount"
                );
            }
            merged.Add(key, receipt);
        }
        return merged;
    }

    private static AdminScopeResult OpenScope(
        string repositoryPath,
        RefId refId
    ) {
        try {
            HistoryTimelineReaderOpenResult opened =
                HistoryTimelineMaintenance.OpenReader(
                    repositoryPath,
                    refId
                );
            return opened switch {
                HistoryTimelineReaderOpenResult.Opened available
                    => new AdminScopeResult.Opened(
                        new ControlPaths(
                            repositoryPath,
                            refId,
                            available.Handle.Locator.ActiveTimelineId
                        ),
                        available.Handle
                    ),
                HistoryTimelineReaderOpenResult.Absent
                    => new AdminScopeResult.TimelineAbsent(),
                HistoryTimelineReaderOpenResult.Busy
                    => new AdminScopeResult.Busy(),
                HistoryTimelineReaderOpenResult.UnsupportedSchema schema
                    => new AdminScopeResult.TimelineUnsupportedSchema(
                        schema.SchemaVersion
                    ),
                HistoryTimelineReaderOpenResult.Invalid invalid
                    => new AdminScopeResult.Invalid(
                        invalid.Code,
                        invalid.Detail
                    ),
                _ => new AdminScopeResult.Invalid(
                    "TimelineReaderOpenOutcomeInvalid",
                    "The Timeline reader returned an unknown outcome."
                )
            };
        }
        catch (Exception exception) {
            (string code, string detail) = ControlError.Invalid(exception);
            return new AdminScopeResult.Invalid(code, detail);
        }
    }

    private static ControlState ReadState(ControlPaths paths) {
        ControlState state = ControlState.Decode(
            ControlDurableFiles.ReadState(paths)
        );
        RecapGridControlFactory.RequireScope(state, paths);
        return state;
    }

    private static RecapGridControlBackupManifest WriteBackup(
        string backupDirectory,
        ControlState state,
        ControlPersistenceTestHooks hooks
    ) {
        if (Directory.Exists(backupDirectory)
            || File.Exists(backupDirectory)) {
            throw new ControlStoreException(
                "ControlBackupAlreadyExists",
                "The exact backup target already exists."
            );
        }
        string? parent = Path.GetDirectoryName(backupDirectory);
        if (parent is null || !Directory.Exists(parent)) {
            throw new ControlStoreException(
                "ControlBackupParentAbsent",
                "The backup parent directory is absent."
            );
        }
        byte[] stateBytes = (byte[])state.CanonicalBytes.Clone();
        string stateSha256 = Convert.ToHexStringLower(
            SHA256.HashData(stateBytes)
        );
        var manifest = new RecapGridControlBackupManifest(
            state.Head,
            stateSha256,
            stateBytes.LongLength
        );
        byte[] manifestBytes = EncodeManifest(manifest);
        string temporary = Path.Combine(
            parent,
            $".{Path.GetFileName(backupDirectory)}.{Guid.NewGuid():N}.tmp"
        );
        Directory.CreateDirectory(temporary);
        bool published = false;
        try {
            WriteNewFile(
                Path.Combine(temporary, BackupStateFileName),
                stateBytes
            );
            WriteNewFile(
                Path.Combine(temporary, BackupManifestFileName),
                manifestBytes
            );
            ControlDurableFiles.FlushDirectory(temporary);
            hooks.BeforeBackupPublish?.Invoke();
            Directory.Move(temporary, backupDirectory);
            published = true;
            hooks.AfterBackupPublish?.Invoke();
            ControlDurableFiles.FlushDirectory(parent);
            return manifest;
        }
        catch (Exception exception) when (published
            && exception is not ControlBackupPublishIndeterminateException) {
            RecapGridControlBackupManifest? observed = null;
            try {
                BackupPayload payload = ReadBackup(backupDirectory);
                observed = payload.Manifest;
            }
            catch { }
            throw new ControlBackupPublishIndeterminateException(
                manifest,
                observed,
                exception
            );
        }
        catch {
            if (Directory.Exists(temporary)) {
                Directory.Delete(temporary, recursive: true);
            }
            throw;
        }
    }

    private static BackupPayload ReadBackup(string backupDirectory) {
        byte[] manifestBytes = ReadBoundedFile(
            Path.Combine(backupDirectory, BackupManifestFileName),
            ControlStorageLimits.MaximumBackupManifestUtf8Bytes,
            "ControlBackupManifestBytes"
        );
        RecapGridControlBackupManifest manifest = DecodeManifest(
            manifestBytes
        );
        byte[] stateBytes = ReadBoundedFile(
            Path.Combine(backupDirectory, BackupStateFileName),
            ControlStorageLimits.MaximumStateCanonicalUtf8Bytes,
            "ControlBackupStateBytes"
        );
        if (stateBytes.LongLength != manifest.StateFileBytes
            || !string.Equals(
                Convert.ToHexStringLower(SHA256.HashData(stateBytes)),
                manifest.StateFileSha256,
                StringComparison.Ordinal)) {
            throw new ControlStoreException(
                "ControlBackupDigestMismatch",
                "The backup state bytes differ from its manifest."
            );
        }
        ControlState state = ControlState.Decode(stateBytes);
        if (state.Head != manifest.Head) {
            throw new ControlStoreException(
                "ControlBackupHeadMismatch",
                "The backup state head differs from its manifest."
            );
        }
        return new BackupPayload(manifest, state);
    }

    private static byte[] EncodeManifest(
        RecapGridControlBackupManifest manifest
    ) {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            new ControlBackupManifestDto(
                1,
                HeadDto(manifest.Head),
                manifest.StateFileSha256,
                manifest.StateFileBytes
            ),
            ControlJson.Options
        );
        if (bytes.Length
            > ControlStorageLimits.MaximumBackupManifestUtf8Bytes) {
            throw new ControlLimitException("ControlBackupManifestBytes");
        }
        return bytes;
    }

    private static RecapGridControlBackupManifest DecodeManifest(
        ReadOnlySpan<byte> bytes
    ) {
        ControlBackupManifestDto? dto;
        try {
            dto = JsonSerializer.Deserialize<ControlBackupManifestDto>(
                bytes,
                ControlJson.Options
            );
        }
        catch (JsonException exception) {
            throw new ControlStoreException(
                "ControlBackupManifestInvalid",
                "The backup manifest is not strict JSON.",
                exception
            );
        }
        if (dto is null
            || dto.SchemaVersion != 1
            || !bytes.SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(
                dto,
                ControlJson.Options
            ))) {
            throw new ControlStoreException(
                "ControlBackupManifestNonCanonical",
                "The backup manifest is not the exact V1 canonical encoding."
            );
        }
        if (dto.StateFileBytes is < 2
            or > ControlStorageLimits.MaximumStateCanonicalUtf8Bytes
            || dto.StateFileSha256.Length != 64
            || dto.StateFileSha256.Any(static character =>
                character is not (>= '0' and <= '9')
                    and not (>= 'a' and <= 'f'))) {
            throw new ControlStoreException(
                "ControlBackupManifestInvalid",
                "The backup manifest commitments are invalid."
            );
        }
        return new RecapGridControlBackupManifest(
            Head(dto.Head),
            dto.StateFileSha256,
            dto.StateFileBytes
        );
    }

    private static ControlHeadDto HeadDto(ControlHeadRef head) => new(
        head.InstanceId.Value,
        head.RefId.Packed,
        head.TimelineId.Value,
        head.Generation,
        head.StateDigest.Value,
        head.ActiveRecipeDigest?.Value
    );

    private static ControlHeadRef Head(ControlHeadDto value) => new(
        new ControlInstanceId(value.InstanceId),
        new RefId(value.RefId),
        new TimelineId(value.TimelineId),
        value.Generation,
        new ControlStateDigest(value.StateDigest),
        value.ActiveRecipeDigest is null
            ? null
            : new GridBuildRecipeDigest(value.ActiveRecipeDigest)
    );

    private static void WriteNewFile(string path, ReadOnlySpan<byte> bytes) {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough
        );
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static byte[] ReadBoundedFile(
        string path,
        int maximumBytes,
        string limit
    ) {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan
        );
        if (stream.Length is < 2 or > int.MaxValue
            || stream.Length > maximumBytes) {
            throw new ControlLimitException(limit);
        }
        byte[] bytes = GC.AllocateUninitializedArray<byte>(
            checked((int)stream.Length)
        );
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) {
            throw new ControlStoreException(
                "ControlBackupChangedDuringRead",
                "A backup file changed during its bounded read."
            );
        }
        return bytes;
    }

    private abstract record AdminScopeResult {
        private AdminScopeResult() { }
        internal sealed record Opened(
            ControlPaths Paths,
            HistoryTimelineReaderHandle TimelineHandle
        ) : AdminScopeResult;
        internal sealed record TimelineAbsent : AdminScopeResult;
        internal sealed record TimelineUnsupportedSchema(
            int SchemaVersion
        ) : AdminScopeResult;
        internal sealed record Busy : AdminScopeResult;
        internal sealed record Invalid(string Code, string Detail)
            : AdminScopeResult;
    }

    private sealed record BackupPayload(
        RecapGridControlBackupManifest Manifest,
        ControlState State
    );
}

internal sealed record ControlBackupManifestDto(
    int SchemaVersion,
    ControlHeadDto Head,
    string StateFileSha256,
    long StateFileBytes
);

internal sealed class ControlBackupPublishIndeterminateException
    : Exception {
    internal ControlBackupPublishIndeterminateException(
        RecapGridControlBackupManifest intended,
        RecapGridControlBackupManifest? observed,
        Exception inner
    ) : base(
        "The Control backup was published but its durability confirmation failed.",
        inner
    ) {
        Intended = intended;
        Observed = observed;
    }

    internal RecapGridControlBackupManifest Intended { get; }
    internal RecapGridControlBackupManifest? Observed { get; }
}
