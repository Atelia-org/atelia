using Atelia.EventJournal;

namespace Atelia.SessionJournal.HistoryTimeline;

/// <summary>One durable HistoryTimeline file discovered by maintenance-only inventory.</summary>
public sealed record HistoryTimelineScope(RefId RefId, TimelineId TimelineId);

public abstract record HistoryTimelineScopeInventoryResult {
    private HistoryTimelineScopeInventoryResult() { }
    public sealed record Available(IReadOnlyList<HistoryTimelineScope> Scopes)
        : HistoryTimelineScopeInventoryResult;
    public sealed record Invalid(string Code, string Detail)
        : HistoryTimelineScopeInventoryResult;
}

public abstract record HistoryTimelineExactReaderOpenResult {
    private HistoryTimelineExactReaderOpenResult() { }
    public sealed record Opened(HistoryTimelineExactReaderHandle Handle)
        : HistoryTimelineExactReaderOpenResult;
    public sealed record Absent : HistoryTimelineExactReaderOpenResult;
    public sealed record Busy : HistoryTimelineExactReaderOpenResult;
    public sealed record UnsupportedSchema(int SchemaVersion)
        : HistoryTimelineExactReaderOpenResult;
    public sealed record Invalid(string Code, string Detail)
        : HistoryTimelineExactReaderOpenResult;
}

/// <summary>Read-only handle bound to an explicit historical scope, never to a locator.</summary>
public sealed class HistoryTimelineExactReaderHandle : IDisposable {
    private readonly HistoryTimelineLifetime _lifetime;
    private bool _transferred;

    internal HistoryTimelineExactReaderHandle(
        HistoryTimelineScope scope,
        HistoryTimelineReader reader,
        HistoryTimelineLifetime lifetime
    ) {
        Scope = scope;
        Reader = reader;
        _lifetime = lifetime;
    }

    public HistoryTimelineScope Scope { get; }
    public HistoryTimelineReader Reader { get; }

    /// <summary>Transfers this maintenance handle to a consumer that owns its lifetime.</summary>
    internal HistoryTimelineReaderHandle DetachReaderHandle() {
        ObjectDisposedException.ThrowIf(_transferred, this);
        _transferred = true;
        return new HistoryTimelineReaderHandle(
            new ActiveTimelineLocator(Scope.RefId, Scope.TimelineId, 0),
            Reader,
            _lifetime
        );
    }

    public void Dispose() {
        if (!_transferred) _lifetime.Dispose();
    }
}

public static partial class HistoryTimelineMaintenance {
    private const int MaximumInventoryScopes = 4_096;

    /// <summary>
    /// Enumerates only canonical V2 Timeline database names below the supplied
    /// repository. This is maintenance inventory, not a runtime locator lookup.
    /// </summary>
    public static HistoryTimelineScopeInventoryResult InventoryScopes(
        string repositoryPath
    ) {
        try {
            string repository = CanonicalRepositoryPath(repositoryPath);
            string refs = Path.Combine(repository, "derived", "history-timeline", "v2", "refs");
            HistoryTimelineDurableFiles.RequireSafePath(repository, refs);
            if (!Directory.Exists(refs)) {
                return new HistoryTimelineScopeInventoryResult.Available([]);
            }
            var scopes = new List<HistoryTimelineScope>();
            foreach (string refPath in Directory.EnumerateFileSystemEntries(refs).Order(StringComparer.Ordinal)) {
                RequireSafeDirectory(repository, refPath);
                string refName = Path.GetFileName(refPath);
                RefId refId = ParseRefName(refName);
                string timelines = Path.Combine(refPath, "timelines");
                HistoryTimelineDurableFiles.RequireSafePath(repository, timelines);
                foreach (string entry in Directory.EnumerateFileSystemEntries(refPath)) {
                    string name = Path.GetFileName(entry);
                    if (name == "timelines") continue;
                    if (name == "locator.json" && IsRegularFile(repository, entry)) continue;
                    throw new InvalidDataException("Timeline inventory encountered a foreign Ref entry.");
                }
                RequireSafeDirectory(repository, timelines);
                foreach (string databasePath in Directory.EnumerateFileSystemEntries(timelines).Order(StringComparer.Ordinal)) {
                    HistoryTimelineDurableFiles.RequireSafePath(repository, databasePath);
                    if (!IsRegularFile(repository, databasePath)) {
                        throw new InvalidDataException("Timeline inventory encountered a non-regular file.");
                    }
                    string name = Path.GetFileName(databasePath);
                    if (!name.EndsWith(".sqlite", StringComparison.Ordinal)) {
                        throw new InvalidDataException("Timeline inventory encountered a foreign filename.");
                    }
                    string idText = name[..^".sqlite".Length];
                    var timelineId = new TimelineId(idText);
                    if (!string.Equals(name, $"{timelineId.Value}.sqlite", StringComparison.Ordinal)) {
                        throw new InvalidDataException("Timeline inventory encountered a non-canonical filename.");
                    }
                    HistoryTimelineExactReaderOpenResult opened = OpenExactReader(
                        repository,
                        refId,
                        timelineId
                    );
                    switch (opened) {
                        case HistoryTimelineExactReaderOpenResult.Opened available:
                            available.Handle.Dispose();
                            break;
                        case HistoryTimelineExactReaderOpenResult.Invalid invalid:
                            throw new InvalidDataException(
                                $"Timeline inventory scope is invalid: {invalid.Code}."
                            );
                        case HistoryTimelineExactReaderOpenResult.UnsupportedSchema schema:
                            throw new HistoryTimelineUnsupportedSchemaException(
                                schema.SchemaVersion
                            );
                        case HistoryTimelineExactReaderOpenResult.Busy:
                            throw new HistoryTimelineLeaseBusyException();
                        default:
                            throw new InvalidDataException(
                                "Timeline inventory scope disappeared during verification."
                            );
                    }
                    scopes.Add(new HistoryTimelineScope(refId, timelineId));
                    if (scopes.Count > MaximumInventoryScopes) {
                        throw new HistoryTimelineStoreLimitException("InventoryScopeCount", "Timeline inventory exceeds its code-owned scope bound.");
                    }
                }
            }
            return new HistoryTimelineScopeInventoryResult.Available(
                scopes.OrderBy(static value => value.RefId.ToHexString(), StringComparer.Ordinal)
                    .ThenBy(static value => value.TimelineId.Value, StringComparer.Ordinal).ToArray());
        }
        catch (Exception exception) when (IsMaintenanceFailure(exception)) {
            return new HistoryTimelineScopeInventoryResult.Invalid(MaintenanceErrorCode(exception), exception.Message);
        }
    }

