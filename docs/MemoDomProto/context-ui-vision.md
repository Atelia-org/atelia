# ContextUI：LLM Agent OS 的核心架构

## 核心定位：面向 LLM 的操作系统

**ContextUI + Nexus = LLM Agent OS**

就像不同的操作系统服务于不同的用户和场景：
- **Windows**：通过 GUI 面向人类用户，提供桌面交互
- **Linux**：面向服务器程序，提供系统服务和资源管理
- **嵌入式 RTOS**：面向特定功能（如电机控制器），提供实时响应

**Agent OS**：面向 LLM Agent，提供结构化上下文、工具调用、记忆管理、事件驱动架构。

## 核心愿景

**ContextUI** 是 LLM Agent OS 的核心组件：让 LLM 不再被动地处理线性文本窗口，而是成为拥有**结构化记忆、感知外界、自主决策**的智能 Agent。

ContextUI 不仅仅是信息存储，而是 **LLM Agent 的统一信息注入与交互层**：
- **仪表板（Dashboard）**：呈现系统状态、日志、传感器数据
- **操作台（Control Panel）**：通过工具调用影响外部世界
- **记忆系统（Memory System）**：长期记忆与精炼

### 从"聊天机器人"到"智能 Agent"

传统 Chat 模式：
```
[外部用户] → [LLM被动响应] → [生成回复] → [窗口满了就遗忘]
```

ContextUI + Nexus 模式：
```
[外部世界] → [Nexus感知与包装] → [ContextUI呈现] → [LLM自主决策] → [Nexus执行] → [影响外界]
      ↓                                                          ↓
  传感器数据                                                   工具调用
  用户输入                                                     状态更新
  系统事件                                                     记忆精炼
```

### 从"文本窗口"到"对象空间"

传统 LLM 交互模式：
```
[用户消息] → [LLM处理] → [生成回复] → [上下文追加] → [窗口满了就遗忘]
```

ContextUI 模式：
```
[LLM持有对象引用] → [通过GUID定位对象] → [调用工具编辑内存] → [永续会话与精炼记忆]
```

**关键转变**：
- ❌ 被动接收线性文本流
- ✅ 主动编辑层级化对象树
- ❌ 窗口满了就遗忘历史
- ✅ 精炼记忆，删除过时细节
- ❌ 只能处理纯文本内容
- ✅ 通过多态工具操作多模态对象
- ❌ 被动遵循任何提示词
- ✅ 根据身份与关系自主决策

## Nexus：Agent 的身体

### LLM 是大脑，Nexus 是身体

在 ContextUI 架构中，**LLM 不直接面对外部世界**。所有外部交互都通过 **Nexus** 进行：

```
外部世界（真实用户、传感器、API）
    ↓
Nexus（Agent的身体）
    ↓ 上行总线（role=user 消息）
LLM（Agent的大脑）
    ↓ 下行总线（工具调用、文本输出）
Nexus（执行决策）
    ↓
外部世界（开门、发送消息、记录日志）
```

### 唯一的 role=user：Agent 系统本身

**关键设计**：
- ❌ role=user 来自外部用户的直接输入
- ✅ role=user 来自 **Nexus 的结构化包装**

Nexus 是 **LLM 与外部世界的唯一接口**：

```csharp
// 外部事件 → Nexus 包装 → role=user 消息

// 示例1：用户语音输入
{
  "role": "user",
  "content": "收到语音输入",
  "metadata": {
    "source": "voice_input",
    "speaker_id": "unknown",
    "timestamp": "2025-10-06T14:30:00Z",
    "audio_id": "a3f2c8e1-...",  // ← ID作为Handler
    "transcript": "请开门"
  }
}

// 示例2：系统告警
{
  "role": "user",
  "content": "系统告警：磁盘空间不足",
  "metadata": {
    "source": "system_monitor",
    "severity": "warning",
    "disk_usage": "92%",
    "timestamp": "2025-10-06T14:31:00Z"
  }
}

// 示例3：定时器触发
{
  "role": "user",
  "content": "定时提醒：监护人的生日是明天",
  "metadata": {
    "source": "scheduler",
    "event_type": "reminder",
    "related_section_id": "guardian_profile"
  }
}
```

