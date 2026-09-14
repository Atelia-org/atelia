# StateJournal 源码拆仓与封存实施方案

> 状态：**方案已撰写，尚未实施**。本轮只完成仓库调查与方案编写，没有创建新仓、迁移代码、运行构建测试或提交/push。
> 调查日期：2026-09-14；Atelia 基准：`c7ef0fcc925bc259b8bbb78fce1ee865d23b84b7`；调查开始时工作树干净。
> 预期执行者：`gpt-5.6-terra`。按 P0 → P4 顺序执行即可，不需要多 Agent 编队。
> 建议仓名：`Atelia-org/atelia-statejournal`；建议本地位置：`E:\repos\Atelia-org\atelia-statejournal`。这是本方案的默认命名，远端是否存在、是否允许创建/push，以实施时实际状态与用户授权为准。

## 1. 目标与本次裁剪

将 `src/StateJournal`、`src/StateJournal.Generators` 及其自有测试、benchmark、文档移到一个独立源码仓库，作为停止活跃开发的历史实现保存。未来主要演进方向是 DurableGraph；本次不实现两者的数据迁移或兼容适配。

完成后的状态：

- 新仓可以仅凭自身源码、指定的 .NET SDK 和 nuget.org 依赖完成构建与原有测试，不需要旁边放着 Atelia、atelia-storage 或 atelia-completion 的源码。
- 新仓不再引用 `Analyzers.Style`、`Analyzers.Style.CodeFixes`；**继续保留 StateJournal 自己的 Source Generator**。
- 不生产、不发布 StateJournal NuGet 包，不设置 NuGet policy、API Key、Trusted Publishing 或发布工作流。
- Atelia 移除迁出项目和自有测试/benchmark，不新增 StateJournal 的 PackageReference、跨仓 ProjectReference、源码模式开关、submodule 或取源码脚本。
- 历史源码、测试与设计资料可追溯，README 明确“历史实验实现，停止活跃开发，不提供 NuGet 包”。

这里的“封存”首先指项目定位。**不默认执行 GitHub 的 Archive repository 操作**：先保持普通 Git 仓可修正文档；远端设为只读归档是用户后续可选动作，不影响本次完成。

### 1.1 默认决策

| 项目 | 本方案选择 | 原因 |
| --- | --- | --- |
| Git 历史 | 从实施时固定的源 commit 导出受控路径，做新仓初始提交 | 这次重在可靠封存；原 Atelia 历史仍可查询，无须投入历史过滤 |
| 源码布局 | 保留原相对路径 | 源码、测试、文档之间的多数相对链接可以直接成立 |
| 外部依赖 | 直接写入现有明确版本的 PackageReference | 三个直接依赖已在使用，不复制前两次的双模式设施 |
| 包输出 | 新仓全部项目 `IsPackable=false` | 明确这次只交付源码 |
| CI | 默认不新增 GitHub Actions；提供一条本地验证入口 | 没有发布或持续维护需求，减少维护面 |
| 验证 | 原库 Release 测试基线对比、新仓独立重建、Atelia 移除后构建 | 没有活跃产品消费者，不复制上一轮公开包验证设施 |
| 本轮代码改动 | 只改项目/构建配置与文档，迁移 C# 文件保持原样 | 避免把退休整理变成产品重构 |

这些是为当前目标选定的实施默认值，不表示用户要求保留完整 Git 历史或建立发布服务。实施时如用户改选历史提取，再使用 `extract-dotnet-repo` 的 fresh clone 路线；不要在源仓运行 `git filter-repo`。

## 2. 已核实的事实

本节来自当前工作树、Git 跟踪清单、MSBuild 求值和现有 assets。**现有 assets 不是本轮重新 restore 的结果；实施者必须在 P0/P2 重新验证。**

### 2.1 项目与依赖

| 项目 | TFM / 程序集 | 当前关系 |
| --- | --- | --- |
| `src/StateJournal/StateJournal.csproj` | `net10.0` / `Atelia.StateJournal` | 直接消费 Rbf、Primitives、Diagnostics；构建时加载自有 Generator |
| `src/StateJournal.Generators/StateJournal.Generators.csproj` | `netstandard2.0` / `Atelia.StateJournal.Generators` | Roslyn incremental generator；不应改成 net10.0 |
| `tests/StateJournal.Tests/StateJournal.Tests.csproj` | `net10.0` / `Atelia.StateJournal.Tests` | 直接 ProjectReference StateJournal，使用 IVT |
| `benchmarks/RevisionCommit.Bench/RevisionCommit.Bench.csproj` | `net10.0` / `RevisionCommit.Bench` | 直接 ProjectReference StateJournal，使用 IVT |
| `benchmarks/StateJournal.Bench/StateJournal.Bench.csproj` | `net10.0` / `StateJournal.Bench` | 独立数值/布局实验，目前不引用 StateJournal；主题相关，一起保存 |

