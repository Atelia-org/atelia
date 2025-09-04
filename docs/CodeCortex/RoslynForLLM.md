# Roslyn For LLM
（本文件是最初概念草稿，已逐步演化。新的系统化顶层设计文档参见：`CodeCortex_Design.md`）

通过CLI命令和上下文注入文件，来让LLM Coder获得尽量直接的Roslyn能力，并可自行迭代完善。

### 初版设计关键特征：
输入以.sln / .csproj文件为单位，类似dotnet-format。使用Microsoft.CodeAnalysis.Workspaces / MSBuild。
绑定Roslyn生态，暂时不考虑扩展支持C#以外的编程语言。
核心dll + 组件dll + service exe + cli exe 结构。
service exe 与 cli exe 之间用StreamJsonRpc + Nerdbank.Streams通信。

### 查询方向的实现的层次
第1层：以类型为单位，在内存中生成outline(外观+xml文档内容)的文本。附带结构化的元信息(命名空间、程序集、源文件路径)，后续考虑添加git commit hash/源文件hash等来进一步支持增量生成。
第2层：依赖第1层。将第1层生成的结果在特定根目录内(例如.outline目录)内以易于git diff处理的形式形成缓存，并加入git，批量执行。
第3层：依赖第2层。一个service进程，.exe程序。把第2层实现的outline缓存，以json rpc服务的形式暴露出来。并通过file watcher监视workspace变化，确保缓存新鲜。
第4层：依赖第3层。一个CLI工具，能调用json rpc调用service进程提供的对目标类型的outline文本查询能力，直接控制台输出。
第5层：依赖第3层。向预先配置好的文件路径生成prompt文件。大多数AI Agent环境(比如你所在的这个Copilot Chat环境就会把“.github/copilot-instructions.md”文件的最新内容常驻在你上下文中)都支持把特定的本地文本文件保持常驻LLM上下文中，让LLM直接看到这些信息，在我们的应用中可以作为“一块面向LLM Coder的显示屏”。为限制占用的上下文空间，我们可以维护一个FIFO队列，仅渲染最近的几条查询结果，用总字符长度作为限制阈值。后续可以给CLI中添加更多命令，来让LLM可以自主pin住特定的类型outline不滑出窗口，也不受窗口长度限制。还可以进一步增加LLM可自主管理的更多命令，比如从窗口中删除不再关心的特定类型。

### 下面是更有野心的编辑方向
以一定的AST粒度，来暴露Roslyn重构能力，补充和替代目前基于文本的LLM编码能力。
以命名空间路径作为目标token，例如`MyNamespace.MyClass.NestedClass.SomeFunc(int,string)`
例如：重命名标识符、创建类型、替换函数体、替换函数签名等。

### 内部集成用LLM实现的语义功能
生成超越Roslyn能力的语义信息。需要用Roslyn先追踪类型依赖关系，沿着依赖顺序从底层开始生成LLM语义分析结果，在生成目标类型的语义分析时，自动为LLM准备其所依赖的类型的信息。对于识别到的环，则以环上类型整体作为语义分析单元(例如链表的实现)。形成缓存，加入git，图状依赖。

用hash追踪生成语义分析时依赖的文件的变动，这就是在前面“第2层”时，要用“易于git diff处理的形式形成缓存，并加入git”的原因。不论LLM语义分析依赖的是源码还是先前对其他类型的语义分析结果，基础发生变动就重新生成语义分析，通过git diff追踪，链式传播。

### 由调查形成的认知
提供语义查询接口。
能直接使用现有信息回答的，就直接回答。
无法直接回答的，开启一个“调查任务”，由内部Agent执行。对于形成的结果，同样和其他认知一样缓存成文件，并记录对其他文件的依赖关系。这样随着查询，这个库会加深对项目的理解，并自动维护新鲜度。


### 更新：泛型基础名匹配（GenericBase）
在符号解析中新增对“泛型基础名”的支持：
- 允许输入 `List` / `List<int>` / `List<` / `System.Collections.Generic.List<int>` 这类查询，统一归一化为基础名 `List` 后进行匹配。
- 匹配基于索引中的 `GenericBaseNameIndex`，收录所有泛型族（同名不同命名空间会返回多条）。
- 排序优先级调整为：`GenericBase` 优先于 `Fuzzy`（以更快地命中如 `List<T>`）。
- CLI 输出会显示 `MatchKind`，例如：
  - `GenericBase   System.Collections.Generic.List1 [class] (Id=...)`

更多细节见附录：`Appendix_SymbolResolveAlgorithms_P1.md`。
