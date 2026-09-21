using Atelia.SessionJournal;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

/// <summary>
/// Test-only assertions for nullable projection members. Tests that inspect
/// completed turns, assistant DTOs, or delegation handles treat null as a
/// test failure rather than a silent dereference.
/// </summary>
internal static class GalateaTestProjectionAssertions {
    public static SessionTerminalActionProjection RequireTerminalAction(
        this SessionCompletedTurnProjection turn
    ) {
        Assert.NotNull(turn.TerminalAction);
        return turn.TerminalAction;
    }

    public static AssistantMessageDto RequireAssistant(this RecentTurnDto turn) {
        Assert.NotNull(turn.Assistant);
        return turn.Assistant;
    }

    public static GalateaDelegationSessionHandle RequireDelegationHandle(
        this CharacterSessionHost session
    ) {
        Assert.NotNull(session.DelegationHandle);
        return session.DelegationHandle;
    }
}
