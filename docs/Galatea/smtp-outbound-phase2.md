# 对外邮件第二期：宿主账号绑定与 SMTP 发送边界

基线：`127a0b1f9a18761f341e4266faa28c2b01595fec`，分支 `g01/smtp-outbound`。本期只开发与离线验证，不部署、不读取真实凭据、不连接外部 SMTP、不真实发送。第一期持久状态机与事务边界沿用；数据库仍为 V6，不回填旧记录。

兼容边界：新代码可以读取第一期 V6 离线行；第一期二进制的身份校验不能读取含 `smtp:` 引用的新行，旧严格配置读取器也不接受新增 SMTP 字段。第一期未部署，本期不需要运行库迁移；不提供自动降级或生产回退工具。

## 实现选择

使用 .NET 10 `TcpClient`、`SslStream` 与取消令牌实现范围受限的 SMTP 客户端，不新增 NuGet 依赖。每封独立连接，顺序执行 EHLO、必需的 TLS、AUTH LOGIN、MAIL、单个 RCPT、DATA；不做连接池、PIPELINING、BDAT 或重试。自有协议边界可以在首次 DATA 正文写入之前记录“可能已发送”，不依赖异常消息猜测阶段。没有使用 `System.Net.Mail.SmtpClient`；其公开接口不能直接提供本设计需要的 DATA 写入边界，且[官方文档](https://learn.microsoft.com/en-us/dotnet/api/system.net.mail.smtpclient)不推荐用于新开发。

只支持窄语法 ASCII 单地址、AUTH LOGIN、隐式 TLS 或必需 STARTTLS。服务端必须广告 LOGIN；不支持 OAuth、SMTPUTF8、地址列表、附件、HTML、DSN 或复杂代理。STARTTLS 缺失即失败，绝不降级。正式路径使用系统信任与主机名校验、在线吊销检查，没有跳过证书验证的配置项。测试专用构造参数只允许字面量回环地址上的内存信任锚，未接入 DI 或 JSON 配置，不安装证书。

正文用 UTF-8 base64 编码，解码后保持宿主捕获字符串，包括 CRLF、空行、代码围栏、分隔线与末尾无换行；SMTP 传输 CRLF 不被当作正文归一化。主题与可选显示名使用按 Unicode 标量切分的 RFC 2047 encoded words。From 与 MAIL FROM 均来自宿主绑定，To 与 RCPT TO 来自捕获收件人；正文中的发件人叙述不参与配置。Message-ID 由逐封 dispatch 身份的 SHA-256 派生。正文不支持附件或内容类型自选。

## config.json 与严格读取

扩展 `runtime.smtp`，保留配置版本 v14。缺省字段意味着关闭；默认 bootstrap 模板显式输出关闭块。严格读取仍拒绝未知字段、重复字段、错误类型；SMTP 配置还验证角色已配置、每角色至多一个绑定、ASCII 发件地址、绝对凭据路径、绑定 ID 与超时范围。

以下是合并进既有 config.json 的片段，所有值都是示例；角色 ID 必须替换为既有角色的精确 ID：

```json
{
  "runtime": {
    "smtp": {
      "enabled": false,
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

全局与绑定均启用才为新捕获选择 `smtp:<角色ID>:<bindingId>`；否则新行固定为 `offline:<角色ID>`。displayName 缺省或 null 时仅用发件地址；它不能包含控制字符。绑定 ID 只允许 1–64 个 ASCII 字母、数字、连字符、下划线。凭据路径只检查配置语法，不在启动、捕获或离线路由时读文件。

文件 loader 冻结每角色的捕获引用，经 reconciler 作为宿主字段交给 capture；引用与 outbox 在原有捕获事务内写入。可写重开只验证身份格式，不读 SMTP 配置或凭据。已有捕获仍返回 AlreadyCaptured，不改引用、不补建路由。消费时必须匹配同一角色、同一绑定 ID且全局与绑定仍启用；缺失、禁用或不同绑定均确定失败，不借用另一个账号，也不自动重发。

绑定 ID 必须标识同一个账号，不能重新分配给另一发件账号。当前数据库冻结的是绑定引用，不是完整账号配置：若管理员保留同一 ID 却改写地址、用户名或主机，程序无法独立发现这种账号重新解释。此为运维契约与未覆盖风险；更换账号应使用新绑定 ID，旧行不会自动转换。凭据轮换可保留账号身份。

## 凭据格式：待刘世超对照实际文件确认字段名

以下是实现期望的格式，不是指定真实文件的字段调查；真实文件从未读取。字段映射集中在 `GalateaNetworkSmtpSender.ReadCredentials`，核对后可在这一处调整。

```json
{
  "host": "127.0.0.1",
  "port": 2525,
  "security": "starttls",
  "username": "synthetic-user",
  "authorizationCode": "SYNTHETIC-FAKE-VALUE",
  "fromAddress": "host@Example.test"
}
```

security 取 `tls`（隐式 TLS）、`starttls`（必需升级）或 `none`。`none` 仅允许字面量回环 IP，供本机假服务器测试；域名即便可能解析为回环，也不能用明文。port 范围 1–65535。fromAddress 必须与宿主绑定逐字一致；它不是凭据文件覆盖宿主发件身份的入口。文件要求不超过 64 KiB、严格 JSON、六个已知字段全部存在、无重复字段；使用已有 Linux no-follow regular-file 读取，拒绝符号链接。未来实际字段确认与真实账号联通验证均未执行。

只有真正选中网络路径、开始发送且尚未取消时读取文件。账号值不进数据库或报告；传输层没有协议日志接口。文件/解析/认证异常与服务器回复正文均不外抛、不记录，只返回固定原因码；后台原有 content-free 日志仍不包含异常正文。读取字节缓冲在解析后清零；托管字符串的内存即时清零不作保证。

## 离线隔离与重放前提

配置路由器首先检查 `offline:` 前缀，合法离线身份仅调用离线替身；真实发送器入口另独立拒绝所有 `offline:` 引用，并在读取凭据或开 socket 前返回 `SMTP_OFFLINE_ISOLATED`。因此任何配置启用或账号绑定都不能把离线 Pending 重新解释为真实发送。没有离线转真实、补寄、状态复位或未知重试接口。

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
