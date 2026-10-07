using Atelia.Galatea.Prompts;
using Atelia.Galatea.Input;
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
        internal HeartbeatActivation(GalateaCharacterName characterName, int intervalMinutes) {
            CharacterName = characterName
                ?? throw new ArgumentNullException(nameof(characterName));
            if (intervalMinutes is < 1 or > GalateaObservationLimits.MaximumExternalIntervalMinutes) {
                throw new ArgumentOutOfRangeException(nameof(intervalMinutes));
            }
            IntervalMinutes = intervalMinutes;
        }

        internal GalateaCharacterName CharacterName { get; }
        internal int IntervalMinutes { get; }
        internal override string DisplayText =>
            PlayerTurnObservationEnvelope.RenderHeartbeatActivationBody(
                CharacterName, IntervalMinutes
            );
    }

    /// <summary>A mailbox Observation with one closed, accepted origin.</summary>
    internal sealed record InboundMail : GalateaFreshInput {
        internal InboundMail(MailboxMessage message, GalateaInboundMailOrigin origin) {
            Message = message ?? throw new ArgumentNullException(nameof(message));
            Origin = origin ?? throw new ArgumentNullException(nameof(origin));
        }

        internal MailboxMessage Message { get; }
        internal GalateaInboundMailOrigin Origin { get; }
        internal override string DisplayText =>
            (Origin is GalateaInboundMailOrigin.ImapDelivery ? "外部 e-mail（发件人是未经认证的声明）\n" : "")
            + GalateaMailboxObservationEnvelope.FormatForDisplay(Message);
    }
}

/// <summary>Only the three actual mail entrypoints may choose mail provenance.</summary>
internal abstract record GalateaInboundMailOrigin {
    private GalateaInboundMailOrigin() { }

    internal virtual GalateaMailDeliveryBinding? DeliveryBinding => null;

    internal sealed record PlayerInjection : GalateaInboundMailOrigin {
        internal PlayerInjection(GalateaSenderSnapshot player) {
            ArgumentNullException.ThrowIfNull(player);
            if (player.Kind != "player") {
                throw new ArgumentException("Mail injection requires a Player identity.", nameof(player));
            }
            Player = player;
        }

        internal GalateaSenderSnapshot Player { get; }
    }

    internal sealed record CharacterDelivery : GalateaInboundMailOrigin {
        internal CharacterDelivery(GalateaSenderSnapshot character, GalateaInternalMailDeliveryBinding binding) {
            ArgumentNullException.ThrowIfNull(character);
            if (character.Kind != "character") {
                throw new ArgumentException("Character mail requires a Character identity.", nameof(character));
            }
            Character = character;
            Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        }

        internal GalateaSenderSnapshot Character { get; }
        internal GalateaInternalMailDeliveryBinding Binding { get; }
        internal override GalateaMailDeliveryBinding DeliveryBinding => Binding;
    }

    internal sealed record ImapDelivery : GalateaInboundMailOrigin {
        internal ImapDelivery(GalateaImapMailDeliveryBinding binding, int attachmentCount) {
            Binding = binding ?? throw new ArgumentNullException(nameof(binding));
            if (attachmentCount < 0) { throw new ArgumentOutOfRangeException(nameof(attachmentCount)); }
            AttachmentCount = attachmentCount;
        }

        internal GalateaImapMailDeliveryBinding Binding { get; }
        internal int AttachmentCount { get; }
        internal override GalateaMailDeliveryBinding DeliveryBinding => Binding;
    }
}
