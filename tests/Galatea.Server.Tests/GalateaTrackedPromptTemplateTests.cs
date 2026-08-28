using Atelia.Galatea.Prompts;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaTrackedPromptTemplateTests {
    [Fact]
    public void TrpgHostIsRenderableWithoutChangingMemorySlots() {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "docs",
            "Galatea",
            "prompt",
            "trpg-host.md"
        ));

        Assert.DoesNotContain("Galatea", source, StringComparison.Ordinal);
        Assert.DoesNotContain("刘世超", source, StringComparison.Ordinal);
        Assert.DoesNotContain("老刘", source, StringComparison.Ordinal);
        Assert.DoesNotContain("[角色名]", source, StringComparison.Ordinal);
        Assert.DoesNotContain("加拉泰亚", source, StringComparison.Ordinal);
        Assert.Equal(5, CountOccurrences(source, "{{}}"));
        Assert.Equal(source, GalateaBuiltInSystemPromptTemplate.Source);

        string rendered = GalateaPromptTemplate.Render(
            source,
            new GalateaCharacterName("Alice"),
            new GalateaPlayerName("Alex"),
            maximumUtf8Bytes: 1024 * 1024
        );

        Assert.Contains("**[Alice]**", rendered, StringComparison.Ordinal);
        Assert.Contains("**Alex**", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("${", rendered, StringComparison.Ordinal);
        Assert.Equal(5, CountOccurrences(rendered, "{{}}"));
    }

    private static int CountOccurrences(string value, string target) {
        int count = 0;
        int start = 0;
        while ((start = value.IndexOf(
                   target,
                   start,
                   StringComparison.Ordinal)) >= 0) {
            count++;
            start += target.Length;
        }
        return count;
    }

    private static string FindRepositoryRoot() {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null) {
            if (File.Exists(Path.Combine(current.FullName, "Atelia.sln"))) {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