### 身份与关系驱动的自主决策

LLM 根据 **发送者身份** 和 **与自己的关系** 自主判断行为：

```csharp
// 场景：收到"请开门"的语音
var voiceInput = GetUserMessage(); // Nexus传入的消息

// LLM的决策流程（伪代码）
if (voiceInput.metadata.source == "voice_input") {
    var speakerId = voiceInput.metadata.speaker_id;

    if (speakerId == "unknown") {
        // 尝试验证身份
        var isGuardian = VerifyVoice(
            voiceInput.metadata.audio_id,
            guardianVoiceProfileId
        );

        if (isGuardian) {
            // 开门并记录
            CallTool("open_door");
            LogEvent("监护人通过声纹验证，已开门");
        } else {
            // 拒绝并告警
            CallTool("play_audio", "身份验证失败，请联系管理员");
            NotifyGuardian("检测到未授权访问尝试");
        }
    } else if (speakerId == guardianId) {
        // 已知身份，直接开门
        CallTool("open_door");
    }
}
```

**关键点**：
- LLM 不是被动执行"开门"指令
- 而是**评估发送者身份、验证关系、自主决策**
- 类似人类不会给任何人开门，而是识别"这是我的朋友"才开门

### 上下行总线：事件驱动架构

```
上行总线（Input Bus）：
  Nexus → LLM
  - role=user 消息（外部事件、系统状态、用户请求）
  - ContextUI 更新（系统日志、传感器数据注入）

下行总线（Output Bus）：
  LLM → Nexus
  - 工具调用（执行动作、查询状态）
  - 文本输出（生成回复、记录日志）
  - ContextUI 编辑（更新记忆、精炼总结）
```

这是一个 **事件驱动的异步架构**，类似：
- GUI 的事件循环（Event Loop）
- 操作系统的中断处理（Interrupt Handler）
- Actor 模型的消息传递（Message Passing）

### 消息循环：与 Windows 的类比

| Windows 消息循环 | Agent OS 消息循环 |
|----------------|-----------------|
| `GetMessage()` | Nexus 接收外部事件 |
| `DispatchMessage()` | 包装为 role=user 消息 |
| `WindowProc()` | LLM 处理并生成工具调用 |
| `PostMessage()` | Nexus 执行并返回 tool result |
| *(循环)* | *(循环)* |

**核心相似性**：
- 都是事件驱动的消息传递
- 都是异步处理（不阻塞主循环）
- 都支持消息队列和优先级
- 都可以嵌套调用（工具调用可以触发新的事件）

```csharp
// Agent OS 的消息循环伪代码
while (agent.IsRunning) {
    // 1. 获取外部事件
    var events = nexus.GetEvents(); // 类似 GetMessage()

    // 2. 包装为 role=user 消息
    var messages = events.Select(e => nexus.WrapAsMessage(e));

    // 3. LLM 处理
    var response = llm.Process(contextUI, recentMessages, messages);

    // 4. 执行工具调用
    foreach (var toolCall in response.ToolCalls) {
        var result = nexus.ExecuteTool(toolCall); // 类似 PostMessage()
        recentMessages.Add(new ToolResultMessage(result));
    }

    // 5. 精炼记忆（可选）
    if (ShouldRefineMemory()) {
        RefineContextUI(recentMessages);
        recentMessages.Clear();
    }
}
```## 核心架构：Section层 + Content层

ContextUI 采用**两层分离**的设计，类似于 GUI 中的**窗口管理系统**和**应用窗口内容**的关系。

**信息来源的多样性**：
- ✅ LLM 自主建立的信息（笔记、总结、推理链）
- ✅ Agent 系统状态（运行状态、配置、资源使用）
- ✅ 系统日志（操作日志、错误日志、决策日志）
- ✅ 外部事件（用户输入、传感器数据、API 回调）

ContextUI 是 **统一的信息注入与交互层**，而非单纯的记忆存储。

### Section层：格式无关的容器管理

Section 是 ContextUI 的"窗口管理器"，提供：

