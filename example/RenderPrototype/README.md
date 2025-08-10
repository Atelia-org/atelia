# RenderPrototype

目的：在不影响正式实现的前提下，原型化验证 IRenderSource / RenderContext / RenderSection 设计与组合器。

- 完全内存假数据，NodeRef 不与 ViewName 耦合
- 两个源：视图面板（Meta）+ 假层次树（支持可选 Expand/Collapse 能力）
- 简单合成器：按 Priority 合并为 Markdown

运行：
- 将本项目加入解决方案后运行；或独立在该目录执行 `dotnet run`

后续：
- 可加第三个源（如 EnvInfoSource）验证多源拼接
- 评估 ActionToken 的位置与替换策略，再并入正式实现
