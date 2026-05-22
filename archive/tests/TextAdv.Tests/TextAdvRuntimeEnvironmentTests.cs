using Xunit;

namespace Atelia.TextAdv.Tests;

public sealed class TextAdvRuntimeEnvironmentTests {
    [Fact]
    public void GetRepoDir_ShouldReturnDefault_WhenEnvIsMissing() {
        using var _ = new EnvironmentVariableScope(TextAdvRuntimeEnvironment.RepoDirEnv, null);

        Assert.Equal("/tmp/atelia-textadv-game", TextAdvRuntimeEnvironment.GetRepoDir());
    }

    [Fact]
    public void GetRepoDir_ShouldTrimConfiguredValue() {
        using var _ = new EnvironmentVariableScope(TextAdvRuntimeEnvironment.RepoDirEnv, "  /tmp/textadv-custom  ");

        Assert.Equal("/tmp/textadv-custom", TextAdvRuntimeEnvironment.GetRepoDir());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("abc")]
    public void GetPositiveIntEnvironment_ShouldFallback_WhenValueIsInvalid(string? rawValue) {
        using var _ = new EnvironmentVariableScope("ATELIA_TEXTADV_TEST_POSITIVE_INT", rawValue);

        Assert.Equal(
            7,
            TextAdvRuntimeEnvironment.GetPositiveIntEnvironment("ATELIA_TEXTADV_TEST_POSITIVE_INT", 7)
        );
    }

    [Fact]
    public void BuildProviderErrorMessage_ShouldFilterBlankEntries_AndUsePrefix() {
        var message = TextAdvRuntimeEnvironment.BuildProviderErrorMessage(
            ["  first  ", "", "   ", "second"],
            prefix: "provider: ",
            defaultMessage: "unknown"
        );

        Assert.Equal("provider: first; second", message);
    }

    [Fact]
    public void BuildProviderErrorMessage_ShouldUseDefault_WhenNoReadableErrorsRemain() {
        var message = TextAdvRuntimeEnvironment.BuildProviderErrorMessage(
            ["", "   "],
            prefix: string.Empty,
            defaultMessage: "unknown"
        );

        Assert.Equal("unknown", message);
    }

    private sealed class EnvironmentVariableScope : IDisposable {
        private readonly string _key;
        private readonly string? _originalValue;

        public EnvironmentVariableScope(string key, string? value) {
            _key = key;
            _originalValue = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }

        public void Dispose() {
            Environment.SetEnvironmentVariable(_key, _originalValue);
        }
    }
}