运行时闭包：

```text
StateJournal
  ├─ Atelia.Rbf         0.1.1-preview.2
  │    └─ 传递依赖含 Atelia.Data 0.1.1-preview.2
  ├─ Atelia.Primitives  0.1.1-preview.2
  └─ Atelia.Diagnostics 0.1.0-preview.1

StateJournal ──构建期──> StateJournal.Generators
StateJournal.Tests / RevisionCommit.Bench ──源码引用──> StateJournal
```

当前 StateJournal assets 的四个库都是 `type=package`：Data、Primitives、Rbf、Diagnostics。不要顺手引入 RbfSegmentStore、EventJournal 或 Completion 的另外三个包。StateJournal 源码使用 `Atelia.Data`，当前通过 Rbf 的依赖闭包取得；本次保持该关系，不以拆仓为理由调整依赖设计。

其他构建/测试依赖保留现值：

| 项目 | 包 |
| --- | --- |
| Generator | `Microsoft.CodeAnalysis.CSharp 4.10.0`、`Microsoft.CodeAnalysis.Analyzers 3.11.0`，均保留 `PrivateAssets=all` |
| Tests | `xunit 2.9.2`、`xunit.runner.visualstudio 2.8.2`、`Microsoft.NET.Test.Sdk 17.12.0` |
| 两个 benchmark | `BenchmarkDotNet 0.15.8` |

**“不再依赖两个 Style 分析器”不等于删除所有 Analyzer。** 不移除 Roslyn 的上述包，也不删除下面的自有 Generator 引用及其元数据：

```xml
<ProjectReference Include="../StateJournal.Generators/StateJournal.Generators.csproj"
                  OutputItemType="Analyzer"
                  ReferenceOutputAssembly="false"
                  PrivateAssets="all" />
```

Generator 会生成 Mixed Deque / Dict / OrderedDict 的实现，以及属性声明。冷构建必须真实运行生成器，不能靠复制旧 `obj` 或把生成文件手工纳入源码通过验收。

### 2.2 根目录继承关系

- 原根 `Directory.Build.props` 注入两个 Style 项目引用，并提供 `Atelia.{项目名}` 的 AssemblyName、RootNamespace 和 PackageId 默认规则。只复制两个 csproj 会丢失默认程序集身份。
- 原根 `Directory.Build.targets` 包含 storage/completion 源码模式及自分析规则，新仓不需要这些规则。
- StateJournal 当前包版本来自 `eng/StorageDependency.props` 与 `eng/CompletionDependency.props`；新仓不能继续依赖这些原仓文件。
- 当前 Atelia 根没有 `global.json`，调查时 `dotnet --version` 为 **10.0.201**。新仓建议固定此 SDK；P0 确认它确实可用后记录。
- 当前 StateJournal 求值 `IsPackable=true`，迁出时要明确改成 false；Generator 与 Tests 已为 false。
- 当前 Tests csproj 没有向仓外链接自有源码/测试资源；P0 仍需检查实际求值后的 Compile/Content/None 路径。

### 2.3 “没有使用者”的工程含义

调查时，在 `src/`、`prototypes/`、`tests/` 的非迁出部分，没有找到 StateJournal 的 C# 使用或项目引用；仍存在自有测试与 benchmark，以及 `archive/` 中的历史引用。

`Atelia.sln` 中实际列入的相关项目是：

| 项目 | GUID |
| --- | --- |
| StateJournal | `{580A7B1E-2034-4F55-8856-BC391F2193C6}` |
| StateJournal.Tests | `{30F37BB9-DFE8-4056-992B-FCC15DD9C346}` |
| RevisionCommit.Bench | `{9D109B16-FAFA-4D2C-A799-C2E45A93CE94}` |

Generator 由 ProjectReference 引入，目前没有独立 solution 条目；StateJournal.Bench 也未列在原 solution。不能假定“两个源码目录 = 只删两个 solution 项”。

以下归档项目仍写着旧 StateJournal 引用，**本次保留为历史材料，不恢复构建，不改为跨仓引用**：

- `archive/tests/TextAdv.Tests`
- `archive/src/TextAdv`、`archive/src/PersistentAgentProto`、`archive/src/DebugApps`
- `archive/prototypes/TextAdv2`、`archive/prototypes/ChatSession`、`archive/prototypes/MemoTree`、`archive/prototypes/Agent.Core`

因此最终检查应要求“活跃构建图无残留”，而不是全仓搜索字符串必须零命中。StateJournal 与 **SessionJournal** 是不同模块，不要按相似名称批量删除后者。

