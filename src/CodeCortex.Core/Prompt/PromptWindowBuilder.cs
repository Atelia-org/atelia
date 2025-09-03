using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeCortex.Core.Outline;

namespace CodeCortex.Core.Prompt {
    /// <summary>
    /// 聚合 Pinned/Focus/Recent 区域，生成 Prompt 窗口 markdown 文件。
    /// </summary>
    public class PromptWindowBuilder {
        private readonly int _focusCount;
        private readonly int _maxChars;
        private readonly string _outlineDir;
        private readonly CodeCortex.Core.IO.IFileSystem _fs;

        public PromptWindowBuilder(string outlineDir, CodeCortex.Core.IO.IFileSystem fs, int focusCount = 8, int maxChars = 40000) {
            _outlineDir = outlineDir;
            _focusCount = focusCount;
            _maxChars = maxChars;
            _fs = fs;
        }


        /// <summary>
        /// 通过类型 id 列表自动加载 outline 内容并聚合窗口。
        /// </summary>
        public string BuildWindow(IEnumerable<string> pinned, IEnumerable<string> focus, IEnumerable<string> recent) {
            return BuildWindowWithLoader(pinned, focus, recent, LoadOutlineById, _maxChars);
        }

        /// <summary>
        /// 允许注入 outline 加载委托，便于测试。
        /// </summary>
        public static string BuildWindowWithLoader(
            IEnumerable<string> pinned,
            IEnumerable<string> focus,
            IEnumerable<string> recent,
            Func<string, string?> outlineLoader,
            int maxChars = 40000
        ) {
            var sections = new List<(string title, List<string> outlines)>
            {
                ("Pinned", pinned.Select(outlineLoader).Where(x => !string.IsNullOrEmpty(x)).Cast<string>().ToList()),
                ("Focus", focus.Select(outlineLoader).Where(x => !string.IsNullOrEmpty(x)).Cast<string>().ToList()),
                ("Recent", recent.Select(outlineLoader).Where(x => !string.IsNullOrEmpty(x)).Cast<string>().ToList())
            };
            var result = new List<string>();
            int total = 0;
            foreach (var (title, outlines) in sections) {
                if (outlines.Count == 0) {
                    continue;
                }

                result.Add($"# {title}");
                foreach (var outline in outlines) {
                    if (total + outline.Length > maxChars && title == "Recent") {
                        break;
                    }

                    result.Add(outline);
                    total += outline.Length;
                }
            }
            return string.Join("\n\n", result);
        }

        // 便于测试：直接聚合 outline 内容（不关心 id）
        public static string BuildWindowFromContents(
            IReadOnlyList<string> pinnedContents,
            IReadOnlyList<string> focusContents,
            IReadOnlyList<string> recentContents,
            int maxChars = 40000
        ) {
            var sections = new List<(string title, IReadOnlyList<string> outlines)>
            {
                ("Pinned", pinnedContents),
                ("Focus", focusContents),
                ("Recent", recentContents)
            };
            var result = new List<string>();
            int total = 0;
            foreach (var (title, outlines) in sections) {
                if (outlines.Count == 0) {
                    continue;
                }

                result.Add($"# {title}");
                foreach (var outline in outlines) {
                    if (total + outline.Length > maxChars && title == "Recent") {
                        break;
                    }

                    result.Add(outline);
                    total += outline.Length;
                }
            }
            return string.Join("\n\n", result);
        }

        private List<string> LoadOutlines(IEnumerable<string> typeIds) {
            var list = new List<string>();
            foreach (var id in typeIds) {
                var content = LoadOutlineById(id);
                if (!string.IsNullOrEmpty(content)) {
                    list.Add(content);
                }
            }
            return list;
        }

        private string? LoadOutlineById(string id) {
            var path = Path.Combine(_outlineDir, $"{id}.outline.md");
            if (_fs.FileExists(path)) {
                try { return _fs.ReadAllText(path); } catch { }
            }
            return null;
        }
    }
}
