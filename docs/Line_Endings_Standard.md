# 行尾规范（EOL）与仓库统一策略

本文档阐述 Atelia 仓库的行尾统一策略、相关配置与使用建议，确保跨平台一致与干净稳定的 diff。

## 总体原则

- 仓库内文本文件一律以 LF (\n) 作为规范存储格式。
- Windows 场景确需 CRLF 的少数文件类型在工作副本保留 CRLF（仓库存储仍为 LF）。
- 不依赖开发者本机的 `core.autocrlf`，所有转换由仓库内的 `.gitattributes` 明确控制。
- 使用 `.editorconfig` 帮助编辑器在保存时采用期望的行尾，减少本地/提交时的来回转换。

## 关键配置

### .gitattributes（仓库根）

作用：决定 Git 在提交/检出时的行尾规范化行为，权威来源。

要点：
- 默认文本 = LF：`* text=auto eol=lf`
- 例外（工作副本保留 CRLF）：`*.sln`、`*.bat`、`*.cmd`、`*.ps1`
- `*.sh` 强制 LF（适配类 Unix 环境）
- 常见二进制文件标记为 `-text`，禁用行尾转换

片段（摘录）：

```
* text=auto eol=lf

# Windows-specific files (working tree CRLF)
*.sln text eol=crlf
*.bat text eol=crlf
*.cmd text eol=crlf
*.ps1 text eol=crlf

# Shell scripts
*.sh text eol=lf

# Binaries
*.png -text
*.jpg -text
...（略）
```

### .editorconfig（仓库根）

作用：指导编辑器/IDE 的保存行为，减少“保存时行尾与提交时规范化”之间的摩擦。

要点：
- 全局 `end_of_line = lf`
- 按需覆盖 CRLF：`[*.ps1]`、`[*.sln]`、`[*.{cmd,bat}]`
- 其它与缩进、空白、C# 格式化等设置保持一致

片段（摘录）：

```
[*]
end_of_line = lf

[*.{cmd,bat}]
end_of_line = crlf

[*.ps1]
end_of_line = crlf

[*.sln]
end_of_line = crlf
```

### Git 层设置（仓库本地）

- 统一禁用：`core.autocrlf=false`（避免隐式转换，由 .gitattributes 统一控制）
- 行尾安全提示：`core.safecrlf=warn`

可在当前仓库内设置：

```powershell
# 在本仓库生效
git config --local core.autocrlf false
git config --local core.safecrlf warn
```

如需全局设置，可改用 `--global`。

## 初次落地或改动后的“再归一化”

当新增或调整 `.gitattributes` 后，建议进行一次行尾再归一化，使历史与工作副本一致：

```powershell
git add --renormalize .
git commit -m "chore: normalize line endings across repo"
```

注意：这是一次性基线提交，可能出现较大的 diff，之后历史将稳定。

## 预提交钩子（可选）

仓库提供示例脚本：`scripts/git-hooks/pre-commit.ps1`
- 默认运行 `git diff --check` 检测空白/EOL 问题，发现问题则阻止提交并提示。
- 可按注释开启更严格的 CRLF 检测（排除允许 CRLF 的文件类型）。

安装方式（示例之一）：
- 在 `.git/hooks/pre-commit` 中调用该 PowerShell 脚本（可通过简短的 cmd/sh 包装器实现）。

## 常见问题（FAQ）

- 为什么仓库统一用 LF？
  - 跨平台工具链与 CI 更友好；GitHub 等托管平台默认处理更稳定，避免 Windows/Unix 混用导致的噪声 diff。

- Windows 上编辑器显示 CRLF 与仓库存储 LF 是否冲突？
  - 不冲突。`.gitattributes` 会在检入/检出阶段转换；`.editorconfig` 让编辑器保存时尽量采用正确行尾，减少不必要的转换。

- 哪些文件保留 CRLF？
  - `.sln`、`.bat`、`.cmd`、`.ps1` 在工作副本保留 CRLF，避免工具链/脚本兼容性问题；仓库存储仍为 LF。

- 我看到提交时提示 “CRLF will be replaced by LF” 是什么？
  - 说明 Git 正在按 `.gitattributes` 将文件规范化为 LF 存储。若发生在应保留 CRLF 的文件上，请检查文件是否匹配到对应规则。

## 变更记录

- 2025-09-20：确立“仓库 LF + Windows 少量文件 CRLF 例外”的统一策略；引入 `.gitattributes` 与 `.editorconfig` 对应规则；提供预提交钩子示例。
