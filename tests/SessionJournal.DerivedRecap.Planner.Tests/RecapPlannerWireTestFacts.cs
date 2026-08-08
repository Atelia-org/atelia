using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;

namespace Atelia.SessionJournal.DerivedRecap.Planner.Tests;

internal static class RecapPlannerWireTestFacts {
    internal static string PriorDigest(
        RecapPriorContext priorContext
    ) => DerivedRecapCodec.ComputePriorContextPayloadSha256(
        priorContext
    );

    internal static SessionContextAnchorSetupReferences SetupsAt(
        SessionJournalEngine engine,
        EventAddress address
    ) {
        ArgumentNullException.ThrowIfNull(engine);
        return engine.ResolveContextAnchorSetupReferences(address);
    }

    internal static SessionContextAnchorSetupReferences SetupsAt(
        SessionHistoryPlanningWindow window,
        EventAddress address
    ) {
        ArgumentNullException.ThrowIfNull(window);
        if (address == window.StartExclusive) {
            return window.StartSetups;
        }
        if (window.ReplaySafeBoundarySetups.TryGetValue(
                address,
                out SessionContextAnchorSetupReferences? setups
            )) {
            return setups
                ?? throw new InvalidDataException(
                    "Planning window contains null boundary setups."
                );
        }
        if (address == window.ObservedRawHead) {
            return window.EndSetups;
        }
        throw new ArgumentException(
            "Address is not an exact boundary in the planning window.",
            nameof(address)
        );
    }

    internal static RecapReplayBoundary Boundary(
        SessionHistoryPlanningWindow window,
        EventAddress address
    ) => new(address, SetupsAt(window, address));

    internal static IReadOnlyList<RecapReplayBoundary> Boundaries(
        SessionHistoryPlanningWindow window,
        IEnumerable<EventAddress> addresses
    ) {
        ArgumentNullException.ThrowIfNull(addresses);
        return Array.AsReadOnly([
            .. addresses.Select(address => Boundary(window, address))
        ]);
    }

    internal static SessionContextAnchorSetupReferences SyntheticSetups(
        EventAddress address
    ) => new(
        new SessionContextSetupReference(address, 1, new('a', 64)),
        new SessionContextSetupReference(address, 1, new('b', 64))
    );

    internal static RecapReplayBoundary SyntheticBoundary(
        EventAddress address
    ) => new(address, SyntheticSetups(address));

    internal static SessionContextAnchorSetupReferences WrongSetups(
        SessionContextAnchorSetupReferences actual
    ) {
        ArgumentNullException.ThrowIfNull(actual);
        string replacement = actual.RuntimeConfig.PayloadSha256
            == new string('f', 64)
                ? new string('e', 64)
                : new string('f', 64);
        return actual with {
            RuntimeConfig = actual.RuntimeConfig with {
                PayloadSha256 = replacement
            }
        };
    }
}
