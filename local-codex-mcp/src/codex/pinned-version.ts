import { fileURLToPath } from "node:url";

export const PINNED_CODEX_VERSION = "0.154.0-alpha.3";
export const CODEX_BRIDGE_CLIENT_NAME = "atelia_local_codex_mcp";

export const PINNED_CODEX_ENTRYPOINT = fileURLToPath(
  new URL(
    `../../../.codex-packages/${PINNED_CODEX_VERSION}/node_modules/@openai/codex/bin/codex.js`,
    import.meta.url,
  ),
);

export function codexVersionFromUserAgent(userAgent: string): string | undefined {
  const prefix = `${CODEX_BRIDGE_CLIENT_NAME}/`;
  if (!userAgent.startsWith(prefix)) return undefined;
  const version = userAgent.slice(prefix.length).split(" ", 1)[0];
  return version && !version.includes("/") ? version : undefined;
}
