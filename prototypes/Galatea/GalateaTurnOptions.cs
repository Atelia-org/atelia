using Atelia.EventJournal;

namespace Atelia.Galatea.Server;

internal enum GalateaTurnMode {
    FreshSend,
    Resume
}

internal sealed record GalateaTurnOptions(
    string ConnectionId,
    GalateaTurnMode Mode = GalateaTurnMode.FreshSend,
    bool RestartUncertainCompletion = false,
    EventAddress? ExpectedHead = null
);
