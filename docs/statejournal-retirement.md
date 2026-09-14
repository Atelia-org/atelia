# StateJournal 迁出与封存

StateJournal 与 `StateJournal.Generators` 已从 Atelia 的活跃构建图迁出，作为停止活跃开发的历史源码保存。它不是 DurableGraph 的兼容层，也没有 StateJournal → DurableGraph 数据迁移或 NuGet 交付。

- 封存仓：<https://github.com/Atelia-org/atelia-statejournal>
- 新仓交付 commit：`76a6afaf00c32f5410e4c19e55dd759d390ccc9f`
- Atelia 来源 commit：`c7ef0fcc925bc259b8bbb78fce1ee865d23b84b7`
- 本机位置：`E:\repos\Atelia-org\atelia-statejournal`

新仓保留运行时、Source Generator、自有测试、两个 benchmark、`docs/StateJournal` 与 `archive/StateJournal`；它不发布 NuGet，也不要求本仓或其他 sibling checkout。当前用法请从新仓的 [usage guide](https://github.com/Atelia-org/atelia-statejournal/blob/76a6afaf00c32f5410e4c19e55dd759d390ccc9f/docs/StateJournal/usage-guide.md) 开始，独立验证和来源证据见其 [extraction validation](https://github.com/Atelia-org/atelia-statejournal/blob/76a6afaf00c32f5410e4c19e55dd759d390ccc9f/docs/extraction-validation.md)。

需要回看迁出前 Atelia 原文时，固定查看 [来源 commit 的 StateJournal 目录](https://github.com/Atelia-org/atelia/tree/c7ef0fcc925bc259b8bbb78fce1ee865d23b84b7/docs/StateJournal)。Atelia 的其他 `archive/` 原型仍保留其旧目录语境，不会通过新仓重新接入构建。
