import { describe, it, beforeEach, afterEach } from "node:test";
import assert from "node:assert/strict";
import { join } from "node:path";
import { homedir } from "node:os";
import {
  resolveConfig,
  formatAddedTodo,
  formatAddedNote,
  formatCreatedNode,
  formatOrgError,
  buildAddTodoArgs,
  buildAddNoteArgs,
} from "./lib.ts";
import type { OrgCliConfig } from "./lib.ts";

const envKeys = [
  "ORG_CLI_DIR",
  "ORG_CLI_ROAM_DIR",
  "ORG_CLI_DB",
  "ORG_CLI_BIN",
  "ORG_CLI_INBOX_FILE",
];

let savedEnv: Record<string, string | undefined>;

beforeEach(() => {
  savedEnv = {};
  for (const k of envKeys) {
    savedEnv[k] = process.env[k];
    delete process.env[k];
  }
});

afterEach(() => {
  for (const k of envKeys) {
    if (savedEnv[k] === undefined) {
      delete process.env[k];
    } else {
      process.env[k] = savedEnv[k];
    }
  }
});

const home = homedir();

describe("resolveConfig", () => {
  describe("defaults", () => {
    it("uses default directory", () => {
      const cfg = resolveConfig();
      assert.equal(cfg.dir, join(home, "org"));
    });

    it("roam dir defaults to <dir>/roam", () => {
      const cfg = resolveConfig();
      assert.equal(cfg.roamDir, join(home, "org/roam"));
    });

    it("db defaults to <dir>/.org.db", () => {
      const cfg = resolveConfig();
      assert.equal(cfg.db, join(home, "org/.org.db"));
    });

    it("orgBin defaults to org", () => {
      const cfg = resolveConfig();
      assert.equal(cfg.orgBin, "org");
    });

    it("inboxFile defaults to inbox.org", () => {
      const cfg = resolveConfig();
      assert.equal(cfg.inboxFile, "inbox.org");
    });
  });

  describe("plugin config overrides", () => {
    it("overrides dir from plugin config", () => {
      const cfg = resolveConfig({ dir: "/custom" });
      assert.equal(cfg.dir, "/custom");
    });

    it("roam dir derives from overridden dir", () => {
      const cfg = resolveConfig({ dir: "/custom" });
      assert.equal(cfg.roamDir, "/custom/roam");
      assert.equal(cfg.db, "/custom/.org.db");
    });

    it("roam dir can be overridden independently", () => {
      const cfg = resolveConfig({ dir: "/custom", roamDir: "/custom/notes" });
      assert.equal(cfg.dir, "/custom");
      assert.equal(cfg.roamDir, "/custom/notes");
    });

    it("db can be overridden independently", () => {
      const cfg = resolveConfig({ db: "/custom.db" });
      assert.equal(cfg.db, "/custom.db");
      assert.equal(cfg.dir, join(home, "org"));
    });
  });

  describe("env var overrides", () => {
    it("env vars take precedence over plugin config", () => {
      process.env.ORG_CLI_DIR = "/env";
      const cfg = resolveConfig({ dir: "/config" });
      assert.equal(cfg.dir, "/env");
    });

    it("env roam dir overrides derived default", () => {
      process.env.ORG_CLI_ROAM_DIR = "/env/roam";
      const cfg = resolveConfig({ dir: "/config" });
      assert.equal(cfg.roamDir, "/env/roam");
    });

    it("roam dir derives from env dir when not set", () => {
      process.env.ORG_CLI_DIR = "/env";
      const cfg = resolveConfig();
      assert.equal(cfg.roamDir, "/env/roam");
      assert.equal(cfg.db, "/env/.org.db");
    });

    it("db env var overrides derived default", () => {
      process.env.ORG_CLI_DB = "/env/custom.db";
      const cfg = resolveConfig();
      assert.equal(cfg.db, "/env/custom.db");
    });
  });

  describe("roam dir is never the same as workspace dir", () => {
    it("default config has distinct dirs", () => {
      const cfg = resolveConfig();
      assert.notEqual(cfg.dir, cfg.roamDir);
    });

    it("roam dir is a subdirectory of workspace dir by default", () => {
      const cfg = resolveConfig();
      assert.ok(cfg.roamDir.startsWith(cfg.dir + "/"));
    });
  });
});

describe("formatAddedTodo", () => {
  it("prefixes custom_id when present in JSON response", () => {
    const stdout = JSON.stringify({ ok: true, data: { custom_id: "abc", title: "Fix thing" } });
    const result = formatAddedTodo(stdout);
    assert.ok(result.startsWith("TODO created with ID: abc\n\n"));
    assert.ok(result.includes(stdout));
  });

  it("returns stdout unchanged when custom_id is absent", () => {
    const stdout = JSON.stringify({ ok: true, data: { title: "Fix thing" } });
    assert.equal(formatAddedTodo(stdout), stdout);
  });

  it("returns stdout unchanged for non-JSON", () => {
    assert.equal(formatAddedTodo("plain"), "plain");
  });

  it("returns stdout unchanged when ok is false", () => {
    const stdout = JSON.stringify({ ok: false, error: { message: "bad" } });
    assert.equal(formatAddedTodo(stdout), stdout);
  });
});

