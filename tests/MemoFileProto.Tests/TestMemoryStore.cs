using MemoFileProto.Tools;

namespace Atelia.MemoFileProto.Tests.Tools;

internal sealed class TestMemoryStore {
    private string _value;

    public TestMemoryStore(string initialValue = "") {
        _value = initialValue;
    }

    public string Value => _value;

    public MemoReplaceLiteral CreateLiteralTool() => new(Get, Set);

    public MemoReplaceSpan CreateSpanTool() => new(Get, Set);

    private string Get() => _value;

    private void Set(string updated) => _value = updated;
}
