using System.Text;
using Atelia.Galatea.Server.Mailbox;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaImapMimeTests {
    [Fact]
    public void EnvelopeReadsOnlyHeaders_WithoutDecodingInvalidBody() {
        byte[] raw = Mail("known@example.test", "Content-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64", "/w==");
        Assert.Equal("known@example.test", GalateaImapMimeDecoder.ReadEnvelope(raw).From);
        Assert.Equal("IMAP_INVALID_ENCODING", GalateaImapMimeDecoder.ProjectAllowed(raw).RejectedCode);
    }

    [Theory]
    [InlineData("", "IMAP_INVALID_FROM")]
    [InlineData("From: a@example.test\r\nFrom: b@example.test\r\n", "IMAP_INVALID_FROM")]
    [InlineData("From: a@example.test, b@example.test\r\n", "IMAP_INVALID_FROM")]
    [InlineData("From: Group: a@example.test;\r\n", "IMAP_INVALID_FROM")]
    [InlineData("From: 用户@example.test\r\n", "IMAP_INVALID_FROM")]
    [InlineData("From: a@[127.0.0.1]\r\n", "IMAP_INVALID_FROM")]
    public void DeclaredFromMustBeOneSupportedMailbox(string fromHeader, string code) {
        byte[] raw = Encoding.UTF8.GetBytes(fromHeader + "Reply-To: fallback@example.test\r\nContent-Type: text/plain\r\n\r\nbody");
        Assert.Equal(code, GalateaImapMimeDecoder.ReadEnvelope(raw).RejectedCode);
    }

    [Fact]
    public void FromHeaderByteLimitIncludesDisplayName() {
        byte[] raw = Mail(new string('a', GalateaImapBounds.MaximumFromBytes) + " <friend@example.test>", "Content-Type: text/plain", "body");
        Assert.Equal("IMAP_INVALID_FROM", GalateaImapMimeDecoder.ReadEnvelope(raw).RejectedCode);
    }

    [Fact]
    public void Base64Utf8_PreservesTextAndDecodesSubject() {
        const string body = "你好\r\n\r\n```cs\ncode\n```\n末尾无换行";
        const string subject = "主题：中文";
        byte[] raw = Mail("Friendly Name <Alice@EXAMPLE.test>",
            "Subject: =?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(subject)) + "?=\r\n"
            + "Content-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64", Convert.ToBase64String(Encoding.UTF8.GetBytes(body)));
        Assert.Equal("Alice@EXAMPLE.test", GalateaImapMimeDecoder.ReadEnvelope(raw).From);
        var result = GalateaImapMimeDecoder.ProjectAllowed(raw);
        Assert.Null(result.RejectedCode);
        Assert.Equal(subject, result.Subject);
        Assert.Equal(body, result.Body);
    }

    [Fact]
    public void QuotedPrintable_DecodesWithoutNormalizingNewlines() {
        var projection = GalateaImapMimeDecoder.ProjectAllowed(Mail("friend@example.test",
            "Content-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: quoted-printable", "caf=C3=A9\r\nnext=0Aline"));
        Assert.Null(projection.RejectedCode);
        Assert.Equal("café\r\nnext\nline", projection.Body);
    }

    [Fact]
    public void LegalLegacyCharset_IsStrictlyDecoded() {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string encoded = Convert.ToBase64String(Encoding.GetEncoding("gb18030").GetBytes("中文回信"));
        var projection = GalateaImapMimeDecoder.ProjectAllowed(Mail("friend@example.test",
            "Content-Type: text/plain; charset=gb18030\r\nContent-Transfer-Encoding: base64", encoded));
        Assert.Null(projection.RejectedCode);
        Assert.Equal("中文回信", projection.Body);
    }

    [Fact]
    public void MultipartMainPlain_ExcludesAttachmentAndForwardedBody() {
        string parts = "--outer\r\nContent-Type: multipart/alternative; boundary=inner\r\n\r\n"
            + "--inner\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nmain body\r\n"
            + "--inner\r\nContent-Type: text/html\r\n\r\n<p>HTML_NOT_BODY</p>\r\n--inner--\r\n"
            + "--outer\r\nContent-Type: text/plain; name=secret.txt\r\nContent-Disposition: attachment; filename=secret.txt\r\n\r\nATTACHMENT_NOT_BODY\r\n"
            + "--outer\r\nContent-Type: message/rfc822\r\n\r\nFrom: forwarded@example.test\r\nContent-Type: text/plain\r\n\r\nFORWARDED_NOT_BODY\r\n--outer--\r\n";
        var projection = GalateaImapMimeDecoder.ProjectAllowed(Mail("friend@example.test",
            "Content-Type: multipart/mixed; boundary=outer", parts));
        Assert.Null(projection.RejectedCode);
        Assert.Equal("main body", projection.Body);
        Assert.Equal(2, projection.AttachmentCount);
    }

    [Theory]
    [InlineData("Content-Type: text/html", "<p>no plain</p>", "IMAP_UNSUPPORTED_BODY")]
    [InlineData("Content-Type: text/plain; charset=utf-8", " \r\n\t", "IMAP_EMPTY_BODY")]
    [InlineData("Content-Type: text/plain; charset=nonexistent-synthetic", "body", "IMAP_UNSUPPORTED_CHARSET")]
    [InlineData("Content-Type: text/plain; charset=utf-8", "body\0bad", "IMAP_INVALID_TEXT")]
    public void UnsupportedOrInvalidText_IsOrdinaryMimeRejection(string headers, string body, string code) {
        var result = GalateaImapMimeDecoder.ProjectAllowed(Mail("friend@example.test", headers, body));
        Assert.Equal(code, result.RejectedCode);
        Assert.Null(result.Body);
    }

    [Fact]
    public void HtmlMainWithLaterTextResource_IsNotMistakenForPlainMain() {
        string body = "--b\r\nContent-Type: text/html\r\n\r\n<p>main</p>\r\n--b\r\nContent-Type: text/plain\r\n\r\nnot the main body\r\n--b--\r\n";
        Assert.Equal("IMAP_UNSUPPORTED_BODY", GalateaImapMimeDecoder.ProjectAllowed(
            Mail("friend@example.test", "Content-Type: multipart/mixed; boundary=b", body)).RejectedCode);
    }

    [Theory]
    [InlineData("line\nsecond")]
    [InlineData("\n")]
    [InlineData("line\u2028second")]
    public void EncodedSubjectLineBreaks_AreRejectedBeforeStore(string subject) {
        string header = "Subject: =?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(subject)) + "?=\r\nContent-Type: text/plain";
        Assert.Equal("IMAP_INVALID_TEXT", GalateaImapMimeDecoder.ProjectAllowed(Mail("friend@example.test", header, "body")).RejectedCode);
    }

    [Fact]
    public void BlankSubject_IsNullAndDoesNotBlockStore() {
        var result = GalateaImapMimeDecoder.ProjectAllowed(Mail("friend@example.test", "Subject:   \r\nContent-Type: text/plain", "body"));
        Assert.Null(result.RejectedCode);
        Assert.Null(result.Subject);
    }

    [Fact]
    public void LegitimateWireHeaderFolding_IsUnfoldedBeforeSubjectDecoding() {
        var result = GalateaImapMimeDecoder.ProjectAllowed(Mail("friend@example.test",
            "Subject: first\r\n\tsecond\r\nContent-Type: text/plain", "body"));
        Assert.Null(result.RejectedCode);
        Assert.Equal("first\tsecond", result.Subject);
    }

    [Fact]
    public void FinalBodyAndSubjectBudgets_AreUtf8Bytes() {
        Assert.Equal("IMAP_BODY_TOO_LARGE", GalateaImapMimeDecoder.ProjectAllowed(Mail("friend@example.test",
            "Content-Type: text/plain; charset=utf-8", new string('中', 22_000))).RejectedCode);
        Assert.Equal("IMAP_SUBJECT_TOO_LARGE", GalateaImapMimeDecoder.ProjectAllowed(Mail("friend@example.test",
            "Subject: " + new string('a', 4097) + "\r\nContent-Type: text/plain", "body")).RejectedCode);
        Assert.Equal("IMAP_RAW_TOO_LARGE", GalateaImapMimeDecoder.ReadEnvelope(new byte[GalateaImapBounds.MaximumRawBytes + 1]).RejectedCode);
    }

    [Fact]
    public void FortyLevelNestedMultipart_IsBoundedAndCannotExposeItsLeafBody() {
        string body = "LEAF_BODY_SHOULD_NOT_PROJECT";
        for (int depth = 39; depth >= 0; depth--) {
            body = "--b" + depth + "\r\n" + (depth == 39 ? "Content-Type: text/plain"
                : "Content-Type: multipart/mixed; boundary=b" + (depth + 1)) + "\r\n\r\n"
                + body + "\r\n--b" + depth + "--\r\n";
        }
        var projection = GalateaImapMimeDecoder.ProjectAllowed(Mail("friend@example.test",
            "Content-Type: multipart/mixed; boundary=b0", body));
        Assert.NotNull(projection.RejectedCode);
        Assert.Null(projection.Body);
    }

    internal static byte[] Mail(string from, string headers, string body) => Encoding.UTF8.GetBytes(
        "From: " + from + "\r\nMIME-Version: 1.0\r\n" + headers + "\r\n\r\n" + body);
}