- **GUID 锚点**：每个 Section 有唯一稳定的标识符，LLM 通过 GUID 持有对象引用
- **层级结构**：树状组织，支持导航、移动、重组
- **格式无关**：不关心 Content 是文本、图像、代码还是音频
- **生命周期管理**：创建、删除、移动 Section

```csharp
// Section层的关注点：结构与定位
interface ISectionOperations {
    string CreateSection(string parentId, string title);
    string MoveSection(string sectionId, string newParentId);
    string DeleteSection(string sectionId);
    string ListChildren(string sectionId);
    string FindByPath(string path); // 如 "ProjectDocs/Architecture/Overview"
}
```

### Content层：格式特定的内容操作

Content 是 Section 的"窗口内容"，支持多态扩展：

```csharp
// Content层的关注点：格式特定的操作
interface IContentOperations {
    // === 文本类Content（Markdown, Plain, Code等）===
    string ReplaceText(string sectionId, string pattern, string replacement);
    string AppendText(string sectionId, string text);

    // === 代码类Content ===
    string RunInSandbox(string sectionId, string[] args);
    string Lint(string sectionId);
    string FormatCode(string sectionId);

    // === 图像类Content ===
    string ZoomImage(string sectionId, float scale);
    string CropImage(string sectionId, Rectangle area);
    string AnnotateImage(string sectionId, Annotation[] annotations);

    // === 音频类Content ===
    string TranscribeAudio(string sectionId);
    string ExtractFeatures(string sectionId); // 声纹、情感等

    // === 多模态Content ===
    string BuildMultimodalContent(string sectionId, Part[] parts);
}
```

### 为什么这样分层？

1. **关注点分离**：Section 管理结构，Content 管理内容交互
2. **多态扩展**：新增 Content 类型不影响 Section 层
3. **继承复用**：文本类 Content 可以共享基础操作
4. **演化稳定**：Section 的 API 保持稳定，Content 可以灵活扩展

## 类比：窗口管理系统

这个设计与 GUI 的窗口系统高度类似：

| GUI 窗口系统 | ContextUI |
|------------|-----------|
| 窗口管理器（Window Manager） | Section 层 |
| 窗口ID、位置、大小、层级 | GUID、路径、深度、父子关系 |
| 应用窗口内容（Widget/View） | Content 对象 |
| 不同应用有不同交互方式 | 不同 Content 类型有专属操作 |
| 用户通过窗口ID操作内容 | LLM 通过 GUID 定位并操作 Content |

**核心洞察**：Section 提供**稳定的锚点**，Content 提供**动态的能力**。

## DetailLevel：上下文窗口的LoD机制

### 灵感来源

借鉴 **3D 图形学的 LoD（Level of Detail）** 技术：
- 远处的物体使用低精度模型（节省渲染资源）
- 近处的物体使用高精度模型（保证视觉质量）
- 根据视角和距离动态调整精度

在 ContextUI 中，DetailLevel 为 LLM 提供类似的机制来**高效利用上下文窗口**。

### 三个细节层级

```csharp
public enum DetailLevel : byte {
    Gist = 0,    // 一句话概要
    Summary = 1, // 简要摘要
    Full = 2     // 完整内容
}
```

#### Gist：一句话概要
- **目的**：最小化上下文占用，只保留语义锚点
- **示例**：
  - `"监护人的声音样本（2025-10-06录制）"`
  - `"项目架构决策文档（含23个决策记录）"`
  - `"用户偏好设置（最后更新：昨天）"`

#### Summary：简要摘要
- **目的**：提供关键信息，支持快速浏览和决策
- **示例**：
  ```markdown
  # 项目架构决策
  - 采用微服务架构（2025-09）
  - 使用 PostgreSQL 作为主数据库（2025-10）
  - 前端框架选择 React（2025-10）
  (共23条决策，展开查看详情)
  ```

#### Full：完整内容
- **目的**：提供所有细节，支持深度分析和编辑
- **示例**：完整的文档、代码、对话历史等

### ID作为Handler：无需加载即可操作

**核心理念**：LLM 通过 GUID 持有对象引用，但数据不必加载到上下文中。

