namespace Atelia.Galatea.Server.Tests;

internal static class GalateaDelegationTestInputs {
    internal static Atelia.SessionJournal.SessionInputContent InternalMailInput(
        GalateaDelegationSqliteStore store, GalateaInternalMailOutboxSnapshot outbox) {
        var sender = Sender(store, outbox.FromCharacterName);
        var message = GalateaCharacterMailDeliveryReconciler.RestoreMessage(new(
            sender.Id, store, outbox));
        return GalateaObservationContent.Create(new GalateaFreshInput.InboundMail(message, Sender: sender),
            DateTimeOffset.UnixEpoch, new GalateaSenderSnapshot("character", outbox.TargetCharacterId, message.To));
    }
    internal static void ImportQueuedLegacyTasks(GalateaDelegationSqliteStore store) {
        // Explicit historical fixture import, used only by legacy preflight
        // cases whose original durable body was already the submitted task.
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder {
            DataSource = Path.Combine(store.StoreDirectory, GalateaDelegationSqliteStore.DatabaseFileName),
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE outbound_mail SET content_format='legacy-task', sender_name=NULL WHERE state='Queued';";
        command.ExecuteNonQuery();
    }
    // Callers explicitly choose their fixture Character's name. This helper
    // never consults current configuration to infer a historic author.
    internal static GalateaSenderSnapshot Sender(GalateaDelegationSqliteStore store, string characterName) =>
        new("character", store.ReadSnapshot().Owner.CharacterId, characterName);

    internal static string Task(GalateaDelegationSqliteStore store, string dispatchId) {
        GalateaDelegationStateSnapshot snapshot = store.ReadSnapshot();
        GalateaOutboundMailSnapshot mail = snapshot.Mails.Single(value => value.DispatchId == dispatchId);
        string body = mail.Body ?? throw new InvalidDataException("Fixture task content is unavailable.");
        if (mail.ContentFormat == "legacy-task") return body;
        return GalateaInputProjector.Instance.Project(GalateaDelegateTaskContent.Create(
            new GalateaSenderSnapshot("character", snapshot.Owner.CharacterId,
                mail.SenderName ?? throw new InvalidDataException("Fixture captured sender is unavailable.")),
            mail.Recipient, mail.Subject, body, mail.InReplyToMessageId,
            mail.SourceActionAddress, mail.DispatchId));
    }

    internal static GalateaTaskCommitment Commitment(GalateaDelegationSqliteStore store, string dispatchId) {
        GalateaOutboundMailSnapshot mail = store.ReadSnapshot().Mails.Single(value => value.DispatchId == dispatchId);
        // A negative transition test can refer to already started/terminal work;
        // its saved evidence remains authoritative even after content is cleared.
        if (mail.TaskSha256 is not null) return GalateaTaskCommitment.FromStored(mail);
        return GalateaTaskCommitment.FromTask(Task(store, dispatchId));
    }
}