## 3. 路径处理清单

数量为调查基准下的 Git 跟踪文件数，执行时重新统计。禁止把 `bin/obj`、真实数据目录、缓存或其他未跟踪文件当成源码整目录复制。

| 处理 | 路径 | 当前跟踪文件数 | 要求 |
| --- | --- | ---: | --- |
| 迁出 | `src/StateJournal/` | 125（124 个 C#） | C# 原样，调整 csproj |
| 迁出 | `src/StateJournal.Generators/` | 3（2 个 C#） | Generator 实现原样 |
| 迁出 | `tests/StateJournal.Tests/` | 93（92 个 C#） | 保留全部测试，不为“全绿”删除或跳过失败用例 |
| 迁出 | `benchmarks/RevisionCommit.Bench/` | 3 | 保留独立 AssemblyName |
| 迁出 | `benchmarks/StateJournal.Bench/` | 7 | 保留独立 AssemblyName；只要求 build，不跑性能测量 |
| 迁出 | `docs/StateJournal/` | 66 | 包括已有历史子目录；原仓保留少量迁移入口，见 P3 |
| 迁出 | `archive/StateJournal/` | 10 | 自有废弃实验，仅保存，不加入新 solution 或 Compile |
| 复制 | 根 `LICENSE` | 1 | 保持 MIT 原文与归属；原仓继续保留 |
| 新建 | 新仓基础配置、README、AGENTS、验证脚本、来源/验收文档 | — | 使用下节最小结构 |
| 留原仓 | 其余 `archive/`、`agent-team/`、`wish/`、业务代码及历史计划 | — | 不做大规模历史文档清理 |

源码快照清单共 **307 个原有文件**，另复制根 LICENSE。其中 236 个 C# 文件（包含 10 个不参与编译的归档文件）应保持原 Git blob 内容。

新仓建议结构：

```text
atelia-statejournal/
  README.md
  AGENTS.md
  LICENSE
  .gitignore
  .gitattributes
  global.json
  nuget.config
  Directory.Build.props
  Atelia.StateJournal.slnx
  src/StateJournal/
  src/StateJournal.Generators/
  tests/StateJournal.Tests/
  benchmarks/RevisionCommit.Bench/
  benchmarks/StateJournal.Bench/
  archive/StateJournal/                 # 不编译
  docs/StateJournal/
  docs/dependencies.md
  docs/extraction-origin.md
  docs/extraction-validation.md
  eng/Verify.ps1
```

默认只需一个 `Directory.Build.props`，不创建空的 targets、中央包管理或通用依赖准备框架。新仓 AGENTS 写自身用途和操作入口，不复制 Atelia 的整份团队使命、工具说明与历史任务树。

## 4. P0：固定输入并建立基线

### 4.1 输入与工作树

1. 阅读本方案、新旧目录中实际存在的 AGENTS.md 与 `extract-dotnet-repo` 技能。
2. 重新记录源仓绝对路径、branch、HEAD、origin 与 `git status --short`。本文的调查 SHA 不是未来实施必须回退到的提交。
3. 本方案本身可能尚未提交；不要误把“有方案文档修改”当成迁出源码不可靠。列清已有修改，按路径区分。
4. **迁出路径有未提交实现改动时，先明确快照应该包含哪一版**。不要静默忽略，也不要擅自提交用户的其他修改。可以先完成独立检查，再让用户决定这些改动的归属。
5. 检查新仓目标路径。不存在时可以创建；已存在且非空时先读 status/origin/内容，不覆盖、不 reset/clean。
6. 本地实验目录建议 `E:\repos\Atelia-org\.statejournal-extraction`。不复用之前 completion/storage 的实验输出目录。

建议先运行的检查（PowerShell，独立命令；原生命令失败立即停下并检查退出码）：

```powershell
git status --short
git rev-parse HEAD
git remote -v
dotnet --info
dotnet msbuild src/StateJournal/StateJournal.csproj -getProperty:AssemblyName,RootNamespace,TargetFramework,AssemblyVersion,FileVersion,IsPackable -getItem:ProjectReference,PackageReference
dotnet msbuild src/StateJournal.Generators/StateJournal.Generators.csproj -getProperty:AssemblyName,RootNamespace,TargetFramework -getItem:ProjectReference,PackageReference
rg -n 'StateJournal' --glob '*.csproj' --glob '*.props' --glob '*.targets' --glob '*.sln*'
```

对 Tests 和两个 benchmark 也记录 AssemblyName、RootNamespace、项目引用。查询/保存必要结果，不输出环境中的 credential 或完整环境变量列表。

### 4.2 基线验证

