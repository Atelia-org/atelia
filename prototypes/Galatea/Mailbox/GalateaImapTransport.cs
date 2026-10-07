using System.Net;
using System.Security.Cryptography.X509Certificates;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;

namespace Atelia.Galatea.Server.Mailbox;

internal static class GalateaImapBounds {
    internal const int MaximumRawBytes = 2 * 1024 * 1024;
    internal const int MaximumMimeDepth = 32;
    internal const int MaximumFromBytes = 1024;
    internal const int MaximumSearchUidSpan = 256;
    internal const int MaximumMessagesPerPoll = 16;
}

internal sealed record GalateaImapRawMessage(byte[]? Bytes, string? RejectedCode, bool Missing = false);

internal interface IGalateaImapTransport {
    Task<IGalateaImapConnection> OpenAsync(GalateaEmailAccount account, CancellationToken cancellationToken);
}

internal interface IGalateaImapConnection : IAsyncDisposable {
    uint UidValidity { get; }
    uint? UidNext { get; }
    Task<IReadOnlyList<uint>> SearchUidsAsync(uint first, uint last, CancellationToken cancellationToken);
    Task<GalateaImapRawMessage> ReadRawAsync(uint uid, CancellationToken cancellationToken);
}

// Never expose MailKit exceptions: server responses can contain credentials or message text.
internal sealed class GalateaImapReadException(string code) : IOException(code) {
    internal string Code { get; } = code;
}

