# Galatea playerName 与内建 system prompt template 设计

状态：**Implemented**  
日期：2026-08-28  
范围：Galatea root config V4、主 system prompt、RecapGrid V6 member prompts，以及缺失
`systemPromptTemplateFile` 的安全 bootstrap。

## 1. 结论

`playerName` 模板化合理且可行，但它不是 `userId` 的别名，也不是通用 persona profile。它表示故事世界内由
User 消息承载行动意图的玩家角色名称；`characterName` 则表示 GM 扮演、并以 exact voice marker 输出的
主要 NPC。两者都由每个 user 的 root config 明确给出。

本轮继续使用 config V4，不推进版本号。理由不是保留兼容，而是当前项目尚未发布，唯一 development
instance 在 character-name V4 delta 后尚未进入新 binary 的正式运行；player-name delta 是同一批 prompt
identity hard-cut 的补全。V4 reader 现在要求两个字段，旧的半完成 V4 文件直接 fail closed。

## 2. Current contract

```json
{
  "v": 4,
  "users": [
    {
      "userId": "alice",
      "characterName": "Alice",
      "playerName": "Alex",
      "systemPromptTemplate": "",
      "systemPromptTemplateFile": "prompts/trpg-host-standard-zh-cn.md"
    }
  ]
}
```

- `characterName` 与 `playerName` 使用同一套 canonical single-line label grammar：NFC、already trimmed、
  1..128 UTF-8 bytes、closed delimiters/reserved labels。
- template language 仍是封闭语言，只新增 exact `${playerName}`；`${characterName}` 仍是每份合法 source
  的 required token。没有字典、反射、表达式、include、条件或递归展开。
- `playerName` 不生成代词、昵称、年龄、性别、外貌、职业、关系或历史；标准 template 也不预设这些内容。
- customized prompt 可记录这些 persona/history，但它们是 operator-authored story state，不属于 name field。

## 3. 物化边界

```text
config V4 user.characterName + user.playerName
        |
        +-- host load -> system prompt template -> finalized SystemPrompt
        |
        +-- operator/host -> RecapGrid V6 member templates
                              -> Definition / BuildTarget identity
```

主 system prompt 与两份 RecapGrid source-attribution prompt 使用同一对 validated names。Outbound mail
extractor 只需要识别 `[characterName]` voice marker，不需要 player name；login、HTTP、mail envelope、provider
route 和 connection identity 也不使用 `playerName`。

以 `characterName:"Galatea"`、`playerName:"刘世超"` 渲染 RecapGrid V6 时，finalized prompts 与旧 V5/V6
bytes exact 相同，因此 Family、两个 Definitions 与 registration command 的 golden digests不变。改变任一
name 都会旋转 member Definition/BuildTarget identity；首版不支持 existing-session character/player rename。

## 4. 内建标准 template 与缺失文件 bootstrap

[`prompt/trpg-host.md`](prompt/trpg-host.md) 是 Galatea.Server embedded、code-owned 的标准 zh-CN TRPG
template。它保留通用 GM 协议、赛博空间设定与五个空 memory slots，但删除具体 Player 的个人信息、昵称和
交互历史，并以 `${playerName}` 表达唯一玩家角色及临时 memory editor。

启动 bootstrap 对配置中 nonblank `systemPromptTemplateFile` 执行以下 closed policy：

1. existing file：不写、不覆盖，后续仍由 strict no-follow loader读取；
2. missing target 且 resolved path 位于 config directory 内：检查 existing ancestors 无 symlink/reparse，
   创建 missing parent，以 `FileMode.CreateNew` 写入 exact embedded bytes并执行`Flush(true)`；
3. missing target 位于 config directory 外：不创建，正常 load 继续 `FileNotFoundException`；
4. 任一文件生成后：列出 generated paths 并 fail-stop，要求 operator 检查后重启。

该机制只创建 operator 已在 config 中明确指向的 source file；不改 config、不替换 existing content、不创建
SessionJournal/RecapGrid derived state，也不调用 provider。多个 user 共享同一 missing path 时只创建一次。

## 5. 验证闸门

- strict config 覆盖 `playerName` missing/null/wrong type、Unicode/bounds/delimiters；
- renderer 覆盖双 token、unknown token、character-only overload、non-recursive 与 rendered byte cap；
- bootstrap 覆盖 new root、existing root missing in-root file、shared path、existing no-overwrite、outside-root
  no-create 与 ancestor symlink；
- standard template 锁定 embedded/source exact、无 `刘世超`/`老刘`、五个 `{{}}` slots；
- RecapGrid 锁定 `Galatea + 刘世超` golden digests不变，以及不同 player name 只旋转 member authority；
- CLI `scaffold` / `provision-asset` 同时要求 `--character-name` 与 `--player-name`，其他 asset 拒绝二者。
