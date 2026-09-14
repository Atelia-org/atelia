# StateJournal 拆仓验收记录

## 输入与范围

- Atelia 来源：`c7ef0fcc925bc259b8bbb78fce1ee865d23b84b7`；迁出目录为 StateJournal、其 Generator、自有测试、两个 benchmark、`docs/StateJournal`、`archive/StateJournal` 和根 `LICENSE`。
- 调查时工作树干净；实施开始时额外存在的 `AGENTS.md` 修改与未跟踪的拆仓方案均为本任务文档，已随 Atelia 迁出交接纳入。
- 新仓：<https://github.com/Atelia-org/atelia-statejournal>，交付 commit `76a6afaf00c32f5410e4c19e55dd759d390ccc9f`，public，未发布 NuGet。

## 新仓独立证据

- 固定 commit 的受控 `git archive` 含 308 个来源文件；236 个迁入 C# Git blob 与来源逐一相同。
- 新仓使用 SDK `10.0.201`、自身 `nuget.config` 与新包缓存；五项目 Release Rebuild 通过，0 warning / 0 error。
- 独立 clone 后运行 `eng/Verify.ps1`：1961 total，1961 passed，0 failed，0 skipped；Generator 产生 MixedDeque / MixedDict / MixedOrderedDict 相关 `.g.cs`。
- 没有两个 Style 项目、原仓 eng import、StorageSourceRoot 或 CompletionSourceRoot；自有 Generator 的 ProjectReference 保留。

## Atelia 基线与迁出后验证

- 迁出前 Release Rebuild：通过，0 warning / 0 error。
- 迁出前 StateJournal 测试：1961 total，1960 passed，0 skipped，1 failed；失败为 `RepositoryTests.MaintainSegmentLayout_ArchivesExcessRecentSegmentsIntoBuckets` 的 Windows `PublishPrimaryRef` access-denied。
- 迁出后 `dotnet build Atelia.sln -c Release -t:Rebuild`：通过，0 warning / 0 error。
- `Atelia.sln` 不再包含 StateJournal；活跃 `src/`、`prototypes/`、`tests/`、`benchmarks/` 没有 StateJournal 代码或项目引用。
- 当前的 `AGENTS.md`、`CLAUDE.md` 与三个 `docs/StateJournal` 跳转文件指向迁出说明。`docs/Galatea/backlog/` 中三份已有历史任务仍保留旧链接，但它们通过跳转文件到达新仓，不属于活跃构建引用。
- `git diff --check` 在提交前通过。

## 交付

- 新仓 `main`：`76a6afaf00c32f5410e4c19e55dd759d390ccc9f`，已推送至 `https://github.com/Atelia-org/atelia-statejournal.git`。
- Atelia 迁出提交：`592020e9232ea1a56f0b9b2bbba18c3f17a71d62`，已推送至 `origin/main`。
- 两仓均保持普通 public Git 仓；没有创建 NuGet 包、CI、Trusted Publishing、release、tag 或 GitHub Archive。

## 允许的残留

`archive/` 内老项目可保留历史 StateJournal 引用；活跃 `src/`、`prototypes/`、`tests/`、`benchmarks/` 不保留对已迁出项目的源码或项目引用。SessionJournal 不在本次范围内。
