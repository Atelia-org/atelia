#!/usr/bin/env node

import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import {
  cpSync,
  createReadStream,
  existsSync,
  lstatSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  readdirSync,
  renameSync,
  rmSync,
  statSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { dirname, isAbsolute, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

export const PINNED_CODEX_VERSION = "0.154.0-alpha.3";

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const projectRoot = resolve(scriptDirectory, "..");
const packageSource = join(scriptDirectory, "pinned-codex");
const contentManifestPath = join(packageSource, "content-manifest.json");
const defaultInstallRoot = join(projectRoot, ".codex-packages");
const expectedLockEntries = Object.freeze({
  "node_modules/@openai/codex": Object.freeze({
    version: PINNED_CODEX_VERSION,
    integrity: "sha512-X3SUk9iM9ft1m5627exaCxY3Tjup4RH1W85l51W6lQSdGCce+A0fNh4oOjmYiwrtyfAyMEvs/HG/VFZNmxqBcw==",
  }),
  "node_modules/@openai/codex-darwin-arm64": Object.freeze({
    version: `${PINNED_CODEX_VERSION}-darwin-arm64`,
    integrity: "sha512-DzbxvvYyflmrTmsZQ2BjUo7GGXxNQ+pWnVyfvfu+24adQnBXjNb7rCHVLfOocvICgZhZbFvhixwY66D91TkYgQ==",
  }),
  "node_modules/@openai/codex-darwin-x64": Object.freeze({
    version: `${PINNED_CODEX_VERSION}-darwin-x64`,
    integrity: "sha512-hL46w31B8FVPAOjriLUSymHqdJFhXAUsVY04baRbsAgW8NNQBClbuIe1Crif42Rol2sJHqMAvHOfgjxS2ee3Fg==",
  }),
  "node_modules/@openai/codex-linux-arm64": Object.freeze({
    version: `${PINNED_CODEX_VERSION}-linux-arm64`,
    integrity: "sha512-Nikr1OGSEypO5y9W0K1u0vsiLfVSWlXX9zQgPdqk84rsNL2g62w40hBPCFa6/UzMURKKFZuEzTWa/09UO8aRrA==",
  }),
  "node_modules/@openai/codex-linux-x64": Object.freeze({
    version: `${PINNED_CODEX_VERSION}-linux-x64`,
    integrity: "sha512-/LDIHrggFFHM1ALSpv+YsA2e1FHmxQIXiVSuwCivany9c+bFNewi2EO71LLCZimt5L/gMR1r/7WAdfD5O+s/eQ==",
  }),
  "node_modules/@openai/codex-win32-arm64": Object.freeze({
    version: `${PINNED_CODEX_VERSION}-win32-arm64`,
    integrity: "sha512-BMTlpA/FhQSKLrM1ow2M2bK2/2JSuXpM3BuBRtqMmfc0R/cWLPsWcbpBI+VQE8q41yr+TuvrCq/iEOtVrKxHMA==",
  }),
  "node_modules/@openai/codex-win32-x64": Object.freeze({
    version: `${PINNED_CODEX_VERSION}-win32-x64`,
    integrity: "sha512-YBlCyIlulDDaPv7+cwtJl4N4a66lP+jIn/T+jpm1lTklCvwalyZG0tVHbZta9/JsEoQs2r9bwe3okrjEeOt+1A==",
  }),
});

const platformPackageKeys = Object.freeze({
  "darwin-arm64": "node_modules/@openai/codex-darwin-arm64",
  "darwin-x64": "node_modules/@openai/codex-darwin-x64",
  "linux-arm64": "node_modules/@openai/codex-linux-arm64",
  "linux-x64": "node_modules/@openai/codex-linux-x64",
  "win32-arm64": "node_modules/@openai/codex-win32-arm64",
  "win32-x64": "node_modules/@openai/codex-win32-x64",
});

function parseJson(path) {
  return JSON.parse(readFileSync(path, "utf8"));
}

function assertExactPackageLock(source = packageSource) {
  const packageJson = parseJson(join(source, "package.json"));
  const packageLock = parseJson(join(source, "package-lock.json"));
  if (packageJson.dependencies?.["@openai/codex"] !== PINNED_CODEX_VERSION) {
    throw new Error(`Pinned package manifest must require @openai/codex exactly ${PINNED_CODEX_VERSION}.`);
  }
  if (packageLock.lockfileVersion !== 3) {
    throw new Error("Pinned Codex package-lock.json must use lockfileVersion 3.");
  }
  const rootDependency = packageLock.packages?.[""]?.dependencies?.["@openai/codex"];
  if (rootDependency !== PINNED_CODEX_VERSION) {
    throw new Error(`Pinned lock root must require @openai/codex exactly ${PINNED_CODEX_VERSION}.`);
  }
  const actualPackageKeys = Object.keys(packageLock.packages ?? {}).sort();
  const expectedPackageKeys = ["", ...Object.keys(expectedLockEntries)].sort();
  if (JSON.stringify(actualPackageKeys) !== JSON.stringify(expectedPackageKeys)) {
    throw new Error("Pinned Codex package-lock.json contains an unexpected package set.");
  }
  for (const [key, expected] of Object.entries(expectedLockEntries)) {
    const actual = packageLock.packages?.[key];
    if (actual?.version !== expected.version || actual.integrity !== expected.integrity) {
      throw new Error(`Pinned lock entry ${key} does not match the reviewed version and integrity.`);
    }
  }
}

function platformPackageKey(platform = process.platform, architecture = process.arch) {
  const key = platformPackageKeys[`${platform}-${architecture}`];
  if (!key) throw new Error(`Unsupported Codex package platform: ${platform}-${architecture}.`);
  return key;
}

export function pinnedCodexDirectory(installRoot = defaultInstallRoot) {
  if (!isAbsolute(installRoot)) throw new Error("Pinned Codex install root must be absolute.");
  return join(installRoot, PINNED_CODEX_VERSION);
}

export function pinnedCodexEntrypoint(installRoot = defaultInstallRoot) {
  return join(pinnedCodexDirectory(installRoot), "node_modules", "@openai", "codex", "bin", "codex.js");
}

function verifyPackageManifest(path, expectedVersion, label) {
  const manifest = parseJson(path);
  if (manifest.version !== expectedVersion) {
    throw new Error(`${label} version drift: expected ${expectedVersion}, found ${String(manifest.version)}.`);
  }
}

function digestFile(path) {
  return new Promise((resolveDigest, reject) => {
    const hash = createHash("sha256");
    const input = createReadStream(path);
    input.on("data", (chunk) => hash.update(chunk));
    input.on("error", reject);
    input.on("end", () => resolveDigest(hash.digest("hex")));
  });
}

function packageFiles(root, directory = root) {
  const files = [];
  for (const name of readdirSync(directory).sort()) {
    const path = join(directory, name);
    const stat = lstatSync(path);
    if (stat.isDirectory()) files.push(...packageFiles(root, path));
    else if (stat.isFile()) files.push({
      path: relative(root, path).split("\\").join("/"),
      absolutePath: path,
      bytes: stat.size,
    });
    else throw new Error(`Installed package contains unsupported non-regular entry: ${relative(root, path)}.`);
  }
  return files;
}

export async function verifyContentTree(root, expectedFiles) {
  const actualFiles = packageFiles(root);
  const actualPaths = actualFiles.map((file) => file.path);
  const expectedPaths = expectedFiles.map((file) => file.path);
  if (JSON.stringify(actualPaths) !== JSON.stringify(expectedPaths)) {
    throw new Error("Installed package file set does not match the reviewed tarball content manifest.");
  }
  for (let index = 0; index < actualFiles.length; index += 1) {
    const actual = actualFiles[index];
    const expected = expectedFiles[index];
    if (actual.bytes !== expected.bytes || await digestFile(actual.absolutePath) !== expected.sha256) {
      throw new Error(`Installed package content drifted: ${actual.path}.`);
    }
  }
}

function reviewedContentManifest() {
  const manifest = parseJson(contentManifestPath);
  if (manifest.schema !== 1 || manifest.codexVersion !== PINNED_CODEX_VERSION) {
    throw new Error("Pinned Codex content manifest identity drifted.");
  }
  const expectedPackageNames = Object.keys(expectedLockEntries)
    .map((key) => key.slice("node_modules/".length))
    .sort();
  if (JSON.stringify(Object.keys(manifest.packages ?? {}).sort()) !== JSON.stringify(expectedPackageNames)) {
    throw new Error("Pinned Codex content manifest contains an unexpected package set.");
  }
  for (const [lockKey, expectedLock] of Object.entries(expectedLockEntries)) {
    const directoryName = lockKey.slice("node_modules/".length);
    const expectedContent = manifest.packages?.[directoryName];
    if (expectedContent?.version !== expectedLock.version
        || expectedContent.integrity !== expectedLock.integrity
        || !Array.isArray(expectedContent.files)
        || expectedContent.files.length === 0) {
      throw new Error(`Pinned Codex content manifest drifted for ${directoryName}.`);
    }
    for (const file of expectedContent.files) {
      if (typeof file?.path !== "string"
          || file.path.length === 0
          || file.path.startsWith("/")
          || file.path.split("/").includes("..")
          || !Number.isSafeInteger(file.bytes)
          || file.bytes < 0
          || typeof file.sha256 !== "string"
          || !/^[0-9a-f]{64}$/u.test(file.sha256)) {
        throw new Error(`Pinned Codex content manifest has an invalid entry for ${directoryName}.`);
      }
    }
    const sorted = [...expectedContent.files].sort((left, right) =>
      left.path < right.path ? -1 : left.path > right.path ? 1 : 0);
    if (JSON.stringify(sorted) !== JSON.stringify(expectedContent.files)) {
      throw new Error(`Pinned Codex content manifest paths are not canonical for ${directoryName}.`);
    }
  }
  return manifest;
}

function runEntrypoint(entrypoint, args, options = {}) {
  const result = spawnSync(process.execPath, [entrypoint, ...args], {
    encoding: "utf8",
    ...options,
  });
  if (result.error) throw result.error;
  if (result.status !== 0) {
    throw new Error(`Pinned Codex command failed with exit code ${String(result.status)}: ${result.stderr?.trim() ?? ""}`);
  }
  return result;
}

async function verifyInstalledDirectory({
  directory,
  platform = process.platform,
  architecture = process.arch,
}) {
  assertExactPackageLock();
  const rootManifest = join(directory, "node_modules", "@openai", "codex", "package.json");
  const platformKey = platformPackageKey(platform, architecture);
  const platformDirectoryName = platformKey.slice("node_modules/@openai/".length);
  const platformManifest = join(directory, "node_modules", "@openai", platformDirectoryName, "package.json");
  verifyPackageManifest(rootManifest, PINNED_CODEX_VERSION, "@openai/codex");
  verifyPackageManifest(platformManifest, expectedLockEntries[platformKey].version, platformDirectoryName);
  const expectedDirectories = ["codex", platformDirectoryName].sort();
  const actualDirectories = readdirSync(join(directory, "node_modules", "@openai")).sort();
  if (JSON.stringify(actualDirectories) !== JSON.stringify(expectedDirectories)) {
    throw new Error("Installed @openai package set does not match the exact platform pin.");
  }
  const contentManifest = reviewedContentManifest();
  await verifyContentTree(
    join(directory, "node_modules", "@openai", "codex"),
    contentManifest.packages["@openai/codex"].files,
  );
  await verifyContentTree(
    join(directory, "node_modules", "@openai", platformDirectoryName),
    contentManifest.packages[`@openai/${platformDirectoryName}`].files,
  );
  const entrypoint = join(directory, "node_modules", "@openai", "codex", "bin", "codex.js");
  const version = runEntrypoint(entrypoint, ["--version"]).stdout.trim();
  const expected = `codex-cli ${PINNED_CODEX_VERSION}`;
  if (version !== expected) {
    throw new Error(`Pinned Codex executable drift: expected ${expected}, found ${version || "<empty>"}.`);
  }
  return { directory, entrypoint, version };
}

export async function verifyPinnedCodex({
  installRoot = defaultInstallRoot,
  platform = process.platform,
  architecture = process.arch,
} = {}) {
  return await verifyInstalledDirectory({
    directory: pinnedCodexDirectory(installRoot),
    platform,
    architecture,
  });
}

export async function installPinnedCodex({ installRoot = defaultInstallRoot } = {}) {
  assertExactPackageLock();
  const target = pinnedCodexDirectory(installRoot);
  if (existsSync(target)) {
    try {
      return { ...await verifyPinnedCodex({ installRoot }), installed: false };
    } catch (error) {
      const reason = error instanceof Error ? error.message : String(error);
      throw new Error(
        `Refusing to replace the existing pinned Codex directory ${target}: ${reason} Move it aside and retry after reviewing the drift.`,
        { cause: error },
      );
    }
  }

  mkdirSync(installRoot, { recursive: true });
  const staging = mkdtempSync(join(installRoot, `.install-${PINNED_CODEX_VERSION}-`));
  try {
    cpSync(join(packageSource, "package.json"), join(staging, "package.json"));
    cpSync(join(packageSource, "package-lock.json"), join(staging, "package-lock.json"));
    const npmArguments = ["ci", "--ignore-scripts", "--no-audit", "--no-fund", "--omit=dev"];
    const npmExecPath = process.env.npm_execpath;
    const install = npmExecPath
      ? spawnSync(process.execPath, [npmExecPath, ...npmArguments], { cwd: staging, stdio: "inherit" })
      : spawnSync(process.platform === "win32" ? "npm.cmd" : "npm", npmArguments, {
          cwd: staging,
          stdio: "inherit",
          shell: process.platform === "win32",
        });
    if (install.error) throw install.error;
    if (install.status !== 0) {
      throw new Error(`npm ci failed with exit code ${String(install.status)}.`);
    }
    await verifyInstalledDirectory({ directory: staging });
    renameSync(staging, target);
    return { ...await verifyPinnedCodex({ installRoot }), installed: true };
  } catch (error) {
    rmSync(staging, { recursive: true, force: true });
    throw error;
  }
}

export async function generateSchemas({ installRoot = defaultInstallRoot } = {}) {
  const { entrypoint } = await verifyPinnedCodex({ installRoot });
  const schemaDirectory = join(projectRoot, "schemas");
  mkdirSync(defaultInstallRoot, { recursive: true });
  const stagingRoot = mkdtempSync(join(defaultInstallRoot, ".schema-generation-"));
  const nextDirectory = join(stagingRoot, "next");
  const previousDirectory = join(stagingRoot, "previous");
  try {
    runEntrypoint(entrypoint, ["app-server", "generate-ts", "--out", nextDirectory], {
      stdio: "inherit",
    });
    for (const required of ["InitializeResponse.ts", join("v2", "index.ts")]) {
      if (!statSync(join(nextDirectory, required)).isFile()) {
        throw new Error(`Generated schema output is missing ${required}.`);
      }
    }
    cpSync(join(schemaDirectory, "README.md"), join(nextDirectory, "README.md"));
    renameSync(schemaDirectory, previousDirectory);
    try {
      renameSync(nextDirectory, schemaDirectory);
    } catch (error) {
      renameSync(previousDirectory, schemaDirectory);
      throw error;
    }
    rmSync(previousDirectory, { recursive: true });
  } finally {
    rmSync(stagingRoot, { recursive: true, force: true });
  }
}

function relativeFiles(root, prefix = "") {
  const files = [];
  for (const name of readdirSync(join(root, prefix)).sort()) {
    const relative = join(prefix, name);
    const stat = statSync(join(root, relative));
    if (stat.isDirectory()) files.push(...relativeFiles(root, relative));
    else if (relative !== "README.md") files.push(relative);
  }
  return files;
}

export async function verifySchemas({ installRoot = defaultInstallRoot } = {}) {
  const { entrypoint } = await verifyPinnedCodex({ installRoot });
  const generated = mkdtempSync(join(tmpdir(), "atelia-codex-schemas-"));
  try {
    runEntrypoint(entrypoint, ["app-server", "generate-ts", "--out", generated]);
    const schemaDirectory = join(projectRoot, "schemas");
    const expectedFiles = relativeFiles(generated);
    const actualFiles = relativeFiles(schemaDirectory);
    if (JSON.stringify(actualFiles) !== JSON.stringify(expectedFiles)) {
      throw new Error("Checked-in schema file set does not match the exact pinned Codex generator.");
    }
    for (const relative of expectedFiles) {
      const expected = readFileSync(join(generated, relative));
      const actual = readFileSync(join(schemaDirectory, relative));
      if (!expected.equals(actual)) {
        throw new Error(`Checked-in schema content drifted: ${relative}.`);
      }
    }
  } finally {
    rmSync(generated, { recursive: true, force: true });
  }
}

function printVerification(result, action) {
  process.stdout.write(`${action}: ${result.version}\nentrypoint: ${result.entrypoint}\n`);
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const action = process.argv[2];
    if (action === "install") printVerification(await installPinnedCodex(), "verified");
    else if (action === "verify") printVerification(await verifyPinnedCodex(), "verified");
    else if (action === "generate-schemas") await generateSchemas();
    else if (action === "verify-schemas") await verifySchemas();
    else throw new Error("Usage: manage-pinned-codex.mjs install|verify|generate-schemas|verify-schemas");
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    process.stderr.write(`pinned-codex: ${message}\n`);
    process.exitCode = 1;
  }
}
