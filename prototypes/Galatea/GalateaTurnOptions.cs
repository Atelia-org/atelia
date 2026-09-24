using Atelia.EventJournal;

namespace Atelia.Galatea.Server;

internal enum GalateaTurnMode {
    FreshSend,
    Resume
}

internal sealed record GalateaTurnOptions(
    string ConnectionId,
    GalateaTurnMode Mode = GalateaTurnMode.FreshSend,
    EventAddress? ExpectedHead = null,
    GalateaConnectionStateSnapshot? ConnectionState = null
);
