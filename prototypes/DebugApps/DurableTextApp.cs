using System.Text;
using Atelia.Agent.Core;
using Atelia.Agent.Core.App;
using Atelia.Agent.Core.Tool;
using Atelia.Completion.Abstractions;
using Atelia.StateJournal;

namespace Atelia.DebugApps;

/// <summary>
/// IApp wrapper：把一个 <see cref="DurableText"/> 实例的编辑能力暴露给 LLM Agent。
///
/// <list type="bullet">
///   <item><description>RenderWindow 渲染当前所有 block，并显式标注 [blockId]，让 LLM 学会用稳定 ID 引用</description></item>
///   <item><description>提供 8 个 tool：append / prepend / insert_after / insert_before / set / delete / clear / load</description></item>
///   <item><description>tool 反馈包含新 blockId（对插入类）或操作摘要（对 set/delete 类），帮 LLM 形成"ID 是稳定身份证"的直觉</description></item>
/// </list>
///
/// 状态保存在进程内 <see cref="DurableText"/> 实例中。底层 Revision 是临时的（未接入 Repository 持久化）。
/// </summary>
public sealed class DurableTextApp : IApp {
    private const string AppName = "DurableText";

    private Revision _revision;
    private DurableText _text;

    public DurableTextApp() {
        _revision = new Revision(boundSegmentNumber: 1);
        _text = _revision.CreateText();

        Tools = new ITool[] {
            MethodToolWrapper.FromDelegate<string>(AppendAsync),
            MethodToolWrapper.FromDelegate<string>(PrependAsync),
            MethodToolWrapper.FromDelegate<long, string>(InsertAfterAsync),
            MethodToolWrapper.FromDelegate<long, string>(InsertBeforeAsync),
            MethodToolWrapper.FromDelegate<long, string>(SetAsync),
            MethodToolWrapper.FromDelegate<long>(DeleteAsync),
            MethodToolWrapper.FromDelegate(ClearAsync),
            MethodToolWrapper.FromDelegate<string>(LoadAsync),
        };
    }

    public string Name => AppName;

    public string Description => "持久化文本容器，每个 block 拥有稳定 ID。编辑通过 blockId 寻址，无需复述内容。 注意：Window 里看到的 `<id> │ <content>` 是渲染格式，content 参数仅填正文。";

    public IReadOnlyList<ITool> Tools { get; }