    public static HistoryTimelineExactReaderOpenResult OpenExactReader(
        string repositoryPath,
        RefId refId,
        TimelineId timelineId
    ) {
        FileStream? lease = null;
        try {
            string repository = CanonicalRepositoryPath(repositoryPath);
            var paths = new HistoryTimelinePaths(repository, refId);
            HistoryTimelineSyntax.RequireTimelineId(timelineId);
            string database = paths.TimelineDatabasePath(timelineId);
            if (!HistoryTimelineDurableFiles.ExistsExact(repository, database)) {
                return new HistoryTimelineExactReaderOpenResult.Absent();
            }
            lease = HistoryTimelineDurableFiles.AcquireSharedExisting(paths);
            var ledger = new SqliteHistoryTimelineLedger(database, timelineId, refId, HistoryTimelineStorageLimits.Production, readOnly: true);
            HistoryTimelineStoreReadResult<TimelineHeadRef> head = ledger.VerifyAndReadHead();
            if (head is HistoryTimelineStoreReadResult<TimelineHeadRef>.Busy) return new HistoryTimelineExactReaderOpenResult.Busy();
            if (head is HistoryTimelineStoreReadResult<TimelineHeadRef>.UnsupportedSchema schema) return new HistoryTimelineExactReaderOpenResult.UnsupportedSchema(schema.SchemaVersion);
            if (head is HistoryTimelineStoreReadResult<TimelineHeadRef>.Invalid invalid) return new HistoryTimelineExactReaderOpenResult.Invalid(invalid.Code, invalid.Detail);
            if (head is not HistoryTimelineStoreReadResult<TimelineHeadRef>.Found found
                || found.Value.RefId != refId || found.Value.TimelineId != timelineId) {
                return new HistoryTimelineExactReaderOpenResult.Invalid("TimelineScopeMismatch", "The Timeline metadata differs from its requested exact scope.");
            }
            HistoryTimelineStoreReadResult<PartitionPolicyRevision> policy = ledger.ReadPolicy(found.Value.ActivePartitionPolicyDigest);
            if (policy is HistoryTimelineStoreReadResult<PartitionPolicyRevision>.Busy) return new HistoryTimelineExactReaderOpenResult.Busy();
            if (policy is HistoryTimelineStoreReadResult<PartitionPolicyRevision>.UnsupportedSchema policySchema) return new HistoryTimelineExactReaderOpenResult.UnsupportedSchema(policySchema.SchemaVersion);
            if (policy is HistoryTimelineStoreReadResult<PartitionPolicyRevision>.Invalid policyInvalid) return new HistoryTimelineExactReaderOpenResult.Invalid(policyInvalid.Code, policyInvalid.Detail);
            if (policy is not HistoryTimelineStoreReadResult<PartitionPolicyRevision>.Found) return new HistoryTimelineExactReaderOpenResult.Invalid("PartitionPolicyUnavailable", found.Value.ActivePartitionPolicyDigest);
            var lifetime = new HistoryTimelineLifetime(lease);
            var reader = new HistoryTimelineReader(repository, ledger, lifetime);
            lease = null;
            return new HistoryTimelineExactReaderOpenResult.Opened(new HistoryTimelineExactReaderHandle(new HistoryTimelineScope(refId, timelineId), reader, lifetime));
        }
        catch (HistoryTimelineLeaseBusyException) { return new HistoryTimelineExactReaderOpenResult.Busy(); }
        catch (Exception exception) when (IsMaintenanceFailure(exception)) { return new HistoryTimelineExactReaderOpenResult.Invalid(MaintenanceErrorCode(exception), exception.Message); }
        finally { lease?.Dispose(); }
    }

    private static void RequireSafeDirectory(string repository, string path) {
        HistoryTimelineDurableFiles.RequireSafePath(repository, path);
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory) {
            throw new InvalidDataException("Timeline inventory encountered a reparse or non-directory entry.");
        }
    }

    private static bool IsRegularFile(string repository, string path) {
        HistoryTimelineDurableFiles.RequireSafePath(repository, path);
        return (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
    }

    private static RefId ParseRefName(string value) {
        if (value.Length != 16 || value.Any(static ch => ch is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))) {
            throw new InvalidDataException("Timeline inventory encountered a non-canonical RefId directory.");
        }
        return new RefId(Convert.ToUInt64(value, 16));
    }
}