describe("formatAddedNote", () => {
  it("prefixes custom_id when present", () => {
    const stdout = JSON.stringify({ ok: true, data: { custom_id: "abc", title: "A thought" } });
    const result = formatAddedNote(stdout);
    assert.ok(result.startsWith("Note created with ID: abc\n\n"));
  });

  it("returns stdout unchanged for non-JSON", () => {
    assert.equal(formatAddedNote("plain"), "plain");
  });
});

describe("formatCreatedNode", () => {
  it("prefixes id when present", () => {
    const stdout = JSON.stringify({ ok: true, data: { id: "uuid-1234", title: "A Node" } });
    const result = formatCreatedNode(stdout);
    assert.ok(result.startsWith("Node created with ID: uuid-1234\n\n"));
  });

  it("falls back to custom_id when id is absent", () => {
    const stdout = JSON.stringify({ ok: true, data: { custom_id: "k4t", title: "A Node" } });
    const result = formatCreatedNode(stdout);
    assert.ok(result.startsWith("Node created with ID: k4t\n\n"));
  });

  it("returns stdout unchanged when neither is present", () => {
    const stdout = JSON.stringify({ ok: true, data: { title: "A Node" } });
    assert.equal(formatCreatedNode(stdout), stdout);
  });

  it("returns stdout unchanged for non-JSON", () => {
    assert.equal(formatCreatedNode("plain"), "plain");
  });
});

describe("formatOrgError", () => {
  it("extracts error.message from JSON error envelope on stdout", () => {
    const stdout = JSON.stringify({ ok: false, error: { message: "headline not found" } });
    const msg = formatOrgError(["todo", "set", "xyz", "DONE", "-f", "json"], stdout, "", "exit 1");
    assert.equal(msg, "org todo failed: headline not found");
  });

  it("falls back to stderr when JSON parse fails", () => {
    const msg = formatOrgError(["fts", "query", "-f", "json"], "not json", "fts: syntax error", "exit 1");
    assert.equal(msg, "org fts failed: fts: syntax error");
  });

  it("falls back to stdout when stderr is empty and response not JSON", () => {
    const msg = formatOrgError(["today", "-f", "json"], "raw problem output", "", "exit 1");
    assert.equal(msg, "org today failed: raw problem output");
  });

  it("falls back to the supplied fallback when stdout and stderr are empty", () => {
    const msg = formatOrgError(["read", "file", "id"], "", "", "ETIMEDOUT");
    assert.equal(msg, "org read failed: ETIMEDOUT");
  });

  it("does not attempt JSON parse when -f json is absent", () => {
    const stdout = JSON.stringify({ ok: false, error: { message: "nope" } });
    const msg = formatOrgError(["schedule", "id", "2026-05-01"], stdout, "", "exit 1");
    assert.ok(msg.startsWith("org schedule failed: "));
    assert.ok(msg.includes(stdout));
  });
});

const cfg: OrgCliConfig = {
  dir: "/ws",
  roamDir: "/ws/roam",
  db: "/ws/.org.db",
  orgBin: "org",
  inboxFile: "inbox.org",
};

describe("buildAddTodoArgs", () => {
  it("defaults to workspace inbox with TODO state and json output", () => {
    const args = buildAddTodoArgs(cfg, { title: "Buy milk" });
    assert.deepEqual(args, [
      "add",
      "/ws/inbox.org",
      "Buy milk",
      "--todo",
      "TODO",
      "--db",
      "/ws/.org.db",
      "-f",
      "json",
    ]);
  });

  it("honors custom file relative to the workspace dir", () => {
    const args = buildAddTodoArgs(cfg, { title: "Ship", file: "projects.org" });
    assert.ok(args.includes("/ws/projects.org"));
  });

  it("appends --scheduled when provided", () => {
    const args = buildAddTodoArgs(cfg, { title: "X", scheduled: "2026-05-20" });
    const idx = args.indexOf("--scheduled");
    assert.notEqual(idx, -1);
    assert.equal(args[idx + 1], "2026-05-20");
  });

  it("appends --deadline when provided", () => {
    const args = buildAddTodoArgs(cfg, { title: "X", deadline: "2026-05-20" });
    const idx = args.indexOf("--deadline");
    assert.notEqual(idx, -1);
    assert.equal(args[idx + 1], "2026-05-20");
  });

  it("passes title literally (no shell-quoting)", () => {
    const title = "Fix: double \"quoted\" 'and' $unescaped";
    const args = buildAddTodoArgs(cfg, { title });
    assert.ok(args.includes(title));
  });
});

describe("buildAddNoteArgs", () => {
  it("omits --todo and date flags", () => {
    const args = buildAddNoteArgs(cfg, { text: "A thought" });
    assert.deepEqual(args, [
      "add",
      "/ws/inbox.org",
      "A thought",
      "--db",
      "/ws/.org.db",
      "-f",
      "json",
    ]);
    assert.ok(!args.includes("--todo"));
    assert.ok(!args.includes("--scheduled"));
    assert.ok(!args.includes("--deadline"));
  });

  it("honors custom file", () => {
    const args = buildAddNoteArgs(cfg, { text: "Idea", file: "ideas.org" });
    assert.ok(args.includes("/ws/ideas.org"));
  });
});
