using Atelia.Galatea.Server.Mailbox;

namespace Atelia.Galatea.Server;

internal abstract record GalateaFreshInput {
    private GalateaFreshInput() { }

    internal abstract string DisplayText { get; }

    internal sealed record PlayerAction : GalateaFreshInput {
        internal PlayerAction(
            string text,
            IEnumerable<PlayerTurnNotice>? notices = null
        ) {
            var observation = new PlayerTurnObservation(
                text,
                notices
            );
            Text = observation.PlayerText;
            Notices = observation.Notices;
        }

        internal string Text { get; }
        internal IReadOnlyList<PlayerTurnNotice> Notices { get; }
        internal override string DisplayText => Text;
    }

    internal sealed record InboundMail(MailboxMessage Message)
        : GalateaFreshInput {
        internal override string DisplayText =>
            GalateaMailboxObservationEnvelope.FormatForDisplay(Message);
        internal string DurableObservation =>
            GalateaMailboxObservationEnvelope.Wrap(Message);
    }
}
