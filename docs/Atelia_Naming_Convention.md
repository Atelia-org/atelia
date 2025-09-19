
# Atelia Project Naming Convention

**Audience:** LLM Agents & Developers
**Version:** 1.1

本规范定义 Atelia 项目所有子系统的命名与结构一致性要求。遵循本规范有助于维护结构统一性、可预测性和自动化。

---

## 1. 目录结构

- **规则：** 项目源码目录直接放在 `src/` 或 `tests/` 下，目录名为短项目名（不带 Atelia 前缀）。
- **格式：** `src/{ProjectName}/`、`tests/{ProjectName}.Tests/`
- **理由：** 路径简短，便于 CLI/IDE 操作。

**示例：**
```
src/
  CodeCortex/
  CodeCortex.DevCli/
  MemoTree/
  MemoTree.Contracts/
tests/
  CodeCortex.Tests/
  MemoTree.Tests/
```

## 2. 命名空间

- **规则：** 所有命名空间必须以 `Atelia.` 开头。
- **格式：** `Atelia.{ProjectName}[.FeatureArea]`
- **理由：** 保证全局唯一性与品牌一致性。

**示例：**
```csharp
// src/CodeCortex/
namespace Atelia.CodeCortex;
// src/CodeCortex.Cli/
namespace Atelia.CodeCortex.Cli;
```

## 3. 程序集名与包名

- **规则：** 程序集名、根命名空间、NuGet 包名三者保持一致，均为 `Atelia.{ProjectName}[.FeatureArea]`。
- **配置方式：** 统一在 `Directory.Build.props` 中集中设置：
  - `<AssemblyName>Atelia.$(MSBuildProjectName)</AssemblyName>`
  - `<RootNamespace>Atelia.$(MSBuildProjectName)</RootNamespace>`
  - `<PackageId>Atelia.$(MSBuildProjectName)</PackageId>`
- **项目文件名：** 不带 Atelia 前缀，直接用短名（如 `CodeCortex.csproj`）。
- **CLI/特殊项目例外：** 可在各自 `.csproj` 内覆盖上述属性。

**示例（以 CodeCortex.csproj 为例）：**
```xml
<!-- src/CodeCortex/CodeCortex.csproj -->
<!-- Directory.Build.props 已统一配置，无需重复写 AssemblyName/RootNamespace -->
```

**CLI 例外示例：**
```xml
<!-- src/CodeCortex.DevCli/CodeCortex.DevCli.csproj -->
<PropertyGroup>
  <AssemblyName>Atelia.CodeCortex.DevCli</AssemblyName>
  <!-- 如需特殊命名空间可加 RootNamespace -->
</PropertyGroup>
```

## 4. 一致性守护

- **建议：**
  - 通过 Directory.Build.props 集中管理命名规则，减少项目侧样板。
  - 可选：添加构建前校验，防止命名空间前缀走样。
  - 迁移时优先移除项目内重复的 AssemblyName/RootNamespace。

---

## 总结表

| 工件                | 格式                                 | 示例（CodeCortex.Cli 项目）         |
|---------------------|--------------------------------------|-------------------------------------|
| **目录**            | `src/{ProjectName}[.FeatureArea]/`   | `src/CodeCortex.Cli/`               |
| **命名空间**        | `Atelia.{ProjectName}[.FeatureArea]` | `Atelia.CodeCortex.Cli`             |
| **程序集名**        | `Atelia.{ProjectName}[.FeatureArea]` | `Atelia.CodeCortex.Cli`             |
| **NuGet 包名**      | `Atelia.{ProjectName}[.FeatureArea]` | `Atelia.CodeCortex.Cli`             |

---

> 本规范已采纳统一 props 规则，项目文件名不带 Atelia 前缀，CLI/特殊项目可例外覆盖。
