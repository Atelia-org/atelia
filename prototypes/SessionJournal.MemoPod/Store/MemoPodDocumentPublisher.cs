namespace Atelia.SessionJournal.MemoPod;

internal enum MemoPodPublishMode {
    CreateNew,
    ReplaceExisting,
}

internal enum MemoPodPublishSettlement {
    Published,
    NotPublished,
    CommitIndeterminate,
}

internal enum MemoPodPublishFailureKind {
    TargetAlreadyExists,
    TargetMissing,
    PreparationFailed,
    SettlementFailed,
}

internal sealed record MemoPodPublishFailure(
    MemoPodPublishFailureKind Kind,
    Exception? Exception = null
);

internal sealed record MemoPodPublishResult(
    MemoPodPublishSettlement Settlement,
    MemoPodPublishFailure? Failure = null,
    Exception? PostPublishDiagnostic = null
);

internal sealed record MemoPodPublisherTestHooks(
    Action<string>? BeforePublish = null,
    Action<string>? AfterInstallBeforeDirectoryFsync = null,
    Action<string>? AfterDirectoryFsync = null
) {
    internal static MemoPodPublisherTestHooks None { get; } = new();
}

internal static class MemoPodDocumentPublisher {
    internal static MemoPodPublishResult Publish(
        string rootPath,
        MemoPodDocument document,
        MemoPodPublishMode mode,
        MemoPodPublisherTestHooks? hooks = null,
        CancellationToken cancellationToken = default
    ) {
        MemoPodStoreLayout.RequireLinux();
        ArgumentNullException.ThrowIfNull(document);
        if (!Enum.IsDefined(mode)) {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        hooks ??= MemoPodPublisherTestHooks.None;
        cancellationToken.ThrowIfCancellationRequested();

        string? temporaryPath = null;
        bool settlementFenceEntered = false;
        bool directorySynced = false;
        try {
            byte[] canonicalBytes = MemoPodDocumentCodec.Encode(document);
            MemoPodStorePaths paths = MemoPodStoreLayout.Resolve(
                rootPath,
                document.PodId
            );
            MemoPodStoreLayout.EnsureForPublish(paths);

            bool targetExists = MemoPodStoreLayout.DocumentEntryExists(paths);
            if (mode is MemoPodPublishMode.CreateNew && targetExists) {
                return new MemoPodPublishResult(
                    MemoPodPublishSettlement.NotPublished,
                    new MemoPodPublishFailure(
                        MemoPodPublishFailureKind.TargetAlreadyExists
                    )
                );
            }
            if (mode is MemoPodPublishMode.ReplaceExisting && !targetExists) {
                return new MemoPodPublishResult(
                    MemoPodPublishSettlement.NotPublished,
                    new MemoPodPublishFailure(
                        MemoPodPublishFailureKind.TargetMissing
                    )
                );
            }

            temporaryPath = Path.Combine(
                paths.PodsPath,
                $".{document.PodId.Value}.{Guid.NewGuid():N}.tmp"
            );
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough
            )) {
                stream.Write(canonicalBytes);
                stream.Flush(flushToDisk: true);
            }

            hooks.BeforePublish?.Invoke(temporaryPath);
            MemoPodStoreLayout.RequireRegularFile(temporaryPath);
            cancellationToken.ThrowIfCancellationRequested();

            // This assignment is the settlement fence. From immediately before
            // File.Move until the parent-directory fsync succeeds, any ordinary
            // failure is conservatively CommitIndeterminate.
            settlementFenceEntered = true;
            File.Move(
                temporaryPath,
                paths.DocumentPath,
                overwrite: mode is MemoPodPublishMode.ReplaceExisting
            );
            hooks.AfterInstallBeforeDirectoryFsync?.Invoke(
                paths.DocumentPath
            );
            MemoPodStoreLayout.FlushDirectory(paths.PodsPath);
            directorySynced = true;

            Exception? postPublishDiagnostic = null;
            try {
                hooks.AfterDirectoryFsync?.Invoke(paths.DocumentPath);
            }
            catch (Exception exception) when (!IsFatal(exception)) {
                postPublishDiagnostic = exception;
            }

            return new MemoPodPublishResult(
                MemoPodPublishSettlement.Published,
                PostPublishDiagnostic: postPublishDiagnostic
            );
        }
        catch (OperationCanceledException) when (!settlementFenceEntered) {
            _ = CleanupTemporary(temporaryPath);
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception)) {
            Exception failure = exception;
            if (!settlementFenceEntered) {
                Exception? cleanupDiagnostic = CleanupTemporary(
                    temporaryPath
                );
                failure = CombineDiagnostics(
                    exception,
                    cleanupDiagnostic
                ) ?? exception;
            }
            if (directorySynced) {
                return new MemoPodPublishResult(
                    MemoPodPublishSettlement.Published,
                    PostPublishDiagnostic: failure
                );
            }
            return settlementFenceEntered
                ? new MemoPodPublishResult(
                    MemoPodPublishSettlement.CommitIndeterminate,
                    new MemoPodPublishFailure(
                        MemoPodPublishFailureKind.SettlementFailed,
                        failure
                    )
                )
                : new MemoPodPublishResult(
                    MemoPodPublishSettlement.NotPublished,
                    new MemoPodPublishFailure(
                        MemoPodPublishFailureKind.PreparationFailed,
                        failure
                    )
                );
        }
    }

    private static Exception? CleanupTemporary(string? temporaryPath) {
        try {
            if (temporaryPath is null
                || !MemoPodStoreLayout.TryGetAttributes(
                    temporaryPath,
                    out _
                )) {
                return null;
            }
            File.Delete(temporaryPath);
            return null;
        }
        catch (Exception exception) when (!IsFatal(exception)) {
            return exception;
        }
    }

    private static Exception? CombineDiagnostics(
        Exception? first,
        Exception? second
    ) {
        if (first is null) { return second; }
        if (second is null) { return first; }
        return new AggregateException(first, second);
    }

    private static bool IsFatal(Exception exception)
        => exception is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or AppDomainUnloadedException
            or BadImageFormatException
            or CannotUnloadAppDomainException;
}