在原仓以默认公开包路径串行运行：

```powershell
dotnet build Atelia.sln -c Release -t:Rebuild
dotnet test tests/StateJournal.Tests/StateJournal.Tests.csproj -c Release --no-build --logger "trx;LogFileName=baseline-statejournal.trx" --results-directory <本次实验目录>/baseline
dotnet build benchmarks/StateJournal.Bench/StateJournal.Bench.csproj -c Release -t:Rebuild
```

`<本次实验目录>` 是占位符，执行前替换为实际绝对路径。`RevisionCommit.Bench` 已随原 solution 构建。原始日志、TRX 放在实验目录，验收文档记录摘要和路径。

只有前一步构建成功才执行 `--no-build` 测试；若 solution 因无关项目无法构建，应先单独成功构建 StateJournal.Tests，再测它并记录 solution 的原有阻塞。不能用旧 DLL 取得“基线通过”。StateJournal 自身无法构建时，P0 不能以完整基线通过结束。

记录总数、通过/失败/跳过数、失败测试完整名称、关键错误、平台、SDK 和版本。**本方案没有预先承诺测试数量或零失败。** 不把上次 Completion/SessionJournal 的 Windows 平台失败套用到 StateJournal；按这次结果判断。

若出现明确的平台限定失败，可在已有 WSL Linux 目录补做“原实现/迁后实现同平台”的对照。单纯搬仓不承担为 StateJournal 新增跨平台支持。没有失败或新疑点时，不增加第二套平台验收。

**P0 完成条件：** 来源 commit 与迁出清单已固定，基线结果已记录，确认没有遗漏的活跃消费者。若此时发现新的业务消费者，暂停依赖它的删除步骤，给用户说明具体路径；不能偷偷把业务一起搬走。

## 5. P1：生成独立源码仓

### 5.1 导出受控快照

默认不运行 filter-repo，不复制 `.git`。使用 `git archive --output` 直接写 tar，避免通过 PowerShell 文本管道处理二进制归档。

```powershell
$sourceRevision = (git rev-parse HEAD).Trim()
# archive 路径必须在已核实的本次实验目录中；不可覆盖已有取证文件。
git archive --format=tar --output=<本次实验目录>/statejournal-source.tar $sourceRevision src/StateJournal src/StateJournal.Generators tests/StateJournal.Tests benchmarks/RevisionCommit.Bench benchmarks/StateJournal.Bench docs/StateJournal archive/StateJournal LICENSE
```

上例在源仓执行；`$sourceRevision` 应与 P0 冻结的来源一致，不要在不同阶段重新取得一个变化后的 HEAD。生成后检查退出码与归档条目，再解压到已确认的新仓空目录。使用 Git 初始化新仓 `main`，在 P2 通过前不删除源仓路径。

在 `docs/extraction-origin.md` 写入：源仓 URL、完整源 commit、快照方式、精确路径清单、日期、包版本、原源码回看链接，以及“新仓初始提交不继承 Atelia 的 commit 历史”。原提交的永久链接示例为 `https://github.com/Atelia-org/atelia/tree/<sourceRevision>/src/StateJournal`，必须替换占位符。

保存导出文件清单与 Git blob ID/内容哈希。后续比较 C# 内容时优先用 Git blob 身份，避免把 Windows checkout 的 CRLF 差异当成实现变化；若使用 SHA256，必须统一取 Git blob 的原始字节，而不是混用工作树与归档字节。

### 5.2 新仓配置

新建最小 `Directory.Build.props`：

```xml
<Project>
  <PropertyGroup>
    <AssemblyName>Atelia.$(MSBuildProjectName)</AssemblyName>
    <RootNamespace>Atelia.$(MSBuildProjectName)</RootNamespace>
    <PackageId>Atelia.$(MSBuildProjectName)</PackageId>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
</Project>
```

项目自身仍可覆盖程序集名；尤其保留两个 benchmark 的现有覆盖。不要添加 Style 项目引用，不复制原根的 imports、ValidateStorageSourceConfiguration、ValidateCompletionSourceConfiguration 或自分析逻辑。

把 StateJournal csproj 的三个包引用改为以下形式，并删除它们配对的条件 ProjectReference；自有 Generator 引用继续保留：

```xml
<PackageReference Include="Atelia.Rbf" Version="0.1.1-preview.2" />
<PackageReference Include="Atelia.Primitives" Version="0.1.1-preview.2" />
<PackageReference Include="Atelia.Diagnostics" Version="0.1.0-preview.1" />
```

这里的数值来自调查时已经使用的版本。若 P0 发现版本已变，固定 **P0 实际基线版本**，更新文档；不要搬仓时升级依赖。版本只在 csproj 维护一份，不新建 StorageDependency/CompletionDependency props。

