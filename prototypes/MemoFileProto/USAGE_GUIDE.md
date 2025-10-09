# MemoFileProto 使用指南

## 快速开始

### 1. 启动程序
```bash
cd prototypes/MemoFileProto
dotnet run
```

### 2. 基本对话
```
User> Hello, I am learning C# and .NET
Assistant> (LLM 的响应)
```

---

## 核心功能

### 📝 管理长期记忆

#### 查看记忆
```
User> /memory
```
或显式写成：
```
User> /memory view
```

> 当前 MVP 不提供命令行形式的手动编辑/清空操作，如需修改记忆，请通过 LLM 工具调用或直接调整代码。后续若有真实需求，再扩展子命令。

---

### 🤖 让 LLM 自主编辑记忆

LLM 可以通过 `memory_notebook_replace ` 工具自主管理记忆。

#### 示例对话
```
User> Remember that I like minimalist design

Assistant> (调用工具)
[工具调用] memory_notebook_replace
[参数]
{
  "old_text": "",
  "new_text": "我喜欢极简设计风格。"
}
[工具结果] 已追加内容到记忆末尾。当前记忆长度: 24 字符

好的，我已经记住了你喜欢极简设计风格！
```

#### LLM 修改记忆
```
User> Actually, I prefer modern design, not minimalist

Assistant> (调用工具)
[工具调用] memory_notebook_replace
[参数]
{
  "old_text": "我喜欢极简设计风格。",
  "new_text": "我喜欢现代设计风格。"
}
[工具结果] 记忆已更新...

好的，我已经更新了记忆，现在记录的是你喜欢现代设计风格。
```

#### LLM 删除记忆
```
User> Forget about my design preference

Assistant> (调用工具)
[工具调用] memory_notebook_replace
[参数]
{
  "old_text": "我喜欢现代设计风格。\n",
  "new_text": ""
}
[工具结果] 记忆已更新...

好的，我已经删除了关于你设计偏好的记忆。
```

---

## 高级用法

### 查看对话历史
```
User> /history
=== 对话历史 ===
[0] system: 你是一个有帮助的AI助手...
[1] user: ## 当前时间
2025-10-07 14:30:15

## 收到用户发来的消息
Hello
[2] assistant: Hi! How can I help you?
===============
```

### 修改系统提示词
```
User> /system You are a helpful assistant specializing in C# development
系统提示词已设置为: You are a helpful assistant specializing in C# development
```

### 清空对话历史
```
User> /clear
对话历史已清空。
```

---

## 消息结构

### LLM 实际看到的消息格式

当你发送消息时，LLM 看到的是结构化的内容：

```markdown
## 你的记忆
我喜欢简洁的代码风格。
我正在学习 C# 和 .NET。

## 当前时间
2025-10-07 14:30:15

## 收到用户发来的消息
Can you help me with async/await?
```

这种结构化格式让 LLM 能够：
- 了解自己的长期记忆
- 知道当前时间
- 理解用户的实际输入

---

## 实验建议

### 测试 LLM 的记忆能力

1. **记忆积累测试**
   ```
   User> Remember these facts: I work at Microsoft, I prefer tabs over spaces, my favorite language is C#
   (观察 LLM 如何拆分和存储这些信息)
   ```

2. **记忆召回测试**
   ```
   User> What do you know about me?
   (验证 LLM 能否准确回忆记忆文档中的内容)
   ```

3. **记忆更新测试**
   ```
   User> I changed my job, now I work at Google
   (观察 LLM 是否会主动更新旧信息)
   ```

4. **选择性遗忘测试**
   ```
   User> Forget my old job information
   (测试 LLM 是否能精确定位和删除特定记忆)
   ```

### 多轮对话实验

1. 进行 10+ 轮对话
2. 观察 LLM 何时决定写入记忆
3. 测试记忆是否帮助 LLM 保持上下文连贯性
4. 验证记忆文档是否越来越精炼（而非无限增长）

---

## 注意事项

### ⚠️ 当前限制

1. **无持久化**：程序关闭后记忆和历史会丢失
2. **简单的 String Replace**：无法处理复杂的结构化编辑
3. **无版本控制**：误删记忆后无法回滚
4. **无截断策略**：对话历史可能无限增长

### 💡 最佳实践

1. **用英文测试**：避免终端 UTF-8 编码问题
2. **定期检查记忆**：使用 `/memory view` 查看记忆状态
3. **手动备份**：重要对话前用 `/memory view` 保存记忆
4. **观察工具调用**：留意 LLM 何时主动调用 `memory_notebook_replace `

---

## 故障排查

### LLM 不调用 memory_notebook_replace  工具

**可能原因**：
- 模型不支持工具调用
- System instruction 不够明确
- 对话上下文中没有提示需要记忆

**解决方案**：
- 明确告诉 LLM："Please remember this"
- 检查 OpenAI 端点是否正确返回 tool_calls

### 记忆替换失败

**可能原因**：
- `old_text` 不精确匹配记忆中的内容
- 记忆文档中有多处匹配（会替换所有出现）

**解决方案**：
- 使用 `/memory view` 查看精确内容
- 让 LLM 使用更长的 `old_text` 确保唯一匹配

### 程序崩溃

**可能原因**：
- OpenAI 端点未启动
- 网络超时

**解决方案**：
- 确认 `http://localhost:4000/openai/v1` 可访问
- 检查 `HttpClient.Timeout` 设置

---

## 反馈与改进

这是一个实验原型，欢迎在使用过程中：
- 记录 LLM 的记忆管理策略
- 观察哪些信息被记住，哪些被遗忘
- 测试不同的 system instruction 对记忆行为的影响
- 探索更好的消息结构化格式