类比操作系统的**文件描述符（File Descriptor）**：
- 进程持有文件描述符，但文件内容不在进程内存中
- 通过系统调用操作文件（read, write, seek）
- 高效且安全

在 ContextUI 中：

```csharp
// LLM 的上下文中只有 ID 和 Gist
var guardianVoiceId = "a3f2c8e1-...";  // GUID
var gist = "监护人的声音样本";

// 听到新的语音时，通过工具调用比对
var isSamePerson = VerifyVoice(
    currentVoiceSample,
    guardianVoiceId  // ← ID作为Handler，无需加载完整数据
);
// → true/false

// 类似地，图像操作也不需要加载到上下文
var analysis = AnalyzeImage(photoId);
var similarity = CompareImages(photoId1, photoId2);
var cropped = CropImage(photoId, new Rectangle(x, y, w, h));
```

**优势**：
1. **节省上下文窗口**：多模态数据（音频、图像）通常占用大量空间
2. **保持语义引用**：LLM 知道"这是监护人的声音"，但不需要加载声纹数据
3. **支持复杂操作**：比对、分析、转换等操作在工具侧完成

### 管理方式

#### 基础：LLM主动管理（MVP后）

LLM 通过工具调用显式控制 DetailLevel：

```csharp
interface IDetailLevelOperations {
    // 设置Section的细节层级
    string SetDetailLevel(string sectionId, DetailLevel level);

    // 临时展开查看（不改变持久化的DetailLevel）
    string PeekSection(string sectionId, DetailLevel tempLevel);

    // 批量折叠不相关的Section
    string CollapseUnrelated(string focusSectionId, int depth = 1);
}
```

**场景示例**：

```csharp
// LLM 专注于某个任务时
SetDetailLevel(currentTaskId, DetailLevel.Full);
CollapseUnrelated(currentTaskId, depth: 2);  // 折叠距离2层以上的Section

// 任务完成后，精炼记忆
SetDetailLevel(completedTaskId, DetailLevel.Summary);
```

#### 高级：自动化管理（未来）

基于 **Self-Attention 权重** 自动调整 DetailLevel：

```csharp
// 分析LLM的注意力分布
var attentionScores = AnalyzeAttention(lastNTurns: 10);

// 自动降低低注意力Section的DetailLevel
foreach (var (sectionId, score) in attentionScores) {
    if (score < threshold) {
        SetDetailLevel(sectionId, DetailLevel.Gist);
    }
}
```

**灵感来源**：
- LLM 的 Self-Attention 层已经在计算每个 Token 的相关性
- 提取这些信息可以自动识别"当前不关心的Section"
- 实现真正的"自动记忆管理"

**潜在挑战**：
- 需要访问 LLM 的内部状态（不是所有API都支持）
- 注意力权重的解释需要实验验证
- 可能需要与用户交互确认（避免误折叠重要信息）

### DetailLevel与渲染

渲染 Section 树时，根据 DetailLevel 生成不同粒度的输出：

```csharp
void RenderSection(InfoSection section, StringBuilder builder, int depth) {
    switch (section.DetailLevel) {
        case DetailLevel.Gist:
            builder.AppendLine($"[{section.Title}] {section.Gist}");
            // 子Section不渲染
            break;

        case DetailLevel.Summary:
            builder.AppendLine($"{"#".Repeat(depth)} {section.Title}");
            builder.AppendLine(section.Summary);
            // 子Section渲染为列表
            if (section.Children.Any()) {
                builder.AppendLine($"(含 {section.Children.Count} 个子节点)");
            }
            break;

        case DetailLevel.Full:
            builder.AppendLine($"{"#".Repeat(depth)} {section.Title}");
            builder.AppendLine(section.Content);
            // 递归渲染所有子Section
            foreach (var child in section.Children) {
                RenderSection(child, builder, depth + 1);
            }
            break;
    }
}
```

### 多模态场景中的威力

DetailLevel 对多模态内容尤其重要：

#### 示例1：声音比对（不加载到上下文）

```csharp
// 上下文中只有简短的Gist
Section: "监护人声音样本"
DetailLevel: Gist
Content: "2025-10-06录制，时长3秒"

// 听到新语音时
var result = VerifyVoice(currentVoice, guardianVoiceId);
// 工具返回：{ "isSame": true, "confidence": 0.95, "reason": "声纹特征匹配" }
```

