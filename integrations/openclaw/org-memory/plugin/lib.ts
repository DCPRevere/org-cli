import { execFile } from "node:child_process";
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import { homedir } from "node:os";

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

export const DATE_PATTERN = "^\\d{4}-\\d{2}-\\d{2}$";
export const DATE_OR_EMPTY_PATTERN = "^(\\d{4}-\\d{2}-\\d{2})?$";
export const DEFAULT_TIMEOUT_MS = 10_000;
export const READ_TIMEOUT_MS = 30_000;
export const MAX_FILE_BYTES = 32_768;

// ---------------------------------------------------------------------------
// Config resolution: env vars > plugin config > defaults
// ---------------------------------------------------------------------------

export type OrgMemoryConfig = {
  dir: string;
  roamDir: string;
  db: string;
  orgBin: string;
  inboxFile: string;
};

export function resolveConfig(pluginConfig?: Record<string, unknown>): OrgMemoryConfig {
  const home = homedir();
  const cfg = pluginConfig ?? {};

  const dir =
    process.env.ORG_MEMORY_DIR ??
    (cfg.dir as string | undefined) ??
    join(home, "org/agent");

  return {
    dir,
    roamDir:
      process.env.ORG_MEMORY_ROAM_DIR ??
      (cfg.roamDir as string | undefined) ??
      join(dir, "roam"),
    db:
      process.env.ORG_MEMORY_DB ??
      (cfg.db as string | undefined) ??
      join(dir, ".org.db"),
    // Share the org binary with org-cli — one binary, one env var.
    orgBin:
      process.env.ORG_CLI_BIN ??
      (cfg.orgBin as string | undefined) ??
      "org",
    inboxFile:
      (cfg.inboxFile as string | undefined) ??
      "inbox.org",
  };
}

// ---------------------------------------------------------------------------
// Output formatting
// ---------------------------------------------------------------------------

export function formatOrgError(
  args: string[],
  stdout: string,
  stderr: string,
  fallback: string,
): string {
  const cmd = args[0] ?? "org";
  const fIdx = args.indexOf("-f");
  const wantsJson = fIdx !== -1 && args[fIdx + 1] === "json";
  if (wantsJson && stdout) {
    try {
      const parsed = JSON.parse(stdout);
      if (parsed?.ok === false && parsed.error?.message) {
        return `org ${cmd} failed: ${parsed.error.message}`;
      }
    } catch {
      // fall through
    }
  }
  const detail = stderr.trim() || stdout.trim() || fallback;
  return `org ${cmd} failed: ${detail}`;
}

export function formatAddedTodo(stdout: string): string {
  try {
    const parsed = JSON.parse(stdout);
    const customId = parsed?.data?.custom_id;
    if (customId) {
      return `TODO created with ID: ${customId}\n\n${stdout}`;
    }
  } catch {
    // non-JSON
  }
  return stdout;
}

export function formatAddedNote(stdout: string): string {
  try {
    const parsed = JSON.parse(stdout);
    const customId = parsed?.data?.custom_id;
    if (customId) {
      return `Note created with ID: ${customId}\n\n${stdout}`;
    }
  } catch {
    // non-JSON
  }
  return stdout;
}

export function formatCreatedNode(stdout: string): string {
  try {
    const parsed = JSON.parse(stdout);
    const id = parsed?.data?.id ?? parsed?.data?.custom_id;
    if (id) {
      return `Node created with ID: ${id}\n\n${stdout}`;
    }
  } catch {
    // non-JSON
  }
  return stdout;
}

// ---------------------------------------------------------------------------
// Argument builders (pure — testable without spawning org)
// ---------------------------------------------------------------------------

export function buildAddTodoArgs(
  cfg: OrgMemoryConfig,
  params: {
    title: string;
    scheduled?: string;
    deadline?: string;
    file?: string;
  },
): string[] {
  const filePath = join(cfg.dir, params.file ?? cfg.inboxFile);
  const args = [
    "add",
    filePath,
    params.title,
    "--todo",
    "TODO",
    "--db",
    cfg.db,
    "-f",
    "json",
  ];
  if (params.scheduled) {
    args.push("--scheduled", params.scheduled);
  }
  if (params.deadline) {
    args.push("--deadline", params.deadline);
  }
  return args;
}

export function buildAddNoteArgs(
  cfg: OrgMemoryConfig,
  params: {
    text: string;
    file?: string;
  },
): string[] {
  const filePath = join(cfg.dir, params.file ?? cfg.inboxFile);
  return ["add", filePath, params.text, "--db", cfg.db, "-f", "json"];
}

// ---------------------------------------------------------------------------
// Runtime helpers
// ---------------------------------------------------------------------------

export function runOrg(
  bin: string,
  args: string[],
  timeoutMs = DEFAULT_TIMEOUT_MS,
): Promise<{ stdout: string; stderr: string }> {
  return new Promise((resolve, reject) => {
    execFile(bin, args, { timeout: timeoutMs }, (err, stdout, stderr) => {
      if (err) {
        reject(new Error(formatOrgError(args, stdout, stderr, err.message)));
      } else {
        resolve({ stdout, stderr });
      }
    });
  });
}

export async function readOrgFile(
  path: string,
  maxBytes = Number.POSITIVE_INFINITY,
): Promise<string | null> {
  try {
    const content = await readFile(path, "utf-8");
    if (content.length <= maxBytes) {
      return content;
    }
    return content.slice(0, maxBytes) + `\n\n... (truncated at ${maxBytes} bytes)`;
  } catch {
    return null;
  }
}

export function todayStr(): string {
  const d = new Date();
  const y = d.getFullYear();
  const m = String(d.getMonth() + 1).padStart(2, "0");
  const day = String(d.getDate()).padStart(2, "0");
  return `${y}-${m}-${day}`;
}

export function yesterdayStr(): string {
  const d = new Date();
  d.setDate(d.getDate() - 1);
  const y = d.getFullYear();
  const m = String(d.getMonth() + 1).padStart(2, "0");
  const day = String(d.getDate()).padStart(2, "0");
  return `${y}-${m}-${day}`;
}