StateJournal 的 IVT 按新闭包收敛：

- 保留 `Atelia.StateJournal.Tests` 和 `RevisionCommit.Bench`。
- 删除 `StringPool.Bench`：当前没有对应项目。
- 删除 `Atelia.DebugApps`：它是未迁入的历史归档消费者。
- 不增加任何新的 friend assembly，不把 internal API 改成 public。

新建 `global.json`（P0 SDK 确认为 10.0.201 时）：

```json
{
  "sdk": {
    "version": "10.0.201",
    "rollForward": "disable"
  }
}
```

新建 `nuget.config`，`packageSources` 使用 `<clear />` 后仅列 `https://api.nuget.org/v3/index.json`。不增加本地 feed、私有源或 credentials。若上层目录还有 NuGet source mapping / 中央包配置，独立验证时必须识别并隔离，不靠增加兄弟仓目录解决 restore。

`.gitignore` 至少忽略 `bin/`、`obj/`、`.vs/`、`.atelia/`、`artifacts/`、`TestResults/`、`BenchmarkDotNet.Artifacts/`。`.gitattributes` 沿用源仓适用的文本/二进制规则，确保源码在 Git 中保持 LF；不要首次提交时大规模重排源码或运行原仓 `format.ps1`。

新建 `Atelia.StateJournal.slnx`，加入表中 **五个项目**。不将 `archive/StateJournal` 中的实验纳入编译。可以用当前 SDK 的 `dotnet new sln` / `dotnet sln add`；以生成结果为准，不手写未经验证的 solution XML。

### 5.3 最小维护入口

`README.md` 写清：封存状态、为何拆出、没有 NuGet 包、SDK、restore/build/test 命令、项目布局、用法/来源文档链接。可以指向 DurableGraph，但不声称兼容 StateJournal 数据或可直接替换类型。

`AGENTS.md` 至少写清：

- 开始先读 README 与 `docs/extraction-origin.md`。
- 正常任务是查阅历史；没有新的明确任务时不升级依赖、不设计新功能、不创建发布链路。
- Generator 是编译所需；两个 Style 项目已去除。
- 不修改序列化格式、类型标签、对象身份、branch/segment 元数据和生成代码行为来“顺手优化”。
- 用 `eng/Verify.ps1` 验证；任何平台限定/基线失败都查 `docs/extraction-validation.md`。
- 普通用法从 `Repository.Create/Open` 开始；`Revision` 没有 public 构造函数。

`docs/dependencies.md` 简单记录三个直接包及 Data 传递依赖、来源仓、固定版本对应源码 commit。调查时 storage 对应 `976aa345f923da09e2a5cf1dc25ba592b3818b63`，Diagnostics 对应 completion 的 `3ae1ebeccdd94a7bd507444154a283201a68c992`；P0 重核版本与来源再填入。

更新迁入 `usage-guide.md` 顶部的项目状态；其底部原来指向 `../storage-dependency.md` 的两个链接改为新仓 `../dependencies.md`。`memory-notebook.md`、`mixed-value-generator-note.md` 也加一句封存基准说明。保留历史章节内容，不重写 66 篇设计文档。

创建一个短的 `eng/Verify.ps1`：定位新仓根目录，串行执行 solution restore、Release Rebuild、StateJournal.Tests `--no-build --no-restore`，输出 TRX 到 `artifacts/validation/`。每条 dotnet 命令后检查 `$LASTEXITCODE`，任一步失败立即以非零退出；仅设置 `$ErrorActionPreference='Stop'` 不足以可靠捕获原生命令失败。不要在脚本中安装 SDK、改系统代理、自动 clone、pack 或 push。

## 6. P2：验证新仓独立可用

### 6.1 检查编译闭包

1. 用 MSBuild 再次求值五个项目：AssemblyName、RootNamespace、TFM 和版本应与基线一致，全部 `IsPackable=false`。
2. StateJournal 的唯一 ProjectReference 是新仓内的 Generator；Tests/RevisionCommit.Bench 指向新仓内 StateJournal；StateJournal.Bench 无额外源码引用。
3. PackageReference 版本与 P0 一致；不存在两个 Style 项目、原仓 eng imports、StorageSourceRoot 或 CompletionSourceRoot。
4. fresh restore 后检查 assets：StateJournal 的外部 runtime 库来自 package；项目引用和 Compile/Content/None 不指向原仓、兄弟仓源码或旧 bin/obj。

### 6.2 从干净输出构建与测试

