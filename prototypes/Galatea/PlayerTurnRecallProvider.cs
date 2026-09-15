using System.Text;
using Atelia.EventJournal;
using Atelia.Galatea.Server.CharacterMemory;
using Atelia.SessionJournal;

namespace Atelia.Galatea.Server;

internal sealed record GalateaRecentVisibleAction {
    internal const string Kind = "gm-visible-action";
    internal GalateaRecentVisibleAction(string text, EventAddress sourceStartInclusive, EventAddress sourceEndInclusive) {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (sourceStartInclusive == default || sourceEndInclusive == default) {
            throw new ArgumentException("Recent visible Action requires its selected source range.");
        }
        try {
            _ = GalateaBoundedJson.StrictUtf8.GetByteCount(text);
        }
        catch (EncoderFallbackException exception) {
            throw new ArgumentException(
                "Recent visible Action must contain valid Unicode.",
                nameof(text),
                exception
            );
        }
        Text = text;
        SourceStartInclusive = sourceStartInclusive;
        SourceEndInclusive = sourceEndInclusive;
    }

    internal string Text { get; }
    internal EventAddress SourceStartInclusive { get; }
    internal EventAddress SourceEndInclusive { get; }
}

/// <summary>Required semantic content of the retrieval query, independent of its text layout.</summary>
internal static class GalateaMemoRecallInputContract {
    internal const string Instructions = GalateaSystemInstructionContent.ObservationInputMeaning + "\n\n" + """
本查询只投影当前 Observation 的必要内容及选入的外部结果：currentTurn.sender 对应 Observation.sender；currentTurn.externalLocalTimestamp 对应同名的外界时间。currentTurn.trigger.kind 对应 Observation.kind；player-action 的 currentTurn.trigger.playerText 对应 action.text；heartbeat-activation 的 currentTurn.trigger.characterName 对应 action.character.name，externalIntervalMinutes 是既有激活周期语义，不声称测量了精确 elapsed。externalNotices 是从 Observation.notices 中选入的 reply / delivery-failure，保留各自来源；Note 保存回执没有作为检索依据选入。当前输入尚未选择 recalls。
recentVisibleActions 的每项是 GM 可见的模型 Action 叙述，保留所选 History unit 的 sourceStartInclusive / sourceEndInclusive 地址范围；它不是某个 Character 的署名发言，文本中的台词仍只是该叙述的一部分。只使用实际选入的证据，不自行恢复省略项。旧输入没有提供 sender 等字段时，来源保持未知，不从当前注册账号猜测。
""";
}

internal sealed record GalateaPlayerTurnRecallContext {
    internal GalateaPlayerTurnRecallContext(
        RecallBarrier recallBarrier,
        CharacterNoteOriginBarrier characterNoteOriginBarrier,
        IEnumerable<GalateaRecentVisibleAction>? recentVisibleActions = null
    ) {
        ArgumentNullException.ThrowIfNull(recallBarrier);
        ArgumentNullException.ThrowIfNull(characterNoteOriginBarrier);
        GalateaRecentVisibleAction[] frozen = recentVisibleActions?.Select(
            static action => action ?? throw new ArgumentException(
                "Recall context Action collections must not contain null items.",
                nameof(recentVisibleActions)
            )
        ).ToArray() ?? [];

        RecallBarrier = recallBarrier;
        CharacterNoteOriginBarrier = characterNoteOriginBarrier;
        RecentVisibleActions = Array.AsReadOnly(frozen);
    }

    internal RecallBarrier RecallBarrier { get; }
    internal CharacterNoteOriginBarrier CharacterNoteOriginBarrier { get; }
    internal IReadOnlyList<GalateaRecentVisibleAction> RecentVisibleActions {
        get;
    }
}

// The historical PlayerTurn name denotes the shared typed Observation
// contract: PlayerAction, HeartbeatActivation, and DelegateReply all use it.
internal sealed record GalateaPlayerTurnRecallRequest {
    internal GalateaPlayerTurnRecallRequest(
        GalateaCharacterConfig user,
        EventAddress completionBoundary,
        PlayerTurnObservation currentObservation,
        GalateaPlayerTurnRecallContext context,
        Func<IReadOnlyList<PlayerTurnRecall>, bool>? fitsRecalls = null,
        SessionInputContent? currentInput = null
    ) {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(currentObservation);
        ArgumentNullException.ThrowIfNull(context);
        if (completionBoundary == default) {
            throw new ArgumentException(
                "Completion boundary cannot be the default EventAddress.",
                nameof(completionBoundary)
            );
        }
        if (currentObservation.Recalls.Count != 0) {
            throw new ArgumentException(
                "A preliminary recall Observation must not already contain recalls.",
                nameof(currentObservation)
            );
        }
        if (currentObservation.ExternalLocalTimestamp is null) {
            throw new ArgumentException(
                "A preliminary recall Observation requires its sampled external local timestamp.",
                nameof(currentObservation)
            );
        }

        Character = user;
        CompletionBoundary = completionBoundary;
        CurrentObservation = currentObservation;
        Context = context;
        FitsRecalls = fitsRecalls;
        if (currentInput is not null) {
            PlayerTurnObservation captured = GalateaObservationContent.ReadPlayerTurn(currentInput);
            if (captured.TriggerKind != currentObservation.TriggerKind
                || captured.ExternalLocalTimestamp != currentObservation.ExternalLocalTimestamp
                || captured.TriggerKind == PlayerTurnObservationTriggerKind.PlayerAction && captured.PlayerText != currentObservation.PlayerText) {
                throw new ArgumentException("Recall source input must match its selected current Observation.", nameof(currentInput));
            }
        }
        CurrentInput = currentInput;
    }

    internal GalateaCharacterConfig Character { get; }
    internal EventAddress CompletionBoundary { get; }
    internal PlayerTurnObservation CurrentObservation { get; }
    internal GalateaPlayerTurnRecallContext Context { get; }
    internal Func<IReadOnlyList<PlayerTurnRecall>, bool>? FitsRecalls { get; }
    internal SessionInputContent? CurrentInput { get; }
}

internal interface IGalateaPlayerTurnRecallProvider {
    ValueTask<IReadOnlyList<PlayerTurnRecall>> SelectRecallsAsync(
        GalateaPlayerTurnRecallRequest request,
        CancellationToken cancellationToken
    );
}

internal interface IGalateaPlayerTurnRecallPlanningProvider
    : IGalateaPlayerTurnRecallProvider {
    ValueTask<GalateaMemoRecallPlanningResult> PlanRecallsAsync(
        GalateaPlayerTurnRecallRequest request,
        CancellationToken cancellationToken
    );
}

internal delegate IGalateaPlayerTurnRecallProvider
    GalateaPlayerTurnRecallProviderFactory(
        GalateaCharacterConfig user,
        CharacterNoteDefaultPodReconciler? characterMemory
    );

internal sealed class DisabledGalateaPlayerTurnRecallProvider
    : IGalateaPlayerTurnRecallProvider {
    private DisabledGalateaPlayerTurnRecallProvider() { }

    internal static DisabledGalateaPlayerTurnRecallProvider Instance {
        get;
    } = new();

    public ValueTask<IReadOnlyList<PlayerTurnRecall>> SelectRecallsAsync(
        GalateaPlayerTurnRecallRequest request,
        CancellationToken cancellationToken
    ) => throw new InvalidOperationException(
        "The disabled player-turn recall provider must be bypassed before recall context construction."
    );
}
