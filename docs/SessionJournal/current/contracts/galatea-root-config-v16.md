# Galatea root config V16（当前合同）

当前 strict reader、loader、bootstrap 只接受精确整数 `"v": 16`；未知、重复字段、非法数字及旧版本 fail closed，不在启动时迁移配置。Character、Player、路径、连接选项、SMTP 字段沿用 [V15 历史合同](galatea-root-config-v15.md)；完整使用入口见[配置指南](../../../Galatea/configuration.md)。本次新增每角色 IMAP descriptor 与独立全局收件 policy。

## 每角色 IMAP

`characters[].email` 仍可省略或为 null。非空时五个 SMTP 字段仍必需，可额外省略或设置 `imap`；`imap: null` 与省略均表示未绑定收件。IMAP 与 SMTP 共用不可变 `address` / `authorizationCode`，没有独立登录名、凭据源、角色开关或凭据热加载。

| `email.imap` 字段 | 类型与约束 |
|:--|:--|
| `host` | 必需；1..253 字符 ASCII DNS host 或 IP，禁止 scheme、路径、空白、控制字符。 |
| `port` | 必需；整数 1..65535。 |
| `tlsMode` | 必需；精确 `implicit` 或 `starttls`。 |
| `autoDisplaySenders` | 可省略，默认 `[]`；最多128个窄语法单 ASCII mailbox，无通配/正则/重复。local-part Ordinal 精确，domain ASCII 忽略大小写；不合并 plus-tag、别名。 |

启动名单与同角色持久 store 中正常 SMTP 外发受理收件人取并集。正常 `smtp:<角色>:` outbox 一经 capture 提交即建立许可，包含网络失败和 Unknown；blocked/offline 不建立许可。许可不依赖当前 SMTP descriptor、不跨角色/store，换邮箱仍保留。名单只是注意力筛选，声明 From 不成为受信玩家或角色身份。

IMAP namespace 为 `imap:<characterId>:lowercase_hex(SHA256(UTF8(JSON_ARRAY[address,imapHost,imapPort,imapTlsMode,"INBOX"])))`。授权码与名单不参与指纹；改名单重启后继续原游标，改邮箱或 IMAP endpoint 则首次建立新 namespace 基线。SMTP 指纹仍只含原四项，不受 IMAP 配置改变影响。两个网络 policy 视图引用同一账号对象，秘密不复制到 durable state、角色输入、日志或错误文本。

## Runtime IMAP

`runtime.imap` 可省略；object 只接受以下可选字段：

| 字段 | 默认与约束 |
|:--|:--|
| `enabled` | `false`，布尔。 |
| `pollIntervalSeconds` | `60`，整数 1..3600。 |
| `timeoutSeconds` | `60`，整数 1..300。 |

首次成功只读打开 INBOX 后持久保存 UIDVALIDITY / scanUpperUid，跳过服务端采样值以下的 UID；上界优先真实 UIDNEXT−1，缺失时只读取最高现存 UID，明确空箱为 0。它不伪造 UIDNEXT；最高现存 UID 随删除降低时保留原游标。status Ready 表示基线已持久、收件启动完成。采样到本地提交期间到达的邮件可能被保守接收，不承诺精确的到达时间切线。只收基线之后的 UID，不按 Seen 筛选、不标已读、不移动删除。关闭或 maintenance 暂停拉取和新自动投递；已 Bound 的 Journal proof 仍进入所有 writer gate。重新启用继续原游标，不再次跳过积压。

陌生信只原子前移 checkpoint，没有逐信 Filtered 行，也不持久正文或触发回合。后加名单/主动外发不补投已越过的 UID。获准邮件正文投影后与游标同事务入箱，再通过共用 relay / exact Journal proof 自动启动回合；关闭后保留 Pending，删除名单不撤销已入箱事实。

## 持久格式与操作

当前 Delegation **V8** 新增 `imap_checkpoint` / `external_mail_inbox` 和 recipient NOCASE 索引。旧 V7 必须停服备份，通过 `operator upgrade-delegation-store --config <absolute-path> --character <id>` 预览，再加 `--apply` 显式升级；不自动迁移、不删库、不重发旧 SMTP。Observed 表示 Journal 已追加一次 Observation，不表示模型完成或回复；Unknown SMTP 仍不自动重发。

新角色输入为 `galatea.observation.v5`，`email-inbound` 的 sender 固定 runtime，声明 From 只是邮件数据，必需冻结 connection snapshot。旧 v1–v4 只读历史不重写。仅支持持久 UID；UIDVALIDITY 改变会阻断。停服后通过 `operator rebaseline-imap` 预览，携带 `--expected-validity`、`--expected-cursor`、`--expected-revision`、`--new-validity`、`--new-cursor` 五个预览值显式 CAS apply，跳过当时已有信并保留旧 inbox。preview 与 apply 复用自动收件的同一扫描上界，cursor 允许 0..uint.MaxValue。

## 合成配置片段

```json
{
  "v": 16,
  "characters": [{
    "id": "alice",
    "email": {
      "address": "alice@example.test",
      "authorizationCode": "REPLACE_WITH_PRIVATE_IMAP_SMTP_CODE",
      "smtpHost": "smtp.example.test",
      "smtpPort": 465,
      "tlsMode": "implicit",
      "imap": {
        "host": "imap.example.test",
        "port": 993,
        "tlsMode": "implicit",
        "autoDisplaySenders": ["friend@example.test"]
      }
    }
  }],
  "runtime": {
    "smtp": {"enabled": false, "timeoutSeconds": 60},
    "imap": {"enabled": false, "pollIntervalSeconds": 60, "timeoutSeconds": 60}
  }
}
```

该片段省略其它必需角色/运行时字段。正文仅接受可严格解码的主 text/plain；HTML-only、非法 MIME/正文、超限明确 Rejected，附件仅提供数量。自主列信/取信、待审/历史重判、TTL/撤销、HTML转换与其它 folders 延后。完整验收及实际邮箱兼容状态以[准入实施记录](../../../Galatea/imap-email-auto-display-admission-design.md)为准，配置或构建通过不代表真实邮箱通过。
