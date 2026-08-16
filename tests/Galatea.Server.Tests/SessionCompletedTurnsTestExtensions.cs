using Atelia.SessionJournal;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

internal static class SessionCompletedTurnsTestExtensions {
    internal static SessionCompletedTurnsSnapshot RequireSnapshot(
        this SessionCompletedTurnsReadResult result
    ) => Assert.IsType<SessionCompletedTurnsReadResult.Snapshot>(
        result
    ).Value;
}
