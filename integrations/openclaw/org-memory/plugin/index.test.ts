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
import type { OrgMemoryConfig } from "./lib.ts";

const envKeys = [
  "ORG_MEMORY_DIR",
  "ORG_MEMORY_ROAM_DIR",
  "ORG_MEMORY_DB",
  "ORG_CLI_BIN",
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
    it("uses default agent directory", () => {
      const cfg = resolveConfig();
      assert.equal(cfg.dir, join(home, "org/agent"));
    });

    it("roam dir defaults to <dir>/roam", () => {
      const cfg = resolveConfig();
      assert.equal(cfg.roamDir, join(home, "org/agent/roam"));
    });

    it("db defaults to <dir>/.org.db", () => {
      const cfg = resolveConfig();
      assert.equal(cfg.db, join(home, "org/agent/.org.db"));
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
    it("overrides dir", () => {
      const cfg = resolveConfig({ dir: "/custom/agent" });
      assert.equal(cfg.dir, "/custom/agent");
    });

    it("roam dir derives from overridden dir", () => {
      const cfg = resolveConfig({ dir: "/custom/agent" });
      assert.equal(cfg.roamDir, "/custom/agent/roam");
      assert.equal(cfg.db, "/custom/agent/.org.db");
    });

    it("roam dir can be overridden independently", () => {
      const cfg = resolveConfig({ dir: "/custom/agent", roamDir: "/notes" });
      assert.equal(cfg.roamDir, "/notes");
    });
  });

  describe("env var overrides", () => {
    it("env vars take precedence over plugin config", () => {
      process.env.ORG_MEMORY_DIR = "/env/agent";
      const cfg = resolveConfig({ dir: "/config" });
      assert.equal(cfg.dir, "/env/agent");
    });

    it("ORG_CLI_BIN is shared with org-cli", () => {
      process.env.ORG_CLI_BIN = "/usr/local/bin/org";
      const cfg = resolveConfig();
      assert.equal(cfg.orgBin, "/usr/local/bin/org");
    });

    it("roam dir derives from env dir when not set", () => {
      process.env.ORG_MEMORY_DIR = "/env/agent";
      const cfg = resolveConfig();
      assert.equal(cfg.roamDir, "/env/agent/roam");
      assert.equal(cfg.db, "/env/agent/.org.db");
    });

    it("db env var overrides derived default", () => {
      process.env.ORG_MEMORY_DB = "/env/custom.db";
      const cfg = resolveConfig();
      assert.equal(cfg.db, "/env/custom.db");
    });
  });

  describe("roam dir distinct from workspace dir", () => {
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
  it("prefixes custom_id when present", () => {
    const stdout = JSON.stringify({ ok: true, data: { custom_id: "abc", title: "X" } });
    assert.ok(formatAddedTodo(stdout).startsWith("TODO created with ID: abc\n\n"));
  });

  it("returns stdout unchanged when custom_id is absent", () => {
    const stdout = JSON.stringify({ ok: true, data: { title: "X" } });
    assert.equal(formatAddedTodo(stdout), stdout);
  });

  it("returns stdout unchanged for non-JSON", () => {
    assert.equal(formatAddedTodo("plain"), "plain");
  });
});

describe("formatAddedNote", () => {
  it("prefixes custom_id when present", () => {
    const stdout = JSON.stringify({ ok: true, data: { custom_id: "abc", title: "Thought" } });
    assert.ok(formatAddedNote(stdout).startsWith("Note created with ID: abc\n\n"));
  });
});

describe("formatCreatedNode", () => {
  it("prefixes id", () => {
    const stdout = JSON.stringify({ ok: true, data: { id: "uuid-x", title: "Node" } });
    assert.ok(formatCreatedNode(stdout).startsWith("Node created with ID: uuid-x\n\n"));
  });

  it("falls back to custom_id", () => {
    const stdout = JSON.stringify({ ok: true, data: { custom_id: "k4t", title: "Node" } });
    assert.ok(formatCreatedNode(stdout).startsWith("Node created with ID: k4t\n\n"));
  });
});

describe("formatOrgError", () => {
  it("extracts JSON error.message when -f json is present", () => {
    const stdout = JSON.stringify({ ok: false, error: { message: "nope" } });
    const msg = formatOrgError(["todo", "set", "x", "DONE", "-f", "json"], stdout, "", "exit 1");
    assert.equal(msg, "org todo failed: nope");
  });

  it("falls back to stderr when JSON parse fails", () => {
    const msg = formatOrgError(["fts", "q", "-f", "json"], "nope", "stderr msg", "fb");
    assert.equal(msg, "org fts failed: stderr msg");
  });
});

const cfg: OrgMemoryConfig = {
  dir: "/agent",
  roamDir: "/agent/roam",
  db: "/agent/.org.db",
  orgBin: "org",
  inboxFile: "inbox.org",
};

describe("buildAddTodoArgs", () => {
  it("defaults to agent inbox with TODO state and json output", () => {
    const args = buildAddTodoArgs(cfg, { title: "Note to self" });
    assert.deepEqual(args, [
      "add",
      "/agent/inbox.org",
      "Note to self",
      "--todo",
      "TODO",
      "--db",
      "/agent/.org.db",
      "-f",
      "json",
    ]);
  });

  it("honors custom file relative to dir", () => {
    const args = buildAddTodoArgs(cfg, { title: "X", file: "learnings.org" });
    assert.ok(args.includes("/agent/learnings.org"));
  });

  it("appends --scheduled when provided", () => {
    const args = buildAddTodoArgs(cfg, { title: "X", scheduled: "2026-05-01" });
    assert.equal(args[args.indexOf("--scheduled") + 1], "2026-05-01");
  });

  it("passes title literally", () => {
    const title = "Weird: \"quotes\" 'and' $stuff";
    const args = buildAddTodoArgs(cfg, { title });
    assert.ok(args.includes(title));
  });
});

describe("buildAddNoteArgs", () => {
  it("omits --todo and date flags", () => {
    const args = buildAddNoteArgs(cfg, { text: "An observation" });
    assert.ok(!args.includes("--todo"));
    assert.ok(!args.includes("--scheduled"));
    assert.ok(!args.includes("--deadline"));
    assert.ok(args.includes("/agent/inbox.org"));
  });
});
