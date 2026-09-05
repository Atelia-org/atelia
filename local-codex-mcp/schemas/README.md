# Generated Codex app-server protocol

这些文件由 repo-local exact-pinned `codex-cli 0.154.0-alpha.3` 生成：

```bash
npm run codex:install
npm run schemas:generate
npm run schemas:verify
```

安装输入与 registry SRI 固定在 `scripts/pinned-codex/package-lock.json`，逐文件SHA-256固定在
`scripts/pinned-codex/content-manifest.json`；生成命令会先验证package manifest、当前平台完整package tree
与`codex --version`。除本文件外不要手改生成内容。
