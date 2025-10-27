
# Atelia.Diagnostics.DebugUtil 用法说明
- 使用 DebugUtil.Print("类别", "内容") 输出调试信息。
- 日志文件“始终写入”，控制台打印由环境变量 ATELIA_DEBUG_CATEGORIES 控制（类别用逗号/分号分隔，如：TypeHash,Test,Outline；设置为 ALL 打印所有类别）。
- 默认日志目录：.codecortex/ldebug-logs/{category}.log（便于 Agent 实时尾随读取）。
- 推荐在调试代码、测试代码中统一使用本工具，便于全局开关与后续维护；单元测试默认不会被调试输出干扰（除非开启 ATELIA_DEBUG_CATEGORIES）。
- 可用 DebugUtil.ClearLog("类别") 清空某类别日志。
- 实现细节见 src/Diagnostics/DebugUtil.cs。

# 项目性质与阶段
这是个人自用的实验项目
尚未首次发布，处于早期快速迭代阶段，没有下游用户，因此不必担心接口变动

# 关于模型切换
Copilot可以理解成一种职业，这并不与LLM会话的底层模型切换功能矛盾。单一会话中使用多个LLM，类似于一种“多重人格”，或视角切换，可以用来集思广益与多角度分析问题。

# 工具使用经验
想要编辑文件时，那个'insert_edit_into_file'工具不好用，经常产生意外的结果，华而不实。建议用'apply_patch'等其他工具替代。
- VS Code 集成终端偶尔会出现无回显的情况，关闭所有旧终端后新建实例即可恢复，重开后可先跑一条 `Write-Output "hello"` 之类的命令验证。
