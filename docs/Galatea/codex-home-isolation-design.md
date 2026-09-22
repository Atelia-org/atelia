# Galatea 专用 Codex Home 与手动 Provider 切换

> 状态：基础 Home 软件切片已实施；设计曾完成三位独立审阅者的两轮质疑与交叉质询。2026-09-23。
> 范围：Galatea 的 `local-codex-mcp` durable sidecar。实施与隔离验证见第 8 节；未修改真实配置、登录、线程或数据库。第 2–3、7 节保留设计期需求与取证记录。

## 1. 最小模型

一个 Galatea 实例配置一个长期固定的 `sidecar.codexHome`，由 C# 启动层写入子进程的 `CODEX_HOME`。Codex 自己读取该目录下的 `config.toml`。操作者手动切换 Provider 配置并重启；配置切换本身不搬 Home、不重写派发身份，线程恢复继续遵循现有规则。

第一版只增加一个配置字段，并修正 bridge 对第三方 Provider 的认证判断。不增加任意 TOML 文件加载器、profile 转译、自动故障转移、热更新、按角色或 Provider 分配 Home，也不增加持久化配置指纹。

后续用户明确提出日常换新 session 的需求，并选择离线 operator 入口；对应的[解绑与新会话设计](codex-session-reset-design.md)提供独立小切片。它让采用新 Home/Provider 无须以迁移旧 Codex 会话为前提，保留 Galatea 自己的队列和回信。

## 2. 需求账本与证据边界

| 编号 | 要保留的行为或约束 | 来源与强度 |
|:--|:--|:--|
| R1 | Galatea 的 Codex 配置可独立于个人交互式 Codex；订阅额度耗尽后由操作者手动切换其他 Provider | 当前用户直接需求 |
| R2 | 本轮交付设计文档并进行 dialectical simplification；不实施代码或真实状态迁移 | 当前用户直接授权范围 |
| R3 | 采用固定专用 Home 为设计基线；具体目录与已有实例迁移方式尚未选择 | 用户接受前轮推荐；目录和迁移不是已批准操作 |
| R4 | 未提供的原生 thread 配置不补默认值；已有显式 `codexConfig` 继续透传 | 当前配置读取、启动代码与测试，见 E2、E3 |
| R5 | 已派发或派发结果不明的工作不因换配置而重发；thread/turn/dispatch 身份与既有有限恢复规则保留 | 当前 durable backend 与恢复实现，见 E4 |
| R6 | 个人自用、未发布，无下游兼容包袱；存在本机配置和持久委派状态 | 仓库 AGENTS.md；closed V4 reader、delegation store 为实际消费者 |
| R7 | 多 Character 经既有 sidecar 链调用 Codex；进程可退出、重启，外部工作可有不确定结果 | 当前进程生命周期与持久绑定实现；不假设新增分布式调度 |

证据依次采用用户决定、当前代码/测试、具体失败轨迹、相邻文档。相邻文档与本设计不得互相充当独立实现证据。未读取真实 `auth.json`、`ark.config.toml` 或 live delegation store，因此不声称已确认当前实例的认证方式、线程数量或可迁移性。

## 3. 已核实事实与矛盾

