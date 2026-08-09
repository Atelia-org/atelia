using Atelia.EventJournal;

namespace Atelia.SessionJournal.DerivedRecap.Store.Tests;

internal static class RecapWireTestFacts {
    private const string RuntimeHash =
        "0000000000000000000000000000000000000000000000000000000000000000";
    private const string PromptHash =
        "1111111111111111111111111111111111111111111111111111111111111111";

    public static SessionContextAnchorSetupReferences SyntheticSetups(
        EventAddress address
    ) => new(
        new SessionContextSetupReference(address, 1, RuntimeHash),
        new SessionContextSetupReference(address, 1, PromptHash)
    );
}