    public string? RenderWindow() {
        var blocks = _text.GetAllBlocks();
        var sb = new StringBuilder();
        sb.Append("## DurableText\n\n");

        if (blocks.Count == 0) {
            sb.Append("(empty document — use `dt_append` 或 `dt_load` 添加内容)\n");
        }
        else {
            // 渲染约定说明：让 LLM 明确区分"渲染叠加层"与"正文"。
            // 格式参考 bat / delta / GCC：右对齐 blockId + ` │ ` + 内容。
            // ` │ ` (U+2502) 是代码预览工具的通用列分隔符，强信号区隔元数据。
            sb.Append("> 显示格式: `<blockId> │ <content>`. ` │ ` 左侧是元数据列（不属于正文），\n");
            sb.Append("> 调用 dt_* 工具时 content 参数只填正文部分，**不要**包含 blockId 或 ` │ `。\n\n");

            // 计算 blockId 列宽以右对齐
            var idWidth = 1;
            foreach (var b in blocks) {
                var w = b.Id.ToString().Length;
                if (w > idWidth) { idWidth = w; }
            }

            sb.Append("```\n");
            foreach (var b in blocks) {
                var idStr = b.Id.ToString().PadLeft(idWidth);
                // 多行内容：第一行带 blockId 列，后续行用空白占位（与 bat 行为一致）
                var contentLines = b.Content.Split('\n');
                sb.Append(idStr).Append(" │ ").Append(contentLines[0]).Append('\n');
                for (int i = 1; i < contentLines.Length; i++) {
                    sb.Append(new string(' ', idWidth)).Append(" │ ").Append(contentLines[i]).Append('\n');
                }
            }
            sb.Append("```\n");
            sb.Append('\n').Append(blocks.Count).Append(" blocks total.
            // 计算 blockId 列宽以右对齐
            var idWidth = 1;
            foreach (var b in blocks) {
                var w = b.Id.ToString().Length;
                if (w > idWidth) { idWidth = w; }
            }

            sb.Append("```\n");
            foreach (var b in blocks) {
                var idStr = b.Id.ToString().PadLeft(idWidth);
                // 多行内容：第一行带 blockId 列，后续行用空白占位（与 bat 行为一致）
                var contentLines = b.Content.Split('\n');
                sb.Append(idStr).Append(" │ ").Append(contentLines[0]).Append('\n');
                for (int i = 1; i < contentLines.Length; i++) {
                    sb.Append(new string(' ', idWidth)).Append(" │ ").Append(contentLines[i]).Append('\n');
                }
            }
            sb.Append("```\n");
            sb.Append('\n').Append(blocks.Count).Append(" blocks total.\n");
        }

        return sb.ToString();
    }

    // ─────────────────────────────────────────
    // Tools
    // ─────────────────────────────────────────

    [Tool("dt_append", "在 DurableText 尾部追加一个 block。返回新分配的 blockId。")]
    private ValueTask<LodToolExecuteResult> AppendAsync(
        [ToolParam("要追加的 block 正文（可含换行符；不要包含 blockId 或 │ 分隔符）")] string content,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        var id = _text.Append(content);
        return Ok($"Appended block [{id}]. 当前共 {_text.BlockCount} 个 block。");
    }

    [Tool("dt_prepend", "在 DurableText 头部插入一个 block。返回新分配的 blockId。")]
    private ValueTask<LodToolExecuteResult> PrependAsync(
        [ToolParam("要插入的 block 内容")] string content,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        var id = _text.Prepend(content);
        return Ok($"Prepended block [{id}]. 当前共 {_text.BlockCount} 个 block。");
    }

    [Tool("dt_insert_after", "在指定 block 之后插入新 block。返回新分配的 blockId。")]
    private ValueTask<LodToolExecuteResult> InsertAfterAsync(
        [ToolParam("已存在的 block ID（在它之后插入）")] long after_id,
        [ToolParam("要插入的 block 内容")] string content,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        try {
            var id = _text.InsertAfter(checked((uint)after_id), content);
            return Ok($"Inserted block [{id}] after [{after_id}].");
        }
        catch (Exception ex) {
            return Fail($"Failed to insert after [{after_id}]: {ex.Message}");
        }
    }

    [Tool("dt_insert_before", "在指定 block 之前插入新 block。返回新分配的 blockId。")]
    private ValueTask<LodToolExecuteResult> InsertBeforeAsync(
        [ToolParam("已存在的 block ID（在它之前插入）")] long before_id,
        [ToolParam("要插入的 block 内容")] string content,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        try {
            var id = _text.InsertBefore(checked((uint)before_id), content);
            return Ok($"Inserted block [{id}] before [{before_id}].");
        }
        catch (Exception ex) {
            return Fail($"Failed to insert before [{before_id}]: {ex.Message}");
        }
    }

    [Tool("dt_set", "替换指定 block 的内容（block ID 不变）。")]
    private ValueTask<LodToolExecuteResult> SetAsync(
        [ToolParam("要修改的 block ID")] long block_id,
        [ToolParam("新内容")] string content,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        try {
            _text.SetContent(checked((uint)block_id), content);
            return Ok($"Set block [{block_id}].");
        }
        catch (Exception ex) {
            return Fail($"Failed to set [{block_id}]: {ex.Message}");
        }
    }

    [Tool("dt_delete", "删除指定 block。该 block ID 不再可用，但其他 block 的 ID 不受影响。")]
    private ValueTask<LodToolExecuteResult> DeleteAsync(
        [ToolParam("要删除的 block ID")] long block_id,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        try {
            _text.Delete(checked((uint)block_id));
            return Ok($"Deleted block [{block_id}]. 剩余 {_text.BlockCount} 个 block。");
        }
        catch (Exception ex) {
            return Fail($"Failed to delete [{block_id}]: {ex.Message}");
        }
    }

    [Tool("dt_clear", "重置 DurableText 为空文档。所有 block 与其 ID 都会丢失。")]
    private ValueTask<LodToolExecuteResult> ClearAsync(
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        _revision = new Revision(boundSegmentNumber: 1);
        _text = _revision.CreateText();
        return Ok("DurableText cleared. 现在是空文档。");
    }

    [Tool("dt_load", "把多行文本一次性加载为多个 block（按 \\n 分割）。仅当文档为空时可用。")]
    private ValueTask<LodToolExecuteResult> LoadAsync(
        [ToolParam("要加载的多行文本")] string text,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        try {
            _text.LoadText(text);
            return Ok($"Loaded {_text.BlockCount} blocks.");
        }
        catch (Exception ex) {
            return Fail($"Failed to load: {ex.Message}");
        }
    }

    // ─────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────

    public DurableText Snapshot() => _text;

    private static ValueTask<LodToolExecuteResult> Ok(string message)
        => ValueTask.FromResult(new LodToolExecuteResult(
            ToolExecutionStatus.Success,
            new LevelOfDetailContent(message)));

    private static ValueTask<LodToolExecuteResult> Fail(string message)
        => ValueTask.FromResult(new LodToolExecuteResult(
            ToolExecutionStatus.Failed,
            new LevelOfDetailContent(message)));
}
