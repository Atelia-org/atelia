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
            if (!string.IsNullOrEmpty(section.Key)) {
                builder.Append('#').Append(' ').AppendLine(section.Key);
            }

            builder.Append(section.Value ?? string.Empty);

            if (index < sections.Count - 1) {
                builder.AppendLine().AppendLine();
            }
        }

        return builder.ToString();
    }
}
