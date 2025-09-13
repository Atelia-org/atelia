# Atelia.Diagnostics.DebugUtil 用法说明
- 使用 DebugUtil.Print("类别", "内容") 输出调试信息。
- 日志文件“始终写入”，控制台打印由环境变量 ATELIA_DEBUG_CATEGORIES 控制（类别用逗号/分号分隔，如：TypeHash,Test,Outline；设置为 ALL 打印所有类别）。
- 默认日志目录：.codecortex/ldebug-logs/{category}.log（便于 Agent 实时尾随读取）；若不可用则回退至 gitignore/debug-logs/{category}.log，最终回退到当前目录。
- 推荐在调试代码、测试代码中统一使用本工具，便于全局开关与后续维护；单元测试默认不会被调试输出干扰（除非开启 ATELIA_DEBUG_CATEGORIES）。
- 可用 DebugUtil.ClearLog("类别") 清空某类别日志。
- 实现细节见 src/Diagnostics/DebugUtil.cs。

---
