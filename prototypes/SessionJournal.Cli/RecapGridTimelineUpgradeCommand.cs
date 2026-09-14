using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;

namespace Atelia.SessionJournal.Cli;

internal static partial class RecapGridCommands {
    private static int TimelineUpgradeSchemaV2(CliOptions options) {
        options.EnsureOnly("input", "ref", "timeline");
        string repository = options.RequireSingle("input");
        CliIo.EnsurePathChainHasNoReparsePoint(repository, "--input");
        string refText = options.RequireSingle("ref");
        if (!RefId.ParseHex(refText).TryUnwrap(out RefId refId, out _)
            || refId.IsDefault
            || refText != refId.ToHexString()) {
            throw new ArgumentException("--ref must be a canonical non-default physical RefId.");
        }
        var timelineId = new TimelineId(options.RequireSingle("timeline"));
        HistoryTimelineUpgradeResult result = HistoryTimelineMaintenance.UpgradeSchemaV2(
            repository, refId, timelineId);
        const string command = "timeline.upgrade-schema-v2";
        return result switch {
            HistoryTimelineUpgradeResult.Upgraded upgraded => Print(command, "upgraded", upgraded),
            HistoryTimelineUpgradeResult.AlreadyCurrent current => Print(command, "already-current", current),
            HistoryTimelineUpgradeResult.Absent => Print(command, "absent", exitCode: 2),
            HistoryTimelineUpgradeResult.Busy => Print(command, "busy", exitCode: 2),
            HistoryTimelineUpgradeResult.UnsupportedSchema unsupported =>
                Print(command, "unsupported-schema", unsupported, 2),
            HistoryTimelineUpgradeResult.LimitExceeded limit => Print(command, "limit", limit, 2),
            HistoryTimelineUpgradeResult.Invalid invalid => Print(command, "invalid", invalid, 2),
            HistoryTimelineUpgradeResult.PublishIndeterminate indeterminate =>
                Print(command, "publish-indeterminate", indeterminate, 2),
            _ => Print(command, "invalid-outcome", exitCode: 2)
        };
    }
}
