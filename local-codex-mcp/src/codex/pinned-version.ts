import { fileURLToPath } from "node:url";

export const PINNED_CODEX_VERSION = "0.154.0-alpha.3";

export const PINNED_CODEX_ENTRYPOINT = fileURLToPath(
  new URL(
    `../../../.codex-packages/${PINNED_CODEX_VERSION}/node_modules/@openai/codex/bin/codex.js`,
    import.meta.url,
  ),
);

export function codexVersionFromUserAgent(userAgent: string): string | undefined {
  return /^codex_[A-Za-z0-9_-]+\/([^\s/]+)(?:\s|$)/u.exec(userAgent)?.[1];
}
