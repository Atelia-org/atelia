using Atelia.StateJournal;

namespace Atelia.TextAdv;

internal enum TerminalActionMode {
    Immediate,
    Large
}

internal abstract record TerminalActionExecutionPlan(string PreActionReason) {
    internal abstract TerminalActionMode Mode { get; }

    internal abstract string ActionKind { get; }

    internal abstract string ActionSummary { get; }

    internal abstract string? ActionPayload { get; }

    internal bool IsLargeAction => Mode == TerminalActionMode.Large;

    internal sealed record Explore(
        string Direction,
        string? Focus,
        string PreActionReason
    ) : TerminalActionExecutionPlan(PreActionReason) {
        internal override TerminalActionMode Mode => TerminalActionMode.Large;

        internal override string ActionKind => "large/explore";

        internal override string ActionSummary => Focus is null
            ? $"向 {Direction} 探索"
            : $"向 {Direction} 探索：{Focus}";

        internal override string? ActionPayload => GameSimulation.BuildExplorePayload(Direction, Focus);
    }

    internal sealed record RestAWhile(string PreActionReason) : TerminalActionExecutionPlan(PreActionReason) {
        internal override TerminalActionMode Mode => TerminalActionMode.Large;

        internal override string ActionKind => "large/rest-a-while";

        internal override string ActionSummary => "原地休息一会";

        internal override string? ActionPayload => null;
    }

    internal abstract record Interaction(
        string InteractionId,
        string VisibleLabel,
        string InteractionActionKind,
        string InteractionPayload,
        string PreActionReason
    ) : TerminalActionExecutionPlan(PreActionReason) {
        internal abstract InteractionExecutionSpec ExecutionSpec { get; }

        internal sealed record ImmediateSelf(
            string InteractionId,
            string VisibleLabel,
            string InteractionActionKind,
            string InteractionPayload,
            string PreActionReason
        ) : Interaction(InteractionId, VisibleLabel, InteractionActionKind, InteractionPayload, PreActionReason) {
            internal override TerminalActionMode Mode => TerminalActionMode.Immediate;

            internal override string ActionKind => "small/interact";

            internal override InteractionExecutionSpec ExecutionSpec => InteractionExecutionSpec.ImmediateSelf;
        }

        internal sealed record DeferredTurnEnd(
            string InteractionId,
            string VisibleLabel,
            string InteractionActionKind,
            string InteractionPayload,
            string PreActionReason
        ) : Interaction(InteractionId, VisibleLabel, InteractionActionKind, InteractionPayload, PreActionReason) {
            internal override TerminalActionMode Mode => TerminalActionMode.Immediate;

            internal override string ActionKind => "small/interact";

            internal override InteractionExecutionSpec ExecutionSpec => InteractionExecutionSpec.DeferredTurnEnd;
        }

        internal sealed record WorkingStart(
            string InteractionId,
            string VisibleLabel,
            string InteractionActionKind,
            string InteractionPayload,
            string PreActionReason
        ) : Interaction(InteractionId, VisibleLabel, InteractionActionKind, InteractionPayload, PreActionReason) {
            internal override TerminalActionMode Mode => TerminalActionMode.Large;

            internal override string ActionKind => "large/interact";

            internal override InteractionExecutionSpec ExecutionSpec => InteractionExecutionSpec.WorkingStart;
        }

        internal sealed record TurnEnding(
            string InteractionId,
            string VisibleLabel,
            string InteractionActionKind,
            string InteractionPayload,
            string PreActionReason
        ) : Interaction(InteractionId, VisibleLabel, InteractionActionKind, InteractionPayload, PreActionReason) {
            internal override TerminalActionMode Mode => TerminalActionMode.Large;

            internal override string ActionKind => "large/interact";

            internal override InteractionExecutionSpec ExecutionSpec => InteractionExecutionSpec.TurnEnding;
        }

        internal override string ActionSummary => $"{VisibleLabel} ({InteractionActionKind})";

        internal override string? ActionPayload => InteractionPayload;
    }
}

internal abstract class InteractionExecutionSpec {
    internal static InteractionExecutionSpec ImmediateSelf { get; } = new ImmediateSelfSpec();

    internal static InteractionExecutionSpec DeferredTurnEnd { get; } = new DeferredTurnEndSpec();

    internal static InteractionExecutionSpec WorkingStart { get; } = new WorkingStartSpec();

    internal static InteractionExecutionSpec TurnEnding { get; } = new TurnEndingSpec();

