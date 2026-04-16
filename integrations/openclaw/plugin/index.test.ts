import { describe, it, beforeEach, afterEach } from "node:test";
import assert from "node:assert/strict";
import { join } from "node:path";
import { homedir } from "node:os";
import {
  resolveConfig,
  formatAddedTodo,
  formatCreatedNode,
  formatOrgError,
  buildAddTodoArgs,
} from "./lib.ts";
import type { OrgMemoryConfig } from "./lib.ts";

// Save and restore env vars around each test
const envKeys = [
  "ORG_MEMORY_AGENT_DIR",
  "ORG_MEMORY_AGENT_ROAM_DIR",
  "ORG_MEMORY_HUMAN_DIR",
  "ORG_MEMORY_HUMAN_ROAM_DIR",
  "ORG_MEMORY_AGENT_DATABASE_LOCATION",
  "ORG_MEMORY_HUMAN_DATABASE_LOCATION",
  "ORG_MEMORY_ORG_BIN",
  "ORG_MEMORY_INBOX_FILE",
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
    it("uses default directories", () => {
      const cfg = resolveConfig();
      assert.equal(cfg.agentDir, join(home, "org/agent"));
      assert.equal(cfg.humanDir, join(home, "org/human"));
    });

    it("roam dirs default to <dir>/roam", () => {
      const cfg = resolveConfig();
      assert.equal(cfg.agentRoamDir, join(home, "org/agent/roam"));
      assert.equal(cfg.humanRoamDir, join(home, "org/human/roam"));
    });

    it("db paths default to <dir>/.org.db", () => {
      const cfg = resolveConfig();
      assert.equal(cfg.agentDb, join(home, "org/agent/.org.db"));
      assert.equal(cfg.humanDb, join(home, "org/human/.org.db"));
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
    it("overrides workspace dirs from plugin config", () => {
      const cfg = resolveConfig({ agentDir: "/custom/agent", humanDir: "/custom/human" });
      assert.equal(cfg.agentDir, "/custom/agent");
      assert.equal(cfg.humanDir, "/custom/human");
    });

    it("roam dirs derive from overridden workspace dirs", () => {
      const cfg = resolveConfig({ agentDir: "/custom/agent" });
      assert.equal(cfg.agentRoamDir, "/custom/agent/roam");
      assert.equal(cfg.agentDb, "/custom/agent/.org.db");
    });

    it("roam dirs can be overridden independently", () => {
      const cfg = resolveConfig({
        agentDir: "/custom/agent",
        agentRoamDir: "/custom/agent/notes",
      });
      assert.equal(cfg.agentDir, "/custom/agent");
      assert.equal(cfg.agentRoamDir, "/custom/agent/notes");
    });

    it("db paths can be overridden independently", () => {
      const cfg = resolveConfig({ agentDb: "/custom/agent.db" });
      assert.equal(cfg.agentDb, "/custom/agent.db");
      // Other defaults still apply
      assert.equal(cfg.agentDir, join(home, "org/agent"));
    });
  });

  describe("env var overrides", () => {
    it("env vars take precedence over plugin config", () => {
      process.env.ORG_MEMORY_AGENT_DIR = "/env/agent";
      const cfg = resolveConfig({ agentDir: "/config/agent" });
      assert.equal(cfg.agentDir, "/env/agent");
    });

    it("env roam dir overrides derived default", () => {
      process.env.ORG_MEMORY_AGENT_ROAM_DIR = "/env/roam";
      const cfg = resolveConfig({ agentDir: "/config/agent" });
      assert.equal(cfg.agentRoamDir, "/env/roam");
    });

    it("roam dir derives from env workspace dir when not set", () => {
      process.env.ORG_MEMORY_AGENT_DIR = "/env/agent";
      const cfg = resolveConfig();
      assert.equal(cfg.agentRoamDir, "/env/agent/roam");
      assert.equal(cfg.agentDb, "/env/agent/.org.db");
    });

    it("db env var overrides derived default", () => {
      process.env.ORG_MEMORY_AGENT_DATABASE_LOCATION = "/env/custom.db";
      const cfg = resolveConfig();
      assert.equal(cfg.agentDb, "/env/custom.db");
    });
  });

  describe("roam dir is never the same as workspace dir", () => {
    it("default config has distinct dirs", () => {
      const cfg = resolveConfig();
      assert.notEqual(cfg.agentDir, cfg.agentRoamDir);
      assert.notEqual(cfg.humanDir, cfg.humanRoamDir);
    });

    it("roam dir is a subdirectory of workspace dir by default", () => {
      const cfg = resolveConfig();
      assert.ok(cfg.agentRoamDir.startsWith(cfg.agentDir + "/"));
      assert.ok(cfg.humanRoamDir.startsWith(cfg.humanDir + "/"));
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
    const result = formatAddedTodo(stdout);
    assert.equal(result, stdout);
  });

  it("returns stdout unchanged when response is not JSON", () => {
    const stdout = "Headline added";
    const result = formatAddedTodo(stdout);
    assert.equal(result, stdout);
  });

  it("returns stdout unchanged when ok is false", () => {
    const stdout = JSON.stringify({ ok: false, error: { message: "bad" } });
    const result = formatAddedTodo(stdout);
    assert.equal(result, stdout);
  });
});

describe("formatCreatedNode", () => {
  it("prefixes id when present in JSON response", () => {
    const stdout = JSON.stringify({ ok: true, data: { id: "uuid-1234", title: "A Node" } });
    const result = formatCreatedNode(stdout);
    assert.ok(result.startsWith("Node created with ID: uuid-1234\n\n"));
  });

  it("falls back to custom_id when id is absent", () => {
    const stdout = JSON.stringify({ ok: true, data: { custom_id: "k4t", title: "A Node" } });
    const result = formatCreatedNode(stdout);
    assert.ok(result.startsWith("Node created with ID: k4t\n\n"));
  });

  it("returns stdout unchanged when neither id nor custom_id is present", () => {
    const stdout = JSON.stringify({ ok: true, data: { title: "A Node" } });
    const result = formatCreatedNode(stdout);
    assert.equal(result, stdout);
  });

  it("returns stdout unchanged for non-JSON", () => {
    const stdout = "plain text";
    assert.equal(formatCreatedNode(stdout), stdout);
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
    const msg = formatOrgError(["schedule", "id", "2026-03-01"], stdout, "", "exit 1");
    // Without -f json we treat stdout as opaque; should surface raw stdout, not parsed message
    assert.ok(msg.startsWith("org schedule failed: "));
    assert.ok(msg.includes(stdout));
  });
});

describe("buildAddTodoArgs", () => {
  const cfg: OrgMemoryConfig = {
    agentDir: "/agent",
    agentRoamDir: "/agent/roam",
    humanDir: "/human",
    humanRoamDir: "/human/roam",
    agentDb: "/agent/.org.db",
    humanDb: "/human/.org.db",
    orgBin: "org",
    inboxFile: "inbox.org",
  };

  it("defaults to human inbox with TODO state and json output", () => {
    const args = buildAddTodoArgs(cfg, { title: "Buy milk" });
    assert.deepEqual(args, [
      "add",
      "/human/inbox.org",
      "Buy milk",
      "--todo",
      "TODO",
      "--db",
      "/human/.org.db",
      "-f",
      "json",
    ]);
  });

  it("routes to agent dir and agent db when dir is agent", () => {
    const args = buildAddTodoArgs(cfg, { title: "Learn", dir: "agent" });
    assert.ok(args.includes("/agent/inbox.org"));
    assert.ok(args.includes("/agent/.org.db"));
  });

  it("honors custom file relative to the workspace dir", () => {
    const args = buildAddTodoArgs(cfg, { title: "Ship", dir: "human", file: "projects.org" });
    assert.ok(args.includes("/human/projects.org"));
  });

  it("appends --scheduled when provided", () => {
    const args = buildAddTodoArgs(cfg, { title: "X", scheduled: "2026-04-20" });
    const idx = args.indexOf("--scheduled");
    assert.notEqual(idx, -1);
    assert.equal(args[idx + 1], "2026-04-20");
  });

  it("appends --deadline when provided", () => {
    const args = buildAddTodoArgs(cfg, { title: "X", deadline: "2026-04-20" });
    const idx = args.indexOf("--deadline");
    assert.notEqual(idx, -1);
    assert.equal(args[idx + 1], "2026-04-20");
  });

  it("passes title literally (no shell-quoting)", () => {
    const title = "Fix: double \"quoted\" 'and' $unescaped";
    const args = buildAddTodoArgs(cfg, { title });
    assert.ok(args.includes(title));
  });
});
