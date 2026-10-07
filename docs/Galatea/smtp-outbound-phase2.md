# 对外邮件第二期：宿主账号绑定与 SMTP 发送边界

历史定位：本文保留第二期的实现记录与离线验证证据，不定义当前配置合同，也不证明真实 SMTP 已联通。当前配置见 [V15 root-config 合同](../SessionJournal/current/contracts/galatea-root-config-v15.md)，当前实施与实测状态见[SMTP MVP 设计与实施方案](smtp-email-mvp-design-and-implementation.md)。下文旧配置形状和结果仅适用于所记录的历史切片。

初始基线：`127a0b1f9a18761f341e4266faa28c2b01595fec`，分支 `g01/smtp-outbound`。本期只开发与离线验证，不部署、不读取真实凭据、不连接外部 SMTP、不真实发送。第一期持久状态机与事务边界沿用；rebase 到主线 `c0a2bbfa` 后，数据库为 V7（主线 V6 保存冻结回执），不回填旧记录。

兼容边界：rebase 后的第一期、第二期均使用 V7；原分支 V6 与主线 V6 的结构不同，不能互换。主线 V6 可通过显式升级保留冻结回执并建立空 SMTP 表；原分支 V6 从未部署，本次不提供该实验格式的转换。本补充增加 `blocked:` 身份格式，C-022 二进制不能读取这种新行。第一期二进制的身份校验不能读取含 `smtp:` 引用的新行，旧严格配置读取器也不接受新增 SMTP 字段。不提供自动降级或生产回退工具。

## 实现选择