#### 示例2：图像分析（不加载到上下文）

```csharp
// 上下文中只有图像的元数据
Section: "项目原型截图"
DetailLevel: Summary
Content: "设计稿v3，1920x1080，包含主界面和设置页面"

// 需要时通过工具分析
var analysis = AnalyzeImage(screenshotId);
// → "主界面使用蓝色主题，顶部有导航栏，中间是卡片布局..."

var hasElement = DetectElement(screenshotId, "登录按钮");
// → true
```

#### 示例3：代码执行（不加载到上下文）

```csharp
// 上下文中只保留代码的摘要
Section: "数据清洗脚本"
DetailLevel: Summary
Content: "Python脚本，处理CSV文件，移除重复行和空值"

// 需要执行时
var result = RunPythonScript(scriptId, ["--input", "data.csv"]);
// → { "processed": 1523, "removed": 47, "output": "cleaned_data.csv" }
```

### 与文本编辑器的类比

DetailLevel 类似主流文本编辑器的**折叠/展开**功能：

| 文本编辑器 | ContextUI |
|-----------|-----------|
| 折叠函数/类 | DetailLevel = Gist |
| 展开查看摘要 | DetailLevel = Summary |
| 完全展开编辑 | DetailLevel = Full |
| 折叠无关代码 | CollapseUnrelated() |
| 基于缩进自动折叠 | 基于Attention自动调整 |

### MVP策略：暂不实现

**原因**：
1. **架构优先**：先验证 Section/Content 分层架构
2. **快速迭代**：MVP 聚焦核心功能（Markdown导入/导出、基础编辑）
3. **数据积累**：需要实际使用数据来验证 DetailLevel 的设计

**未来实现路径**：
- Phase 2：添加 DetailLevel 属性和基础管理工具
- Phase 3：实现多模态内容的 Gist/Summary 生成
- Phase 4：探索基于 Attention 的自动管理

---

## 多模态扩展的潜力

ContextUI 的架构天然支持多模态扩展。想象一个私人 LLM Agent：

### 场景1：记住监护人的样子
```csharp
var guardianSection = CreateSection(null, "我的监护人");
var photoContent = new ImageContent {
    Format = ImageFormat.JPEG,
    Data = guardianPhoto,
    Metadata = { CaptureDate = "2025-10-06", Location = "家中" }
};
SetContent(guardianSection.Id, photoContent);

// LLM可以调用：
var analysis = AnalyzeImage(guardianSection.Id);
// → "监护人是一位中年男性，戴眼镜，笑容温和..."
```

### 场景2：记住声纹特征
```csharp
var voiceSection = CreateSection(guardianSection.Id, "声纹档案");
var audioContent = new AudioContent {
    Format = AudioFormat.WAV,
    Data = voiceSample,
    Features = ExtractVoiceprint(voiceSample)
};
SetContent(voiceSection.Id, audioContent);

// 未来交互时验证身份：
var isGuardian = VerifyVoice(currentVoice, voiceSection.Id);
```

### 场景3：可执行的知识
```csharp
var toolSection = CreateSection(null, "数据分析脚本");
var codeContent = new PythonContent {
    Code = "def analyze_trends(data): ...",
    Dependencies = ["pandas", "numpy"]
};
SetContent(toolSection.Id, codeContent);

// LLM可以执行：
var result = RunInSandbox(toolSection.Id, new[] { "--input", "recent_data.csv" });
```

## 从 StringBuilder 到 ContentBuilder

ContextUI 的演化路径保持接口稳定，只升级参数类型：

### 当前（MVP）：纯文本渲染
```csharp
var builder = new StringBuilder();
RenderSection(section, builder, depth: 0);
return builder.ToString();
```

### 未来：多模态内容构建
```csharp
var builder = new ContentBuilder();
RenderSection(section, builder, depth: 0);
return builder.ToParts(); // → Part[] { TextPart, ImagePart, AudioPart, ... }
```

