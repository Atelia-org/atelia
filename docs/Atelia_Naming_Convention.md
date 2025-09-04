# Atelia Project Naming Convention

**Audience:** LLM Agents & Developers
**Version:** 1.0

This document defines the naming conventions for all subsystems within the Atelia project. Adherence is mandatory for maintaining structural consistency and predictability.

---

## 1. Directory Structure

- **Rule:** Project source code directories are placed directly under `src/`. They use the short project name **without** the `Atelia.` prefix.
- **Format:** `src/{ProjectName}/`
- **Rationale:** Minimizes path length for better CLI and IDE experience.

**Example:**
```
/src/
  /CodeCortex/
  /CodeCortex.DevCli/
  /CodeCortex.Cli/
  /MemoTree/
  /MemoTree.Contracts/
```

## 2. Namespaces

- **Rule:** All namespaces **must** start with `Atelia.`.
- **Format:** `Atelia.{ProjectName}.{OptionalFeatureArea}`
- **Rationale:** Ensures global uniqueness and brand consistency.

**Example:**
```csharp
// In src/CodeCortex/
namespace Atelia.CodeCortex;

// In src/CodeCortex.Cli/
namespace Atelia.CodeCortex.Cli;
```

## 3. Assembly Names

- **Rule:** The assembly name **must** match its root namespace. This is configured in the `.csproj` file.
- **Format:** `<AssemblyName>Atelia.{ProjectName}.{OptionalFeatureArea}</AssemblyName>`
- **Rationale:** Predictability. A `using` statement directly maps to a required assembly reference. Prevents output collisions.

**Example (`CodeCortex.csproj`):**
```xml
<PropertyGroup>
  <AssemblyName>Atelia.CodeCortex</AssemblyName>
</PropertyGroup>
```

## 4. NuGet Package IDs

- **Rule:** The NuGet package ID **must** match the assembly name.
- **Format:** `Atelia.{ProjectName}.{OptionalFeatureArea}`
- **Rationale:** Consistency across the entire lifecycle (discovery, installation, usage). Enables NuGet prefix reservation.

**Example:**
- The `Atelia.CodeCortex` assembly is packaged as the `Atelia.CodeCortex` NuGet package.

---

## Summary Table

| Artifact             | Format                                     | Example (`CodeCortex.Cli` project)      |
|----------------------|--------------------------------------------|-----------------------------------------|
| **Directory**        | `src/{ProjectName}.{FeatureArea}/`         | `src/CodeCortex.Cli/`                   |
| **Namespace**        | `Atelia.{ProjectName}.{FeatureArea}`       | `Atelia.CodeCortex.Cli`                 |
| **Assembly Name**    | `Atelia.{ProjectName}.{FeatureArea}`       | `Atelia.CodeCortex.Cli`                 |
| **NuGet Package ID** | `Atelia.{ProjectName}.{FeatureArea}`       | `Atelia.CodeCortex.Cli`                 |