internal sealed class GalateaNetworkImapTransport(
    GalateaImapConfig config,
    X509Certificate2? trustedLoopbackCertificateForTest = null
) : IGalateaImapTransport {
    public async Task<IGalateaImapConnection> OpenAsync(
        GalateaEmailAccount account, CancellationToken cancellationToken
    ) {
        var endpoint = account.Imap ?? throw new GalateaImapReadException("IMAP_NOT_CONFIGURED");
        var client = CreateReadOnlyClient(checked(config.TimeoutSeconds * 1000));
        string stageCode = "IMAP_CONNECT_FAILED";
        try {
            if (trustedLoopbackCertificateForTest is not null) {
                // This test seam cannot relax certificate checks for any non-loopback endpoint.
                if (!IPAddress.TryParse(endpoint.Host, out var ip) || !IPAddress.IsLoopback(ip)) {
                    throw new GalateaImapReadException("IMAP_INVALID_TEST_ENDPOINT");
                }
                byte[] expected = trustedLoopbackCertificateForTest.RawData;
                client.ServerCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is not null && certificate.GetRawCertData().AsSpan().SequenceEqual(expected);
            }
            await client.ConnectAsync(endpoint.Host, endpoint.Port,
                endpoint.TlsMode == "implicit" ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls,
                cancellationToken).ConfigureAwait(false);
            stageCode = "IMAP_AUTH_FAILED";
            await client.AuthenticateAsync(account.Address, account.AuthorizationCode, cancellationToken)
                .ConfigureAwait(false);
            stageCode = "IMAP_EXAMINE_FAILED";
            if (client.Capabilities.HasFlag(ImapCapabilities.Id)) {
                await client.IdentifyAsync(new ImapImplementation { Name = "Galatea", Version = "1" },
                    cancellationToken).ConfigureAwait(false);
            }
            await OpenInboxReadOnlyAsync(client, cancellationToken).ConfigureAwait(false);
            return new Connection(client);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            client.Dispose();
            throw new OperationCanceledException(cancellationToken);
        }
        catch (GalateaImapReadException) { client.Dispose(); throw; }
        catch (Exception exception) when (GalateaExceptionClassifier.IsNonFatal(exception)) {
            client.Dispose();
            throw new GalateaImapReadException(stageCode);
        }
    }

    internal static ImapClient CreateReadOnlyClient(int timeoutMilliseconds) =>
        new BoundedImapClient { Timeout = timeoutMilliseconds };

    internal static async Task OpenInboxReadOnlyAsync(ImapClient client, CancellationToken cancellationToken) {
        if (client.Inbox.IsOpen || client.Inbox is not BoundedImapFolder folder) {
            throw new GalateaImapReadException("IMAP_INVALID_SELECTION_STATE");
        }
        // Some servers omit UIDNEXT from EXAMINE. Ask the unopened mailbox
        // for its authoritative UID pair; never infer it from existing UIDs.
        try {
            await client.Inbox.StatusAsync(StatusItems.UidValidity | StatusItems.UidNext, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception) when (GalateaExceptionClassifier.IsNonFatal(exception)) {
            throw new GalateaImapReadException("IMAP_STATUS_FAILED");
        }
        uint statusValidity = client.Inbox.UidValidity;
        uint? statusNext = client.Inbox.UidNext is { IsValid: true } next ? next.Id : null;
        if (statusValidity == 0 || statusNext is null or 0) {
            throw new GalateaImapReadException("IMAP_INVALID_UID_METADATA");
        }
        // Require EXAMINE to identify its own selected namespace, rather than
        // accidentally accepting a cached UIDVALIDITY from the prior STATUS.
        folder.ClearUidValidityForExamine();
        await client.Inbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
        if (client.Inbox.UidValidity == 0) { throw new GalateaImapReadException("IMAP_INVALID_UID_METADATA"); }
        if (client.Inbox.UidValidity != statusValidity) {
            throw new GalateaImapReadException("IMAP_UIDVALIDITY_CHANGED");
        }
        // MailKit 4.18.1 preserves the exact STATUS metadata when EXAMINE
        // omits it. If EXAMINE provides a new UIDNEXT it must be monotonic.
        uint? selectedNext = client.Inbox.UidNext is { IsValid: true } selected ? selected.Id : null;
        if (selectedNext is null or 0) { throw new GalateaImapReadException("IMAP_INVALID_UID_METADATA"); }
        if (selectedNext < statusNext) { throw new GalateaImapReadException("IMAP_UIDNEXT_REGRESSED"); }
    }

    private sealed class BoundedImapClient : ImapClient {
        protected override ImapFolder CreateImapFolder(ImapFolderConstructorArgs args) => new BoundedImapFolder(args);
    }

    private sealed class BoundedImapFolder(ImapFolderConstructorArgs args) : ImapFolder(args) {
        internal void ClearUidValidityForExamine() => UidValidity = 0;
        // MailKit buffers a literal before returning GetStreamAsync. Enforce the
        // requested partial bound before that allocation, even if a server ignores it.
        protected override Stream CreateStream(UniqueId? uid, string section, int offset, int length) {
            if (length < 0 || length > GalateaImapBounds.MaximumRawBytes + 1 || offset != 0 || section != "") {
                throw new GalateaImapReadException("IMAP_PARTIAL_LIMIT_VIOLATION");
            }
            return new MemoryStream(length);
        }
    }

    private sealed class Connection(ImapClient client) : IGalateaImapConnection {
        public uint UidValidity => client.Inbox.UidValidity;
        public uint? UidNext => client.Inbox.UidNext is { IsValid: true } next ? next.Id : null;

        public async Task<IReadOnlyList<uint>> SearchUidsAsync(uint first, uint last, CancellationToken cancellationToken) {
            if (first == 0 || first > last || (ulong)last - first >= GalateaImapBounds.MaximumSearchUidSpan) {
                throw new GalateaImapReadException("IMAP_INVALID_UID_RANGE");
            }
            try {
                var result = await client.Inbox.SearchAsync(
                    SearchQuery.Uids(new UniqueIdRange(new UniqueId(first), new UniqueId(last))),
                    cancellationToken).ConfigureAwait(false);
                if (result.Count > GalateaImapBounds.MaximumSearchUidSpan
                    || result.Any(uid => !uid.IsValid || uid.Id < first || uid.Id > last)
                    || result.Select(uid => uid.Id).Distinct().Count() != result.Count) {
                    throw new GalateaImapReadException("IMAP_INVALID_SEARCH_RESULT");
                }
                return result.Select(uid => uid.Id).Order().ToArray();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (GalateaImapReadException) { throw; }
            catch (Exception exception) when (GalateaExceptionClassifier.IsNonFatal(exception)) {
                throw new GalateaImapReadException("IMAP_SEARCH_FAILED");
            }
        }

        public async Task<GalateaImapRawMessage> ReadRawAsync(uint uid, CancellationToken cancellationToken) {
            if (uid == 0) { throw new GalateaImapReadException("IMAP_INVALID_UID"); }
            try {
                var summaries = await client.Inbox.FetchAsync(new[] { new UniqueId(uid) },
                    new FetchRequest(MessageSummaryItems.UniqueId | MessageSummaryItems.Size), cancellationToken)
                    .ConfigureAwait(false);
                // Unsolicited summaries are allowed by IMAP; only the requested UID is authoritative.
                var matches = summaries.Where(summary => summary.UniqueId.Id == uid).ToArray();
                if (matches.Length == 0) { return new(null, null, Missing: true); }
                if (matches.Length != 1 || matches[0].Size is null) {
                    throw new GalateaImapReadException("IMAP_INVALID_SIZE_RESULT");
                }
                if (matches[0].Size > GalateaImapBounds.MaximumRawBytes) {
                    return new(null, "IMAP_RAW_TOO_LARGE");
                }
                using Stream stream = await client.Inbox.GetStreamAsync(new UniqueId(uid), "", 0,
                    GalateaImapBounds.MaximumRawBytes + 1, cancellationToken).ConfigureAwait(false);
                using var raw = new MemoryStream();
                byte[] buffer = new byte[8192];
                while (true) {
                    int read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length,
                        GalateaImapBounds.MaximumRawBytes + 1 - checked((int)raw.Length))), cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0) { break; }
                    raw.Write(buffer, 0, read);
                    if (raw.Length > GalateaImapBounds.MaximumRawBytes) { return new(null, "IMAP_RAW_TOO_LARGE"); }
                }
                return new(raw.ToArray(), null);
            }
            catch (MessageNotFoundException) { return new(null, null, Missing: true); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (GalateaImapReadException) { throw; }
            catch (Exception exception) when (GalateaExceptionClassifier.IsNonFatal(exception)) {
                throw new GalateaImapReadException("IMAP_FETCH_FAILED");
            }
        }

        public ValueTask DisposeAsync() {
            // Dispose terminates the read-only connection without CLOSE/EXPUNGE
            // and without delaying shutdown for another network command.
            client.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
