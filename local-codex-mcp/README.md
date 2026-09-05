# Local Codex MCP Bridge

私人专用的薄 MCP Server：让 ChatGPT Developer Mode 把高层任务交给本机 `codex app-server`，同时只把短结果、修改文件、验证状态和稳定 thread/turn ID 返回上层。

```text
ChatGPT -> Secure MCP Tunnel -> this bridge -> codex app-server -> allowed local workspace
```

MVP 暴露五个 tools：

- `codex_delegate`：新建 persistent Codex thread 并启动任务。
- `codex_continue`：复用同一 thread 的上下文启动下一 turn。
- `codex_status`：短状态查询。
- `codex_read`：读取 bounded summary/final，不返回完整历史。
- `codex_interrupt`：中断 active turn。

没有裸 shell MCP tool、数据库、approval UI、public plugin 或自制 tunnel protocol。`TaskBackend` 只是为未来 Galatea backend 留出的薄接口。

## 前置条件

- Node.js 20 或更新版本（Inspector 另需 22.19+）。
- 已运行下文的 exact-pin installer，并已用本机自己的 Codex auth 登录。
- 一个或多个允许 ChatGPT 委派任务的绝对目录。
- Secure MCP Tunnel 还需要 Platform tunnel 权限、`tunnel_id` 与 runtime/control-plane API key。