```powershell
dotnet restore Atelia.StateJournal.slnx --configfile nuget.config
dotnet build Atelia.StateJournal.slnx -c Release -t:Rebuild --no-restore
dotnet test tests/StateJournal.Tests/StateJournal.Tests.csproj -c Release --no-build --no-restore --logger "trx;LogFileName=statejournal.trx" --results-directory artifacts/validation
```

随后用 `eng/Verify.ps1` 的实际结果作为统一验证入口证据，避免脚本文档和手工命令不一致。无需连续重复完整测试：可以直接让脚本承载上述首轮验证。

验收包括：

- 原测试集完整执行，数量与 P0 可解释地一致，**无新增失败或新增跳过**。基线失败按完整测试名、原因、平台逐项对照，不仅比较失败数量。
- RepositoryTests 的提交/重开路径、RepositoryCommitFailureTests、RepositoryHistoryReaderTests、RepositoryReplayCommittedTests，以及 Mixed 容器测试都仍被包含，没有用只跑几个“容易过”的用例代替原测试集。
- 两个 benchmark 编译通过；不启动 BenchmarkDotNet 性能跑分。
- 在忽略输出目录中临时设置 `EmitCompilerGeneratedFiles=true` 做一次必要的 Generator 检查，确认产生 MixedDeque / MixedDict / MixedOrderedDict 相关 `.g.cs`。生成目录放 `obj/` 下，防止被默认 Compile glob 纳入二次编译；完成后不提交生成文件。
- 与 P0 快照比较：全部 236 个迁入 C# 的 Git blob 内容保持一致。预期差异集中在 csproj、根配置及文档；若 C# 发生变化，逐项找原因，不能一律归因于格式化。

### 6.3 一次独立取得验证

新仓验收通过并形成可追溯本地提交后，再 **clone 该本地仓的固定提交** 到一个新的实验子目录，使用该目录自身的 `nuget.config` 和本次专用 `NUGET_PACKAGES` 缓存运行同一验证入口。只克隆新仓，不复制其他 repo，也不复制 bin/obj。

这一步证明代码没有遗漏未跟踪文件，也没有依靠原工作目录残留产物。临时 cache/env 只影响这次进程，不清理用户全局缓存。失败时先检查缺文件、上层 MSBuild/NuGet 配置与网络来源；不要回退成兄弟仓源码引用。

因为没有 StateJournal 包或真实下游，这次不做 nupkg/signature/Source Link/symbol server/public PackageReference smoke。因为保持运行时与生成器源码不变、依赖不变，并保留已有持久化测试，也不新增跨版本存储迁移 harness。若发现必须修改实现/存储协议才能迁出，先停止这部分改动并报告，这已超出封存任务。

**P2 完成条件：** 新仓固定提交独立构建/测试成立，来源与差异可追溯；此时才进入原仓迁出删除。

## 7. P3：从 Atelia 移除活跃入口

### 7.1 删除与 solution

1. 先记录 Atelia 当前已有改动。在独立迁移 branch/worktree 操作，或在用户已允许的当前分支操作；不要把新仓目录建在 Atelia 树下。
2. 从 Atelia Git 跟踪树删除第 3 节七个迁出目录，随后按下节重建少量文档入口。优先用明确路径的 `git rm`；不要 `git clean -fdx` 或递归删除整个 src/tests。
3. 用 `dotnet sln Atelia.sln remove` 移除 StateJournal、StateJournal.Tests、RevisionCommit.Bench。检查三个 GUID 在 solution 的项目、配置、NestedProjects 中均已消失；原来没列入的 Generator/StateJournal.Bench 不必虚构删除项。
4. 保留原仓两个 Style 项目与全局配置、storage/completion 的依赖 props/targets；其余项目仍在使用。
5. 不执行批量“StateJournal → DurableGraph”替换，不重命名 SessionJournal，不修改 DurableGraph、DramaBoard 或两个已拆出 repo。

### 7.2 文档入口与历史链接

Atelia 新增简短的 `docs/statejournal-retirement.md`，记录新仓地址/本地位置、固定迁出来源、新仓交付 commit、封存定位以及历史代码回看方式。新仓地址尚不可用时必须标为“尚未上传”，不能写成已经可访问。

原 `docs/StateJournal/` 仅保留以下三个迁移提示文件，替代原文，统一链接到 retirement 文档及新仓对应指南：

- `usage-guide.md`
- `memory-notebook.md`
- `mixed-value-generator-note.md`

这是文档跳转，不是代码兼容层。旧深层锚点可能不再适用，在 retirement 文档给出源 commit 的原文链接即可；不要求为 66 篇历史文档创建重定向副本。

更新 Atelia `AGENTS.md`：StateJournal 用法/可见性段落改为历史查阅入口；目标分解树里的 StateJournal 与 TextAdv 旧目标标为历史项目；新增的本方案入口从“待实施”改为最终状态并指向验收记录。保持 completion/storage 的当前事实。