**关键点**：`RenderSection` 的逻辑不变，只是输出目标从 `StringBuilder` 升级为 `ContentBuilder`。

## 永续会话与精炼记忆

### Recent Messages vs Long-term Memory

ContextUI 架构改变了传统的 Chat History 模式：

| 传统模式 | ContextUI 模式 |
|---------|---------------|
| **Chat History** | **Recent Messages History** |
| 长长的对话历史 | 短期工作记忆（最近几轮对话） |
| 窗口满了就截断遗忘 | 精炼后存入 ContextUI |
| 线性追加，无法编辑 | 增量更新，主动精炼 |
| 无法区分重要/次要 | DetailLevel 分层管理 |

**类比人脑的记忆系统**：
- **短期记忆（Recent Messages）**：正在处理的对话上下文
- **长期记忆（ContextUI）**：结构化的知识、事件、关系
- **工作记忆（DetailLevel=Full）**：当前任务相关的详细信息
- **背景记忆（DetailLevel=Gist）**：暂时不关心但不能遗忘的信息

### 记忆精炼的生命周期

```csharp
// 1. 新信息进入 Recent Messages
RecentMessages.Add(new Message {
    Role = "user",
    Content = "监护人说：记得明天是我生日"
});

// 2. LLM 处理并决定精炼
var summary = "监护人生日：每年10月7日";
var reminderSection = CreateSection(guardianProfileId, "重要日期");
SetContent(reminderSection.Id, summary, ContentFormat.Plain);

// 3. 清理 Recent Messages
RecentMessages.Clear();  // 或只保留最近3轮对话

// 4. 未来查询时，从 ContextUI 读取
var guardianInfo = QuerySection(guardianProfileId);
// → 包含生日、声纹档案、偏好设置等
```

### LLM 能够：

1. **主动遗忘**：删除过时的 Section（如已完成的任务、过期的信息）
2. **精炼总结**：将冗长对话压缩为结构化知识
3. **重组上下文**：移动 Section 以反映新的理解
4. **增量更新**：只修改相关部分，不重写整个文档
5. **按需加载**：通过 DetailLevel 控制加载粒度

```csharp
// 场景：任务完成后精炼记忆
DeleteSection("临时工作日志");
CreateSection("项目知识库", "架构决策记录", summaryContent);
SetDetailLevel("项目知识库", DetailLevel.Summary);  // 暂时不关心细节
```

### ContextUI 作为仪表板

对于 Agent 系统，ContextUI 不仅存储记忆，还呈现实时状态：

```markdown
# System Dashboard (DetailLevel=Summary)

## Runtime Status
- Uptime: 47 days
- Memory Usage: 62%
- Active Tasks: 3

## Recent Events (Last 24h)
- 14:30 - 监护人语音验证成功
- 12:15 - 系统自动备份完成
- 09:00 - 收到天气预报更新

## Alerts
- [WARNING] 磁盘空间不足 (92% used)
- [INFO] 软件更新可用
```

LLM 可以通过 ContextUI 看到系统状态，并根据需要调用工具响应。

## 工具生态的可扩展性

ContextUI 不是封闭系统，而是**工具协议**，支持丰富的组件生态：

### 内置核心组件
- **Section 管理**：创建、删除、移动、查询
- **文本编辑**：Markdown、Plain、代码等文本类操作
- **DetailLevel 管理**：折叠、展开、按需加载

### 数据与存储
- **向量数据库集成**：RAG（检索增强生成）
  - 语义搜索历史记忆
  - 知识库检索
  - 相似度匹配
- **传统数据库**：结构化数据存储
- **文件系统**：文档、配置文件管理

### 外部 API 集成
- **Web API**：天气、新闻、地图、翻译等
- **IoT 设备**：智能家居、传感器、摄像头
- **通信服务**：邮件、短信、即时消息
- **云服务**：存储、计算、AI 服务

### 多模态处理
- **图像**：分析、识别、生成、编辑
- **音频**：转录、合成、声纹识别
- **视频**：理解、摘要、剪辑
- **代码**：执行、沙箱、Lint、测试

