using Atelia.Galatea.Prompts;
using Atelia.Galatea.Server.Mailbox;

namespace Atelia.Galatea.Server;

internal abstract record GalateaFreshInput {
    private GalateaFreshInput() { }

    internal abstract string DisplayText { get; }

    internal sealed record PlayerAction : GalateaFreshInput {
        internal PlayerAction(
            string text,
            GalateaSenderSnapshot sender,
            IEnumerable<PlayerTurnNotice>? notices = null
        ) {
            var observation = new PlayerTurnObservation(
                text,
                notices
            );
            Text = observation.PlayerText;
            Notices = observation.Notices;
            ArgumentNullException.ThrowIfNull(sender);
            if (sender.Kind != "player") {
                throw new ArgumentException("Player action sender must be a Player.", nameof(sender));
            }
            Sender = sender;
        }

        internal string Text { get; }
        internal IReadOnlyList<PlayerTurnNotice> Notices { get; }
        internal GalateaSenderSnapshot Sender { get; }
        internal override string DisplayText => Text;
    }

    internal sealed record DelegateReply : GalateaFreshInput {
        internal DelegateReply(IEnumerable<PlayerTurnNotice> notices) {
            Notices = PlayerTurnObservation.FreezeDelegateReplyNotices(
                notices
            );
        }

        internal IReadOnlyList<PlayerTurnNotice> Notices { get; }
        internal override string DisplayText =>
            PlayerTurnObservationEnvelope.DelegateReplyDisplayText;
    }

    internal sealed record HeartbeatActivation : GalateaFreshInput {
        // Accepted periodic-activation meaning, not a measurement of wall-clock downtime.
        internal const int ExternalIntervalMinutes = Atelia.Galatea.Input.GalateaObservationLimits.ExternalIntervalMinutes;
        internal HeartbeatActivation(GalateaCharacterName characterName) {
            CharacterName = characterName
                ?? throw new ArgumentNullException(nameof(characterName));
        }

        internal GalateaCharacterName CharacterName { get; }
        internal override string DisplayText =>
            PlayerTurnObservationEnvelope.RenderHeartbeatActivationBody(
                CharacterName
            );
    }

    /// <summary>
    /// A mailbox Observation.  HTTP mail has no delivery binding; the binding
    /// is an in-process capability carried only by the character-mail relay.
    /// </summary>
    internal sealed record InboundMail(
        MailboxMessage Message,
        GalateaInternalMailDeliveryBinding? InternalDelivery = null,
        GalateaSenderSnapshot? InjectedBy = null,
        GalateaSenderSnapshot? Sender = null
    )
        : GalateaFreshInput {
        internal override string DisplayText =>
            GalateaMailboxObservationEnvelope.FormatForDisplay(Message);
        internal string DurableObservation =>
            GalateaMailboxObservationEnvelope.Wrap(Message);
    }
}
