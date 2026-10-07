using System.Text;
using System.Xml;
using MimeKit;
using MimeKit.Utils;

namespace Atelia.Galatea.Server.Mailbox;

internal sealed record GalateaImapMimeEnvelope(string? From, string? RejectedCode);
internal sealed record GalateaImapMimeProjection(string? Subject, string? Body, int AttachmentCount, string? RejectedCode);

internal static class GalateaImapMimeDecoder {
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static ParserOptions Options() => new() {
        MaxMimeDepth = GalateaImapBounds.MaximumMimeDepth,
        AddressParserComplianceMode = RfcComplianceMode.Strict,
        AllowAddressesWithoutDomain = false
    };

    // Only headers are parsed here. Even a MIME tree or transfer decoder is not
    // constructed for an unknown sender; the caller must decide admission first.
    internal static GalateaImapMimeEnvelope ReadEnvelope(byte[] raw, CancellationToken cancellationToken = default) {
        if (raw.Length > GalateaImapBounds.MaximumRawBytes) { return new(null, "IMAP_RAW_TOO_LARGE"); }
        try {
            using var stream = new MemoryStream(raw, writable: false);
            var headers = HeaderList.Load(Options(), stream, cancellationToken);
            var fromHeaders = headers.Where(header => header.Id == HeaderId.From).ToArray();
            if (fromHeaders.Length != 1 || fromHeaders[0].RawValue.Length > GalateaImapBounds.MaximumFromBytes
                || !InternetAddressList.TryParse(Options(), fromHeaders[0].RawValue, out var addresses)
                || addresses.Count != 1 || addresses[0] is not MailboxAddress mailbox
                || !GalateaExternalMailAddress.TryParse(mailbox.Address, out var address)
                || address!.Value != mailbox.Address) {
                return new(null, "IMAP_INVALID_FROM");
            }
            return new(mailbox.Address, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (GalateaExceptionClassifier.IsNonFatal(exception)) {
            return new(null, "IMAP_MALFORMED_MIME");
        }
    }

    // The sole body projection path. No TextPart.Text/GetText (which applies
    // replacement fallbacks/newline conversion) and no HTML/attachment decoder.
    internal static GalateaImapMimeProjection ProjectAllowed(byte[] raw, CancellationToken cancellationToken = default) {
        if (raw.Length > GalateaImapBounds.MaximumRawBytes) { return new(null, null, 0, "IMAP_RAW_TOO_LARGE"); }
        try {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            using var stream = new MemoryStream(raw, writable: false);
            using var message = MimeMessage.Load(Options(), stream, cancellationToken);
            // Header.Value unfolds *after* RFC2047 decoding and would erase
            // line breaks concealed in an encoded-word. Unfold only the wire
            // header first, then decode and validate the resulting subject.
            Header? subjectHeader = message.Headers.FirstOrDefault(header => header.Id == HeaderId.Subject);
            string? decodedSubject = subjectHeader is null ? null : Rfc2047.DecodeText(Options(),
                StrictUtf8.GetBytes(Header.Unfold(StrictUtf8.GetString(subjectHeader.RawValue))));
            if (decodedSubject is not null && GalateaMailboxText.ContainsHeaderLineBreak(decodedSubject)) {
                return new(null, null, 0, "IMAP_INVALID_TEXT");
            }
            string? subject = string.IsNullOrWhiteSpace(decodedSubject) ? null : decodedSubject;
            if (subject is not null) {
                if (StrictUtf8.GetByteCount(subject) > GalateaImapPersistenceBounds.MaximumSubjectUtf8Bytes) {
                    return new(null, null, 0, "IMAP_SUBJECT_TOO_LARGE");
                }
                _ = XmlConvert.VerifyXmlChars(subject);
            }
            int attachmentCount = CountAttachments(message.Body);
            TextPart? plain = SelectPlainBody(message.Body);
            if (plain?.Content is null) { return new(null, null, attachmentCount, "IMAP_UNSUPPORTED_BODY"); }
            string charset = plain.ContentType.Charset ?? "us-ascii";
            Encoding encoding;
            try { encoding = Encoding.GetEncoding(charset, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback); }
            catch (ArgumentException) { return new(null, null, attachmentCount, "IMAP_UNSUPPORTED_CHARSET"); }
            using var decoded = new MemoryStream();
            plain.Content.DecodeTo(decoded, cancellationToken);
            // Transfer encoding cannot produce more than the bounded raw input.
            // Charset decoding is explicit and strict; preserve the original text.
            string body = encoding.GetString(decoded.GetBuffer(), 0, checked((int)decoded.Length));
            if (string.IsNullOrWhiteSpace(body)) { return new(null, null, attachmentCount, "IMAP_EMPTY_BODY"); }
            if (StrictUtf8.GetByteCount(body) > GalateaImapPersistenceBounds.MaximumBodyUtf8Bytes) {
                return new(null, null, attachmentCount, "IMAP_BODY_TOO_LARGE");
            }
            _ = XmlConvert.VerifyXmlChars(body);
            return new(subject, body, attachmentCount, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (DecoderFallbackException) { return new(null, null, 0, "IMAP_INVALID_ENCODING"); }
        catch (EncoderFallbackException) { return new(null, null, 0, "IMAP_INVALID_ENCODING"); }
        catch (XmlException) { return new(null, null, 0, "IMAP_INVALID_TEXT"); }
        catch (Exception exception) when (GalateaExceptionClassifier.IsNonFatal(exception)) {
            return new(null, null, 0, "IMAP_MALFORMED_MIME");
        }
    }

    private static bool IsAttachment(MimeEntity entity) => entity.IsAttachment
        || !string.IsNullOrEmpty(entity.ContentDisposition?.FileName)
        || !string.IsNullOrEmpty(entity.ContentType.Name);

    private static TextPart? SelectPlainBody(MimeEntity? entity) {
        if (entity is null || IsAttachment(entity) || entity is MessagePart) { return null; }
        if (entity is TextPart text) { return text.IsPlain ? text : null; }
        if (entity is not Multipart multipart) { return null; }
        // related's first (or explicitly named) root is its only main body;
        // alternative chooses the preferred last usable plain representation.
        if (multipart is MultipartRelated related) { return SelectPlainBody(related.Root); }
        if (multipart is MultipartAlternative) {
            for (int i = multipart.Count - 1; i >= 0; i--) {
                if (SelectPlainBody(multipart[i]) is { } plain) { return plain; }
            }
            return null;
        }
        // mixed's first non-attachment is its primary body. Do not replace an
        // HTML-only main body with a later unattached text resource/signature.
        foreach (var child in multipart) {
            if (!IsAttachment(child) && child is not MessagePart) { return SelectPlainBody(child); }
        }
        return null;
    }

    private static int CountAttachments(MimeEntity? entity) {
        if (entity is null) { return 0; }
        if (IsAttachment(entity) || entity is MessagePart) { return 1; }
        return entity is Multipart multipart ? multipart.Sum(CountAttachments) : 0;
    }
}
