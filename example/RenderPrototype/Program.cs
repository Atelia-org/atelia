using RenderPrototype.Rendering;
using RenderPrototype.Sources;

var meta = new MemoViewMetaSource();
var tree = new FakeHierarchySource();
var live = new LiveDocsSource();
var sink = new FakeMemoTreeSinkSource();
var env = new EnvInfoSource();

var sources = new List<IRenderSource> { env, meta, tree, live, sink };
var composer = new SimpleComposer();
var ctx = new RenderContext(ViewName: "default");

Console.OutputEncoding = System.Text.Encoding.UTF8;

// 初始渲染
var sections = await composer.ComposeAsync(ctx, sources);
Console.WriteLine("# 初始渲染\n");
Console.WriteLine(composer.ToMarkdown(sections));
Console.WriteLine("\n\n");

// 演示：从 LiveDocs 复制第一条到 MemoTree 收件箱
var dispatcher = new DemoActionDispatcher(live, sink);
var liveSec = sections.FirstOrDefault(s => s.SectionId == "livedocs:list");
var copyToken = liveSec?.ActionTokens?.FirstOrDefault(t => t.Kind == "copy");
if (copyToken is not null)
{
    dispatcher.Dispatch(copyToken);
}

sections = await composer.ComposeAsync(ctx, sources);
Console.WriteLine("# 复制到 MemoTree 后\n");
Console.WriteLine(composer.ToMarkdown(sections));
Console.WriteLine("\n\n");

// 演示：展开环境信息
if (env is IExpandableRenderSource expandable)
{
    await expandable.ExpandAsync(new NodeRef("env", "环境"));
}

sections = await composer.ComposeAsync(ctx, sources);
Console.WriteLine("# 展开环境信息后\n");
Console.WriteLine(composer.ToMarkdown(sections));
