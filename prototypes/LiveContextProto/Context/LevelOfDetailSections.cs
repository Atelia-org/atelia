using System;
using System.Collections.Generic;
using System.Text;

namespace Atelia.LiveContextProto.Context;

internal enum LevelOfDetail {
    Full,
    Summary,
    Gist
}

internal sealed class LevelOfDetailSections {
    public LevelOfDetailSections(
        IReadOnlyList<KeyValuePair<string, string>> full,
        IReadOnlyList<KeyValuePair<string, string>> summary,
        IReadOnlyList<KeyValuePair<string, string>> gist
    ) {
        Full = full ?? throw new ArgumentNullException(nameof(full));
        Summary = summary ?? throw new ArgumentNullException(nameof(summary));
        Gist = gist ?? throw new ArgumentNullException(nameof(gist));
    }

    public IReadOnlyList<KeyValuePair<string, string>> Full { get; }

    public IReadOnlyList<KeyValuePair<string, string>> Summary { get; }

    public IReadOnlyList<KeyValuePair<string, string>> Gist { get; }

    public IReadOnlyList<KeyValuePair<string, string>> GetSections(LevelOfDetail detail)
        => detail switch {
            LevelOfDetail.Full => Full,
            LevelOfDetail.Summary => Summary,
            LevelOfDetail.Gist => Gist,
            _ => Full
        };

    public LevelOfDetailSections WithFullSection(string key, string value) {
        if (key is null) { throw new ArgumentNullException(nameof(key)); }
        if (value is null) { throw new ArgumentNullException(nameof(value)); }

        var updatedFull = AddOrReplaceSection(Full, key, value);
        if (ReferenceEquals(updatedFull, Full)) { return this; }

        return new LevelOfDetailSections(updatedFull, Summary, Gist);
    }

    public static LevelOfDetailSections CreateUniform(IReadOnlyList<KeyValuePair<string, string>> sections) {
        if (sections is null) { throw new ArgumentNullException(nameof(sections)); }
        return new LevelOfDetailSections(sections, sections, sections);
    }

    public static LevelOfDetailSections FromSingleSection(string? title, string content) {
        if (content is null) { throw new ArgumentNullException(nameof(content)); }
        var key = title ?? string.Empty;
        var section = new[] { new KeyValuePair<string, string>(key, content) };
        return CreateUniform(section);
    }

    public static string ToPlainText(IReadOnlyList<KeyValuePair<string, string>> sections) {
        if (sections is null) { throw new ArgumentNullException(nameof(sections)); }
        if (sections.Count == 0) { return string.Empty; }
        if (sections.Count == 1) { return sections[0].Value ?? string.Empty; }

        var builder = new StringBuilder();
        for (var index = 0; index < sections.Count; index++) {
            var section = sections[index];
            if (!string.IsNullOrEmpty(section.Key)
                && !string.Equals(section.Key, LevelOfDetailSectionNames.LiveScreen, StringComparison.Ordinal)) {
                builder.Append('#').Append(' ').AppendLine(section.Key);
            }

            builder.Append(section.Value ?? string.Empty);

            if (index < sections.Count - 1) {
                builder.AppendLine().AppendLine();
            }
        }

        return builder.ToString();
    }

    private static IReadOnlyList<KeyValuePair<string, string>> AddOrReplaceSection(
        IReadOnlyList<KeyValuePair<string, string>> sections,
        string key,
        string value
    ) {
        var replaced = false;
        var builder = new List<KeyValuePair<string, string>>(sections.Count + 1);

        for (var index = 0; index < sections.Count; index++) {
            var section = sections[index];
            if (!replaced && string.Equals(section.Key, key, StringComparison.Ordinal)) {
                builder.Add(new KeyValuePair<string, string>(key, value));
                replaced = true;
            }
            else {
                builder.Add(section);
            }
        }

        if (!replaced) {
            builder.Add(new KeyValuePair<string, string>(key, value));
        }

        if (replaced && builder.Count == sections.Count) {
            var unchanged = true;
            for (var index = 0; index < sections.Count; index++) {
                if (!string.Equals(builder[index].Key, sections[index].Key, StringComparison.Ordinal)
                    || !string.Equals(builder[index].Value, sections[index].Value, StringComparison.Ordinal)) {
                    unchanged = false;
                    break;
                }
            }

            if (unchanged) { return sections; }
        }

        var array = new KeyValuePair<string, string>[builder.Count];
        builder.CopyTo(array, 0);
        return array;
    }
}

internal static class LevelOfDetailSectionNames {
    public const string LiveScreen = "[LiveScreen]";
}

internal static class LevelOfDetailSectionExtensions {
    public static string? TryGetSection(this IReadOnlyList<KeyValuePair<string, string>> sections, string key) {
        if (sections is null) { throw new ArgumentNullException(nameof(sections)); }
        if (key is null) { throw new ArgumentNullException(nameof(key)); }

        for (var index = 0; index < sections.Count; index++) {
            var section = sections[index];
            if (string.Equals(section.Key, key, StringComparison.Ordinal)) { return section.Value; }
        }

        return null;
    }

    public static IReadOnlyList<KeyValuePair<string, string>> WithoutSection(
        this IReadOnlyList<KeyValuePair<string, string>> sections,
        string key,
        out string? removedSection
    ) {
        if (sections is null) { throw new ArgumentNullException(nameof(sections)); }
        if (key is null) { throw new ArgumentNullException(nameof(key)); }

        removedSection = null;
        List<KeyValuePair<string, string>>? filtered = null;

        for (var index = 0; index < sections.Count; index++) {
            var section = sections[index];
            if (string.Equals(section.Key, key, StringComparison.Ordinal)) {
                removedSection = section.Value;
                if (filtered is null) {
                    filtered = new List<KeyValuePair<string, string>>(sections.Count - 1);
                    for (var copyIndex = 0; copyIndex < index; copyIndex++) {
                        filtered.Add(sections[copyIndex]);
                    }
                }

                continue;
            }

            filtered?.Add(section);
        }

        if (filtered is null) { return sections; }
        if (filtered.Count == 0) { return Array.Empty<KeyValuePair<string, string>>(); }

        return filtered.ToArray();
    }

    public static IReadOnlyList<KeyValuePair<string, string>> WithoutLiveScreen(
        this IReadOnlyList<KeyValuePair<string, string>> sections,
        out string? liveScreen
    ) => WithoutSection(sections, LevelOfDetailSectionNames.LiveScreen, out liveScreen);
}
