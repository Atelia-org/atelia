using System;
using System.Collections.Generic;
using System.Text;

namespace Atelia.LiveContextProto.Context;

internal enum LevelOfDetail {
    Basic,
    BasicAndExtra
}

internal sealed class LevelOfDetailContent {
    private readonly string _basic;
    private readonly string? _extra;
    private string? _cachedBasicAndExtra;

    public LevelOfDetailContent(string basic, string? extra = null) {
        _basic = basic ?? throw new ArgumentNullException(nameof(basic));
        _extra = string.IsNullOrEmpty(extra) ? null : extra;
    }

    public string Basic => _basic;

    public string? Extra => _extra;

    public string BasicAndExtra => _extra is null
        ? _basic
        : _cachedBasicAndExtra ??= Combine(_basic, _extra);

    public string GetContent(LevelOfDetail detail)
        => detail switch {
            LevelOfDetail.Basic => _basic,
            LevelOfDetail.BasicAndExtra => BasicAndExtra,
            _ => _basic
        };

    private static string Combine(string basic, string extra)
        => string.Concat(basic, "\n", extra);
}

internal sealed class LevelOfDetailSections {
    private readonly IReadOnlyList<KeyValuePair<string, string>> _basic;
    private readonly IReadOnlyList<KeyValuePair<string, string>> _extra;
    private IReadOnlyList<KeyValuePair<string, string>>? _cachedBasicAndExtra;

    public LevelOfDetailSections(
        IReadOnlyList<KeyValuePair<string, string>> basic,
        IReadOnlyList<KeyValuePair<string, string>> extra
    ) {
        _basic = basic ?? throw new ArgumentNullException(nameof(basic));
        _extra = extra ?? throw new ArgumentNullException(nameof(extra));
    }

    public IReadOnlyList<KeyValuePair<string, string>> Basic => _basic;

    public IReadOnlyList<KeyValuePair<string, string>> Extra => _extra;

    public IReadOnlyList<KeyValuePair<string, string>> GetSections(LevelOfDetail detail)
        => detail switch {
            LevelOfDetail.Basic => _basic,
            LevelOfDetail.BasicAndExtra => _extra.Count == 0
                ? _basic
                : _cachedBasicAndExtra ??= Combine(_basic, _extra),
            _ => _basic
        };

    public LevelOfDetailSections WithBasicSection(string key, string value) {
        if (key is null) { throw new ArgumentNullException(nameof(key)); }
        if (value is null) { throw new ArgumentNullException(nameof(value)); }

        var updatedBasic = AddOrReplaceSection(_basic, key, value);
        if (ReferenceEquals(updatedBasic, _basic)) { return this; }

        return new LevelOfDetailSections(updatedBasic, _extra);
    }

    public LevelOfDetailSections WithExtraSection(string key, string value) {
        if (key is null) { throw new ArgumentNullException(nameof(key)); }
        if (value is null) { throw new ArgumentNullException(nameof(value)); }

        var updatedExtra = AddOrReplaceSection(_extra, key, value);
        if (ReferenceEquals(updatedExtra, _extra)) { return this; }

        return new LevelOfDetailSections(_basic, updatedExtra);
    }

    public LevelOfDetailSections WithFullSection(string key, string value)
        => WithBasicSection(key, value);

    public static LevelOfDetailSections CreateUniform(IReadOnlyList<KeyValuePair<string, string>> sections) {
        if (sections is null) { throw new ArgumentNullException(nameof(sections)); }
        return new LevelOfDetailSections(sections, Array.Empty<KeyValuePair<string, string>>());
    }

    public static LevelOfDetailSections FromSingleSection(string? title, string content) {
        if (content is null) { throw new ArgumentNullException(nameof(content)); }
        var key = title ?? string.Empty;
        var section = new[] { new KeyValuePair<string, string>(key, content) };
        return new LevelOfDetailSections(section, Array.Empty<KeyValuePair<string, string>>());
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

    private static IReadOnlyList<KeyValuePair<string, string>> Combine(
        IReadOnlyList<KeyValuePair<string, string>> basic,
        IReadOnlyList<KeyValuePair<string, string>> extra
    ) {
        var combined = new KeyValuePair<string, string>[basic.Count + extra.Count];

        var writeIndex = 0;
        for (var index = 0; index < basic.Count; index++) {
            combined[writeIndex++] = basic[index];
        }

        for (var index = 0; index < extra.Count; index++) {
            combined[writeIndex++] = extra[index];
        }

        return combined;
    }

    private static IReadOnlyList<KeyValuePair<string, string>> AddOrReplaceSection(
        IReadOnlyList<KeyValuePair<string, string>> sections,
        string key,
        string value
    ) {
        if (sections.Count == 0) { return new[] { new KeyValuePair<string, string>(key, value) }; }

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
