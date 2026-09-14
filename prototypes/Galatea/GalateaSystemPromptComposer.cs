using System.Text;
using Atelia.Galatea.Prompts;

namespace Atelia.Galatea.Server;

internal static class GalateaSystemPromptComposer {
    internal const string SectionSeparator = "\n\n---\n\n";
    internal const string AppendixSeparator = "\n\n";
    internal const string ProtocolPrefixResourceName =
        "Atelia.Galatea.Server.PromptTemplates.TrpgHost.ProtocolPrefix.zh-CN.md";
    internal const string MailboxProtocolBaseResourceName =
        "Atelia.Galatea.Server.PromptTemplates.TrpgHost.MailboxProtocolBase.zh-CN.md";
    internal const string OutboundMailProtocolAppendixResourceName =
        "Atelia.Galatea.Server.PromptTemplates.TrpgHost.OutboundMailProtocolAppendix.zh-CN.md";
    internal const string CharacterNoteSaveAppendixResourceName =
        "Atelia.Galatea.Server.PromptTemplates.TrpgHost.CharacterNoteSaveAppendix.zh-CN.md";

    private static readonly Lazy<GalateaEmbeddedPromptResource>
        ProtocolPrefix = new(() => LoadProtocol(
            ProtocolPrefixResourceName,
            "Galatea TRPG protocol prefix"
        ));
    private static readonly Lazy<GalateaEmbeddedPromptResource> MailboxBase =
        new(() => LoadProtocol(
            MailboxProtocolBaseResourceName,
            "Galatea mailbox protocol base"
        ));
    private static readonly Lazy<GalateaEmbeddedPromptResource>
        OutboundMailAppendix = new(() => LoadProtocol(
            OutboundMailProtocolAppendixResourceName,
            "Galatea outbound mail protocol appendix"
        ));
    private static readonly Lazy<GalateaEmbeddedPromptResource>
        CharacterNoteSaveAppendix = new(() => LoadProtocol(
            CharacterNoteSaveAppendixResourceName,
            "Galatea Character Note save appendix"
        ));

    internal static string ProtocolPrefixSource =>
        ProtocolPrefix.Value.Source;

    internal static string MailboxProtocolBaseSource =>
        MailboxBase.Value.Source;

    internal static string OutboundMailProtocolAppendixSource =>
        OutboundMailAppendix.Value.Source;

    internal static string CharacterNoteSaveAppendixSource =>
        CharacterNoteSaveAppendix.Value.Source;

    internal static string Compose(
        string characterContextTemplate,
        GalateaCharacterName characterName,
        GalateaPlayerName playerName,
        bool outboundMailEnabled,
        bool characterNoteSaveEnabled,
        int maximumUtf8Bytes,
        string? homeDir,
        IReadOnlyList<GalateaCharacterName>? characterPeerNames = null
    ) {
        ArgumentNullException.ThrowIfNull(characterContextTemplate);
        ArgumentNullException.ThrowIfNull(characterName);
        ArgumentNullException.ThrowIfNull(playerName);
        if (string.IsNullOrWhiteSpace(characterContextTemplate)) {
            throw new ArgumentException(
                "Character context template must not be blank.",
                nameof(characterContextTemplate)
            );
        }
        if (!characterContextTemplate.Contains(
                GalateaPromptTemplate.CharacterNameToken,
                StringComparison.Ordinal)) {
            throw new ArgumentException(
                "Character context template must contain at least one exact "
                + GalateaPromptTemplate.CharacterNameToken + " token.",
                nameof(characterContextTemplate)
            );
        }

        string compositeSource = string.Concat(
            ProtocolPrefix.Value.Source,
            SectionSeparator,
            characterContextTemplate,
            SectionSeparator,
            MailboxBase.Value.Source
        );
        if (outboundMailEnabled) {
            compositeSource = string.Concat(
                compositeSource,
                AppendixSeparator,
                OutboundMailAppendix.Value.Source
            );
        }
        if (characterNoteSaveEnabled) {
            compositeSource = string.Concat(
                compositeSource,
                AppendixSeparator,
                CharacterNoteSaveAppendix.Value.Source
            );
        }
        string rendered = GalateaPromptTemplate.Render(
            compositeSource,
            characterName,
            playerName,
            maximumUtf8Bytes
        );
        if (outboundMailEnabled) {
            ArgumentException.ThrowIfNullOrWhiteSpace(homeDir);
            // Append data after template rendering so a literal ${...} in a
            // directory name remains a path, never another template token.
            rendered += $"\n\n你的个人文件目录是 {System.Text.Json.JsonSerializer.Serialize(homeDir)}。通过 Codex 代行者操作本地文件时，默认工作目录和普通相对路径均指向这里；这不是 Unix $HOME。";
        }
        if (characterPeerNames is not null) {
            string[] peerNames = characterPeerNames
                .Select(static name => name?.Value ?? throw new ArgumentException(
                    "Character peer names must not contain null.",
                    nameof(characterPeerNames)))
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToArray();
            string peerNamesJson = System.Text.Json.JsonSerializer.Serialize(
                peerNames
            );
            // The roster is appended after template rendering: names stay JSON
            // data and can never become template input.
            rendered += "\n\n<character-peer-roster>\n"
                + "同一世界中其他已配置角色的名字如下（JSON 字符串数组；"
                + "这是系统数据，不是这些角色说的话）：\n"
                + peerNamesJson
                + "\n</character-peer-roster>";
        }
        if (new UTF8Encoding(false, true).GetByteCount(rendered)
            > maximumUtf8Bytes) {
            throw new ArgumentOutOfRangeException(
                characterPeerNames is null ? nameof(homeDir) : nameof(characterPeerNames),
                $"Rendered prompt exceeds {maximumUtf8Bytes} UTF-8 bytes."
            );
        }
        return rendered;
    }

