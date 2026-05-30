namespace Atelia.TextAdv2.Runtime;

public sealed record TextAdv2RuntimeCommandResult(string Output, string ContentType);

public static class TextAdv2RuntimeContentTypes {
    public const string Json = "application/json";
    public const string PlainText = "text/plain; charset=utf-8";
}
