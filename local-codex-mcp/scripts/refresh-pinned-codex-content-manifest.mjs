#!/usr/bin/env node

import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import {
  createReadStream,
  lstatSync,
  mkdtempSync,
  mkdirSync,
  readdirSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, relative } from "node:path";
import { fileURLToPath } from "node:url";
import { PINNED_CODEX_VERSION } from "./manage-pinned-codex.mjs";

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const lock = JSON.parse(readFileSync(join(scriptDirectory, "pinned-codex", "package-lock.json"), "utf8"));
const packageSpecs = [
  ["@openai/codex", `@openai/codex@${PINNED_CODEX_VERSION}`],
  ...["darwin-arm64", "darwin-x64", "linux-arm64", "linux-x64", "win32-arm64", "win32-x64"]
    .map((platform) => [
      `@openai/codex-${platform}`,
      `@openai/codex@${PINNED_CODEX_VERSION}-${platform}`,
    ]),
];

function runNpm(arguments_, options) {
  const npmExecPath = process.env.npm_execpath;
  return npmExecPath
    ? spawnSync(process.execPath, [npmExecPath, ...arguments_], options)
    : spawnSync(process.platform === "win32" ? "npm.cmd" : "npm", arguments_, {
        ...options,
        shell: process.platform === "win32",
      });
}

function digestFile(path, algorithm, encoding) {
  return new Promise((resolve, reject) => {
    const hash = createHash(algorithm);
    const input = createReadStream(path);
    input.on("data", (chunk) => hash.update(chunk));
    input.on("error", reject);
    input.on("end", () => resolve(hash.digest(encoding)));
  });
}

function regularFiles(root, directory = root) {
  const result = [];
  for (const name of readdirSync(directory).sort()) {
    const path = join(directory, name);
    const stat = lstatSync(path);
    if (stat.isDirectory()) result.push(...regularFiles(root, path));
    else if (stat.isFile()) result.push(path);
    else throw new Error(`Package contains unsupported non-regular entry: ${relative(root, path)}.`);
  }
  return result;
}

const staging = mkdtempSync(join(tmpdir(), "atelia-pinned-codex-manifest-"));
try {
  const packages = {};
  for (const [directoryName, spec] of packageSpecs) {
    const packageStaging = join(staging, directoryName.replaceAll("/", "_"));
    mkdirSync(packageStaging);
    const packed = runNpm(["pack", spec, "--ignore-scripts", "--json", "--pack-destination", packageStaging], {
      encoding: "utf8",
    });
    if (packed.error) throw packed.error;
    if (packed.status !== 0) throw new Error(`npm pack failed for ${spec}: ${packed.stderr.trim()}`);
    const [{ filename }] = JSON.parse(packed.stdout);
    const tarball = join(packageStaging, filename);
    const lockKey = `node_modules/${directoryName}`;
    const expectedIntegrity = lock.packages?.[lockKey]?.integrity;
    const actualIntegrity = `sha512-${await digestFile(tarball, "sha512", "base64")}`;
    if (actualIntegrity !== expectedIntegrity) {
      throw new Error(`Downloaded tarball integrity mismatch for ${directoryName}.`);
    }
    const extracted = join(packageStaging, "extracted");
    mkdirSync(extracted);
    const unpacked = spawnSync("tar", ["-xzf", tarball, "-C", extracted]);
    if (unpacked.error) throw unpacked.error;
    if (unpacked.status !== 0) throw new Error(`tar extraction failed for ${directoryName}.`);
    const packageRoot = join(extracted, "package");
    const files = [];
    for (const path of regularFiles(packageRoot)) {
      const stat = lstatSync(path);
      files.push({
        path: relative(packageRoot, path).split("\\").join("/"),
        bytes: stat.size,
        sha256: await digestFile(path, "sha256", "hex"),
      });
    }
    packages[directoryName] = {
      version: lock.packages[lockKey].version,
      integrity: expectedIntegrity,
      files,
    };
  }
  const manifest = {
    schema: 1,
    codexVersion: PINNED_CODEX_VERSION,
    trustBoundary: "Reviewed npm lock SRI and tarball contents; detects later accidental local package-tree drift, not a malicious same-user rewrite of tracked verifier inputs.",
    packages,
  };
  writeFileSync(
    join(scriptDirectory, "pinned-codex", "content-manifest.json"),
    `${JSON.stringify(manifest, null, 2)}\n`,
  );
} finally {
  rmSync(staging, { recursive: true, force: true });
}