| 入口 | 当前事实 |
|:--|:--|
| E1：[pinned-version.ts](../../local-codex-mcp/src/codex/pinned-version.ts) | pin 为 `0.154.0-alpha.3`。前轮临时环境实测 `codex app-server --profile probe` 报未知参数；`codex --profile probe app-server` 报 profile 不适用于该子命令。不能由 TUI 支持 profile 推导 app-server 支持 |
| E2：[GalateaDelegateConfigReader](../../prototypes/Galatea/GalateaDelegateConfigReader.cs)、[GalateaSidecarProcess](../../prototypes/Galatea/GalateaSidecarProcess.cs) | delegates 为 closed V4；尚无 `codexHome`。C# 固定 app-server argv，并保留父环境 `HOME` / `CODEX_HOME` |
| E3：[sidecar-config.ts](../../local-codex-mcp/src/galatea/sidecar-config.ts)、[配置测试](../../tests/Galatea.Server.Tests/GalateaDelegateConfigTests.cs) | Node 清除父会话标识，保留 `CODEX_HOME`；空 `codexConfig` 不生成 thread override，显式值保留 |
| E4：[backend.ts](../../local-codex-mcp/src/codex/backend.ts)、[GalateaDelegationSqliteStore.Snapshot](../../prototypes/Galatea/GalateaDelegationSqliteStore.Snapshot.cs) | `ensureBinding` 创建 durable thread；后续按 ID 读取、恢复和核对 ownership。恢复不是按 Provider 重新发原任务 |
| E5：[GetAccountResponse](../../local-codex-mcp/schemas/v2/GetAccountResponse.ts)、`CodexBackend.ensureReady` | 返回 `account` 与 `requiresOpenaiAuth` 两个字段，当前 gate 只判断 account 非空。前轮 pinned 隔离探针确认第三方配置返回 `account: null, requiresOpenaiAuth: false`，会被当前 bridge 错挡 |
| E6：[配置参考](configuration.md)、[原生配置 canary](../../local-codex-mcp/scripts/provider-free-config-canary.mjs) | delegates 需重启；warm resume 不能当成配置热更新。现有 canary 证明部分配置继承与恢复，不证明跨 Provider 的历史兼容 |
| E7：本轮 pinned 隔离探针 | 临时环境设 `CODEX_HOME=H`、`CODEX_SQLITE_HOME=S`，只调用 `initialize` / `config/read`：H 无 SQLite，S 生成 `state_5.sqlite` 等数据库，但 `config/read.config.sqlite_home` 返回 null。配置返回中的 null 不能排除环境覆盖。未接入 driver、真实凭据或模型，进程和临时目录已清理 |
| E8：[有限恢复实现](../../prototypes/Galatea/GalateaDelegationSqliteStore.Recovery.cs)、[纵向测试](../../tests/Galatea.Server.Tests/GalateaBoundedRecoveryVerticalTests.cs) | `ColdEmptyThread_NotDispatched_RebindsSameMail_AndExecutesOnlyOnce` 证明未派发任务可以安全重绑；不明工作恢复耗尽后可终结并释放绑定，让后续独立任务推进 |

