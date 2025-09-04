using System;
using System.CommandLine;
using System.IO;
using System.Linq;
using CodeCortex.Core.Prompt;

namespace CodeCortex.DevCli {
    public static class WindowCommand {
        public static Command Create(string ctxRoot, string outlineDir, string pinnedPath, CodeCortex.Core.IO.IFileSystem? fs = null) {
            var cmd = new Command("window", "生成 Prompt 窗口 markdown 文件 (Pinned/Focus/Recent)");
            cmd.SetHandler(
                () => {
                    var _fs = fs ?? new CodeCortex.Core.IO.DefaultFileSystem();
                    // 加载 pinned
                    var pinnedSet = new PinnedSet<string>(pinnedPath, _fs);
                    var pinned = pinnedSet.Items.ToList();
                    // 加载最近访问（可后续持久化，这里用内存 stub）
                    var accessTracker = new AccessTracker<string>(32); // TODO: 持久化 recent
                    var focus = accessTracker.GetRecent(8);
                    var recent = accessTracker.GetAll().Except(pinned).Except(focus).ToList();
                    // 构建窗口
                    var builder = new PromptWindowBuilder(outlineDir, _fs);
                    var content = builder.BuildWindow(pinned, focus, recent);
                    var outPath = Path.Combine(ctxRoot, "prompt", "prompt_window.md");
                    _fs.CreateDirectory(Path.GetDirectoryName(outPath)!);
                    _fs.WriteAllText(outPath, content);
                    Console.WriteLine($"Prompt 窗口已生成: {outPath} (Length={content.Length})");
                }
            );
            return cmd;
        }
    }
}
