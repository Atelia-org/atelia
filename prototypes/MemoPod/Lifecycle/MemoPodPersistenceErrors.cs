namespace Atelia.MemoPod;

internal static class MemoPodPersistenceErrors {
    internal static bool CanMap(Exception exception)
        => exception is IOException or UnauthorizedAccessException;

    internal static MemoPodPersistenceException FromException(
        Exception exception,
        string message
    ) => new(MapKind(exception), message, exception);

    internal static MemoPodPersistenceException FromPublishFailure(
        MemoPodPublishFailure? failure
    ) {
        if (failure is null) {
            return new MemoPodPersistenceException(
                MemoPodPersistenceFailureKind.IoFailure,
                "MemoPod publication failed before settlement."
            );
        }

        MemoPodPersistenceFailureKind kind = failure.Kind switch {
            MemoPodPublishFailureKind.TargetAlreadyExists =>
                MemoPodPersistenceFailureKind.AlreadyExists,
            MemoPodPublishFailureKind.TargetMissing =>
                MemoPodPersistenceFailureKind.NotFound,
            MemoPodPublishFailureKind.PreparationFailed
                => failure.Exception is null
                    ? MemoPodPersistenceFailureKind.IoFailure
                    : MapKind(failure.Exception),
            MemoPodPublishFailureKind.SettlementFailed =>
                MemoPodPersistenceFailureKind.IoFailure,
            _ => MemoPodPersistenceFailureKind.IoFailure,
        };
        return new MemoPodPersistenceException(
            kind,
            "MemoPod publication failed before durable settlement.",
            failure.Exception
        );
    }

    internal static MemoPodCommitIndeterminateException CommitIndeterminate(
        MemoPodPublishFailure? failure
    ) => new(
        "MemoPod publication may have changed durable authority, but settlement could not be proven. Discard this handle and reopen the Pod.",
        failure?.Exception
    );

    private static MemoPodPersistenceFailureKind MapKind(
        Exception exception
    ) {
        if (exception is MemoPodStoreException storeException) {
            return storeException.Code switch {
                MemoPodStoreErrorCode.RootAbsent
                    or MemoPodStoreErrorCode.DocumentAbsent =>
                    MemoPodPersistenceFailureKind.NotFound,
                MemoPodStoreErrorCode.PathShapeInvalid
                    or MemoPodStoreErrorCode.PathLinkRejected
                    or MemoPodStoreErrorCode.PathStatFailed =>
                    MemoPodPersistenceFailureKind.UnsafePath,
                MemoPodStoreErrorCode.DocumentChangedDuringRead
                    or MemoPodStoreErrorCode.DocumentIdentityMismatch =>
                    MemoPodPersistenceFailureKind.InvalidDocument,
                _ => MemoPodPersistenceFailureKind.IoFailure,
            };
        }
        if (exception is MemoPodDocumentFormatException
            or MemoPodDocumentLimitException) {
            return MemoPodPersistenceFailureKind.InvalidDocument;
        }
        if (exception is FileNotFoundException
            or DirectoryNotFoundException) {
            return MemoPodPersistenceFailureKind.NotFound;
        }
        return MemoPodPersistenceFailureKind.IoFailure;
    }
}
