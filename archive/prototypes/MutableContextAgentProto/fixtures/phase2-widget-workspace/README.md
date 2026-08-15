# WidgetClient fixture workspace

This tiny workspace exists for the MutableContextAgentProto Phase 2
`view_file` micro-wizard demo. It is intentionally small, but the answer to
"how do I configure timeout and retry policy?" is split across several files so
the wizard has to select useful snippets instead of keeping every line.

Relevant files:

- `src/WidgetClient.cs` shows how `WidgetClient` receives `WidgetOptions` and
  an optional `WidgetRetryPolicy`.
- `src/WidgetOptions.cs` defines request-level settings, including
  `Timeout`.
- `src/WidgetRetryPolicy.cs` defines retry settings such as `RetryCount` and
  `Delay`.
- `src/InternalNotes.cs` is noise for selection tests and should not be needed
  for the timeout/retry answer.

Typical use:

```csharp
var options = new WidgetOptions
{
    Endpoint = new Uri("https://widgets.example.test"),
    Timeout = TimeSpan.FromSeconds(10),
};

var retryPolicy = new WidgetRetryPolicy
{
    RetryCount = 3,
    Delay = TimeSpan.FromMilliseconds(250),
};

var client = new WidgetClient(options, retryPolicy);
```
