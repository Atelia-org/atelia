using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Atelia.TextEditScript;

internal static class TextEditScriptXml {
    private const string RootName = "text-edit-script";
    private const string InsertName = "insert";
    private const string ReplaceName = "replace";
    private const string DeleteName = "delete";

    public static AteliaResult<TextEditScriptDocument> Parse(string xml) {
        if (string.IsNullOrWhiteSpace(xml)) {
            return AteliaResult<TextEditScriptDocument>.Failure(
                new TextEditScriptParseError(
                    "XML text cannot be empty.",
                    RecoveryHint: "Provide a <text-edit-script> root element."));
        }

        XDocument document;
        try {
            document = XDocument.Parse(xml, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex) {
            return AteliaResult<TextEditScriptDocument>.Failure(
                new TextEditScriptParseError(
                    $"Invalid XML: {ex.Message}",
                    RecoveryHint: "Ensure the edit script is well-formed XML.",
                    Cause: null));
        }

        var root = document.Root;
        if (root is null) {
            return AteliaResult<TextEditScriptDocument>.Failure(
                new TextEditScriptParseError(
                    "Missing document root element.",
                    RecoveryHint: "Use <text-edit-script> as the document root."));
        }

        if (!string.Equals(root.Name.LocalName, RootName, StringComparison.Ordinal)) {
            return AteliaResult<TextEditScriptDocument>.Failure(
                CreateParseError(
                    $"Unexpected root element <{root.Name.LocalName}>.",
                    root,
                    $"Use <{RootName}> as the root element."));
        }

        if (root.HasAttributes) {
            return AteliaResult<TextEditScriptDocument>.Failure(
                CreateParseError(
                    $"Root element <{RootName}> does not accept attributes.",
                    root));
        }

        var operations = new List<TextEditOperation>();
        foreach (var node in root.Nodes()) {
            switch (node) {
                case XText textNode when string.IsNullOrWhiteSpace(textNode.Value):
                    continue;
                case XElement element:
                    var operationResult = ParseOperation(element);
                    if (!operationResult.TryGetValue(out var operation)) {
                        return AteliaResult<TextEditScriptDocument>.Failure(operationResult.Error!);
                    }

                    operations.Add(operation!);
                    break;
                default:
                    return AteliaResult<TextEditScriptDocument>.Failure(
                        CreateParseError(
                            $"Only <{InsertName}>, <{ReplaceName}>, and <{DeleteName}> elements are allowed under <{RootName}>.",
                            node));
            }
        }

        return new TextEditScriptDocument(operations);
    }

    public static string Format(TextEditScriptDocument script) {
        ArgumentNullException.ThrowIfNull(script);

        var root = new XElement(RootName);
        foreach (var operation in script.Operations) {
            root.Add(operation switch {
                InsertTextEdit insert => new XElement(
                    InsertName,
                    new XAttribute("side", FormatSide(insert.Side)),
                    new XAttribute("anchor", insert.Anchor.ToString()),
                    insert.Content),
                ReplaceTextEdit replace => new XElement(
                    ReplaceName,
                    new XAttribute("anchor", replace.Anchor.ToString()),
                    replace.Content),
                DeleteTextEdit delete => new XElement(
                    DeleteName,
                    new XAttribute("anchor", delete.Anchor.ToString())),
                _ => throw new InvalidOperationException($"Unsupported {nameof(TextEditOperation)} type: {operation.GetType().FullName}")
            });
        }

        return new XDocument(root).ToString();
    }

    private static AteliaResult<TextEditOperation> ParseOperation(XElement element) {
        return element.Name.LocalName switch {
            InsertName => ParseInsert(element),
            ReplaceName => ParseReplace(element),
            DeleteName => ParseDelete(element),
            _ => AteliaResult<TextEditOperation>.Failure(
                CreateParseError(
                    $"Unexpected operation element <{element.Name.LocalName}>.",
                    element,
                    $"Allowed elements are <{InsertName}>, <{ReplaceName}>, and <{DeleteName}>."))
        };
    }

    private static AteliaResult<TextEditOperation> ParseInsert(XElement element) {
        if (EnsureOnlyAttributes(element, "side", "anchor") is { } attributeError) {
            return AteliaResult<TextEditOperation>.Failure(attributeError);
        }

        var sideText = GetRequiredAttribute(element, "side");
        if (!sideText.TryGetValue(out var sideValue)) {
            return AteliaResult<TextEditOperation>.Failure(sideText.Error!);
        }

        var contentResult = ParseContent(element, operationName: InsertName, allowEmpty: false);
        if (!contentResult.TryGetValue(out var content)) {
            return AteliaResult<TextEditOperation>.Failure(contentResult.Error!);
        }

        var sideResult = ParseSide(sideValue!);
        if (!sideResult.TryGetValue(out var side)) {
            return AteliaResult<TextEditOperation>.Failure(sideResult.Error!);
        }

        var anchorResult = ParseRequiredAnchor(element);
        if (!anchorResult.TryGetValue(out var anchor)) {
            return AteliaResult<TextEditOperation>.Failure(anchorResult.Error!);
        }

        return new InsertTextEdit(side, anchor, content!);
    }

    private static AteliaResult<TextEditOperation> ParseReplace(XElement element) {
        if (EnsureOnlyAttributes(element, "anchor") is { } attributeError) {
            return AteliaResult<TextEditOperation>.Failure(attributeError);
        }

        var anchorResult = ParseRequiredAnchor(element);
        if (!anchorResult.TryGetValue(out var anchor)) {
            return AteliaResult<TextEditOperation>.Failure(anchorResult.Error!);
        }

        var contentResult = ParseContent(element, operationName: ReplaceName, allowEmpty: false);
        if (!contentResult.TryGetValue(out var content)) {
            return AteliaResult<TextEditOperation>.Failure(contentResult.Error!);
        }

        return new ReplaceTextEdit(anchor, content!);
    }

    private static AteliaResult<TextEditOperation> ParseDelete(XElement element) {
        if (EnsureOnlyAttributes(element, "anchor") is { } attributeError) {
            return AteliaResult<TextEditOperation>.Failure(attributeError);
        }

        var anchorResult = ParseRequiredAnchor(element);
        if (!anchorResult.TryGetValue(out var anchor)) {
            return AteliaResult<TextEditOperation>.Failure(anchorResult.Error!);
        }

        if (HasNonWhitespaceContent(element)) {
            return AteliaResult<TextEditOperation>.Failure(
                CreateParseError(
                    $"<{DeleteName}> must be empty.",
                    element,
                    $"Use <{DeleteName} anchor=\"123\" /> without inner text."));
        }

        return new DeleteTextEdit(anchor);
    }

    private static AteliaResult<TextInsertSide> ParseSide(string rawSide) {
        return rawSide.Trim().ToLowerInvariant() switch {
            "before" => TextInsertSide.BeforeAnchor,
            "after" => TextInsertSide.AfterAnchor,
            _ => AteliaResult<TextInsertSide>.Failure(
                new TextEditScriptParseError(
                    $"Invalid insert side '{rawSide}'.",
                    RecoveryHint: "Use 'before' or 'after'."))
        };
    }

    private static AteliaResult<TextAnchor> ParseRequiredAnchor(XElement element, string attributeName = "anchor") {
        var anchorText = GetRequiredAttribute(element, attributeName);
        if (!anchorText.TryGetValue(out var anchorValue)) {
            return AteliaResult<TextAnchor>.Failure(anchorText.Error!);
        }

        var anchorResult = TextAnchor.Parse(anchorValue!);
        if (!anchorResult.TryGetValue(out var anchor)) {
            return AteliaResult<TextAnchor>.Failure(
                CreateParseError(anchorResult.Error!.Message, element, anchorResult.Error.RecoveryHint, anchorResult.Error));
        }

        return anchor;
    }

    private static AteliaResult<string> GetRequiredAttribute(XElement element, string attributeName) {
        var attribute = element.Attribute(attributeName);
        if (attribute is null || string.IsNullOrWhiteSpace(attribute.Value)) {
            return AteliaResult<string>.Failure(
                CreateParseError(
                    $"Missing required attribute '{attributeName}' on <{element.Name.LocalName}>.",
                    element));
        }

        return attribute.Value.Trim();
    }

    private static AteliaResult<string> ParseContent(XElement element, string operationName, bool allowEmpty) {
        if (element.Elements().Any()) {
            return AteliaResult<string>.Failure(
                CreateParseError(
                    $"<{operationName}> does not allow nested elements.",
                    element,
                    "Put the edited text directly inside the operation element."));
        }

        var content = element.Value;
        if (!allowEmpty && string.IsNullOrWhiteSpace(content)) {
            return AteliaResult<string>.Failure(
                CreateParseError(
                    $"<{operationName}> content cannot be empty.",
                    element));
        }

        if (content.Contains('\n', StringComparison.Ordinal) || content.Contains('\r', StringComparison.Ordinal)) {
            return AteliaResult<string>.Failure(
                CreateParseError(
                    $"<{operationName}> content must stay within a single block and therefore cannot contain newlines.",
                    element,
                    "Split multi-line edits into multiple block-level operations."));
        }

        return content;
    }

    private static bool HasNonWhitespaceContent(XElement element)
        => element.Nodes().Any(static node => node switch {
            XText textNode => !string.IsNullOrWhiteSpace(textNode.Value),
            _ => true,
        });

    private static TextEditScriptParseError? EnsureOnlyAttributes(XElement element, params string[] allowedAttributes) {
        var allowed = new HashSet<string>(allowedAttributes, StringComparer.Ordinal);
        foreach (var attribute in element.Attributes()) {
            if (!allowed.Contains(attribute.Name.LocalName)) {
                return CreateParseError(
                    $"Unexpected attribute '{attribute.Name.LocalName}' on <{element.Name.LocalName}>.",
                    attribute);
            }
        }

        return null;
    }

    private static string FormatSide(TextInsertSide side) => side switch {
        TextInsertSide.BeforeAnchor => "before",
        TextInsertSide.AfterAnchor => "after",
        _ => throw new InvalidOperationException($"Unknown {nameof(TextInsertSide)}: {side}")
    };

    private static TextEditScriptParseError CreateParseError(
        string message,
        XObject? node,
        string? recoveryHint = null,
        AteliaError? cause = null) {
        IReadOnlyDictionary<string, string>? details = null;
        if (node is IXmlLineInfo lineInfo && lineInfo.HasLineInfo()) {
            details = new Dictionary<string, string> {
                ["line"] = lineInfo.LineNumber.ToString(CultureInfo.InvariantCulture),
                ["column"] = lineInfo.LinePosition.ToString(CultureInfo.InvariantCulture),
            };
            message = $"{message} (line {lineInfo.LineNumber}, column {lineInfo.LinePosition})";
        }

        return new TextEditScriptParseError(message, recoveryHint, details, cause);
    }
}
