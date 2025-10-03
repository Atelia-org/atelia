using Markdig;
using Markdig.Syntax;

if (args.Length == 0 || args[0] != "ast") {
    Console.WriteLine("Usage: MemoDomProto ast <markdown-file>");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  ast <file>    Visualize Markdown AST as a tree");
    return 1;
}

if (args.Length < 2) {
    Console.WriteLine("Error: Please specify a markdown file path");
    Console.WriteLine("Usage: MemoDomProto ast <markdown-file>");
    return 1;
}

var filePath = args[1];

if (!File.Exists(filePath)) {
    Console.WriteLine($"Error: File not found: {filePath}");
    return 1;
}

var markdown = File.ReadAllText(filePath);
var document = Markdown.Parse(markdown);

Console.WriteLine($"AST for: {Path.GetFileName(filePath)}");
Console.WriteLine(new string('=', 50));

RenderNode(document, "", true);

return 0;

static void RenderNode(MarkdownObject node, string indent, bool isLast) {
    // 绘制当前节点
    var connector = isLast ? "└─ " : "├─ ";
    Console.WriteLine($"{indent}{connector}{node.GetType().Name}");

    // 准备子节点的缩进
    var childIndent = indent + (isLast ? "   " : "│  ");

    // 获取所有直接子节点
    var children = GetDirectChildren(node).ToList();

    // 递归渲染子节点
    for (int i = 0; i < children.Count; i++) {
        bool isLastChild = (i == children.Count - 1);
        RenderNode(children[i], childIndent, isLastChild);
    }
}

static IEnumerable<MarkdownObject> GetDirectChildren(MarkdownObject node) {
    // ContainerBlock 包含子块
    if (node is ContainerBlock container) {
        foreach (var child in container) {
            yield return child;
        }
    }

    // LeafBlock 可能包含内联元素
    if (node is LeafBlock leafBlock && leafBlock.Inline != null) {
        foreach (var inline in leafBlock.Inline) {
            yield return inline;
        }
    }

    // ContainerInline 包含子内联元素
    if (node is Markdig.Syntax.Inlines.ContainerInline containerInline) {
        foreach (var child in containerInline) {
            yield return child;
        }
    }
}
