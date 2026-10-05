// 作者：姬澄(Galatea-02)；复用来源：38959d0293d4456f62d2c5a4a3af26088dfa3c0e。
using Atelia.Galatea.Server.Mailbox;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaExternalMailAddressTests {
    [Theory]
    [InlineData("a@b.co")]
    [InlineData("first.last+tag_01@example-domain.test")]
    [InlineData("a!#$%&'*+-/=?^_`{|}~@example.test")]
    [InlineData("a&b@example.test")]
    [InlineData("Mixed.Case+Tag@Example.COM")]
    public void TryParse_AcceptsSupportedAsciiAddressesWithoutRewriting(
        string input
    ) {
        Assert.True(GalateaExternalMailAddress.TryParse(input, out var address));
        Assert.NotNull(address);
        Assert.Equal(input, address.Value);
    }

    [Fact]
    public void TryParse_RemovesOnlyOuterAsciiSpaces() {
        Assert.True(GalateaExternalMailAddress.TryParse(
            "  Mixed.Case+Tag@Example.COM  ", out var address));
        Assert.NotNull(address);
        Assert.Equal("Mixed.Case+Tag@Example.COM", address.Value);

        Assert.False(GalateaExternalMailAddress.TryParse(
            "\tuser@example.test", out var rejected));
        Assert.Null(rejected);
    }

    [Fact]
    public void TryParse_EnforcesLocalLabelAndTotalLengthBounds() {
        Assert.True(GalateaExternalMailAddress.TryParse(
            new string('a', 64) + "@example.test", out _));
        Assert.False(GalateaExternalMailAddress.TryParse(
            new string('a', 65) + "@example.test", out _));
        Assert.True(GalateaExternalMailAddress.TryParse(
            "a@" + new string('b', 63) + ".test", out _));
        Assert.False(GalateaExternalMailAddress.TryParse(
            "a@" + new string('b', 64) + ".test", out _));

        string longest = new string('a', 64) + "@"
            + new string('b', 63) + "."
            + new string('c', 63) + "."
            + new string('d', 61);
        Assert.Equal(254, longest.Length);
        Assert.True(GalateaExternalMailAddress.TryParse(longest, out _));
        Assert.False(GalateaExternalMailAddress.TryParse(
            longest + "d", out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Codex")]
    [InlineData("Galatea-01")]
    [InlineData("Galatea(Galatea-01)")]
    [InlineData("姬澄(Galatea-02)")]
    [InlineData("user@localhost")]
    [InlineData(".user@example.test")]
    [InlineData("user.@example.test")]
    [InlineData("a..b@example.test")]
    [InlineData("user@-example.test")]
    [InlineData("user@example-.test")]
    [InlineData("user@example..test")]
    [InlineData("user@example.test.")]
    [InlineData("user name@example.test")]
    [InlineData("user@example .test")]
    [InlineData("user@example.test,other@example.test")]
    [InlineData("user@example.test;other@example.test")]
    [InlineData("Name <user@example.test>")]
    [InlineData("<user@example.test>")]
    [InlineData("user@example.test (comment)")]
    [InlineData("\"user\"@example.test")]
    [InlineData("user@[127.0.0.1]")]
    [InlineData("üser@example.test")]
    [InlineData("user@例子.test")]
    [InlineData("user@example.test\r\nBcc:other@example.test")]
    [InlineData("user@example.test\n")]
    [InlineData("user@example.test\0")]
    [InlineData("a&amp;b@example.test")]
    [InlineData("user&amp;quot;@example.test")]
    public void TryParse_RejectsUnsupportedOrUnsafeInputs(string? input) {
        Assert.False(GalateaExternalMailAddress.TryParse(input, out var address));
        Assert.Null(address);
    }
}
