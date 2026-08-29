using Atelia.MemoPod;

namespace Atelia.MemoPod.Tests;

public sealed class MemoIdentityTests {
    private const string PodToken = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void MemoPodIdRoundTripsCanonicalToken() {
        MemoPodId parsed = MemoPodId.Parse(PodToken);

        Assert.Equal(PodToken, parsed.Value);
        Assert.Equal(PodToken, parsed.ToString());
        Assert.True(MemoPodId.TryParse(PodToken, out MemoPodId tried));
        Assert.Equal(parsed, tried);
        Assert.Equal(parsed.GetHashCode(), tried.GetHashCode());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("0123456789ABCDEF0123456789abcdef")]
    [InlineData(" 0123456789abcdef0123456789abcdef")]
    [InlineData("01234567-89abcdef-01234567-89abcdef")]
    [InlineData("0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdef0")]
    [InlineData("g123456789abcdef0123456789abcdef")]
    public void MemoPodIdRejectsNonCanonicalTokens(string? token) {
        Assert.False(MemoPodId.TryParse(token, out MemoPodId parsed));
        Assert.Equal(default, parsed);
        if (token is not null) {
            Assert.Throws<FormatException>(() => MemoPodId.Parse(token));
        }
    }

    [Fact]
    public void MemoPodIdParseRejectsNullAndDefaultIsInvalid() {
        Assert.Throws<ArgumentNullException>(() => MemoPodId.Parse(null!));
        Assert.Equal(string.Empty, default(MemoPodId).Value);
        Assert.Equal(string.Empty, default(MemoPodId).ToString());
    }

    [Theory]
    [InlineData("m1:00000001")]
    [InlineData("m1:1234abcd")]
    [InlineData("m1:ffffffff")]
    public void MemoIdRoundTripsCanonicalToken(string token) {
        MemoId parsed = MemoId.Parse(token);

        Assert.Equal(token, parsed.Value);
        Assert.Equal(token, parsed.ToString());
        Assert.True(MemoId.TryParse(token, out MemoId tried));
        Assert.Equal(parsed, tried);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("m1:00000000")]
    [InlineData("M1:00000001")]
    [InlineData("m1:0000000A")]
    [InlineData("m2:00000001")]
    [InlineData("m1:0000001")]
    [InlineData("m1:000000001")]
    [InlineData(" m1:00000001")]
    [InlineData("m1:0000000g")]
    public void MemoIdRejectsNonCanonicalTokens(string? token) {
        Assert.False(MemoId.TryParse(token, out MemoId parsed));
        Assert.Equal(default, parsed);
        if (token is not null) {
            Assert.Throws<FormatException>(() => MemoId.Parse(token));
        }
    }

    [Fact]
    public void MemoIdParseRejectsNullAndDefaultIsInvalid() {
        Assert.Throws<ArgumentNullException>(() => MemoId.Parse(null!));
        Assert.Equal(string.Empty, default(MemoId).Value);
        Assert.Equal(string.Empty, default(MemoId).ToString());
    }
}
