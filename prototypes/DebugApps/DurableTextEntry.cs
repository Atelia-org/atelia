using System.CommandLine;
using Atelia.Agent.Core.Text;
using Atelia.StateJournal;

namespace Atelia.DebugApps;

/// <summary>
/// PipeMux 入口：交互式把玩 <see cref="DurableText"/>。
///
/// 注册方法：
/// <code>
/// pmux :register dt /path/to/Atelia.DebugApps.dll Atelia.DebugApps.DurableTextEntry.BuildText
/// </code>
///
/// 状态：进程内单例 Revision + DurableText，跨调用保留。
/// `dt new` 重置；其他命令在当前实例上操作。
/// </summary>
public static class DurableTextEntry {
    private static Revision _revision = new(boundSegmentNumber: 1);
    private static DurableText _text = CreateFreshText();

    private static DurableText CreateFreshText() {
        _revision = new Revision(boundSegmentNumber: 1);
        return _revision.CreateText();
    }

    public static RootCommand BuildText() {
        var root = new RootCommand("DurableText interactive playground");

        root.Add(BuildNewCommand());
        root.Add(BuildAppendCommand());
        root.Add(BuildPrependCommand());
        root.Add(BuildInsertAfterCommand());
        root.Add(BuildInsertBeforeCommand());
        root.Add(BuildSetCommand());
        root.Add(BuildDeleteCommand());
        root.Add(BuildListCommand());
        root.Add(BuildRenderCommand());
        root.Add(BuildLoadCommand());
        root.Add(BuildCountCommand());
        root.Add(BuildStatusCommand());

        return root;
    }

    // ─────────────────────────────────────────
    // 编辑命令
    // ─────────────────────────────────────────

    private static Command BuildNewCommand() {
        var cmd = new Command("new", "Reset to a fresh empty DurableText");
        cmd.SetAction(ctx => {
            _text = CreateFreshText();
            ctx.InvocationConfiguration.Output.WriteLine("Created fresh empty DurableText.");
        });
        return cmd;
    }

    private static Command BuildAppendCommand() {
        var contentArg = new Argument<string>("content") { Description = "Block content" };
        var cmd = new Command("append", "Append a block at tail") { contentArg };
        cmd.SetAction(ctx => {
            var content = ctx.GetValue(contentArg)!;
            var id = _text.Append(content);
            ctx.InvocationConfiguration.Output.WriteLine($"Appended block [{id}]: {Truncate(content, 60)}");
        });
        return cmd;
    }

    private static Command BuildPrependCommand() {
        var contentArg = new Argument<string>("content") { Description = "Block content" };
        var cmd = new Command("prepend", "Prepend a block at head") { contentArg };
        cmd.SetAction(ctx => {
            var content = ctx.GetValue(contentArg)!;
            var id = _text.Prepend(content);
            ctx.InvocationConfiguration.Output.WriteLine($"Prepended block [{id}]: {Truncate(content, 60)}");
        });
        return cmd;
    }

    private static Command BuildInsertAfterCommand() {
        var idArg = new Argument<uint>("after-id") { Description = "Insert after this block id" };
        var contentArg = new Argument<string>("content") { Description = "Block content" };
        var cmd = new Command("insert-after", "Insert a block after the given id") { idArg, contentArg };
        cmd.SetAction(ctx => {
            var afterId = ctx.GetValue(idArg);
            var content = ctx.GetValue(contentArg)!;
            try {
                var id = _text.InsertAfter(afterId, content);
                ctx.InvocationConfiguration.Output.WriteLine($"Inserted [{id}] after [{afterId}]: {Truncate(content, 60)}");
            }
            catch (Exception ex) {
                ctx.InvocationConfiguration.Error.WriteLine($"Error: {ex.Message}");
            }
        });
        return cmd;
    }

    private static Command BuildInsertBeforeCommand() {
        var idArg = new Argument<uint>("before-id") { Description = "Insert before this block id" };
        var contentArg = new Argument<string>("content") { Description = "Block content" };
        var cmd = new Command("insert-before", "Insert a block before the given id") { idArg, contentArg };
        cmd.SetAction(ctx => {
            var beforeId = ctx.GetValue(idArg);
            var content = ctx.GetValue(contentArg)!;
            try {
                var id = _text.InsertBefore(beforeId, content);
                ctx.InvocationConfiguration.Output.WriteLine($"Inserted [{id}] before [{beforeId}]: {Truncate(content, 60)}");
            }
            catch (Exception ex) {
                ctx.InvocationConfiguration.Error.WriteLine($"Error: {ex.Message}");
            }
        });
        return cmd;
    }

