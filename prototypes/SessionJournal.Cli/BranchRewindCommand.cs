using System.Globalization;
using System.Text.Json;
using Atelia.EventJournal;
using Atelia.RbfSegmentStore;
using Journal = Atelia.EventJournal.EventJournal;

namespace Atelia.SessionJournal.Cli;

/// <summary>
/// Offline raw-lineage repair, deliberately separate from completed-turn Undo.
/// The caller must stop all repository owners; EventJournal is single-driver.
/// </summary>
internal static class BranchRewindCommand {
    private static readonly EventJournalOptions StrictOptions = new() {
        EventSegmentStoreOptions = new RbfSegmentStoreOptions {
            RecoverActiveTailOnOpen = false
        },
        RefSegmentStoreOptions = new RbfSegmentStoreOptions {
            RecoverActiveTailOnOpen = false
        },
        RefOpLogOptions = new RefOpLogOptions {
            RecoverActiveTailOnOpen = false
        }
    };

    internal static int Run(CliOptions options) {
        options.EnsureOnly(
            "input", "branch", "steps", "apply", "confirm-ref",
            "expected-head", "confirm-target", "accept-external-effects"
        );
        string path = Path.GetFullPath(options.RequireSingle("input"));
        string branch = options.RequireSingle("branch");
        string? stepsText = options.GetOptionalSingle("steps");
        int steps = 1;
        if (stepsText is not null
            && (!int.TryParse(stepsText, NumberStyles.None,
                CultureInfo.InvariantCulture, out steps)
                || steps is < 1 or > 10000)) {
            throw new ArgumentException("--steps must be an integer from 1 to 10000.");
        }
        bool apply = options.HasSingleFlag("apply");
        bool acceptsEffects = options.HasSingleFlag("accept-external-effects");
        string? refText = options.GetOptionalSingle("confirm-ref");
        string? headText = options.GetOptionalSingle("expected-head");
        string? targetText = options.GetOptionalSingle("confirm-target");
        RefId? expectedRef = null;
        EventAddress? expectedHead = null;
        EventAddress? expectedTarget = null;
        if (apply) {
            if (!acceptsEffects || refText is null || headText is null || targetText is null) {
                throw new ArgumentException(
                    "--apply requires --confirm-ref, --expected-head, --confirm-target "
                    + "and --accept-external-effects. Stop every repository owner and back up first."
                );
            }
            var parsedRef = RefId.ParseHex(refText);
            if (parsedRef.IsFailure || parsedRef.Value.IsDefault) {
                throw new ArgumentException("--confirm-ref must be a nonzero canonical RefId.");
            }
            expectedRef = parsedRef.Value;
            expectedHead = ParseAddress(headText, "expected-head");
            expectedTarget = ParseAddress(targetText, "confirm-target");
        }
        else if (refText is not null || headText is not null || targetText is not null || acceptsEffects) {
            throw new ArgumentException("Confirmation options require --apply; preview has no confirmation options.");
        }

        CliIo.EnsurePathChainHasNoReparsePoint(path, "--input");
        bool moveAttempted = false;
        try {
            if (apply) {
                // RW EventJournal open can create missing infrastructure and its
                // ref loader can fall back before a bad target. Reject either
                // shape with a strict read-only open before acquiring the writer.
                using Journal preflight = Journal.OpenReadOnlyExisting(path, StrictOptions);
                RefId observedRef = preflight.OpenBranch(branch).Unwrap();
                if (observedRef != expectedRef || preflight.GetHead(observedRef) != expectedHead) {
                    throw new InvalidOperationException("Stale branch RefId or head; obtain a fresh preview.");
                }
            }
            // Preview never opens a writer or repairs a tail. Apply also disables
            // all open-time tail repair, even when confirmation later fails.
            using Journal journal = apply
                ? Journal.OpenExisting(path, StrictOptions)
                : Journal.OpenReadOnlyExisting(path, StrictOptions);
            RefId refId = journal.OpenBranch(branch).Unwrap();
            EventAddress head = journal.GetHead(refId)
                ?? throw new InvalidOperationException("Cannot rewind an empty branch.");
            if (apply && (refId != expectedRef || head != expectedHead)) {
                throw new InvalidOperationException("Stale branch RefId or head; obtain a fresh preview.");
            }
            // Checked Parent traversal only: no physical-order or reflog-order
            // fallback, no compiled cache, and no invented null/genesis target.
            var removed = new List<RewindEvent>();
            var visited = new HashSet<EventAddress>();
            EventAddress cursor = head;
            for (int index = 0; index < steps; index++) {
                if (!visited.Add(cursor)) {
                    throw new InvalidDataException("Parent cycle in rewind suffix.");
                }
                EventFrameHeader header = journal.ReadEventHeaderChecked(cursor).Unwrap();
                removed.Add(new RewindEvent(
                    EventAddressTextCodec.Format(cursor), header.OpaqueEventKind
                ));
                cursor = header.Parent
                    ?? throw new InvalidOperationException("Rewind would cross the root event; no change made.");
            }
            if (!visited.Add(cursor)) {
                throw new InvalidDataException("Parent cycle at rewind target.");
            }
            EventFrameHeader targetHeader = journal.ReadEventHeaderChecked(cursor).Unwrap();
            if (apply && cursor != expectedTarget) {
                throw new InvalidOperationException("Rewind target does not match --confirm-target; no change made.");
            }
            if (apply) {
                moveAttempted = true;
                journal.MoveRef(refId, head, cursor).Unwrap();
                if (journal.GetHead(refId) != cursor) {
                    throw new InvalidDataException("Ref move did not retain the expected target.");
                }
            }
            Console.WriteLine(JsonSerializer.Serialize(new {
                schema = "atelia.session-journal.branch-rewind.v1",
                status = apply ? "applied" : "preview",
                repository = path,
                branch,
                refId = refId.ToHexString(),
                beforeHead = EventAddressTextCodec.Format(head),
                targetHead = EventAddressTextCodec.Format(cursor),
                targetKind = targetHeader.OpaqueEventKind,
                steps,
                removed,
                warning = "Raw Parent steps, not turns or reflog entries. "
                    + "No SessionJournal phase validation; no external-effect or sidecar rollback. "
                    + "Raw events and reflog remain recoverable. Validate and reopen owners after apply."
            }));
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException
            or UnauthorizedAccessException or NotSupportedException) {
            Console.Error.WriteLine(
                moveAttempted
                    ? "rewind-indeterminate: ref move may have committed. Inspect fresh head/reflog; do not retry automatically."
                    : "rewind-unavailable: no ref move attempted."
            );
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    private static EventAddress ParseAddress(string value, string option) =>
        EventAddressTextCodec.TryParse(value, out EventAddress address)
            && EventAddressTextCodec.Format(address) == value
                ? address
                : throw new ArgumentException($"--{option} must be a canonical EventAddress.");

    private sealed record RewindEvent(string Address, uint Kind);
}