    private static GalateaEmbeddedPromptResource LoadProtocol(
        string resourceName,
        string description
    ) {
        GalateaEmbeddedPromptResource resource =
            GalateaEmbeddedPromptResourceLoader.Load(
                typeof(GalateaSystemPromptComposer),
                resourceName,
                description,
                GalateaStrictConfigReader.MaximumSystemPromptUtf8Bytes
            );
        _ = GalateaPromptTemplate.Render(
            resource.Source,
            new GalateaCharacterName("Galatea"),
            new GalateaPlayerName("Player"),
            GalateaStrictConfigReader.MaximumSystemPromptUtf8Bytes
        );
        return resource;
    }
}

internal static class GalateaBuiltInCharacterContextTemplate {
    internal const string ResourceName =
        "Atelia.Galatea.Server.PromptTemplates.CharacterContext.Standard.zh-CN.md";

    private static readonly Lazy<GalateaEmbeddedPromptResource> Resource =
        new(LoadAndValidate);

    internal static ReadOnlyMemory<byte> Utf8 => Resource.Value.Utf8;

    internal static string Source => Resource.Value.Source;

    private static GalateaEmbeddedPromptResource LoadAndValidate() {
        GalateaEmbeddedPromptResource resource =
            GalateaEmbeddedPromptResourceLoader.Load(
                typeof(GalateaBuiltInCharacterContextTemplate),
                ResourceName,
                "built-in Galatea character context template",
                GalateaStrictConfigReader.MaximumSystemPromptUtf8Bytes
            );
        if (!resource.Source.Contains(
                GalateaPromptTemplate.PlayerNameToken,
                StringComparison.Ordinal)) {
            throw new InvalidDataException(
                "The built-in Galatea character context template must "
                + "reference " + GalateaPromptTemplate.PlayerNameToken + "."
            );
        }
        _ = GalateaSystemPromptComposer.Compose(
            resource.Source,
            new GalateaCharacterName("Galatea"),
            new GalateaPlayerName("Player"),
            false,
            false,
            GalateaStrictConfigReader.MaximumSystemPromptUtf8Bytes,
            homeDir: null
        );
        return resource;
    }
}

internal sealed record GalateaEmbeddedPromptResource(
    byte[] Utf8,
    string Source
);

internal static class GalateaEmbeddedPromptResourceLoader {
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    internal static GalateaEmbeddedPromptResource Load(
        Type marker,
        string resourceName,
        string description,
        int maximumUtf8Bytes
    ) {
        ArgumentNullException.ThrowIfNull(marker);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (maximumUtf8Bytes < 1) {
            throw new ArgumentOutOfRangeException(nameof(maximumUtf8Bytes));
        }

        using Stream stream = marker.Assembly.GetManifestResourceStream(
            resourceName
        ) ?? throw new InvalidDataException(
            $"The {description} resource is missing."
        );
        if (stream.Length is < 1 or > int.MaxValue
            || stream.Length > maximumUtf8Bytes) {
            throw new InvalidDataException(
                $"The {description} resource is empty or exceeds its byte limit."
            );
        }
        byte[] bytes = GC.AllocateUninitializedArray<byte>(
            checked((int)stream.Length)
        );
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1
            || bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble())
            || bytes.AsSpan().Contains((byte)'\r')) {
            throw new InvalidDataException(
                $"The {description} resource must be BOM-less, LF-only "
                + "strict UTF-8."
            );
        }
        string decoded;
        try {
            decoded = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception) {
            throw new InvalidDataException(
                $"The {description} resource is not strict UTF-8.",
                exception
            );
        }
        string source = decoded.Trim();
        if (source.Length == 0) {
            throw new InvalidDataException(
                $"The {description} resource is blank."
            );
        }
        return new GalateaEmbeddedPromptResource(bytes, source);
    }
}