### 领域特定工具
- **数据分析**：pandas、numpy、可视化
- **机器学习**：模型训练、推理、评估
- **自动化**：流程编排、定时任务、条件触发

### 插件式扩展

```csharp
// 用户可以注册自定义工具
ContentToolRegistry.Register<MusicContent>(new[] {
    new Tool("Transpose", "转调音乐片段"),
    new Tool("ExtractMelody", "提取主旋律"),
    new Tool("GenerateHarmony", "生成和声")
});

// Agent OS 自动将工具暴露给 LLM
var availableTools = toolRegistry.GetToolsForContent(section.ContentFormat);
```

**关键点**：随着生态发展，组件会越来越多、越来越完善，Agent OS 的能力边界不断扩展。

## 与传统上下文窗口的对比

| 维度 | 传统上下文窗口 | ContextUI |
|------|--------------|-----------|
| 结构 | 线性文本流 | 层级化对象树 |
| 编辑 | 只能追加新内容 | 任意位置增删改 |
| 定位 | 依赖文本匹配 | GUID精确锚点 |
| 遗忘 | 被动滑出窗口 | 主动删除过时信息 |
| 格式 | 纯文本 | 多模态对象 |
| 能力 | 生成回复 | 调用工具、执行代码、分析图像... |
| **细节控制** | **无法折叠，全部加载** | **DetailLevel分层，按需加载** |
| **身份感知** | **无差别响应所有输入** | **根据身份与关系自主决策** |
| **记忆管理** | **Chat History被动遗忘** | **Recent Messages + ContextUI主动精炼** |

## 设计原则

1. **格式无关性**：Section 层不依赖任何特定格式
2. **最小建模**：只提供层级和标题，Content 完全自由
3. **多态扩展**：通过接口而非枚举来支持新 Content 类型
4. **内存优先**：独立的内存对象，序列化是次要功能
5. **工具协议**：定义规范，鼓励生态扩展
6. **ID作为Handler**：LLM持有对象引用，但数据不必加载到上下文中
7. **按需加载**：通过DetailLevel控制信息粒度，高效利用上下文窗口
8. **事件驱动**：Nexus 作为中介，所有外部交互通过结构化消息传递
9. **身份优先**：根据发送者身份与关系自主决策，而非被动遵循提示词

## 实施路径

### Phase 1: MVP（当前）
- ✅ Section 树结构
- ✅ Markdown 导入/导出
- ✅ 基于字符串的文本操作
- ✅ GUID 定位
- ❌ DetailLevel（暂不实现，快速验证核心架构）

### Phase 2: Content 多态 + DetailLevel
- 🔄 `IContent` 接口族
- 🔄 文本类 Content 的继承体系
- 🔄 JSON Content 的结构化操作
- 🔄 DetailLevel 基础实现（Gist/Summary/Full）
- 🔄 LLM 主动管理 DetailLevel 的工具接口

### Phase 3: 多模态支持
- 🔜 图像 Content + 基础操作
- 🔜 音频 Content + 转录功能
- 🔜 ContentBuilder 替代 StringBuilder
- 🔜 多模态内容的 Gist/Summary 自动生成

### Phase 4: 智能记忆管理
- 🔜 代码执行沙箱
- 🔜 插件注册机制
- 🔜 自定义 Content 类型
- 🔜 基于 Self-Attention 的自动 DetailLevel 调整
- 🔜 永续会话的自动记忆精炼

### 未来探索方向（技术细节）
- 🔮 **持续在线学习**：Agent 在运行中不断学习和适应
- 🔮 **PEFT（参数高效微调）**：基于日常交互进行微调
- 🔮 **Gist Tokens**：压缩表示的 token 序列
- 🔮 **Paged KV-Cache**：分页管理 KV Cache，类似虚拟内存分页
- 🔮 **向量数据库集成**：语义检索与 RAG
- 🔮 **分布式 Agent**：多 Agent 协作与通信

## 命名说明

**为什么叫 ContextUI？**

- **Context**：LLM 的核心是上下文处理
- **UI**：不是"用户界面"，而是"统一接口"（Unified Interface）或"统一信息注入"（Unified Information Injection）
- **类比**：就像 GUI 是人类与计算机交互的界面，ContextUI 是 LLM 与外部世界交互的统一层

