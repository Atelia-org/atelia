namespace Atelia.MutableContextAgentProto.Phase2.Model;

public sealed record NumberedLine {
    public NumberedLine(int number, string text) {
        if (number < 1) { throw new ArgumentOutOfRangeException(nameof(number), number, "Line number must be 1 or greater."); }

        Number = number;
        Text = text ?? throw new ArgumentNullException(nameof(text));
    }

    public int Number { get; }

    public string Text { get; }

    public string Render() => $"{Number}|{Text}";
}