原 `archive/` 当前没有 README，可新建 `archive/README.md`，简短注明 StateJournal 相关老项目使用迁出前布局，默认不参与主 solution；恢复完整历史环境应从原 Atelia 固定 commit 单独检出。不要让用户误以为单独 clone 新仓就能编译所有旧原型。

保留已有旧 extraction plan / 历史研究中的原路径语境，不全文改写过去。当前活跃使用指南与 AGENTS 不能继续把旧路径描述为现行开发入口。

### 7.3 Atelia 验收

```powershell
dotnet build Atelia.sln -c Release -t:Rebuild
git diff --check
rg -n 'StateJournal' Atelia.sln
rg -n 'StateJournal' src prototypes tests benchmarks --glob '*.csproj' --glob '*.props' --glob '*.targets'
```

注意：`rg` 没有匹配时退出码 1 是预期结果。某个已完全迁走的目录不存在也应单独识别，不冒充命中残留或构建失败。

除构建外，复核活跃源码中的 `Atelia.StateJournal`、结构测试中的旧文件路径，以及是否有脚本自行扫描全仓 csproj。若发现受影响的现有结构测试，只修正它对“已退休项目仍存在”的假设，保留其他依赖边界检查，并运行该测试项目。没有受影响的活跃消费者时，不重跑所有业务 E2E，也不调用真实 LLM 服务。

`archive/` 中第 2.3 节列出的旧引用允许保留，在验收记录列明；其他新出现的引用必须解释。原仓仍可正常构建，且完全不需要新仓 checkout。

**P3 完成条件：** 迁出实现不再属于 Atelia 活跃构建图，原仓重建无新增失败，文档能找到封存版本。

## 8. P4：提交、远端保存与交接

本方案是实施说明，**不自行授予 GitHub 创建/push 权限**；实施时沿用用户当时已给的授权，不要反复索取已授予的权限。

建议形成可读的提交：

1. 新仓：`archive: preserve StateJournal as a standalone source repository`。包含可独立构建的完整源码快照、配置与说明。
2. Atelia：`refactor: retire StateJournal to a standalone repository`。包含迁出删除、solution、文档入口与验收记录。

可以为最后的来源/验收文档补一次小提交，不要求将含自身 SHA 的文件做成自指哈希。新仓记录源 commit，Atelia 记录新仓交付 commit；最终用户报告列两仓实际 HEAD。

如果实施请求已授权创建/push：

- 检查 GitHub 新仓是否存在、地址是否正确；不存在时在授权范围内创建。默认与前两仓一样使用 public，但若用户另有安排，以其安排为准。
- 新仓先上传，再上传 Atelia 的迁出提交；首次只推所需分支，不推全部 refs，不 force push。
- 若权限或 token 错误，保留本地已验收提交，记录具体 API 状态和动作，让用户处理账号；不输出 token，不通过重建历史或改仓名绕过错误。
- 无须建立 `nuget` environment、`NUGET_USER`、trusted policy、tag 或 release。
- 不运行 GitHub Archive 操作。若用户另行明确要求，等两仓远端与文档都核对完再归档。

如果尚无网络写入授权，完成本地可审查产物后报告“本地完成，远端未创建/未推送”，不要将远端保存记为完成。

新仓 `docs/extraction-validation.md` 与 Atelia `docs/plans/atelia-statejournal-extraction-validation.md` 分别保存本仓证据，不维护互相冲突的完整流水账。至少写明：

| 证据 | 应记录内容 |
| --- | --- |
| 输入 | 源仓 SHA、路径清单、原有未提交改动如何处理 |
| 独立源码 | 新仓被验证的 commit、SDK、包版本、C# blob 比较结果 |
| 测试 | P0/P2 命令、平台、TRX 摘要、完整失败差异与跳过说明 |
| 构建 | 新仓五项目与 Atelia 移除后的 Rebuild 结果 |
| Generator | 自有引用保留、三类生成代码存在、没有 Style 项目 |
| 残留 | 允许的 archive 引用、文档跳转、没有活跃反向引用 |
| 交付 | 两仓 branch/HEAD/status、实际 push 状态、远端地址 |

提交 hook 若改动文件，应检查最终 diff；只有它改到了源码/项目配置或其他验证输入时才补跑相关检查。不要用 hook 前的版本充当最终验收来源。

## 9. 失败处理与任务边界