使用 .NET 10 `TcpClient`、`SslStream` 与取消令牌实现范围受限的 SMTP 客户端，不新增 NuGet 依赖。每封独立连接，顺序执行 EHLO、必需的 TLS、AUTH LOGIN、MAIL、单个 RCPT、DATA；不做连接池、PIPELINING、BDAT 或重试。自有协议边界可以在首次 DATA 正文写入之前记录“可能已发送”，不依赖异常消息猜测阶段。没有使用 `System.Net.Mail.SmtpClient`；其公开接口不能直接提供本设计需要的 DATA 写入边界，且[官方文档](https://learn.microsoft.com/en-us/dotnet/api/system.net.mail.smtpclient)不推荐用于新开发。

只支持窄语法 ASCII 单地址、AUTH LOGIN、隐式 TLS 或必需 STARTTLS。服务端必须广告 LOGIN；不支持 OAuth、SMTPUTF8、地址列表、附件、HTML、DSN 或复杂代理。STARTTLS 缺失即失败，绝不降级。正式路径使用系统信任与主机名校验、在线吊销检查，没有跳过证书验证的配置项。测试专用构造参数只允许字面量回环地址上的内存信任锚，未接入 DI 或 JSON 配置，不安装证书。

正文用 UTF-8 base64 编码，解码后保持宿主捕获字符串，包括 CRLF、空行、代码围栏、分隔线与末尾无换行；SMTP 传输 CRLF 不被当作正文归一化。主题与可选显示名使用按 Unicode 标量切分的 RFC 2047 encoded words。From 与 MAIL FROM 均来自宿主绑定，To 与 RCPT TO 来自捕获收件人；正文中的发件人叙述不参与配置。Message-ID 由逐封 dispatch 身份的 SHA-256 派生。正文不支持附件或内容类型自选。

## config.json 与严格读取

扩展 `runtime.smtp`，保留配置版本 v14。缺省字段意味着关闭真实发送，也关闭离线替身；默认 bootstrap 模板显式输出关闭块。离线替身必须显式设置 `offlineMode: true`，不可与 `enabled: true` 同时设置。严格读取仍拒绝未知字段、重复字段、错误类型；SMTP 配置还验证角色已配置、每角色至多一个绑定、ASCII 发件地址、绝对凭据路径、绑定 ID 与超时范围。

以下是合并进既有 config.json 的片段，所有值都是示例；角色 ID 必须替换为既有角色的精确 ID：

```json
{
  "runtime": {
    "smtp": {
      "enabled": false,
      "offlineMode": false,
      "timeoutSeconds": 60,
      "senderAccounts": [
        {
          "characterId": "alice",
          "bindingId": "account-v1",
          "fromAddress": "host@Example.test",
          "displayName": "宿主配置的显示名",
          "credentialPath": "/absolute/synthetic-or-owner-confirmed-account.json",
          "enabled": false
        }
      ]
    }
  }
}
```

全局与该角色绑定均启用才为新捕获选择 `smtp:<角色ID>:<bindingId>`。只有显式离线模式（`enabled: false, offlineMode: true`）才创建 `offline:<角色ID>` 待发记录。其余情形按下面的策略直接记录确定失败，不交给离线替身。displayName 缺省或 null 时仅用发件地址；它不能包含控制字符。绑定 ID 只允许 1–64 个 ASCII 字母、数字、连字符、下划线。凭据路径只检查配置语法，不在启动、捕获或离线路由时读文件。

文件 loader 冻结每角色的捕获引用，经 reconciler 作为宿主字段交给 capture；引用与 outbox 在原有捕获事务内写入。可写重开只验证身份格式，不读 SMTP 配置或凭据。已有捕获仍返回 AlreadyCaptured，不改引用、不补建路由。消费时必须匹配同一角色、同一绑定 ID 且全局与绑定仍启用；缺失、禁用或不同绑定均确定失败，不借用另一个账号，也不自动重发。

绑定 ID 必须标识同一个账号，不能重新分配给另一发件账号。当前数据库冻结的是绑定引用，不是完整账号配置：若管理员保留同一 ID 却改写地址、用户名或主机，程序无法独立发现这种账号重新解释。此为运维契约与未覆盖风险；更换账号应使用新绑定 ID，旧行不会自动转换。凭据轮换可保留账号身份。

## 凭据格式：已按刘世超提供的字段名确认

字段名与类型依据刘世超 2026-10-06 经 Galatea 转述的格式确认；没有读取或核验真实文件的内容和值。读取集中在 `GalateaNetworkSmtpSender.ReadCredentials`，以下仍是合成示例。

```json
{
  "v": 1,
  "provider": "synthetic-provider",
  "smtpHost": "127.0.0.1",
  "smtpPort": 2525,
  "tlsMode": "starttls",
  "username": "host@Example.test",
  "authorizationCode": "SYNTHETIC-FAKE-VALUE"
}
```

七个字段全部必需；`v` 必须是整数 1，其他版本或类型返回 `SMTP_CREDENTIAL_VERSION_UNSUPPORTED`。`provider` 只保留为读取结果中的内存元数据，不持久化、不记录原值、不参与主机、端口、TLS 或账号选择；限字符串、128 字符以内且不含控制字符。`smtpPort` 范围 1–65535。`tlsMode` 逐字接受 `implicit`（隐式 TLS）、`starttls`（必需升级）或 `none`，未知值返回 `SMTP_CREDENTIAL_TLS_MODE_UNSUPPORTED`。`none` 仅允许字面量回环 IP，供本机假服务器测试；域名即便可能解析为回环，也不能用明文。文件要求不超过 64 KiB、严格 JSON、无未知或重复字段；使用已有 Linux no-follow regular-file 读取，拒绝符号链接。缺字段、一般类型/范围错误、旧 C-022 格式、未知或重复字段、文件/符号链接错误统一为 `SMTP_CREDENTIALS_FAILED`。

凭据文件不含 `fromAddress`；发件地址仅来自 `runtime.smtp.senderAccounts[].fromAddress`。绑定地址与 `username` 都须是规范的窄语法 ASCII 单邮箱地址，此处不裁空格；按 ASCII 不区分大小写比较（代码使用两者均通过 ASCII 校验后的 OrdinalIgnoreCase）。不一致时在连接前返回 `DefiniteFailure / SENDER_ADDRESS_MISMATCH`，不含地址或凭据原值。AUTH 保留用户名原有大小写，MAIL FROM 与 MIME From 保留宿主绑定地址，不被凭据改写。本期不提供绕过一致性检查的开关；需要不同登录名或别名发件的服务商暂不支持。真实账号联通、文件内容与实际字段值仍未核验。

只有真正选中网络路径、开始发送且尚未取消时读取文件。账号值不进数据库或报告；传输层没有协议日志接口。文件/解析/认证异常与服务器回复正文均不外抛、不记录，只返回固定原因码；后台原有 content-free 日志仍不包含异常正文。读取字节缓冲在解析后清零；托管字符串的内存即时清零不作保证。

## 离线隔离与重放前提

配置路由器首先检查 `offline:` 前缀，只有显式离线模式下的合法离线身份才调用离线替身；其余模式记录确定失败；真实发送器入口另独立拒绝所有 `offline:` 引用，并在读取凭据或开 socket 前返回 `SMTP_OFFLINE_ISOLATED`。因此任何配置启用或账号绑定都不能把离线 Pending 重新解释为真实发送。没有离线转真实、补寄、状态复位或未知重试接口。

旧 Unrouted 不补建 outbox 的前提仍是持久来源身份不被替换：不删除/重建捕获数据库，不以新 Action 地址重放旧正文。迁移不回填、AlreadyCaptured 不重新解析；本期不迁移、不读取运行数据库。若重建持久身份或把旧文字作为新的本次寄信再提交，就超出了这项隔离的保证。

## 状态映射、超时与关闭

沿用 Pending → 已持久 Attempting → ProviderAccepted / DefiniteFailure / OutcomeUnknown。claim 不确定时不得调用发送器；结果事务提交不确定按第一期精确持久后状态确认。发送器调用前持久 Attempting 与重启恢复测试保持不变。

| 观察到的阶段与结果 | 状态 | 原因码示例 |
|---|---|---|
| 无精确启用绑定、凭据缺失/非法 | DefiniteFailure | SMTP_BINDING_UNAVAILABLE / SMTP_CREDENTIALS_FAILED |
| 连接未建立、TLS/问候/EHLO/认证/MAIL/RCPT/DATA 请求阶段失败，未写正文 | DefiniteFailure | SMTP_CONNECT_FAILED / SMTP_AUTH_REJECTED / SMTP_RCPT_REJECTED |
| 开始写 DATA 正文前超时或取消 | DefiniteFailure | SMTP_TIMEOUT_BEFORE_DATA / SMTP_CANCELLED_BEFORE_DATA |
| 已可能写正文，未收到明确最终回复：断线、协议错误、超时、取消 | OutcomeUnknown | SMTP_DATA_FAILED / SMTP_TIMEOUT_AFTER_DATA / SMTP_CANCELLED_AFTER_DATA |
| DATA 结束明确收到 250 | ProviderAccepted | SMTP_DATA_ACCEPTED |
| 各阶段明确 4xx/5xx 拒绝，包括 DATA 最终拒绝 | DefiniteFailure | SMTP_AUTH_REJECTED / SMTP_DATA_REJECTED |

DATA 的 250 只表示服务商受理，不是最终送达；明确拒绝回复与未收到回复分开处理，依据 [RFC 5321](https://datatracker.ietf.org/doc/html/rfc5321#section-4.2.5)。4xx 表示本次事务明确未受理，因此本期也落确定失败，但不把它宣称为地址永远无效、不自动重试。正文写入前设置未知边界，包含部分写入、已写完但最终回复未收到的情况。已观察到最终成功后，关闭连接失败不能改判为未知；不依赖 QUIT 成功来确定受理。

这里的 DATA 风险边界指正文可能写出，不是 DATA 命令已写出：尚未收到 354、未开始正文写入时，该连接不能受理完整邮件，断开归确定失败。测试中的 before-data-close 正是在 DATA 命令之后、354 之前关闭；after-data-close 在完整正文之后、最终回复之前关闭。

默认整体取消预算为 60 秒，配置范围 1–300 秒，自 SendAsync 开始计时。连接、TLS、读回复、写正文均传递同一个关联取消令牌，不为每个阶段重置预算。取消关闭本次连接，不启动新尝试。凭据读取复用同步、大小受限的 regular-file 方法，不能强行取消阻塞的底层文件读取；返回后网络操作再次检查令牌，这是预算的实际限制。

服务关闭取消消费者并等待排空：能返回时按上述 DATA 边界落终态；若进程在结果持久前中断，行留为 Attempting，可写重开转 OutcomeUnknown / PROCESS_RESTART。Unknown 与其他终态均不再被消费。不开普通 sweep 恢复尝试中，以免误伤活跃发送。

## 离线验证入口

- `GalateaNetworkSmtpTests`：127.0.0.1 假服务器；受理、认证/收件人拒绝、4xx、DATA 前后断开、前后超时/取消、格式错误、TLS 与 STARTTLS、默认拒绝未信任证书、缺凭据/错误格式/符号链接、明文外部地址在连接前拒绝、离线双重隔离、绑定缺失/禁用/不匹配。TLS 信任只在进程内，不安装证书。
- `GalateaRootConfigFieldLanguageTests.Smtp*`：严格字段、重复字段、类型/范围及角色/路径/地址语义、无需读凭据的 loader、默认关闭模板。
- `GalateaSmtpOutboundTests` 新增：捕获引用固定、AlreadyCaptured 不重新绑定；真实协议替身消费后持久状态与重开不重发；旧离线 Pending 在启用真实绑定后仍不进入网络。其余第一期迁移/原子性/调用前持久/COMMIT 故障测试继续运行。
- 模型提取仍用确定性工具调用测试宿主，不证明模型自主抽取正确。此处不读取运行数据库或私人数据。

实际用例结果、逐例名称与命令/耗时记录在本轮 `.artifacts/c022/`；全套服务器测试不在本期要求内。邮件相关回归沿用 C-019 筛选并排除五类硬编码 `/dev/shm` 的 Recap 夹具。真实服务商、实际凭据字段、最终送达、生产切换与真实发送尚未验证。

## C-023：显式模式与缺少绑定时的确定失败

本补充基于 `1e0cff0f6763f0756c715848c627d25bcb1db2e3`。只按宿主确认的 `characterId` 精确匹配，使用 Ordinal 语义；角色显示名、家目录名、邮件正文均不参与绑定选择。实际角色 ID 由宿主确认，例如 `cyber`、`gpt`；这些例子不指定任何发件账号。所有示例、测试路径均为假路径，开发期测试凭据不默认属于任何角色，也不默认成为长期身份。

### 捕获策略与可见性

| 配置与角色绑定 | 新捕获 SMTP outbox 结果 | 固定原因码 |
| --- | --- | --- |
| `enabled: true`，该角色有启用的绑定 | `Pending`，冻结 `smtp:<角色ID>:<bindingId>` | 无 |
| `enabled: true`，该角色无绑定 | `DefiniteFailure` | `NO_SENDER_BINDING` |
| `enabled: true`，该角色绑定已禁用 | `DefiniteFailure` | `SENDER_BINDING_DISABLED` |
| `enabled: false, offlineMode: false`（缺省） | `DefiniteFailure` | `SMTP_DISABLED` |
| `enabled: false, offlineMode: true`（显式测试模式） | `Pending`，冻结 `offline:<角色ID>` | 由替身返回 |

拒绝使用 `blocked:<角色ID>:<原因码>` 引用，不含账号路径或凭据。失败行与 capture、outbound_mail 在同一事务中创建，revision 初始为 0；读取时校验 blocked 身份只能对应同一原因码的确定失败终态。不会进入消费循环；重启、之后启用绑定、旧 Action 再协调均不补发。没有跨角色或共享账号回退。缺少引用的旧调用入口也默认确定失败，不能隐式开启离线模式。

**可观察范围：宿主可见，角色目前没有自动 SMTP 回执。** 宿主可在每角色 delegation 存储的 `smtp_mail_outbox` 按 dispatch 查询 `state`、`result_code`、`from_character_id`，并关联 `outbound_mail` 的来源 Action 和 artifact ordinal。`outbound_mail.state` 仍为路由层的 `Unrouted`，不能拿它替代 SMTP 结果。以下是只读查询示意（开发验证只使用合成数据库）：

```sql
SELECT m.source_action_address, m.artifact_ordinal, s.dispatch_id,
       s.from_character_id, s.state, s.result_code
FROM smtp_mail_outbox s JOIN outbound_mail m ON m.dispatch_id = s.dispatch_id;
```

本补充没有把结果注入角色 Observation、邮件或 Note，没有实现角色界面的自动失败提示。因此角色不能仅凭自己的叙事确认是否外发成功；自动回执仍是未覆盖项。选择捕获时记录确定失败，是为了消除静默替身消费并保证逐封失败持久可查；这不等于已完成角色反馈通路。

### 合法配置示例

无绑定：`{"enabled":true,"offlineMode":false,"senderAccounts":[]}`。全局关闭：`{"enabled":false,"offlineMode":false,"senderAccounts":[]}`。显式离线测试：`{"enabled":false,"offlineMode":true,"senderAccounts":[]}`。省略 `offlineMode` 等价于 false；两个模式同时为 true 被严格配置加载拒绝。

已禁用绑定仍使用合法、非空的绝对路径字段，文件无需存在，加载和拒绝捕获均不读文件。无需禁用绑定时直接省略该角色条目；不使用空路径作为开关。

```json
{
  "enabled": true,
  "offlineMode": false,
  "senderAccounts": [{
    "characterId": "alice",
    "bindingId": "synthetic-v1",
    "fromAddress": "host@Example.test",
    "credentialPath": "/absolute/fake-accounts/alice.json",
    "enabled": false
  }]
}
```

### 离线行隔离与配置切换

已有 `offline:` 行永远不交给网络发送器。只有显式离线模式允许替身消费；关闭或启用真实发送时遇到残留离线 Pending，消费结果为 `DefiniteFailure / SMTP_OFFLINE_ISOLATED`，不读取凭据、不连网。其后切回离线模式也不复活该终态。已有真实 Pending 若绑定被删除、禁用或改变 ID，沿用 `SMTP_BINDING_UNAVAILABLE` 的确定失败，不借其他账号。

### 补充合成测试

- `HostPolicyRejection_IsPersistedAtCaptureAndNeverRebound`：四例覆盖无绑定、绑定禁用、只绑定另一角色、全局关闭；捕获即确定失败、重开与 AlreadyCaptured 不改写、消费者不调用任何发送器。
- `ExplicitOfflineMode_CapturesAndConsumesWithOfflineSender`：只有显式模式才按第一期替身路径消费。
- `SmtpModes_StrictLoaderFreezesExactRolePolicy`：四例通过严格 loader 冻结上述策略，不读假凭据文件。
- `SmtpBindings_RejectInvalidStrictOrSemanticPolicy`：增加模式冲突、错误字段类型、禁用条目空路径拒绝。
- `DefaultHost_RejectsEmailWithoutExplicitSendingMode`：默认宿主捕获失败为 `SMTP_DISABLED`。
- `OfflinePending_WithNewEnabledBindingNeverBecomesNetworkMail` 与 `OfflineReferences_NeverEnterInjectedNetworkOrReadCredentials`：更新为显式离线路由，真实模式拒绝历史离线行，网络调用计数为零。
- `UnavailableRole_NeverFallsBackToAnySender`：四例验证无绑定、禁用、另一角色、大小写不匹配均不调用网络或离线替身，不读假凭据。
- `RealReference_RequiresExactEnabledHostBinding`：沿用错角色、缺少绑定、禁用与变更引用的直接发送边界验证。

模型提取仍用确定性工具输出验证宿主，不证明模型的授权或内容判断。无需读取真实数据库、真实凭据或连接外部 SMTP。

## C-024：确认凭据字段与回归范围

基线 `d3b6c2531af8da654579f136216f077709fb08a1`。字段确认只依据 Galatea 转述的刘世超说明，未读取真实凭据。`CredentialsV1_AcceptsProviderMetadataAndCaseInsensitiveLogin`、`CredentialsV1_MissingRequiredFieldFailsBeforeConnect`、`CredentialsV1_InvalidSchemaFailsWithoutConnectingOrLeaking` 使用临时目录中的合成文件和 127.0.0.1 假服务器；既有重复字段、符号链接、地址不一致测试同步改用 V1 格式。

沿用 C-019 邮件过滤条件并排除五类旧 Recap 夹具。C-022 的 408 例在 C-023 后扩为 413 例（新增五例，默认宿主测试改名）；逐例映射中保留改名关系，不将其计为丢失。Debug 中跳过的 Release 专属例另构建 Release 后运行。执行结果、完整命令与逐例对照放在本工作树的忽略目录 `.artifacts/c024/`。

## 2026-10-06：rebase 到冻结回执主线

从已暂停的 rebase 继续，将原分支七个提交重放到 fetch 后的 `origin/main`：`c0a2bbfa5ef9da4b18835dcf871422004a9c868d`。保留原 SMTP 功能、配置与保守发送边界，解决七个冲突文件。

两个分支原先都占用 Delegation store V6。现保留主线 V6 的 `mail_receipt_delivery`，SMTP 表顺延到 V7；显式升级链为 V1–V5 → 主线 V6 → V7，以及主线 V6 → V7。升级不回填 SMTP，不重写已有回执与 Observation 绑定。原分支实验 V6 不属于该升级入口。

新 email 捕获在同一事务内写入 SMTP outbox 和冻结短回执。回执 `accepted` 表示已接纳到该流程，不表示服务商接收或最终送达；后续 SMTP 失败不改写原回执。独立状态通知尚未接入 Observation。

新增三例 V6 升级测试，覆盖 Pending / ObservationBound / Delivered 的回执及绑定保留、dry-run 不改变数据库字节、SMTP 表为空；新增一例长正文 email 短回执及后续 SMTP 失败测试。

首次全集回归为 1674 通过、3 失败、1 跳过；三项失败均来自新增 V6 升级测试，定位到 SMTP 表字段校验仍使用 `expectedVersion >= 6`，已修正为 `>= 7`。修正后新增四例单独复验全部通过。

首轮 SMTP、解析器、Delegation 迁移与语义、Mail/Action receipt 回归：278 通过、0 失败。新增测试纳入随后完整 Galatea 非 Live 测试集，执行命令如下：

```bash
env -u ATELIA_RUN_GALATEA_NOTE_LIVE \
    -u ATELIA_RUN_GALATEA_LAB_LIVE \
    -u ATELIA_RUN_GALATEA_CODEX_DELEGATION_LIVE \
  dotnet test tests/Galatea.Server.Tests/Galatea.Server.Tests.csproj \
    --no-restore -m:1 -nr:false \
    --filter 'FullyQualifiedName!~CharacterNoteTranscriptionLiveTests&FullyQualifiedName!~GalateaCodexDelegationLiveTests&FullyQualifiedName!~GalateaScenarioLabLiveTests' \
    --verbosity minimal -- xUnit.MaxParallelThreads=4
```

最终以相同过滤条件、额外 `--no-build` 复跑：**1677 通过、0 失败、1 跳过，共 1678 项**，耗时 2 分 20 秒。跳过项为既有 Release 专属 `ReleaseCapture_PersistsWithoutDiagnostic`；本轮未另跑 Release。`git diff --check` 通过；文档检查仍为 66 文件、4 处主线既有旧链接诊断，无新增诊断。

本轮仅合并代码与离线验证，未迁移实例、读取真实凭据、连接外部 SMTP、重启服务或 push。