    private static Command BuildSetCommand() {
        var idArg = new Argument<uint>("id") { Description = "Block id to overwrite" };
        var contentArg = new Argument<string>("content") { Description = "New block content" };
        var cmd = new Command("set", "Replace block content") { idArg, contentArg };
        cmd.SetAction(ctx => {
            var id = ctx.GetValue(idArg);
            var content = ctx.GetValue(contentArg)!;
            try {
                _text.SetContent(id, content);
                ctx.InvocationConfiguration.Output.WriteLine($"Set [{id}]: {Truncate(content, 60)}");
            }
            catch (Exception ex) {
                ctx.InvocationConfiguration.Error.WriteLine($"Error: {ex.Message}");
            }
        });
        return cmd;
    }

    private static Command BuildDeleteCommand() {
        var idArg = new Argument<uint>("id") { Description = "Block id to delete" };
        var cmd = new Command("delete", "Delete a block") { idArg };
        cmd.SetAction(ctx => {
            var id = ctx.GetValue(idArg);
            try {
                _text.Delete(id);
                ctx.InvocationConfiguration.Output.WriteLine($"Deleted [{id}]");
            }
            catch (Exception ex) {
                ctx.InvocationConfiguration.Error.WriteLine($"Error: {ex.Message}");
            }
        });
        return cmd;
    }

    private static Command BuildLoadCommand() {
        var textArg = new Argument<string>("text") {
            Description = "Multi-line text to load (split by \\n into blocks). Requires empty document."
        };
        var cmd = new Command("load", "Bulk load text into an empty document") { textArg };
        cmd.SetAction(ctx => {
            var text = ctx.GetValue(textArg)!;
            try {
                _text.LoadText(text);
                ctx.InvocationConfiguration.Output.WriteLine($"Loaded {_text.BlockCount} blocks.");
            }
            catch (Exception ex) {
                ctx.InvocationConfiguration.Error.WriteLine($"Error: {ex.Message}");
            }
        });
        return cmd;
    }

    // ─────────────────────────────────────────
    // 查询/渲染命令
    // ─────────────────────────────────────────

    private static Command BuildListCommand() {
        var cmd = new Command("list", "List all blocks (raw, one per line)");
        cmd.SetAction(ctx => {
            var blocks = _text.GetAllBlocks();
            if (blocks.Count == 0) {
                ctx.InvocationConfiguration.Output.WriteLine("(empty)");
                return;
            }
            foreach (var b in blocks) {
                ctx.InvocationConfiguration.Output.WriteLine($"[{b.Id}] {b.Content}");
            }
        });
        return cmd;
    }

    private static Command BuildRenderCommand() {
        var styleOpt = new Option<string>("--style") {
            Description = "Render style: bracketed | fenced",
            DefaultValueFactory = _ => "bracketed",
        };
        var maxBlocksOpt = new Option<int>("--max-blocks") {
            Description = "Truncate after N blocks (0 = no limit)",
            DefaultValueFactory = _ => 0,
        };
        var maxLenOpt = new Option<int>("--max-content") {
            Description = "Truncate each block content beyond N chars (0 = no limit)",
            DefaultValueFactory = _ => 0,
        };
        var cmd = new Command("render", "Render via TextRenderer") { styleOpt, maxBlocksOpt, maxLenOpt };
        cmd.SetAction(ctx => {
            var style = ctx.GetValue(styleOpt) switch {
                "fenced" => RenderStyle.Fenced,
                _ => RenderStyle.Bracketed,
            };
            var opts = new RenderOptions {
                Style = style,
                MaxBlocks = ctx.GetValue(maxBlocksOpt),
                MaxContentLength = ctx.GetValue(maxLenOpt),
            };
            var blocks = _text.GetAllBlocks();
            var tuples = new List<(uint Id, string Content)>(blocks.Count);
            foreach (var b in blocks) { tuples.Add((b.Id, b.Content)); }
            ctx.InvocationConfiguration.Output.Write(TextRenderer.Render(tuples, opts));
        });
        return cmd;
    }

    private static Command BuildCountCommand() {
        var cmd = new Command("count", "Show block count");
        cmd.SetAction(ctx => {
            ctx.InvocationConfiguration.Output.WriteLine($"{_text.BlockCount} blocks");
        });
        return cmd;
    }

    private static Command BuildStatusCommand() {
        var cmd = new Command("status", "Show document state summary");
        cmd.SetAction(ctx => {
            var output = ctx.InvocationConfiguration.Output;
            output.WriteLine($"Blocks:      {_text.BlockCount}");
            output.WriteLine($"HasChanges:  {_text.HasChanges}");
            output.WriteLine($"Kind:        {_text.Kind}");
        });
        return cmd;
    }

    private static string Truncate(string s, int max) {
        if (s.Length <= max) { return s; }
        return string.Concat(s.AsSpan(0, max), "…");
    }
}
