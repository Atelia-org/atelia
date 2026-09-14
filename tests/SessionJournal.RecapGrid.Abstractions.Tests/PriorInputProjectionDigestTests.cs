using System.Text;
using Atelia.SessionJournal.HistoryTimeline;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Abstractions.Tests;

public sealed class PriorInputProjectionDigestTests {
    [Fact]
    public void FromCellsKeepsOldProductionDigestGoldens() {
        // Captured before this refactor with the compiled production Create API,
        // using these real cells (f53ac2e2 source), not computed by FromCells.
        RecapCellArtifact first = Cell("case.alpha", "alpha content");
        RecapCellArtifact second = Cell("case.beta", "中文 \"quoted\" \\ tail");
        Assert.Equal("cc60c918a0b1ebf1e62aa1060a77ca96732cee96f8b24151b06c57fdcf78b107",
            PriorInputProjectionDigest.FromCells([]).Value);
        Assert.Equal("42dfef1eb45a9de0c2436c196b9f7acf070c6343c3c1e9736efcd35c53696198",
            PriorInputProjectionDigest.FromCells([first]).Value);
        Assert.Equal("7200baac0bb3109909f83daaa507dfd37432b66728619afe925dc2be8e34f41a",
            PriorInputProjectionDigest.FromCells([first, second]).Value);
    }

    [Fact]
    public void IdentityUsesOrderedColumnContentRatherThanCellSourceOrDefinition() {
        RecapCellArtifact first = Cell("case.alpha", "same content");
        RecapCellArtifact otherSource = Cell("case.alpha", "same content", 'e', 'b');
        Assert.NotEqual(first.CellDigest, otherSource.CellDigest);
        Assert.NotEqual(first.DefinitionDigest, otherSource.DefinitionDigest);
        Assert.Equal(PriorInputProjectionDigest.FromCells([first]),
            PriorInputProjectionDigest.FromCells([otherSource]));
        Assert.NotEqual(PriorInputProjectionDigest.FromCells([first]),
            PriorInputProjectionDigest.FromCells([Cell("case.beta", "same content")]));
        Assert.NotEqual(PriorInputProjectionDigest.FromCells([first]),
            PriorInputProjectionDigest.FromCells([Cell("case.alpha", "changed content")]));
        RecapCellArtifact second = Cell("case.beta", "second");
        Assert.NotEqual(PriorInputProjectionDigest.FromCells([first, second]),
            PriorInputProjectionDigest.FromCells([second, first]));
    }

    [Fact]
    public void FirstRowAndEmptyProjectionHaveDifferentEvaluationKeys() {
        var history = new HistorySegmentDescriptorDigest(new string('a', 64));
        var definition = new MaintainerDefinitionDigest(new string('d', 64));
        EvaluationKey first = EvaluationKey.Create(history, definition, PriorInputReference.FirstRow.Value);
        EvaluationKey projected = EvaluationKey.Create(history, definition,
            new PriorInputReference.Projection(PriorInputProjectionDigest.FromCells([])));
        Assert.NotEqual(first.Digest, projected.Digest);
        Assert.NotEqual(first.ToCanonicalBytes(), projected.ToCanonicalBytes());
    }

    [Theory]
    [InlineData('\u00a0')]
    [InlineData('\u2028')]
    [InlineData('\\')]
    [InlineData('"')]
    public void MaximumUniqueColumnsAndEscapedIdentifiersRemainValid(char character) {
        RecapCellArtifact[] cells = Enumerable.Range(0, 128).Select(index => {
            string name = index.ToString("D3") + "a"
                + new string(character, 120 / Encoding.UTF8.GetByteCount(character.ToString())) + "tail";
            Assert.Equal(128, Encoding.UTF8.GetByteCount(name));
            return Cell(name, "content");
        }).ToArray();
        Assert.Equal(64, PriorInputProjectionDigest.FromCells(cells).Value.Length);
    }

    [Fact]
    public void FromCellsRejectsUnboundedOrInvalidCollections() {
        Assert.Throws<ArgumentNullException>(() => PriorInputProjectionDigest.FromCells(null!));
        Assert.Throws<ArgumentException>(() => PriorInputProjectionDigest.FromCells([null!]));
        RecapCellArtifact first = Cell("case.alpha", "first");
        Assert.Throws<ArgumentException>(() => PriorInputProjectionDigest.FromCells(
            [first, Cell("case.alpha", "second")]));
        RecapCellArtifact[] tooMany = Enumerable.Range(0, 129)
            .Select(index => Cell($"case.column-{index}", "content")).ToArray();
        Assert.Throws<ArgumentOutOfRangeException>(() => PriorInputProjectionDigest.FromCells(tooMany));
    }

    private static RecapCellArtifact Cell(string column, string content,
        char definitionCharacter = 'd', char historyCharacter = 'a') {
        var definition = new MaintainerDefinitionDigest(new string(definitionCharacter, 64));
        EvaluationKey key = EvaluationKey.Create(
            new HistorySegmentDescriptorDigest(new string(historyCharacter, 64)),
            definition, PriorInputReference.FirstRow.Value);
        return RecapCellArtifact.Create(new LogicalColumnId(column), definition, key,
            RecapCellOutcome.Updated, content, 16 * 1024);
    }
}