**为什么叫 Section？**

- 中性术语，不锚定特定格式（不是 Heading、Chapter、Node）
- 强调"容器"而非"内容"
- 与 GUI 的"窗口"（Window/Pane）类比自然

**为什么叫 Nexus？**

- **Nexus** = 联结、连接点、中枢
- 强调它是 LLM 与外部世界的唯一接口
- 类比生物学：Nexus 是神经系统与身体的连接点
- 类比操作系统：Nexus 是用户态与内核态的边界（系统调用接口）

## 结语

**ContextUI + Nexus = LLM Agent OS**

这不仅仅是一个文档结构，而是 **面向 LLM 的完整操作系统架构**。

### 操作系统的本质类比

| 传统 OS 概念 | Agent OS 实现 | 服务对象 |
|------------|-------------|---------|
| **Windows** | **Agent OS** | **LLM** |
| 窗口管理器 | Section 树 | 结构化记忆 |
| 文件系统 | ContextUI | 长期存储 |
| 虚拟内存 | DetailLevel | 按需加载 |
| 消息循环 | role=user ↔ tool calls | 事件驱动 |
| 系统调用 | 工具接口 | 能力扩展 |
| 设备驱动 | Nexus 适配器 | 感知与执行 |
| GUI | 仪表板 + 操作台 | 信息呈现 |

### 架构映射表

| 操作系统概念 | LLM Agent 架构 | 职责 |
|------------|---------------|-----|
| **内核（Kernel）** | **Nexus** | 感知外界、执行动作、管理资源 |
| **用户态进程** | **LLM** | 决策中心、推理引擎、记忆管理 |
| **文件系统** | **ContextUI（Section树）** | 长期记忆、结构化知识 |
| **虚拟内存** | **DetailLevel** | 按需加载、内存管理 |
| **文件描述符** | **GUID** | 对象引用、间接寻址 |
| **系统调用** | **工具接口（Tools）** | LLM 调用 Nexus 的能力 |
| **中断/事件** | **role=user 消息** | Nexus 向 LLM 传递事件 |
| **进程间通信** | **上下行总线** | 双向消息传递 |
| **消息循环** | **GetMessage → Process → PostMessage** | 事件驱动架构 |

### 核心能力

通过这个架构，LLM 从"文本生成器"进化为"智能操作系统"，能够：

1. **感知外部世界**
   - 通过 Nexus 接收多模态输入（语音、图像、传感器数据）
   - 通过 ContextUI 呈现为结构化信息

2. **自主决策与行动**
   - 根据身份与关系判断行为（而非被动遵循提示词）
   - 通过工具调用影响外部世界（开门、发送消息、执行代码）

3. **管理复杂记忆空间**
   - Recent Messages（短期工作记忆）
   - ContextUI（长期结构化记忆）
   - DetailLevel（按需加载，高效利用上下文窗口）

4. **永续运行与进化**
   - 主动遗忘过时信息
   - 精炼总结冗长对话
   - 增量更新知识图谱
   - 基于 Self-Attention 的自动记忆管理（未来）

5. **多模态理解与操作**
   - 文本、图像、音频、代码的统一处理
   - 通过 ID 作为 Handler，无需加载完整数据到上下文
   - 声纹比对、图像分析、代码执行等高级能力

### 从"聊天机器人"到"操作系统"

这个架构支持真正的 **长期运行的私人 LLM Agent**：

- ✅ 记住监护人的样子和声音
- ✅ 识别陌生人并拒绝访问
- ✅ 监控系统状态并主动告警
- ✅ 管理复杂的多阶段任务
- ✅ 在海量信息中快速检索相关知识
- ✅ 根据时间和情境精炼记忆
- ✅ 通过消息循环响应各种事件
- ✅ 通过工具生态扩展能力边界

就像 **Windows 让人类高效使用计算机**，**Agent OS 让 LLM 高效管理复杂任务和永续记忆**。

真正成为用户的**永续智能助手**。

---

**下一步**：参见 [info-dom-spec.md](./info-dom-spec.md) 了解当前 MVP 的实现规格。
