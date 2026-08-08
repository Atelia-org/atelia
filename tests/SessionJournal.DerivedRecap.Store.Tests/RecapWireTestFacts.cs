using Atelia.EventJournal;

namespace Atelia.SessionJournal.DerivedRecap.Store.Tests;

internal static class RecapWireTestFacts {
    public static string PriorDigest(RecapPriorContext priorContext)
        => DerivedRecapCodec.ComputePriorContextPayloadSha256(
            priorContext
        );

    private const string RuntimeHash =
        "0000000000000000000000000000000000000000000000000000000000000000";
    private const string PromptHash =
        "1111111111111111111111111111111111111111111111111111111111111111";

    public static SessionContextAnchorSetupReferences ResolveSetups(
        SessionJournalEngine engine,
        EventAddress address
    ) => engine.ResolveContextAnchorSetupReferences(address);

    public static RecapReplayBoundary ResolveBoundary(
        SessionJournalEngine engine,
        EventAddress address
    ) => new(address, ResolveSetups(engine, address));

    public static DerivedRecapSetManifest CreateManifest(
        SessionJournalEngine engine,
        EventAddress setAdmissionAnchor,
        IReadOnlyList<RecapBlockPlan> blocks,
        RecapPriorContext? priorContext = null
    ) => DerivedRecapCodec.CreateManifest(
        engine.BranchRefId,
        setAdmissionAnchor,
        ResolveSetups(engine, setAdmissionAnchor),
        priorContext ?? EmptyRecapPriorContext.Instance,
        blocks
    );

    public static DerivedRecapFrozenInput CreateFrozenInput(
        SessionJournalEngine engine,
        RecapBlockId recapBlockId,
        ContextHeaderBlockPath target,
        EventAddress absorbedThrough,
        string content
    ) => DerivedRecapCodec.CreateFrozenInput(
        recapBlockId,
        target,
        absorbedThrough,
        ResolveSetups(engine, absorbedThrough),
        content
    );

    public static IReadOnlyList<RecapReplayBoundary> ResolveBoundaries(
        SessionJournalEngine engine,
        IEnumerable<EventAddress> addresses
    ) => Array.AsReadOnly(
        addresses.Select(address => ResolveBoundary(engine, address))
            .ToArray()
    );

    public static SessionContextAnchorSetupReferences SyntheticSetups(
        EventAddress address
    ) => new(
        new SessionContextSetupReference(address, 1, RuntimeHash),
        new SessionContextSetupReference(address, 1, PromptHash)
    );

    public static RecapReplayBoundary SyntheticBoundary(
        EventAddress address
    ) => new(address, SyntheticSetups(address));
}