本工程只支持 repo-local `@openai/codex@0.154.0-alpha.3`。它包含 official SQLite thread-history
projector 对 malformed/discontinuous records（包括 duplicate/regressed ordinal）的容错修复
([#42369](https://github.com/openai/codex/pull/42369)、
[`095ac4f`](https://github.com/openai/codex/commit/095ac4f131e759b204fa6368dc42d2feff6eb21a))，以及 rollout
读取统一走 canonical JSON decoder 的修复
([#42378](https://github.com/openai/codex/pull/42378)、
[`69cebb5`](https://github.com/openai/codex/commit/69cebb5d15939bf9b6c1b4647b53879beab91ba2))。
这对应 [#35746](https://github.com/openai/codex/issues/35746) 与
[#42027](https://github.com/openai/codex/issues/42027) 所覆盖的上游故障类别；本项目不维护 Codex fork。

## 1. 安装、生成 schema 与构建

```bash
cd /repos/focus/atelia/local-codex-mcp
npm ci
npm run codex:install
npm run codex:verify
npm run schemas:generate
npm run schemas:verify
npm run build
```

installer 只写 ignored `.codex-packages/0.154.0-alpha.3/`，不会安装或覆盖全局 npm Codex。
它以 tracked `scripts/pinned-codex/package-lock.json` 的 exact version 与 registry SRI 执行 `npm ci`；重复执行会验证并复用，
但若同版本目录内容漂移会 fail closed，要求人工移走后再装。Bridge 未配置 command override 时直接通过 Node 启动该 repo-local
wrapper；`initialize.userAgent` 若不报告 exact `0.154.0-alpha.3`，sidecar 会以 `CODEX_VERSION_MISMATCH` 退出并只记录
expected/actual 规范化版本。

升级流程是一个 hard cut：先审阅新 package 与平台 package 的 registry SRI，更新
`scripts/pinned-codex/package.json`、lockfile、installer 与 runtime version 常量，再安装到新的 versioned 目录；随后用该 binary
重生成 `schemas/`、适配类型并跑完整测试和 provider-free projector canary。不要让两个 Codex 版本共用“受支持”语义。

## 2. 确认 Codex auth

```bash
codex login status
```

若未登录，先运行：

```bash
codex login
```

Bridge 启动和每次进程恢复都会调用 `account/read`。未登录时 tool 返回稳定错误 `CODEX_NOT_AUTHENTICATED`；Bridge 不读取、复制或代理 ChatGPT OAuth token。

## 3. 配置 allowed roots

环境变量采用严格 JSON array：

```bash
export CODEX_BRIDGE_ALLOWED_ROOTS='["/repos/focus/atelia"]'
export CODEX_BRIDGE_DEFAULT_CWD='/repos/focus/atelia'
```

Windows 原生 Node 示例：

```powershell
$env:CODEX_BRIDGE_ALLOWED_ROOTS='["D:\\Projects","D:\\Repos"]'
$env:CODEX_BRIDGE_DEFAULT_CWD='D:\Projects'
```

Bridge 会对 root 和每个请求 cwd 做 `realpath`、目录检查、symlink/traversal 解析与 containment 检查。省略 cwd 时只使用 configured default，不使用进程当前目录。

完整配置见 `.env.example`。程序本身不自动加载 `.env`；请由 shell、systemd 或其他进程管理器注入环境。

## 4. 本地启动

推荐的 stdio 模式：

```bash
npm run build
npm start
```

stdio 的 stdout 专用于 MCP JSON-RPC，结构化日志只写 stderr。

### Galatea durable sidecar

同一 backend 另有一个不暴露 MCP 的 Galatea adapter。它把工作目录、sandbox mode、本地命令
出网权限与内建工具 policy 固定在启动环境中，并提供三个可恢复的阶段式操作：建立持久 thread binding、
启动一个 turn、按 exact `{threadId, dispatchId, task, expectedTurnId}` 检查结果。Codex 的自然 Markdown final 原样返回，
不使用 `AgentReport` output schema。

```bash
export CODEX_BRIDGE_ALLOWED_ROOTS='["/repos/focus/atelia"]'
export CODEX_BRIDGE_DEFAULT_CWD='/repos/focus/atelia'
export GALATEA_CODEX_MODE=work
export GALATEA_CODEX_LOCAL_COMMAND_NETWORK=true
export GALATEA_CODEX_WEB_SEARCH=live
export GALATEA_CODEX_IMAGE_GENERATION=true
export GALATEA_CODEX_VIEW_IMAGE=true
npm run build
npm run start:galatea
```

stdin/stdout 是 strict bounded JSONL V3，stdout 只有协议 frame，日志只写 stderr。默认命令只启动这一版协议：

```json
{"v":3,"type":"ready"}
{"v":3,"type":"ensure-binding","requestId":"r1","bindingOperationId":"binding-1"}
{"v":3,"type":"binding-established","requestId":"r1","bindingOperationId":"binding-1","threadId":"thread-id"}
{"v":3,"type":"start-turn","requestId":"r2","dispatchId":"d1","threadId":"thread-id","task":"请调查并回复"}
{"v":3,"type":"turn-accepted","requestId":"r2","dispatchId":"d1","threadId":"thread-id","turnId":"turn-id"}
{"v":3,"type":"inspect-dispatch","requestId":"r3","dispatchId":"d1","threadId":"thread-id","task":"请调查并回复","expectedTurnId":"turn-id"}
{"v":3,"type":"dispatch-inspected","requestId":"r3","dispatchId":"d1","threadId":"thread-id","outcome":"completed","turnId":"turn-id","final":"自然 Markdown 回信","source":"live"}
```

失败以 `failed` frame 返回稳定的 `stage`/`code`。`turn-accepted` 只表示 `turn/start` 已返回稳定 handle；
sidecar 不同步等待 final。runtime 应持续发送 `inspect-dispatch`，并处理 `not-found`、`unavailable`、
`running`、`completed`、`failed` 或 `ambiguous`；所有semantic结果都携带exact `source=live|persistent`。
`OutcomeUnknown`必须发送`expectedTurnId:null`并仅按dispatch marker发现；`Accepted`必须发送已持久化的exact
turn ID。`ACCEPTED_TURN_NOT_VISIBLE`表示官方persistent projection尚未给出完整accepted turn/item view，
它是可重试的`unavailable`，不是ordinary not-found、terminal、quarantine或再次`turn/start`的授权。
`START_OUTCOME_UNKNOWN` 之后必须先 inspect，不能盲目重发 `start-turn`。
缺失、截断或超过上限的 final 均不会伪装成完整回信。EOF、SIGINT 与 SIGTERM 会回收 app-server child。
每封 frame 不接受 `cwd`、`mode`、本地命令出网或内建工具字段；相关 capability 只能由启动环境决定。

可选边界配置：`GALATEA_CODEX_MAX_INPUT_FRAME_BYTES`、`GALATEA_CODEX_MAX_OUTPUT_FRAME_BYTES`、
`GALATEA_CODEX_MAX_TASK_BYTES`、`GALATEA_CODEX_MAX_FINAL_BYTES`、
`GALATEA_CODEX_OUTPUT_WRITE_TIMEOUT_MS`。同一进程内并发的相同 `dispatchId` 会被
`DISPATCH_ALREADY_ACTIVE` fail closed；跨进程恢复与去重由调用方的 durable outbox/inbox 状态机负责，
并使用 `inspect-dispatch` 对已落到 app-server 的 exact turn 做 reconciliation。
continuation 会要求 persisted thread cwd 与启动时配置的 code-owned cwd 完全一致，即使漂移后的
目录仍位于 allowed roots 内也会拒绝。
持久thread ownership只认response ID、profile-specific exact name marker和canonical/path-policy
cwd；`threadSource`只是`thread/start`的optional analytics hint，持久化后为`null`也不影响
continue，`source`同样不参与authorization。Galatea profile启动app-server前会精确移除
`CODEX_SESSION_ID`、`CODEX_THREAD_ID`、`CODEX_INTERNAL_ORIGINATOR_OVERRIDE`、
`CODEX_PERMISSION_PROFILE`、`CODEX_CI`，但保留`HOME`、`PATH`、`CODEX_HOME`及
auth/provider/proxy环境；默认MCP profile不启用这层Galatea-specific scrub。

需要 Streamable HTTP 时：

```bash
export CODEX_BRIDGE_TRANSPORT=http
export CODEX_BRIDGE_HTTP_HOST=127.0.0.1
export CODEX_BRIDGE_HTTP_PORT=3000
npm start
```

endpoint 为 `http://127.0.0.1:3000/mcp`，支持 POST、GET/SSE 与 DELETE session。默认拒绝 non-loopback bind；Bridge 本身没有公网 authentication。

## 5. MCP Inspector

当前 Inspector 需要 Node 22.19+。Bridge 本身支持 Node 20+；运行 Inspector 前请切换到 Node 22.19+，`npm run inspect` 会临时获取固定版本的 Inspector：

```bash
# terminal A
export CODEX_BRIDGE_ALLOWED_ROOTS='["/repos/focus/atelia"]'
export CODEX_BRIDGE_TRANSPORT=http
npm start

# terminal B
npm run inspect
```

在 Inspector 中选择 **Streamable HTTP**，填写：

```text
http://127.0.0.1:3000/mcp
```

如果本机设置了代理，而 Inspector 对 loopback 报 `invalid onRequestStart method`，在 Inspector 进程中取消 `HTTP_PROXY`/`HTTPS_PROXY`/`ALL_PROXY`（或使用能正确 bypass loopback 的代理配置）后重试。

依次验证：

1. Initialize/Connect；
2. `tools/list` 出现五个 tools；
3. `codex_status`/`codex_read` 是 read-only annotations；
4. `codex_delegate` 的空 task 被 schema 拒绝；
5. allowed root 外 cwd 返回 `CWD_NOT_ALLOWED`；
6. 用 `mode: research, local_command_network: false, web_search: disabled` 做一次短调查。

也可以在 Inspector UI 选择 stdio，command 填绝对路径的 `node`，arguments 填 `dist/src/index.js`；Inspector 进程必须继承上面的 Bridge 环境变量。

## 6. 自动测试与真实 app-server integration

```bash
npm test
```

默认测试覆盖 MCP discovery/schema、路径与 symlink escape、initialize handshake、乱序 request correlation、notification dispatch、server-request fail-closed、RPC timeout、process crash、malformed stdout、stdin EPIPE、stubborn child 回收、bounded wait、turn completion 和 interruption。

本机已有 Codex auth 时运行真实测试：

```bash
CODEX_BRIDGE_RUN_LIVE=1 npm run test:integration
```

它会创建临时 git repo，执行 read-only 调查，停止并重启 Bridge/app-server client，再用同一 `thread_id` 创建内容精确的 `hello.txt`，最后删除临时 repo。不会修改当前仓库。

SQLite projector 修复可用一份包含已知 duplicate-ordinal 形状的本机 rollout 做 provider-free 验证：

```bash
npm run canary:projection -- /absolute/path/to/private-rollout.jsonl
```

脚本严格要求恰好一个相邻重复，且形状为 `token_count → thread_settings_applied`；它把内容复制到 disposable
`CODEX_HOME`，替换 session cwd/git 元数据，只调用 `initialize`、`thread/resume(excludeTurns=true)` 与
`thread/turns/list(itemsView=full)`，确认重复 ordinal 后的 completed turn/final 可见后删除临时目录。
它不会调用 `turn/start`，不会访问 provider，也不会改写源 rollout 或 active Codex home。private rollout 不得提交。

## 7. Secure MCP Tunnel（推荐）

Windows 原生部署另见 [Windows Secure MCP Tunnel 配置](./WINDOWS-TUNNEL.md)。

Bridge 不实现 tunnel protocol，直接使用 OpenAI 官方 `tunnel-client`。先在 [Platform tunnel settings](https://platform.openai.com/settings/organization/tunnels) 创建 tunnel，并把目标 ChatGPT workspace/account 与 Platform organization 关联。

从 tunnel settings 下载当前 `tunnel-client` 后：

```bash
export CONTROL_PLANE_API_KEY='sk-...'
export CODEX_BRIDGE_ALLOWED_ROOTS='["/repos/focus/atelia"]'
export CODEX_BRIDGE_DEFAULT_CWD='/repos/focus/atelia'
# 默认使用 repo-local exact pin；只有受控诊断时才显式覆盖 command。

tunnel-client init \
  --sample sample_mcp_stdio_local \
  --profile local-codex \
  --tunnel-id tunnel_REPLACE_ME \
  --mcp-command '/ABSOLUTE/PATH/TO/node /repos/focus/atelia/local-codex-mcp/dist/src/index.js'

tunnel-client doctor --profile local-codex --explain
tunnel-client run --profile local-codex
```

让 `tunnel-client run` 保持运行。它只需 outbound HTTPS 到 OpenAI，并在本机启动 stdio Bridge；不需要家庭网络入站端口。runtime API key 只放在 tunnel-client 的进程环境/secret store 中，不写入 Bridge 配置。

官方流程：[Secure MCP Tunnel](https://developers.openai.com/api/docs/guides/secure-mcp-tunnels)。

## 8. ChatGPT Developer Mode 中填写什么

1. ChatGPT Web → **Settings → Security and login → Developer mode**，开启。
2. 打开 **ChatGPT Plugins**，点击加号创建 developer-mode app。
3. Name：`Local Codex Bridge`。
4. Connection：选择 **Tunnel**。
5. 选择刚创建的 tunnel；若未列出，粘贴 `tunnel_id`。
6. MCP app authentication：选择 **No Authentication**。Tunnel control plane 自己验证 tunnel-client；本地 Bridge 不接收 ChatGPT/OAuth 凭据。
7. 保存为 Draft，回到对话刷新 tools，确认出现五个 `codex_*` tools。

如果 tunnel 不可见，先检查：tunnel 是否关联目标 ChatGPT workspace、当前操作者是否有 Tunnels Read + Use、`tunnel-client doctor` 是否通过。Developer Mode 与 Tunnel RBAC 是两套独立权限。

官方流程：[ChatGPT Developer mode](https://developers.openai.com/api/docs/guides/developer-mode)。

第一次测试 prompt：

```text
请调用 codex_delegate，让本地 Codex 以 research 模式、local_command_network=false、web_search=disabled 调查 /repos/focus/atelia/local-codex-mcp：说明它解决什么问题、列出最多 8 个关键文件。不要自己读取仓库；只整合 Codex 返回的短摘要，并保留 thread_id 供后续继续。
```

随后测试 continuation：

```text
请用刚才的 thread_id 调用 codex_continue，让同一个 Codex thread 新增一个很小的回归测试并运行相关测试。
```

## 9. Public HTTPS fallback

若个人账号暂时无法创建/关联 Tunnel，保持 Bridge 监听 loopback HTTP，再通过 WireGuard/private link/reverse connection 接到 VPS 上的 authenticated HTTPS reverse proxy：

```text
ChatGPT -> authenticated VPS HTTPS /mcp -> private link -> 127.0.0.1:3000/mcp
```

不要直接把 Bridge 的无认证 HTTP 暴露公网。第一版没有实现 OAuth、多用户或公网 auth；这些属于 transport/deployment 层，不应混入 Codex backend。

## 安全与语义边界

- `work`：`approvalPolicy=never`，`workspaceWrite`，唯一 writable root 是 canonical cwd，默认 `local_command_network=true`，并排除 `/tmp` 写入。
- `research`：`readOnly`。`local_command_network`只控制sandboxed command出网；`web_search`独立选择`disabled|cached|indexed|live`。开发默认分别为`true`与`live`。
- `image_generation`与`view_image`也按turn显式配置，默认开启；provider或运行环境不支持时仍由Codex/app-server给出真实能力结果，不由Bridge伪造fallback。
- 成功的`imageGeneration.savedPath`会进入bounded `changed_files`投影，并在最终返回前继续接受canonical cwd containment过滤。
- 默认 child args 关闭 inherited Codex MCP servers 与 apps；如果用 `CODEX_BRIDGE_CODEX_ARGS` 覆盖，调用者必须保留等价限制。
- approval、permission、elicitation 与未知 server requests 全部 fail-closed；绝不自动批准 escalation。
- Bridge-created threads 用response ID、持久化exact name marker与canonical cwd做ownership协调；optional analytics `threadSource`和origin `source`不参与。普通其他 thread ID 会返回 `THREAD_NOT_FOUND`。这是私人同一用户进程间的防误用边界，不是对同机恶意进程的认证：能直接调用 app-server 的本机进程也能伪造 title。若威胁模型包含不可信本机进程，需要在第二阶段增加bridge私有持久allowlist/签名元数据。
- 只存运行时 turn 状态；重启后从 `thread/read` 恢复 persisted thread。stdio child 的 in-flight turn 不保证跨 Bridge 进程重启存活。
- Galatea inspection先用metadata-only `thread/read`核对thread ID、ownership name和canonical cwd。Accepted只按
  durable `expectedTurnId`选择，OutcomeUnknown只按exact `dispatchId/clientId + task`发现；两者随后使用官方
  bounded `thread/turns/list`与`thread/items/list`分页，检查page shape、cursor progress、generation、capacity、
  duplicate identity及final bounds。只有OutcomeUnknown零匹配可返回`not-found`；Accepted turn或其identity items
  未出现在persistent projection时返回`ACCEPTED_TURN_NOT_VISIBLE`并重试。
- 同一app-server generation内另维护一份bounded、non-durable exact turn observation，只接收`turn/start`
  response和官方`turn/started`、`item/completed`、`turn/completed`通知。它只保存task digest、bounded identity/
  final evidence，不保存raw task、command output或完整items；terminal证据压过late Running，冲突fail closed。
  app-server exit、Bridge stop或generation切换会清空它，`TaskStore.hydrate()`绝不重建live evidence。它只是
  persistent projection损坏时的warm-process低延迟证据，Galatea SQLite terminal CAS仍是唯一local publication
  authority。通用MCP `status/read`仍可按自身接口读取history，不参与Galatea reconciliation。
- 当前本机生成的 `SandboxPolicy` 还没有官方新文档展示的 restricted read roots 字段。因此 allowed roots 严格控制 cwd 与**写入**，但本版本不能承诺 Codex 完全无法读取 cwd 外文件。需要更强读取隔离时，应升级到支持该协议的 Codex 或增加 OS/container sandbox。
- `local_command_network`不再连带控制hosted tools；要关闭全部外部访问，必须显式使用`local_command_network=false, web_search=disabled`。Codex apps/MCP仍由child args独立关闭；本地Codex hooks/未来新增执行通道仍应在部署时审计。
- MCP output 有字符/数组硬上限，不返回 reasoning、命令 stdout、完整 diff、完整文件或 thread transcript。

工具 annotations 按真实能力声明：delegate/continue 是 write + potentially destructive + open-world；status/read 是 read-only；interrupt 会改变运行状态但不声明 destructive。

相关官方文档：

- [Build an MCP server](https://developers.openai.com/plugins/build/mcp-server)
- [Define MCP tools and annotations](https://developers.openai.com/plugins/plan/tools)
- [Codex app-server protocol](https://developers.openai.com/codex/app-server/)
- [Codex configuration reference](https://learn.chatgpt.com/docs/config-file/config-reference)