官方文档描述 profile 为先加载 `config.toml` 再叠加 `<name>.config.toml`，不是完全替换；这是官方当前语义，不能代替 E1 对项目 pin 的验证。参考：[profiles 与状态位置](https://learn.chatgpt.com/docs/config-file/config-advanced)、[认证与 env_key](https://learn.chatgpt.com/docs/auth)。资料核对日期：2026-09-23。

因此，共用 Home + profile 适合“继承个人环境、仅覆盖少数设置”，专用 Home 适合 R1 的配置独立和长期运维归属；当前 pin 又使后者可以直接复用受支持机制。未来即使 app-server 支持 profile，也只需在固定 Home 内评估它，不必改变线程存储归属。

## 4. 配置与进程合同

### 4.1 一个必填字段

选定设计将 machine-local `delegates.json` 升为 closed V5，在 `sidecar` 增加必填 `codexHome`。这是配置文件版本，不是 delegation SQLite V5，也不改变 sidecar wire。

现有代码已经支持从 Galatea 启动环境继承固定 `CODEX_HOME`，所以仅固定服务环境也是可行的更少代码方案，仍需修正认证 gate。选择显式字段是本设计的取舍，不是用户需求的唯一实现：实例已在 delegates 中集中选择 Node、entrypoint 与 Codex executable，把 Home 放在同一处可以校验、只影响子进程，并避免不同启动入口遗漏环境变量。代价是一个字段和一次配置升级。

以下仅展示新增字段，不是可直接运行的完整配置：

```json
{
  "v": 5,
  "sidecar": {
    "codexHome": "/srv/galatea/dev/codex-home"
  }
}
```

- 使用现有 `RequireCanonicalDirectory` 规则：预先存在的 Linux absolute canonical directory；不接受相对路径、`~`、空值或 symlink 祖先。不新增 Home 专用路径校验框架。
- 一个 delegates 配置选一个 Home，覆盖该实例各 Character 的 Codex 子进程；它不同于 Character 的任务 `homeDir` / CWD。
- 不提供缺省回退到个人 Home，缺失字段在启动配置校验阶段报错。新 placeholder 模板提示操作者先准备目录。
- 不自动创建、复制或同步配置、凭据、skills、plugins、会话。`codexHome` 是部署位置，不加入邮件、Journal 或 thread identity。
- V4 由操作者显式加字段并改版本；不增加双版本 reader。为分开配置升级和数据迁移，可暂时显式填原先使用的 Home，此时不宣称已经隔离。

### 4.2 只在拥有子进程的 C# 层选择 Home

```text
validated delegates.sidecar.codexHome
  -> ProcessStartInfo.Environment[CODEX_HOME]
  -> Node sidecar inherited environment
  -> Codex app-server inherited environment
  -> Codex native config loader
```

C# 在 `ConfigureSidecarEnvironment` 覆盖子环境的 `CODEX_HOME`，保留 `HOME`、PATH、provider key/proxy 等既有环境；不修改父进程的全局环境。Node 沿用现有透传，不增加 `GALATEA_CODEX_HOME` 别名或第二次配置解析。进程 CWD 仍由既有启动规则决定，不改成 Codex Home。

专用 Home 不是 OS 沙盒，也不隔离所有原生配置来源。项目级配置、系统策略、显式路径和环境变量仍遵循 Codex 规则。C# 不自动清除 `CODEX_SQLITE_HOME`，也不覆盖原生 `sqlite_home`。否则已有 H + SQLite S 的实例即使继续显式填写 H，软件升级也可能把状态位置改到 H，破坏“先升级软件、暂缓迁移”的操作边界。

若部署要求状态也独立，操作者核查有效子环境中的 `CODEX_SQLITE_HOME`、原生 `sqlite_home` 与实际数据库位置，再显式调整启动环境或配置；位置改变按迁移处理。E7 已证明 `config/read` 中 `sqlite_home: null` 不能单独作为落盘位置证据。若数据库仍共享，备份和停 writer 的范围必须包含实际数据库及其全部相关进程，不能只看 Home 目录。应用不新增 SQLite 字段、隔离检查器或迁移逻辑。

### 4.3 保留原生配置权威

Provider、model、API 地址和认证声明放专用 Home 的 `config.toml`。第三方密钥由 Provider 的 `env_key` 引用子进程可见的环境变量；ChatGPT 登录在同一 Home 下显式完成。不要建立个人 Home 与专用 Home 的凭据自动同步机制。

现有 `routes[0].codexConfig` 的显式覆盖保持原语义。为了让人工切换由一处配置决定，操作配置中不要再用它重复指定 Provider/model；已有冲突需在部署时检查。Galatea 不复制 Codex 的完整配置 schema，也不解析 TOML 来验证 Provider。专用 Home 所需的 skills、MCP、plugins、instructions 由操作者准备，不承诺自动继承个人 Home 的全部资源。

### 4.4 按协议含义判断认证

`ensureReady` 采用：

```text
if response.requiresOpenaiAuth && response.account == null:
    CODEX_NOT_AUTHENTICATED
else:
    continue existing flow
```

`requiresOpenaiAuth == false` 仅表示不要求 OpenAI 登录，不等于 API key 有效、额度足够或目标 Provider 支持所需能力。缺失密钥、401、429 等仍由 Codex 返回，沿用现有错误和恢复边界。不新增 Provider 白名单或以 `env_key` 是否存在来伪造认证成功。

保留现有 readiness 缓存及进程退出/stop 时重置的生命周期，不增加认证状态机。未登录错误提示改为“在配置的 `CODEX_HOME` 下运行 `codex login`”，避免裸命令把操作者引向个人 Home；错误文本不输出具体路径或凭据。

## 5. 手动切换与恢复

### 5.1 在同一 Home 内切换 Provider

如果希望新 Provider/model/toolset 从新 session 开始，日常推荐停服后执行[解除绑定流程](codex-session-reset-design.md#6-配合-home--provider-切换)，再换配置；无须试着恢复旧 thread。以下步骤适用于选择继续旧 thread 的情况：

1. 停止接纳新的自主/人工委派；核查已有任务并进入维护窗口。先让可结算任务结算；额度耗尽而不能结算时，保留已有持久状态后停服，不要求先成功完成才能维护。存在 Accepted/Unknown 时不能把换 Provider 当作重试授权。
2. 停止 Galatea 与其 sidecar/app-server，确认旧进程已退出。修改该 Home 的 `config.toml` 中 Provider、model 及配套设置，或用操作者保存的完整配置副本替换它。应用不实现副本管理或切换 API。
3. 保持 Galatea driver 停止，用相同 executable、环境及配置启动独立 app-server 探针，核对配置、`account/read` 与已知 thread 的只读可见性。这里的“只读”指不派发任务、不修改 Galatea store；app-server 本身仍可能维护自己的日志/SQLite，不能当成文件系统零写入检查。新 Provider 的调用能力用隔离任务验证，历史兼容性用隔离副本/专门 canary 验证，不借真实未知任务试错。
4. 停止探针进程后再恢复 Galatea；此后恢复驱动和持久预算正常运行。只停止新 ingress 并不停止 recovery：错误配置下重启 driver 会经 `RecordMailPollMiss` 消耗已有失败预算，达到总上限 8 次可能产生 `RESULT_UNCONFIRMED` 并释放绑定。不要通过反复生产重启尝试配置；不新增 pause/preflight API 或预算豁免。

配置切换只对新进程明确生效，不许诺正在运行的 turn 改用新 Provider。旧 thread 能否在目标 Provider 继续，取决于 Codex 恢复规则、历史内容和目标协议能力；“能创建新线程”与“能继续旧线程”必须分开验收。

切换配置不新增重试授权：`MayHaveDispatched`、`OutcomeUnknown`、`Accepted` 及已经 `RESULT_UNCONFIRMED` 的原任务不得因切换而重发。受控 `NotDispatched` 的有限重入队、失效空线程重绑，以及有限恢复结算后清绑定并推进后续独立任务，继续遵循 E8 的现有规则。不新增“线程永不变”或“永久等待修好 Provider”的约束。

### 5.2 首次采用专用 Home

新实例或尚无绑定线程时，准备专用 Home 即可。已有实例改到空 Home 后，Galatea store 的绑定仍指向旧 ID，新 app-server 却读不到对应 Codex 状态；现有有限恢复可能最终结束不明任务或重建失效绑定，这不是成功迁移。

根据后续日常换新 session 需求，推荐通过[离线解除绑定](codex-session-reset-design.md)采用新上下文：保留原 delegation-state，处理或显式结束活动邮件，只清未来发信绑定，再指向新 Home；旧 Codex 数据留在原处，不要求复制它。这是更换绑定的正常操作，不声称迁移了旧会话。

只有选择延续旧 Codex 历史时，才需要独立迁移工作：盘点实际 Home/SQLite 位置、所有相关 writer 和全部 Character 的绑定，验证离线迁移。本设计不编造选择性复制 rollout/SQLite 的通用算法，不把整个个人 Home 的盲目复制当作方案。也可在新配置中暂时显式指向旧 Home，先验证软件改动。

人工取证与恢复继续使用[现有 operator 边界](codex-delegation-operator-recovery.md)。备份、迁移、重新绑定和恢复旧备份都不由配置升级隐式执行；具体目标与授权留到真实部署时确定。

### 5.3 必须保留的失败轨迹

| 轨迹 | 最小防线 |
|:--|:--|
| 个人配置改了 Provider；Galatea 重启意外跟随 | 固定显式 Home；缺字段不回退 |
| 第三方 Provider 不要求 OpenAI 登录，但 account 为空 | 依据 `requiresOpenaiAuth` 判断，保留真正缺登录时的拒绝 |
| Accepted 已执行；切换后查不到历史；再次发送原任务 | 原任务继续按既有 no-replay 与有限恢复规则处理，切换不产生重发授权 |
| Home A 改为 B；Galatea 绑定还在 A | Home 稳定；首次迁移单独取证，空目录不算迁移 |
| 配置 model 已改，但 warm thread 仍用旧设置 | 停服改配置、冷启动；检查实际 thread 响应与调用结果 |
| 子环境仍有外部 SQLite 路径 | 部署核查并显式处置；软件不通过清变量隐式重定位 |
| 错误配置下反复重启 driver 做探针 | driver 停止时先独立核验，避免无意消耗已有恢复预算 |

## 6. 最小实现切片与验收

一个纵向切片完成字段、子环境和认证 gate，然后用隔离实例验证。修改入口限于 `GalateaDelegateConfig`、reader/template、`GalateaSidecarProcess`、`CodexBackend.ensureReady`、相应测试及配置文档；不更改 delegation store 格式、业务状态机或 sidecar 协议。无需升级 Codex pin。

### 6.1 软件切片验收

| 验收项 | 方法与成功证据 |
|:--|:--|
| V5 字段与路径合同 | 扩展 [GalateaDelegateConfigTests](../../tests/Galatea.Server.Tests/GalateaDelegateConfigTests.cs)：缺失/非法路径/V4 拒绝；模板及 programmatic Validate 一致 |
| 环境选择与隔离范围 | C# 子环境使用配置 Home，父环境不变，HOME/key/proxy 及 SQLite 环境覆盖保留；Node 仍透传 |
| 认证三种状态 | fake server 覆盖 required+null 拒绝、required+account 通过、not-required+null 通过；覆盖新建与恢复入口 |
| 配置来源 | 用临时个人 Home 与专用 Home 放不同哨兵值，经生产启动链及 pinned `config/read` / thread 响应证明读取专用配置；不使用真实 auth/config |
| 持久线程与重启 | 临时 Home 中建立可持久化线程并冷重启；同一 thread ID 可读、ownership 不变；切到另一空 Home 不伪造恢复成功 |
| 手动换配置后的调用 | 本地假 Provider 验证冷启动后的请求目的地及新/旧线程实际配置；这不构成真实 Provider 兼容性证明 |
| 恢复语义回归 | 现有测试覆盖 Accepted/Unknown 不重放、NotDispatched 可有限重试重绑、预算耗尽可终结并让后续任务推进 |

测试入口：[C# 配置测试](../../tests/Galatea.Server.Tests/GalateaDelegateConfigTests.cs)、[backend 测试](../../local-codex-mcp/tests/backend.test.ts)、[Galatea backend 测试](../../local-codex-mcp/tests/galatea-staged-backend.test.ts)、[原生配置 canary](../../local-codex-mcp/scripts/provider-free-config-canary.mjs)、[有限恢复纵向测试](../../tests/Galatea.Server.Tests/GalateaBoundedRecoveryVerticalTests.cs)。实现阶段按变更运行相关检查。

### 6.2 首次部署或实际 Provider 切换验收

| 适用条件 | 所需证据 |
|:--|:--|
| 准备真实实例 | 所选 Home、有效 SQLite 位置、认证及所需资源已核对；共享状态的 writer 和备份边界明确 |
| 新 Provider | 实际目标的新任务 canary 成功；仅 `account/read` 通过不能证明 key、额度或模型能力 |
| 要继续已有历史 | 在独立验证环境证明旧 thread 冷恢复及实际调用兼容；失败是部署限制，不自动扩展为 bridge 历史转译工程 |
| 首次采用新 Home | 选择新上下文时验证离线解绑后新邮件新建thread且旧队列/回信保留；只有选择搬迁旧Codex历史时才要求迁移和独立重开证据 |

软件切片完成不证明 Ark 或任何目标 Provider 的实际兼容性，也不证明真实状态已搬迁。本表是对应部署的准入证据，不是文档或软件切片完成的前置条件；本轮未执行这些真实操作。

## 7. 辩证简化裁决

2026-09-23，需求怀疑者、最小架构师、语义守护者分别阅读同一初稿与 R1–R7，先独立提出论点，再交换对方的最强失败轨迹。主线程核对代码、纵向测试并补做 E7 探针，裁决依据是可复现行为而非票数。第二轮已无剩余架构分歧，未进行第三轮。

| 裁决 | 项目 | 依据与最小机制 |
|:--|:--|:--|
| keep | 单一显式 Home、closed V5、复用路径校验 | 与既有 delegates 部署入口一致；缺字段报错，避免回退；不引入双版本 reader |
| keep | 原生配置透传、两字段认证 gate | 第三方 account 可为空；真正需要 OpenAI 登录时仍明确拒绝；不新增认证状态 |
| defer | 自动清理 SQLite 覆盖、通用迁移器 | E7 证明清理可能改变状态位置；当前由部署显式处理，只有另行提出完整状态隔离/自动迁移需求才重评 |
| delete | 无条件“不得重绑、线程永不变”的表述 | 与 E8 的受控 NotDispatched 和预算耗尽行为冲突；引用现有恢复权威即可 |
| simplify | 维护窗口与验收分工 | 独立探针先于恢复 driver；软件证明注入和协议，真实 canary 证明对应部署可用 |
| defer | profile、热切换、自动 Provider fallback、Home 指纹 | 当前无必要消费者；仅在 pin 支持且用户提出相应产品需求时重评 |

明确修正过的立场：需求怀疑者撤回“显式字段是需求唯一解”，承认固定启动环境已可行；最小架构师撤回对无条件保持线程的认同；各方在 E7 后将 SQLite 覆盖从待核实机制改为已证实的部署风险，但均未据此扩展应用权限。

相对初稿，删除一条隐式环境清理规则，收窄一条恢复约束，并分开两类验收的完成条件；新增运行时状态、API、业务持久化字段均为零。保留的新增应用配置只有 `sidecar.codexHome`。

上述两轮裁决记录针对基础 Home 方案。后续新增的日常解绑需求与独立审阅见[新会话设计](codex-session-reset-design.md)；现在为 Home/Provider 切换提供显式采用新上下文的正常路径，保留历史迁移为可选部署工作。目录、认证和资源准备仍需真实部署时确认，文档完成不代表已操作真实状态。


## 8. 软件实施与隔离验证（2026-09-23）

已实现 closed delegates V5 的必填 `sidecar.codexHome`、JSON/template/programmatic 一致路径校验、C# 子环境注入，以及 `requiresOpenaiAuth && account == null` 认证判定。未新增 Node 配置字段、thread override、持久化状态或配置兼容分支。配置升级操作见 [configuration](configuration.md)。

验证入口：

- `GalateaDelegateConfigTests`：字段、非法路径、V4 拒绝、父环境不变及原生覆盖保留。可通过 `ATELIA_CODEX_HOME_CANARY_REPO`（仓库绝对路径）和 `ATELIA_CODEX_HOME_CANARY_COMMAND`（pinned executable canonical path）启用 `PinnedConfigProbeThroughProductionStartEnvironment`：使用生产 `CreateStartInfo` 启动 Node 探针，复用 Node loader/环境透传并调用 pinned `config/read` 与 `thread/start`；个人/专用 Home 哨兵不同，只读取专用哨兵。该探针替换 Node entrypoint，不声称覆盖全部 durable wire；wire 由既有 sidecar 测试覆盖。
- `local-codex-mcp` 的 `npm test`：包括三种认证状态的新建与进程重启后 inspection，以及现有 Accepted/Unknown 不重发行为。
- `npm run canary:home`：临时 HOME/CODEX_HOME、本地 Responses SSE 假 Provider、合成 env_key。无需 auth.json 即可完成真实 pinned turn；同 Home 冷重启后可恢复已完成任务并核对 ownership；空 Home 查不到旧 thread。
- `npm run canary:config`：原生部分配置覆盖、冷/热恢复和临时目录读写。
- `GalateaBoundedRecoveryVerticalTests`：既有有限恢复、NotDispatched 重绑与不明工作不重发。

本轮执行结果：C# 配置/有限恢复及已启用的 pinned 启动环境探针 41 项通过，配置校验/durable transport 120 项通过；Node 全套 137 项通过、2 项 live 测试未启用；两个 provider-free canary 均通过；文档检查 59 文件、0 诊断。

实测限制：Provider `first` 的旧 thread 在默认切到 `second` 并删除 `first` 定义后，`thread/resume` 仍要求 `first`，在派发前报 `Model provider first not found`，分类为 `not-dispatched`；它不会自动转换 Provider。新 thread 使用 `second` 的 model 和 localhost endpoint。canary 明确断言只有两个成功请求（旧配置一次、新线程一次），失败恢复没有发出请求。此证据加强了原设计中“新任务可用不代表旧历史可继续”的边界，不新增会话转换逻辑。

基础 Home 切片实施时未实现日常解绑；后续已按 [独立设计](codex-session-reset-design.md)交付离线命令，实际操作记录见该文第 9 节。真实 Home 切换、Provider 认证和历史迁移不由软件实现隐式执行，也未验证 Ark 的真实兼容性。

### 真实实例切换记录（2026-09-23，Asia/Singapore）

经用户显式授权，已升级 `prototypes/Galatea/.atelia/galatea/`：root V11→V12 使用官方 operator，唯一 maintenance route candidate 为 `gpt5-6-sol-codex` / concurrency 1 / timeout 900000 ms；delegates V4→V5，`sidecar.codexHome` 设为 `/galatea-homes/codex-home`。补入原已定义的 `gpt-6-astra-codex` selectable connection，使 gpt 的原默认选择通过严格校验。

新 Home 权限为 0700，配置与认证副本为 0600。当前 `config.toml` 从个人配置选取 model、reasoning、service tier、context window、sandbox、approval、web search 基础设置，使用 ChatGPT / `gpt-6-astra`；不复制个人 plugins、skills 或历史。另存 `chatgpt.config.toml` 与 `ark.config.toml`，并复制 Ark model catalog、改为专用 Home 内路径。日常停服、解绑和配置切换步骤保存在该 Home 的 `README.md`。

验证：当前 Debug build 零警告、零错误；真实配置 strict open 后两个角色均 `AlreadyUnbound`；使用生产 Node 子环境清理函数启动 pinned app-server，`config/read` 返回预期 model，`account/read` 返回 requiresOpenaiAuth=true、account 非空。当前环境无 `CODEX_SQLITE_HOME`，配置无显式 `sqlite_home`，SQLite 实际生成在专用 Home。真实 host 启动后 `/login` 返回 HTTP 200，随后正常停服（exit 0）。两个 delegation store 的全部表与本次备份完全一致、quick_check=ok，仍为 Unbound。没有发起真实模型调用，也没有将旧 Codex 历史迁入新 Home；本次不证明订阅额度或 Ark 上游调用可用。

完整实例备份位于状态目录之外的 `prototypes/Galatea/.atelia/galatea-upgrade-backup-20260922T184803Z/instance/`，同级 `verification.json` 与 `host-smoke.log` 保存核验结果。这些本机配置、凭据和备份均不入 Git。实例保持停服，后续按原启动方式启动即可。
