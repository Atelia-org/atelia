namespace Atelia.TextAdv2.WorldTruth;

internal static class EmbodiedProcessRules {
    public static void EnsureCanInterrupt(Actor actor, string requestedOperationDescription) {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedOperationDescription);

        if (actor.EmbodiedState is ActorActiveProcessState { IsInterruptible: false } activeProcess) {
            throw new InvalidOperationException(
                $"Actor '{actor.Id}' cannot {requestedOperationDescription} while current process '{activeProcess.ProcessKind}' is non-interruptible."
            );
        }
    }
}
