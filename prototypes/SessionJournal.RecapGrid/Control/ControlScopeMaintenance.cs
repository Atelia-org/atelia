using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;

namespace Atelia.SessionJournal.RecapGrid.Control;

/// <summary>One canonical Control file discovered by maintenance-only inventory.</summary>
public sealed record RecapGridControlScope(RefId RefId, TimelineId TimelineId);

public abstract record RecapGridControlScopeInventoryResult {
    private RecapGridControlScopeInventoryResult() { }
    public sealed record Available(IReadOnlyList<RecapGridControlScope> Scopes)
        : RecapGridControlScopeInventoryResult;
    public sealed record Invalid(string Code, string Detail)
        : RecapGridControlScopeInventoryResult;
}

public static partial class RecapGridControlMaintenance {
    private const int MaximumInventoryScopes = 4_096;

    /// <summary>
    /// Cooperative durable-layout no-reparse inventory; it does not claim to
    /// prevent hostile rename races after a path has been observed.
    /// </summary>
    public static RecapGridControlScopeInventoryResult InventoryScopes(string repositoryPath) {
        try {
            string repository = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
            string refs = Path.Combine(repository, "control", "recap-grid", "v1", "refs");
            RequireSafe(repository, refs);
            if (!Directory.Exists(refs)) return new RecapGridControlScopeInventoryResult.Available([]);
            var scopes = new List<RecapGridControlScope>();
            foreach (string refPath in Directory.EnumerateFileSystemEntries(refs).Order(StringComparer.Ordinal)) {
                RequireDirectory(repository, refPath);
                RefId refId = ParseRefName(Path.GetFileName(refPath));
                string timelines = Path.Combine(refPath, "timelines");
                RequireSafe(repository, timelines);
                foreach (string entry in Directory.EnumerateFileSystemEntries(refPath)) {
                    string name = Path.GetFileName(entry);
                    if (name == "timelines") continue;
                    if (name == "cadence") {
                        RequireDirectory(repository, entry);
                        continue;
                    }
                    throw new InvalidDataException("Control inventory encountered a foreign Ref entry.");
                }
                RequireDirectory(repository, timelines);
                foreach (string timelinePath in Directory.EnumerateFileSystemEntries(timelines).Order(StringComparer.Ordinal)) {
                    RequireDirectory(repository, timelinePath);
                    var timelineId = new TimelineId(Path.GetFileName(timelinePath));
                    if (!string.Equals(timelineId.Value, Path.GetFileName(timelinePath), StringComparison.Ordinal)) throw new InvalidDataException("Control inventory encountered a non-canonical TimelineId directory.");
                    string state = Path.Combine(timelinePath, "control.json");
                    RequireSafe(repository, state);
                    if (!File.Exists(state)) throw new InvalidDataException("Control inventory encountered a scope without its canonical control.json.");
                    if ((File.GetAttributes(state) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) throw new InvalidDataException("Control inventory encountered a non-regular state file.");
                    foreach (string entry in Directory.EnumerateFileSystemEntries(timelinePath)) {
                        string name = Path.GetFileName(entry);
                        RequireSafe(repository, entry);
                        if (name is "control.json" or "lifetime.lock" or "writer.lock") {
                            if ((File.GetAttributes(entry) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) throw new InvalidDataException("Control inventory encountered a non-regular durable file.");
                            continue;
                        }
                        if (name.StartsWith(".control.json.", StringComparison.Ordinal)
                            && name.EndsWith(".tmp", StringComparison.Ordinal)
                            && Guid.TryParseExact(name[".control.json.".Length..^".tmp".Length], "N", out _)) continue;
                        throw new InvalidDataException("Control inventory encountered a foreign filename.");
                    }
                    RecapGridControlReaderOpenResult opened = OpenExactReader(
                        repository, refId, timelineId);
                    switch (opened) {
                        case RecapGridControlReaderOpenResult.Opened available:
                            available.Handle.Dispose();
                            break;
                        case RecapGridControlReaderOpenResult.Invalid invalid:
                            throw new ControlStoreException("ControlInventoryScopeInvalid", invalid.Code);
                        case RecapGridControlReaderOpenResult.Busy:
                            throw new ControlBusyException();
                        case RecapGridControlReaderOpenResult.UnsupportedSchema schema:
                            throw new ControlUnsupportedSchemaException(schema.SchemaVersion);
                        default:
                            throw new ControlStoreException("ControlInventoryScopeUnavailable", "Control scope disappeared or its Timeline is unavailable.");
                    }
                    scopes.Add(new RecapGridControlScope(refId, timelineId));
                    if (scopes.Count > MaximumInventoryScopes) throw new ControlLimitException("ControlInventoryScopeCount");
                }
            }
            return new RecapGridControlScopeInventoryResult.Available(scopes.OrderBy(static x => x.RefId.ToHexString(), StringComparer.Ordinal).ThenBy(static x => x.TimelineId.Value, StringComparer.Ordinal).ToArray());
        }
        catch (Exception exception) {
            (string code, string detail) = ControlError.Invalid(exception);
            return new RecapGridControlScopeInventoryResult.Invalid(code, detail);
        }
    }

    /// <summary>
    /// Opens the named Control scope and its named Timeline without consulting a locator.
    /// Apply callers require a stopped repository, exclusive lock, and witness recheck.
    /// </summary>
    public static RecapGridControlReaderOpenResult OpenExactReader(string repositoryPath, RefId refId, TimelineId timelineId) {
        HistoryTimelineExactReaderHandle? timeline = null;
        FileStream? lease = null;
        try {
            HistoryTimelineExactReaderOpenResult opened = HistoryTimelineMaintenance.OpenExactReader(repositoryPath, refId, timelineId);
            switch (opened) {
                case HistoryTimelineExactReaderOpenResult.Absent: return new RecapGridControlReaderOpenResult.TimelineAbsent();
                case HistoryTimelineExactReaderOpenResult.Busy: return new RecapGridControlReaderOpenResult.Busy();
                case HistoryTimelineExactReaderOpenResult.UnsupportedSchema schema: return new RecapGridControlReaderOpenResult.TimelineUnsupportedSchema(schema.SchemaVersion);
                case HistoryTimelineExactReaderOpenResult.Invalid invalid: return new RecapGridControlReaderOpenResult.Invalid(invalid.Code, invalid.Detail);
                case HistoryTimelineExactReaderOpenResult.Opened available: timeline = available.Handle; break;
                default: return new RecapGridControlReaderOpenResult.Invalid("TimelineExactReaderOpenOutcomeInvalid", "The exact Timeline reader returned an unknown outcome.");
            }
            var paths = new ControlPaths(repositoryPath, refId, timelineId);
            if (!ControlDurableFiles.StateExists(paths)) return new RecapGridControlReaderOpenResult.Absent();
            lease = ControlDurableFiles.AcquireSharedLifetime(paths);
            ControlState state = ControlState.Decode(ControlDurableFiles.ReadState(paths));
            RecapGridControlFactory.RequireScope(state, paths);
            var lifetime = new ControlLifetime(lease, timeline.DetachReaderHandle());
            // The exact Timeline handle is intentionally retained by ControlLifetime through a reader handle.
            lease = null;
            timeline = null;
            return new RecapGridControlReaderOpenResult.Opened(new RecapGridControlReaderHandle(new RecapGridControlReader(paths, lifetime), lifetime));
        }
        catch (ControlUnsupportedSchemaException schema) { return new RecapGridControlReaderOpenResult.UnsupportedSchema(schema.Version); }
        catch (ControlBusyException) { return new RecapGridControlReaderOpenResult.Busy(); }
        catch (Exception exception) { (string code, string detail) = ControlError.Invalid(exception); return new RecapGridControlReaderOpenResult.Invalid(code, detail); }
        finally { lease?.Dispose(); timeline?.Dispose(); }
    }

    private static void RequireSafe(string repository, string path) {
        var paths = new ControlPaths(repository, new RefId(1), new TimelineId("00000000000000000000000000000000"));
        paths.RequireSafe(path);
    }
    private static void RequireDirectory(string repository, string path) {
        RequireSafe(repository, path);
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory) throw new InvalidDataException("Control inventory encountered a reparse or non-directory entry.");
    }
    private static RefId ParseRefName(string value) {
        if (value.Length != 16 || value.Any(static ch => ch is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))) throw new InvalidDataException("Control inventory encountered a non-canonical RefId directory.");
        return new RefId(Convert.ToUInt64(value, 16));
    }
}
