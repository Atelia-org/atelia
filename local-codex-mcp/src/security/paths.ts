import { realpath, stat } from "node:fs/promises";
import path from "node:path";
import { BridgeError } from "../errors.js";

function comparable(value: string): string {
  const normalized = path.normalize(value);
  return process.platform === "win32" ? normalized.toLowerCase() : normalized;
}

function isInside(root: string, candidate: string): boolean {
  const relative = path.relative(comparable(root), comparable(candidate));
  return relative === "" || (!relative.startsWith(`..${path.sep}`) && relative !== ".." && !path.isAbsolute(relative));
}

async function canonicalDirectory(input: string, errorCode: "INVALID_CWD" | "INVALID_CONFIG"): Promise<string> {
  if (!path.isAbsolute(input)) {
    throw new BridgeError(errorCode, "Working directories and allowed roots must be absolute paths.");
  }

  try {
    const canonical = await realpath(input);
    const metadata = await stat(canonical);
    if (!metadata.isDirectory()) {
      throw new Error("path is not a directory");
    }
    return canonical;
  } catch (error) {
    if (error instanceof BridgeError) throw error;
    throw new BridgeError(errorCode, `Directory does not exist or is not accessible: ${input}`, {
      cause: error,
    });
  }
}

export class PathPolicy {
  private constructor(
    readonly allowedRoots: readonly string[],
    readonly defaultCwd: string,
  ) {}

  static async create(allowedRoots: readonly string[], defaultCwd?: string): Promise<PathPolicy> {
    if (allowedRoots.length === 0) {
      throw new BridgeError("INVALID_CONFIG", "At least one allowed root is required.");
    }

    const canonicalRoots = await Promise.all(
      allowedRoots.map((root) => canonicalDirectory(root, "INVALID_CONFIG")),
    );
    const deduplicated = [...new Set(canonicalRoots.map(comparable))].map(
      (key) => canonicalRoots.find((root) => comparable(root) === key)!,
    );
    const canonicalDefault = defaultCwd
      ? await canonicalDirectory(defaultCwd, "INVALID_CONFIG")
      : deduplicated[0]!;

    if (!deduplicated.some((root) => isInside(root, canonicalDefault))) {
      throw new BridgeError("INVALID_CONFIG", "The default cwd must be inside an allowed root.");
    }

    return new PathPolicy(deduplicated, canonicalDefault);
  }

  async resolveCwd(requested?: string): Promise<string> {
    const canonical = await canonicalDirectory(requested ?? this.defaultCwd, "INVALID_CWD");
    if (!this.allowedRoots.some((root) => isInside(root, canonical))) {
      throw new BridgeError("CWD_NOT_ALLOWED", "Requested cwd is outside configured project roots.");
    }
    return canonical;
  }
}