| 情况 | 处理 |
| --- | --- |
| 原仓 baseline 已失败 | 记录具体失败，迁后同条件对照；新增失败必须解决，旧失败不假称通过 |
| Generator 找不到或 Mixed 方法缺失 | 查 ProjectReference 元数据、Roslyn 包、程序集名和冷编译输出；不手拷生成代码 |
| 新仓仍查找 Style 项目 | 查根 props/targets 继承，去掉原仓注入；不要复制 Style 项目来“修复” |
| restore 缺包 | 查确切包 ID/版本、nuget.config、mapping、网络与缓存；不升级或改 source mode 掩盖问题 |
| NuGet 被网络重定向到滞后镜像 | 记录实际 endpoint；必要时使用用户已有代理的进程级设置，不改系统代理或禁用 TLS |
| benchmark internal 访问失败 | 对照 IVT 和真实 AssemblyName，保留 `RevisionCommit.Bench` 原身份 |
| 多出旧生成文件引起重复定义 | 查是否复制了 obj/手工把生成目录放进 Compile glob；移除本次造成的重复输入 |
| 新仓目标目录已有用户文件 | 停止覆盖，报告现有目录身份；在新的隔离目录继续可独立的准备工作 |
| 本地快照未包含用户预期改动 | 回到 P0 明确来源，不把工作树新代码归因到旧 commit |
| 新仓未验收成功 | 原仓不删除；修复构建配置或报告具体阻塞 |
| 原仓移除后构建失败 | 查遗漏的 solution/项目引用；不添加新仓引用作为临时补丁 |
| 出现格式/API/存储语义修改需求 | 单独记录并询问是否扩大范围，默认不做 |

源仓迁出提交形成后，回退采用明确 revert；不使用 reset --hard 清掉其他工作。新仓中失败的试验保留在实验目录或独立分支，不擅自删除已发布远端。任何 Windows 递归删除必须先核对最终绝对目标在本次目录内；本任务不需要清理旧 storage/completion 实验目录。

## 10. 用户需要准备什么

| 资源 | 是否需要 |
| --- | --- |
| 本机 .NET SDK 10.0.201、Git、PowerShell、nuget.org 网络 | 必需；调查时 SDK 可用，实施时复核即可 |
| 新仓目录及保存实验输出的磁盘空间 | 必需；普通源码/构建/测试规模，不需要额外机器 |
| GitHub 仓名与创建/push 权限 | 远端保存时需要；建议 `Atelia-org/atelia-statejournal` |
| nuget.org 账号、组织、API Key、policy | **不需要** |
| 新 VM、另一台电脑 | **不需要** |
| 现有 WSL/Linux 环境 | 仅基线显示平台问题时使用，不作为默认门槛 |
| LLM API Key 或真实模型调用 | **不需要** |
| 旧业务数据、StateJournal → DurableGraph 转换样本 | **不需要**；本次不做数据转换 |

需要用户参与的实际情况只有：确认未提交源码应如何纳入、处理目标目录冲突/新发现的业务消费者、补足远端账号权限，或决定是否扩大范围。正常的项目编辑、测试和已授权 git 操作可连续推进。

## 11. 可直接交给 gpt-5.6-terra 的执行提示

下面代码块是供用户发给下一位 Agent 的任务文本，**不是本次方案编写期间的执行授权**。

```text
请实施 docs/plans/atelia-statejournal-extraction-plan.md，按 P0→P4 顺序推进。

目标是把 StateJournal 和 StateJournal.Generators 连同自有测试、两个 benchmark、
docs/StateJournal 和 archive/StateJournal 保存到独立 atelia-statejournal 源码仓，
从 Atelia 活跃构建图移除，停止活跃开发。

采用方案默认的固定 commit 快照，不必保留新仓 Git 历史。
不制作/发布 NuGet 包，不做双源码模式，不配置 CI 或 Trusted Publishing，
不自动执行 GitHub Archive。删除两项 Style 项目依赖，保留自有 Generator。

先记录基线与已有工作树改动，再创建独立源码仓。新仓独立 clone 验收成功后，
才删除原仓迁出路径。保持所有迁入 C# 实现原样，不升级外部依赖。
StateJournal 与 SessionJournal 不同，后者不在迁出范围。

你可以进行本任务必要的本地 Git/文件操作，创建并推送
Atelia-org/atelia-statejournal（public），以及推送 Atelia 的迁出提交。
保留用户已有改动；提交仅包含本任务内容；不 force push、不清理其他实验目录。
遇到账号权限、目标目录冲突、无法纳入的未提交实现，或新的活跃消费者时叫我。

按方案完成源码核对、测试基线对照、两个仓库的构建验收和文档交接。
不用启动 subagents；用一个主线程串行实施即可。
最终列两仓 commit/status、实际 push 状态、测试结果和未完成项，
不要把“已有失败未增加”写成“全部测试通过”。
```