    internal static AteliaResult<InteractionExecutionSpec> Describe(InteractionPerception interaction) {
        ArgumentNullException.ThrowIfNull(interaction);

        if (GameSimulation.SupportsImmediateSelfInteraction(interaction)) { return ImmediateSelf; }
        if (GameSimulation.SupportsDeferredTurnEndInteraction(interaction)) { return DeferredTurnEnd; }
        if (GameSimulation.SupportsWorkingInteraction(interaction)) { return WorkingStart; }
        if (interaction.TurnCost == 0) {
            return new TextAdvError(
                "TextAdv.UnsupportedInteractionExecutionSpec",
                "这个 interaction 目前属于零回合但非即时私有效果的动作类型。",
                "当前实现还不能安全结算它；请先改用会结束回合的动作，或补完 turn-end / working 流程。"
            );
        }

        return TurnEnding;
    }

    internal abstract TerminalActionExecutionPlan.Interaction BuildPlan(
        InteractionPerception interaction,
        string preActionReason
    );

    internal abstract Task<AsyncAteliaResult<ActionResolution>> ExecuteAsync(
        DurableDict<string> root,
        TerminalActionExecutionPlan.Interaction interactionPlan,
        string validatorFeedback,
        CancellationToken cancellationToken
    );

    private sealed class ImmediateSelfSpec : InteractionExecutionSpec {
        internal override TerminalActionExecutionPlan.Interaction BuildPlan(
            InteractionPerception interaction,
            string preActionReason
        ) {
            return new TerminalActionExecutionPlan.Interaction.ImmediateSelf(
                interaction.InteractionId,
                interaction.VisibleLabel,
                interaction.ActionKind,
                GameSimulation.BuildInteractionPayload(interaction),
                preActionReason
            );
        }

        internal override Task<AsyncAteliaResult<ActionResolution>> ExecuteAsync(
            DurableDict<string> root,
            TerminalActionExecutionPlan.Interaction interactionPlan,
            string validatorFeedback,
            CancellationToken cancellationToken
        ) {
            return GameSimulation.ApplyImmediateSelfInteractionAsync(
                root,
                interactionPlan.InteractionId,
                interactionPlan.PreActionReason,
                validatorFeedback,
                cancellationToken
            );
        }
    }

    private sealed class DeferredTurnEndSpec : InteractionExecutionSpec {
        internal override TerminalActionExecutionPlan.Interaction BuildPlan(
            InteractionPerception interaction,
            string preActionReason
        ) {
            return new TerminalActionExecutionPlan.Interaction.DeferredTurnEnd(
                interaction.InteractionId,
                interaction.VisibleLabel,
                interaction.ActionKind,
                GameSimulation.BuildInteractionPayload(interaction),
                preActionReason
            );
        }

        internal override Task<AsyncAteliaResult<ActionResolution>> ExecuteAsync(
            DurableDict<string> root,
            TerminalActionExecutionPlan.Interaction interactionPlan,
            string validatorFeedback,
            CancellationToken cancellationToken
        ) {
            return Task.FromResult(
                GameSimulation.ApplyDeferredTurnEndInteraction(
                    root,
                    interactionPlan.InteractionId,
                    interactionPlan.PreActionReason,
                    validatorFeedback
                )
            );
        }
    }

    private sealed class WorkingStartSpec : InteractionExecutionSpec {
        internal override TerminalActionExecutionPlan.Interaction BuildPlan(
            InteractionPerception interaction,
            string preActionReason
        ) {
            return new TerminalActionExecutionPlan.Interaction.WorkingStart(
                interaction.InteractionId,
                interaction.VisibleLabel,
                interaction.ActionKind,
                GameSimulation.BuildInteractionPayload(interaction),
                preActionReason
            );
        }

        internal override Task<AsyncAteliaResult<ActionResolution>> ExecuteAsync(
            DurableDict<string> root,
            TerminalActionExecutionPlan.Interaction interactionPlan,
            string validatorFeedback,
            CancellationToken cancellationToken
        ) {
            return GameSimulation.ApplyWorkingInteractionAsync(
                root,
                interactionPlan.InteractionId,
                interactionPlan.PreActionReason,
                validatorFeedback,
                cancellationToken
            );
        }
    }

    private sealed class TurnEndingSpec : InteractionExecutionSpec {
        internal override TerminalActionExecutionPlan.Interaction BuildPlan(
            InteractionPerception interaction,
            string preActionReason
        ) {
            return new TerminalActionExecutionPlan.Interaction.TurnEnding(
                interaction.InteractionId,
                interaction.VisibleLabel,
                interaction.ActionKind,
                GameSimulation.BuildInteractionPayload(interaction),
                preActionReason
            );
        }

        internal override Task<AsyncAteliaResult<ActionResolution>> ExecuteAsync(
            DurableDict<string> root,
            TerminalActionExecutionPlan.Interaction interactionPlan,
            string validatorFeedback,
            CancellationToken cancellationToken
        ) {
            return GameSimulation.ApplyInteractionAsync(
                root,
                interactionPlan.InteractionId,
                interactionPlan.PreActionReason,
                validatorFeedback,
                cancellationToken
            );
        }
    }
}
