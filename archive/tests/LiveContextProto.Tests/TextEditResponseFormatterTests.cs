using System.Collections.Generic;
using Atelia.Agent.Text;
using Xunit;

namespace Atelia.LiveContextProto.Tests;

public sealed class TextEditResponseFormatterTests {
    [Fact]
    public void FormatResponse_Success_SingleMatch_GeneratesCorrectMarkdown() {
        // Arrange
        var status = TextEditStatus.Success;
        var state = TextEditWorkflowState.Idle;
        var flags = TextEditFlag.None;
        var summary = "已在 MyFile.txt 中完成替换。";
        var guidance = (string?)null;
        var metrics = new TextEditMetrics(delta: 5, newLength: 105, selectionCount: null);

        // Act
        var result = TextEditResponseFormatter.FormatResponse(
            status, state, flags, summary, guidance, metrics, null
        );

        // Assert
        Assert.Contains("status: `Success`", result);
        Assert.Contains("state: `Idle`", result);
        Assert.Contains("flags: -", result);
        Assert.Contains("### [OK] 概览", result);
        Assert.Contains("- summary: 已在 MyFile.txt 中完成替换。", result);
        Assert.Contains("- guidance: (留空)", result);
        Assert.Contains("### [Metrics] 指标", result);
        Assert.Contains("| delta | +5 |", result);
        Assert.Contains("| new_length | 105 |", result);
        Assert.Contains("| selection_count | - |", result);
        Assert.DoesNotContain("### [Target] 候选选区", result);
    }

    [Fact]
    public void FormatResponse_MultiMatch_WithCandidates_GeneratesCorrectMarkdown() {
        // Arrange
        var status = TextEditStatus.MultiMatch;
        var state = TextEditWorkflowState.SelectionPending;
        var flags = TextEditFlag.SelectionPending;
        var summary = "检测到 MyFile.txt 中的多处匹配，已生成选区。";
        var guidance = "请调用 myfile_replace_selection 工具并指定 selection_id 完成替换。";
        var metrics = new TextEditMetrics(delta: 0, newLength: 200, selectionCount: 2);
        var candidates = new List<TextEditCandidate> {
            new(
                id: 1,
                preview: "...Foo() {",
                markerStart: "[[SEL#1]]",
                markerEnd: "[[/SEL#1]]",
                occurrence: 0,
                contextStart: 100,
                contextEnd: 110
            ),
            new(
                id: 2,
                preview: "...Bar() {",
                markerStart: "[[SEL#2]]",
                markerEnd: "[[/SEL#2]]",
                occurrence: 1,
                contextStart: 150,
                contextEnd: 160
            )
        };

        // Act
        var result = TextEditResponseFormatter.FormatResponse(
            status, state, flags, summary, guidance, metrics, candidates
        );

        // Assert
        Assert.Contains("status: `MultiMatch`", result);
        Assert.Contains("state: `SelectionPending`", result);
        Assert.Contains("flags: `SelectionPending`", result);
        Assert.Contains("### [Warning] 概览", result);
        Assert.Contains("- summary: 检测到 MyFile.txt 中的多处匹配，已生成选区。", result);
        Assert.Contains("- guidance: 请调用 myfile_replace_selection 工具并指定 selection_id 完成替换。", result);
        Assert.Contains("| delta | +0 |", result);
        Assert.Contains("| new_length | 200 |", result);
        Assert.Contains("| selection_count | 2 |", result);
        Assert.Contains("### [Target] 候选选区", result);
        Assert.Contains("| 1 | `[[SEL#1]]` | `[[/SEL#1]]` | `...Foo() {` | 0 | 100 | 110 |", result);
        Assert.Contains("| 2 | `[[SEL#2]]` | `[[/SEL#2]]` | `...Bar() {` | 1 | 150 | 160 |", result);
    }

