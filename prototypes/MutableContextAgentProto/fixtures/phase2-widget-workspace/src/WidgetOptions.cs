namespace WidgetFixture;

public sealed class WidgetOptions {
    public Uri Endpoint { get; init; } = new("https://widgets.example.test");

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    public string UserAgent { get; init; } = "WidgetFixture/1.0";
}
