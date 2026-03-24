import { describe, it, beforeEach, afterEach } from "node:test";
import assert from "node:assert/strict";
import { join } from "node:path";
import { homedir } from "node:os";
import { resolveConfig, formatAddedTodo } from "./index.ts";

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
      assert.equal(cfg.agentDir, join(home, "org/alcuin"));
      assert.equal(cfg.humanDir, join(home, "org/human"));
    });

    it("roam dirs default to <dir>/roam", () => {
      const cfg = resolveConfig();
      assert.equal(cfg.agentRoamDir, join(home, "org/alcuin/roam"));
      assert.equal(cfg.humanRoamDir, join(home, "org/human/roam"));
    });

    it("db paths default to <dir>/roam/.org.db", () => {
      const cfg = resolveConfig();
      assert.equal(cfg.agentDb, join(home, "org/alcuin/roam/.org.db"));
      assert.equal(cfg.humanDb, join(home, "org/human/roam/.org.db"));
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
      assert.equal(cfg.agentDb, "/custom/agent/roam/.org.db");
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
      assert.equal(cfg.agentDir, join(home, "org/alcuin"));
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
      assert.equal(cfg.agentDb, "/env/agent/roam/.org.db");
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
