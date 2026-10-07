# Galatea root config V15（历史合同）

当前入口已升级为 [V16](galatea-root-config-v16.md)。本文保留 V15 发布前实施时的字段语境，不是当前 host 可接受的配置。

V15 已由 strict reader、loader 与 bootstrap 实施。SMTP 的离线验证与真实发件验收状态见 [SMTP MVP 设计与实施方案](../../../Galatea/smtp-email-mvp-design-and-implementation.md)；接受配置不等于真实账号已验收。

Galatea host 只接受精确整数 `"v": 15`。V14 的 Character、Player、路径、连接选项和 Runtime 其余字段沿用 [V14 历史合同](galatea-root-config-v14.md)及[配置指南](../../../Galatea/configuration.md)，本版本新增每角色可空 `email`，并收敛 `runtime.smtp` 为全局发送策略。未知、重复、缺失字段及非 canonical JSON 继续 fail closed；正常启动不自动迁移或重写旧配置。

## Character email

每个 Character 的 `email` 可省略、为 `null`，或为包含且仅包含以下五个字段的 object：

| 字段 | 类型与约束 |
|:--|:--|
| `address` | 窄语法 ASCII 单邮箱地址；解析结果必须与配置字面值一致，不自动裁空格或改大小写。 |
| `authorizationCode` | 非空且不含控制字符的明文邮箱授权码。不得投影到 Character context、Setup、Journal、日志或错误消息。 |
| `smtpHost` | 1..253 字符的 ASCII DNS 主机名或 IP 字面值；不含 scheme、路径、空白或控制字符。 |
| `smtpPort` | 整数 1..65535。 |
| `tlsMode` | 精确为 `implicit` 或 `starttls`；不接受 `none`。 |

同一 `address` 用作 SMTP AUTH 登录名、`MAIL FROM` 和 MIME `From`。不支持独立 username、发件别名、display name、provider、手写 binding ID、凭据路径或每角色启用开关。`null` 或省略表示该角色没有 SMTP 账号。

账号在进程启动时读取一次并作为只读快照使用；改动后需重启，不监视配置文件或逐封重读凭据。持久账号引用按以下固定 JSON 数组计算 SHA-256：

```text
lowercase_hex(SHA256(UTF8(JSON_ARRAY[ address, smtpHost, smtpPort, tlsMode ])))
```

数组顺序固定，使用校验后的配置字面值，不裁剪或改写。授权码不参与 hash，因此轮换授权码不改变账号身份；地址、host、port 或 TLS 模式改变会产生新引用，旧 Pending 不跟随新绑定发送，而以确定失败结束。

## Runtime SMTP

`runtime.smtp` 可省略或为 object；object 只接受以下字段：

| 字段 | 类型与约束 |
|:--|:--|
| `enabled` | 可省略，默认 `false`。唯一全局 SMTP 发送开关。 |
| `timeoutSeconds` | 可省略，默认 `60`，整数 1..300。 |

缺省 `runtime.smtp` 等价于发送关闭、超时 60 秒。该开关只控制 SMTP，不关闭 Codex 委派或角色站内信。关闭不是保留 Pending 的 queue pause：运行中的消费者仍可能领取 Pending 并将其记录为确定失败。需要保留旧队列供检查时，应停止宿主或使用 maintenance mode，再进行配置与只读检查。

## 持久数据边界

根配置版本独立于 Delegation schema。V15 不要求升级 Delegation V7；不删除或重建数据库，不回填旧邮件，也不把旧离线记录解释为真实外发。历史 `offline:` Pending 仍识别，并以 `SMTP_OFFLINE_ISOLATED` 确定失败；既有三个 blocked 原因 `SMTP_DISABLED`、`NO_SENDER_BINDING`、`SENDER_BINDING_DISABLED` 继续识别。旧 `smtp:` Pending 仅可匹配完全相同的派生账号引用，不重绑、不复位、不自动重试。

捕获时冻结的 `accepted` 短回执仅表示宿主已接纳邮件提交，不表示 SMTP 服务商受理。每封邮件的 SMTP 状态由持久 outbox 分别记录为服务商受理、确定失败或结果未知；结果未知不得自动重发。MVP 不包含自动 SMTP 结果通知或 IMAP 收信。真实收件需通过受控邮箱人工核验，代码或配置存在本身不构成真实 SMTP 验证。

## 示例

首次生成的示例建议不绑定邮箱、全局关闭 SMTP：

```json
{
  "v": 15,
  "characters": [
    { "id": "alice", "email": null }
  ],
  "runtime": {
    "smtp": {
      "enabled": false,
      "timeoutSeconds": 60
    }
  }
}
```

下例仅展示某个 Character 的可填写五字段形状，值为合成占位符，不是服务商配置或可用凭据：

```json
{
  "id": "alice",
  "email": {
    "address": "alice@example.test",
    "authorizationCode": "REPLACE_WITH_PRIVATE_IMAP_SMTP_CODE",
    "smtpHost": "smtp.example.test",
    "smtpPort": 465,
    "tlsMode": "implicit"
  }
}
```

## V14 → V15 人工转换

正常启动仅接受 V15，不提供 V14 双 reader 或启动自动迁移。具体操作入口和逐项清单见[配置指南的转换步骤](../../../Galatea/configuration.md#从历史-root-config-v14-切换到-v15)。转换前停止宿主并备份配置；若需保留队列状态，按配置指南使用停服/maintenance 边界。V14 中的 SMTP senderAccounts、offlineMode、credentialPath 和手写 bindingId 不属于 V15。