    [Fact]
    public void FormatResponse_Failure_NoMatch_GeneratesCorrectMarkdown() {
        // Arrange
        var status = TextEditStatus.NoMatch;
        var state = TextEditWorkflowState.Idle;
        var flags = TextEditFlag.None;
        var summary = "未找到要替换的文本。";
        var guidance = "请检查 old_text 是否与目标内容精确匹配（包括空格、换行等）。";
        var metrics = new TextEditMetrics(delta: 0, newLength: 100, selectionCount: null);

        // Act
        var result = TextEditResponseFormatter.FormatResponse(
            status, state, flags, summary, guidance, metrics, null
        );

        // Assert
        Assert.Contains("status: `NoMatch`", result);
        Assert.Contains("state: `Idle`", result);
        Assert.Contains("flags: -", result);
        Assert.Contains("### [Fail] 概览", result);
        Assert.Contains("- summary: 未找到要替换的文本。", result);
        Assert.Contains("- guidance: 请检查 old_text 是否与目标内容精确匹配（包括空格、换行等）。", result);
    }

    [Fact]
    public void FormatResponse_WithMultipleFlags_FormatsCorrectly() {
        // Arrange
        var status = TextEditStatus.ExternalConflict;
        var state = TextEditWorkflowState.OutOfSync;
        var flags = TextEditFlag.OutOfSync | TextEditFlag.ExternalConflict | TextEditFlag.DiagnosticHint;
        var summary = "检测到外部冲突。";
        var guidance = "请先调用 _diff 查看差异，再决定是否 _refresh。";
        var metrics = new TextEditMetrics(delta: 0, newLength: 150, selectionCount: null);

        // Act
        var result = TextEditResponseFormatter.FormatResponse(
            status, state, flags, summary, guidance, metrics, null
        );

        // Assert
        Assert.Contains("flags: `OutOfSync`, `ExternalConflict`, `DiagnosticHint`", result);
    }

    [Fact]
    public void FormatDelta_PositiveValue_IncludesPlusSign() {
        // Arrange & Act
        var result = TextEditResponseFormatter.FormatResponse(
            TextEditStatus.Success,
            TextEditWorkflowState.Idle,
            TextEditFlag.None,
            "测试",
            null,
            new TextEditMetrics(delta: 10, newLength: 100),
            null
        );

        // Assert
        Assert.Contains("| delta | +10 |", result);
    }

    [Fact]
    public void FormatDelta_NegativeValue_NoExtraSign() {
        // Arrange & Act
        var result = TextEditResponseFormatter.FormatResponse(
            TextEditStatus.Success,
            TextEditWorkflowState.Idle,
            TextEditFlag.None,
            "测试",
            null,
            new TextEditMetrics(delta: -5, newLength: 95),
            null
        );

        // Assert
        Assert.Contains("| delta | -5 |", result);
    }

    [Fact]
    public void EscapeMarkdownTableCell_EscapesNewlinesAndPipes() {
        // Arrange
        var candidates = new List<TextEditCandidate> {
            new(
                id: 1,
                preview: "Line1\nLine2|WithPipe",
                markerStart: "[[SEL#1]]",
                markerEnd: "[[/SEL#1]]",
                occurrence: 0,
                contextStart: 0,
                contextEnd: 10
            )
        };

        // Act
        var result = TextEditResponseFormatter.FormatResponse(
            TextEditStatus.MultiMatch,
            TextEditWorkflowState.SelectionPending,
            TextEditFlag.SelectionPending,
            "测试",
            null,
            new TextEditMetrics(0, 100, 1),
            candidates
        );

        // Assert
        Assert.Contains("Line1\\nLine2\\|WithPipe", result);
    }

    [Fact]
    public void FormatResponse_CandidateWithBackticks_UsesExpandedFence() {
        // Arrange
        var candidates = new List<TextEditCandidate> {
            new(
                id: 1,
                preview: "value `foo`",
                markerStart: "[[SEL#1]]",
                markerEnd: "[[/SEL#1]]",
                occurrence: 0,
                contextStart: 0,
                contextEnd: 5
            )
        };

        // Act
        var result = TextEditResponseFormatter.FormatResponse(
            TextEditStatus.MultiMatch,
            TextEditWorkflowState.SelectionPending,
            TextEditFlag.SelectionPending,
            "测试",
            null,
            new TextEditMetrics(0, 100, 1),
            candidates
        );

        // Assert
        Assert.Contains("`` value `foo` ``", result);
    }
}
