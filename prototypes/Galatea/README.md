# Galatea.Server

家庭局域网里的单会话 Chat 服务。每个账号绑定一个已经 provision 的
`SessionJournal` repository；raw journal 是会话正确性来源，DerivedRecap 是可恢复、可重建的
provider context sidecar。

## 启动前准备

```bash
dotnet run --project prototypes/Galatea/Galatea.Server.csproj
```

首次启动会生成 `.atelia/galatea/config.json` 和同目录的 `connections.json` 模板，然后退出。
Galatea 不会在第一次 Send 时自动创建或补齐会话仓库。每个 `sessionDir` 必须由 operator 预先准备：

- 一个有效的 SessionJournal main branch；
- 对应 RefId 的 `derived/recap/v4` Store；
- repository-owned `config/recap-planner-config.json`。

可使用 `prototypes/SessionJournal.Cli` 的 import/provision/config 命令完成这一步。Host 启动只打开
raw repo并检查 durable phase；不会创建 Completion client、DerivedRecap scaffolding或加载 active
Planner config。

## 配置

`config.json` 只保存账号、session路径、system prompt与监听地址：

```json
{
  "users": [
    {
      "userId": "alice",
      "password": "replace-me",
      "sessionDir": ".atelia/galatea/sessions/alice",
      "systemPromptFile": "prompts/alice.md"
    }
  ],
  "callLogDir": "../../../../gitignore/galatea/completion-calls",
  "listenUrls": ["http://0.0.0.0:3510"]
}
```

`systemPromptFile` 相对 `config.json` 所在目录解析，内容覆盖内联 `systemPrompt`。旧
`compactionThresholdTokens` / `compactionSystemPrompt` / `compactionPrompt` 已删除；Recap cadence、
profiles和limits统一由每个SessionJournal repo中的 `recap-planner-config.json` 管理。

`callLogDir`是可选的，也相对`config.json`所在目录解析。配置后，agent调用写入
`agent/`，Maintainer调用按profile写入`maintenance/<maintainer-id>/`；该目录必须与所有
`sessionDir`互不包含。未配置时不包装client、不创建日志目录。日志wrapper透传client identity，
因此开关日志不会改变Prepared中冻结的completion target；日志可能包含完整prompt/response，应位于
repo之外且按敏感数据管理。

`connections.json` 保存可选的Completion routes：

```json
{
  "defaultConnectionId": "dsv4p",
  "connections": [
    {
      "id": "dsv4p",
      "kind": "openai-chat",
      "modelId": "deepseek-v4",
      "completionSurfaceId": "openai-chat/deepseek-v4",
      "baseAddress": "https://example.invalid/v1/",
      "apiKeyEnv": "DEEPSEEK_API_KEY"
    }
  ]
}
```

Fresh Send 会在 exact Idle head 执行 desired setup reconciliation，再做 Building-first Recap
preparation；只有这些检查成功后才运行输入清洗并创建provider client。Prepared recovery精确绑定
durable completion identity，不回退default connection，也不读取active Recap config。

## Durable recovery

- 新消息只能进入 `Idle`；`TurnFailed` 会先做 exact abandon。
- Observation、Prepared、Started或tool tail不会被新消息覆盖，返回 `409 recovery-required`。
- `POST /api/chat/turns/resume` 不接受新消息，并携带 current endpoint 返回的 exact
  `recoveryHead`。
- Prepared 可安全恢复；Started 默认拒绝，只有显式
  `restartUncertainCompletion=true` 才接受可能重复调用的风险。
- G1 的 tool continuation 明确 unsupported。

页面会自动恢复安全的 Observation/Prepared tail；Started uncertain 保留为人工确认状态。

## Recent与Undo

`GET /api/recent-turns` 直接消费raw completed-turn projection，newest-first返回最近6个可见turn。只有captured
raw head本身就是最新terminal Action时才返回 `rewindLatestToken`。Undo必须原样回传此token；server
使用它执行exact-head CAS，因此陈旧页面不会误撤后来新增的turn。DerivedRecap不会被投影为conversation
turn。

## 输入清洗与停止

设置 `DEEPSEEK_BASE_URL` 和 `DEEPSEEK_API_KEY` 后，可在主模型调用前进行短消息最小纠错。失败会回退
原文；成功结果进入durable Observation。

Stop 在Recap lifecycle成功前取消pre-dispatch工作；成功后只通过stream observer停止provider，避免
取消Prepared/Started持久化步骤。只有known failed turn被exact abandon成功后，Host才承诺该轮未进入
active history。每个账号同一时刻只有一个writer turn；SSE订阅与stop endpoint不占writer lock。
