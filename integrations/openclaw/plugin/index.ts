import { execFile } from "node:child_process";
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import { homedir } from "node:os";
import { Type } from "@sinclair/typebox";
import type { OpenClawPluginApi } from "openclaw/plugin-sdk";

// ---------------------------------------------------------------------------
// Config resolution: env vars > plugin config > defaults
// ---------------------------------------------------------------------------

export type OrgMemoryConfig = {
  agentDir: string;
  agentRoamDir: string;
  humanDir: string;
  humanRoamDir: string;
  agentDb: string;
  humanDb: string;
  orgBin: string;
  inboxFile: string;
};

export function resolveConfig(pluginConfig?: Record<string, unknown>): OrgMemoryConfig {
  const home = homedir();
  const cfg = pluginConfig ?? {};

  const agentDir =
    process.env.ORG_MEMORY_AGENT_DIR ??
    (cfg.agentDir as string | undefined) ??
    join(home, "org/alcuin");
  const humanDir =
    process.env.ORG_MEMORY_HUMAN_DIR ??
    (cfg.humanDir as string | undefined) ??
    join(home, "org/human");

  return {
    agentDir,
    agentRoamDir:
      process.env.ORG_MEMORY_AGENT_ROAM_DIR ??
      (cfg.agentRoamDir as string | undefined) ??
      join(agentDir, "roam"),
    humanDir,
    humanRoamDir:
      process.env.ORG_MEMORY_HUMAN_ROAM_DIR ??
      (cfg.humanRoamDir as string | undefined) ??
      join(humanDir, "roam"),
    agentDb:
      process.env.ORG_MEMORY_AGENT_DATABASE_LOCATION ??
      (cfg.agentDb as string | undefined) ??
      join(agentDir, "roam/.org.db"),
    humanDb:
      process.env.ORG_MEMORY_HUMAN_DATABASE_LOCATION ??
      (cfg.humanDb as string | undefined) ??
      join(humanDir, "roam/.org.db"),
    orgBin:
      process.env.ORG_MEMORY_ORG_BIN ??
      (cfg.orgBin as string | undefined) ??
      "org",
    inboxFile:
      process.env.ORG_MEMORY_INBOX_FILE ??
      (cfg.inboxFile as string | undefined) ??
      "inbox.org",
  };
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function runOrg(
  bin: string,
  args: string[],
  timeoutMs = 10_000,
): Promise<{ stdout: string; stderr: string }> {
  return new Promise((resolve, reject) => {
    execFile(bin, args, { timeout: timeoutMs }, (err, stdout, stderr) => {
      if (err) {
        reject(new Error(`org ${args[0]} failed: ${stderr || err.message}`));
      } else {
        resolve({ stdout, stderr });
      }
    });
  });
}

async function readOrgFile(path: string): Promise<string | null> {
  try {
    return await readFile(path, "utf-8");
  } catch {
    return null;
  }
}

function todayStr(): string {
  const d = new Date();
  const y = d.getFullYear();
  const m = String(d.getMonth() + 1).padStart(2, "0");
  const day = String(d.getDate()).padStart(2, "0");
  return `${y}-${m}-${day}`;
}

function yesterdayStr(): string {
  const d = new Date();
  d.setDate(d.getDate() - 1);
  const y = d.getFullYear();
  const m = String(d.getMonth() + 1).padStart(2, "0");
  const day = String(d.getDate()).padStart(2, "0");
  return `${y}-${m}-${day}`;
}

// ---------------------------------------------------------------------------
// Plugin
// ---------------------------------------------------------------------------

const orgMemoryPlugin = {
  id: "org-memory",
  name: "org-memory",
  description:
    "Structured knowledge base and task management using org-mode files and the org CLI.",
  kind: "memory" as const,

  register(api: OpenClawPluginApi) {
    const cfg = resolveConfig(api.pluginConfig);

    api.logger.info(
      `org-memory: agentDir=${cfg.agentDir}, agentRoamDir=${cfg.agentRoamDir}, orgBin=${cfg.orgBin}`,
    );

    // ======================================================================
    // Session-start hook: inject memory.org + daily files as context
    // ======================================================================

    api.on("before_agent_start", async () => {
      const parts: string[] = [];

      // Inject shortcut instructions so the agent knows how to handle quick-capture
      parts.push(`<org-memory-instructions>
## Quick-Capture Shortcuts

These shortcuts trigger org-cli actions. Act on them immediately, confirm briefly.

| Prefix | Alias | Target | Action |
|---|---|---|---|
| \`t:\` | \`Todo:\` | Human's org | Create TODO in inbox.org (extract dates → --scheduled/--deadline) |
| \`d:\` | \`Done:\` / \`Finished:\` | Human's org | Mark matching TODO as DONE |
| \`s:\` | | Human's org | Reschedule a TODO to a new date |
| \`r:\` | \`Note:\` | Human's roam | Save knowledge/info to human's roam |
| \`k:\` | \`Know:\` / \`Remember:\` | Agent's roam | Agent learns — store in agent's knowledge base |

### Rules
- **Todo vs Note**: Both create TODOs. \`t:\` is a concrete task (always extract a date if present). \`Note:\` is broader (ideas, reminders). When no date, add without one.
- **Done**: Search for matching TODO first. If multiple matches, ask which one. If no match, say so.
- **Know**: Search for existing node first (\`org roam node find\`), then create or update. Never create duplicates.
- **Schedule**: Use \`--deadline\` for hard due dates ("by Friday"). Use \`--scheduled\` for softer timing ("next week").
- **After every write**: Confirm what you did: \`org-memory: <action> <file-path>\`
- **Shell safety**: Always single-quote user-provided text in org-cli commands. Use \`--stdin\` for multi-line content.
- **Always use \`-f json\`** for structured output and **\`--db\`** to point at the correct database.

### Directories
- Human's org workspace: \`${cfg.humanDir}\` (tasks, projects, inbox.org)
- Human's roam nodes: \`${cfg.humanRoamDir}\` (knowledge, people, concepts)
- Agent's org workspace: \`${cfg.agentDir}\` (agent's own files)
- Agent's roam nodes: \`${cfg.agentRoamDir}\` (agent's knowledge base)
- Daily notes: \`${cfg.agentDir}/daily/YYYY-MM-DD.org\`

**Important:** Roam nodes are ONLY created in the roam directories, never in the workspace root. The plugin tools handle this automatically.

### Ambient Capture
When the human mentions facts in passing (a person's preference, a date, a relationship) that have lasting value, offer to save them to the agent's knowledge base. Complete the explicit request first, then tell the human what you'd like to capture and confirm before writing. Always print \`org-memory: <action> <file-path>\` after every write.
</org-memory-instructions>`);

      const memoryOrg = await readOrgFile(join(cfg.agentDir, "memory.org"));
      if (memoryOrg) {
        parts.push(`<org-memory-file path="memory.org">\n${memoryOrg}\n</org-memory-file>`);
      }

      const today = todayStr();
      const yesterday = yesterdayStr();

      const todayContent = await readOrgFile(
        join(cfg.agentDir, "daily", `${today}.org`),
      );
      if (todayContent) {
        parts.push(
          `<org-memory-file path="daily/${today}.org">\n${todayContent}\n</org-memory-file>`,
        );
      }

      const yesterdayContent = await readOrgFile(
        join(cfg.agentDir, "daily", `${yesterday}.org`),
      );
      if (yesterdayContent) {
        parts.push(
          `<org-memory-file path="daily/${yesterday}.org">\n${yesterdayContent}\n</org-memory-file>`,
        );
      }

      if (parts.length === 0) {
        return;
      }

      return {
        prependContext: `<org-memory>\n${parts.join("\n")}\n</org-memory>`,
      };
    });

    // ======================================================================
    // Tool: org_memory_search — full-text search across org files
    // ======================================================================

    api.registerTool(
      {
        name: "org_memory_search",
        label: "Org Memory Search",
        description:
          "Full-text search across org files in the agent's knowledge base. Returns matching headlines and snippets.",
        parameters: Type.Object({
          query: Type.String({ description: "FTS5 search query" }),
          dir: Type.Optional(
            Type.Union([Type.Literal("agent"), Type.Literal("human")], {
              description: 'Which directory to search: "agent" or "human" (default: "agent")',
            }),
          ),
        }),
        async execute(_id, params) {
          const { query, dir = "agent" } = params as {
            query: string;
            dir?: "agent" | "human";
          };
          const d = dir === "human" ? cfg.humanDir : cfg.agentDir;
          const db = dir === "human" ? cfg.humanDb : cfg.agentDb;

          try {
            const { stdout } = await runOrg(cfg.orgBin, [
              "fts",
              query,
              "-d",
              d,
              "--db",
              db,
              "-f",
              "json",
            ]);
            return {
              content: [{ type: "text" as const, text: stdout }],
              details: { dir },
            };
          } catch (err) {
            return {
              content: [
                { type: "text" as const, text: `Search failed: ${String(err)}` },
              ],
              details: { error: true },
            };
          }
        },
      },
      { optional: true },
    );

    // ======================================================================
    // Tool: org_memory_read_node — read a roam node by title or id
    // ======================================================================

    api.registerTool(
      {
        name: "org_memory_read_node",
        label: "Org Memory Read Node",
        description:
          "Read a roam node by title or ID from the agent's knowledge base. Returns the full node content.",
        parameters: Type.Object({
          identifier: Type.String({
            description: "Node title, org-id (UUID), or CUSTOM_ID",
          }),
          dir: Type.Optional(
            Type.Union([Type.Literal("agent"), Type.Literal("human")], {
              description: 'Which directory: "agent" or "human" (default: "agent")',
            }),
          ),
        }),
        async execute(_id, params) {
          const { identifier, dir = "agent" } = params as {
            identifier: string;
            dir?: "agent" | "human";
          };
          const roamDir = dir === "human" ? cfg.humanRoamDir : cfg.agentRoamDir;
          const db = dir === "human" ? cfg.humanDb : cfg.agentDb;

          try {
            // First find the node to get its file path
            const { stdout: findOut } = await runOrg(cfg.orgBin, [
              "roam",
              "node",
              "find",
              identifier,
              "-d",
              roamDir,
              "--db",
              db,
              "-f",
              "json",
            ]);

            const result = JSON.parse(findOut);
            if (!result.ok || !result.data) {
              return {
                content: [
                  {
                    type: "text" as const,
                    text: `Node not found: ${identifier}`,
                  },
                ],
                details: { found: false },
              };
            }

            // Use `org read` to get just the subtree (or full file for file-level nodes)
            const filePath = result.data.file;
            if (filePath) {
              const { stdout: readOut } = await runOrg(cfg.orgBin, [
                "read",
                filePath,
                identifier,
                "-d",
                roamDir,
                "--db",
                db,
                "-f",
                "json",
              ]);
              return {
                content: [{ type: "text" as const, text: readOut }],
                details: { node: result.data },
              };
            }

            return {
              content: [{ type: "text" as const, text: findOut }],
              details: { node: result.data },
            };
          } catch (err) {
            return {
              content: [
                {
                  type: "text" as const,
                  text: `Read node failed: ${String(err)}`,
                },
              ],
              details: { error: true },
            };
          }
        },
      },
      { optional: true },
    );

    // ======================================================================
    // Tool: org_todo_add — add a TODO to the human's inbox
    // ======================================================================

    api.registerTool(
      {
        name: "org_todo_add",
        label: "Org Todo Add",
        description:
          "Add a TODO headline to the human's inbox.org. Optionally schedule or set a deadline.",
        parameters: Type.Object({
          title: Type.String({ description: "TODO title" }),
          scheduled: Type.Optional(
            Type.String({ description: "Scheduled date (YYYY-MM-DD)" }),
          ),
          deadline: Type.Optional(
            Type.String({ description: "Deadline date (YYYY-MM-DD)" }),
          ),
        }),
        async execute(_id, params) {
          const { title, scheduled, deadline } = params as {
            title: string;
            scheduled?: string;
            deadline?: string;
          };

          const args = [
            "add",
            join(cfg.humanDir, cfg.inboxFile),
            title,
            "--todo",
            "TODO",
            "--db",
            cfg.humanDb,
            "-f",
            "json",
          ];
          if (scheduled) {
            args.push("--scheduled", scheduled);
          }
          if (deadline) {
            args.push("--deadline", deadline);
          }

          try {
            const { stdout } = await runOrg(cfg.orgBin, args);
            return {
              content: [{ type: "text" as const, text: stdout }],
              details: { action: "added" },
            };
          } catch (err) {
            return {
              content: [
                {
                  type: "text" as const,
                  text: `Failed to add TODO: ${String(err)}`,
                },
              ],
              details: { error: true },
            };
          }
        },
      },
      { optional: true },
    );

    // ======================================================================
    // Tool: org_todo_done — mark a TODO done by CUSTOM_ID
    // ======================================================================

    api.registerTool(
      {
        name: "org_todo_done",
        label: "Org Todo Done",
        description:
          "Mark a TODO as DONE by its CUSTOM_ID. Use org_memory_search to find the ID first if needed.",
        parameters: Type.Object({
          customId: Type.String({
            description: "The CUSTOM_ID of the headline to mark DONE",
          }),
        }),
        async execute(_id, params) {
          const { customId } = params as { customId: string };

          try {
            const { stdout } = await runOrg(cfg.orgBin, [
              "todo",
              "set",
              customId,
              "DONE",
              "-d",
              cfg.humanDir,
              "--db",
              cfg.humanDb,
              "-f",
              "json",
            ]);
            return {
              content: [{ type: "text" as const, text: stdout }],
              details: { action: "done", customId },
            };
          } catch (err) {
            return {
              content: [
                {
                  type: "text" as const,
                  text: `Failed to mark DONE: ${String(err)}`,
                },
              ],
              details: { error: true },
            };
          }
        },
      },
      { optional: true },
    );

    // ======================================================================
    // Tool: org_todo_list — show today's agenda / upcoming tasks
    // ======================================================================

    api.registerTool(
      {
        name: "org_todo_list",
        label: "Org Todo List",
        description:
          "Show today's agenda and upcoming tasks. Returns scheduled, deadline, and active TODO items.",
        parameters: Type.Object({
          dir: Type.Optional(
            Type.Union([Type.Literal("agent"), Type.Literal("human")], {
              description: 'Which directory: "agent" or "human" (default: "human")',
            }),
          ),
        }),
        async execute(_id, params) {
          const { dir = "human" } = params as { dir?: "agent" | "human" };
          const d = dir === "human" ? cfg.humanDir : cfg.agentDir;
          const db = dir === "human" ? cfg.humanDb : cfg.agentDb;

          try {
            const { stdout } = await runOrg(cfg.orgBin, [
              "today",
              "-d",
              d,
              "--db",
              db,
              "-f",
              "json",
            ]);
            return {
              content: [{ type: "text" as const, text: stdout }],
              details: { dir },
            };
          } catch (err) {
            return {
              content: [
                { type: "text" as const, text: `Todo list failed: ${String(err)}` },
              ],
              details: { error: true },
            };
          }
        },
      },
      { optional: true },
    );

    // ======================================================================
    // Tool: org_memory_append — append text to a headline body
    // ======================================================================

    api.registerTool(
      {
        name: "org_memory_append",
        label: "Org Memory Append",
        description:
          "Append text to a headline's body by its CUSTOM_ID. Use for adding notes, observations, or content to existing knowledge nodes.",
        parameters: Type.Object({
          customId: Type.String({
            description: "The CUSTOM_ID of the headline to append to",
          }),
          text: Type.String({ description: "Text to append to the headline body" }),
          dir: Type.Optional(
            Type.Union([Type.Literal("agent"), Type.Literal("human")], {
              description: 'Which directory: "agent" or "human" (default: "agent")',
            }),
          ),
        }),
        async execute(_id, params) {
          const { customId, text, dir = "agent" } = params as {
            customId: string;
            text: string;
            dir?: "agent" | "human";
          };
          const d = dir === "human" ? cfg.humanDir : cfg.agentDir;
          const db = dir === "human" ? cfg.humanDb : cfg.agentDb;

          try {
            const { stdout } = await runOrg(cfg.orgBin, [
              "append",
              customId,
              text,
              "-d",
              d,
              "--db",
              db,
              "-f",
              "json",
            ]);
            return {
              content: [{ type: "text" as const, text: stdout }],
              details: { action: "appended", customId },
            };
          } catch (err) {
            return {
              content: [
                { type: "text" as const, text: `Append failed: ${String(err)}` },
              ],
              details: { error: true },
            };
          }
        },
      },
      { optional: true },
    );

    // ======================================================================
    // Tool: org_roam_create — create a new roam node
    // ======================================================================

    api.registerTool(
      {
        name: "org_roam_create",
        label: "Org Roam Create",
        description:
          "Create a new roam node (org file) with the given title and optional tags.",
        parameters: Type.Object({
          title: Type.String({ description: "Title for the new node" }),
          tags: Type.Optional(
            Type.Array(Type.String(), {
              description: "Tags to apply to the node",
            }),
          ),
          dir: Type.Optional(
            Type.Union([Type.Literal("agent"), Type.Literal("human")], {
              description: 'Which directory: "agent" or "human" (default: "agent")',
            }),
          ),
        }),
        async execute(_id, params) {
          const { title, tags, dir = "agent" } = params as {
            title: string;
            tags?: string[];
            dir?: "agent" | "human";
          };
          const roamDir = dir === "human" ? cfg.humanRoamDir : cfg.agentRoamDir;
          const db = dir === "human" ? cfg.humanDb : cfg.agentDb;

          const args = ["roam", "node", "create", title];
          if (tags) {
            for (const tag of tags) {
              args.push("-t", tag);
            }
          }
          args.push("-d", roamDir, "--db", db, "-f", "json");

          try {
            const { stdout } = await runOrg(cfg.orgBin, args);
            return {
              content: [{ type: "text" as const, text: stdout }],
              details: { action: "created", title },
            };
          } catch (err) {
            return {
              content: [
                { type: "text" as const, text: `Create node failed: ${String(err)}` },
              ],
              details: { error: true },
            };
          }
        },
      },
      { optional: true },
    );

    // ======================================================================
    // Tool: org_todo_reschedule — reschedule a TODO by CUSTOM_ID
    // ======================================================================

    api.registerTool(
      {
        name: "org_todo_reschedule",
        label: "Org Todo Reschedule",
        description:
          'Reschedule a TODO by its CUSTOM_ID. Pass a date in YYYY-MM-DD format, or "" to clear the schedule.',
        parameters: Type.Object({
          customId: Type.String({
            description: "The CUSTOM_ID of the headline to reschedule",
          }),
          date: Type.String({
            description: 'New scheduled date (YYYY-MM-DD) or "" to clear',
          }),
          repeater: Type.Optional(
            Type.String({
              description:
                "Repeater: +N[hdwmy], ++N[hdwmy], or .+N[hdwmy] (e.g. +1w, .+1d)",
            }),
          ),
          delay: Type.Optional(
            Type.String({
              description: "Warning delay: N[hdwmy] (e.g. 2d for -2d)",
            }),
          ),
          dir: Type.Optional(
            Type.Union([Type.Literal("agent"), Type.Literal("human")], {
              description: 'Which directory: "agent" or "human" (default: "human")',
            }),
          ),
        }),
        async execute(_id, params) {
          const { customId, date, repeater, delay, dir = "human" } = params as {
            customId: string;
            date: string;
            repeater?: string;
            delay?: string;
            dir?: "agent" | "human";
          };
          const d = dir === "human" ? cfg.humanDir : cfg.agentDir;
          const db = dir === "human" ? cfg.humanDb : cfg.agentDb;

          try {
            const args = [
              "schedule",
              customId,
              date,
              "-d",
              d,
              "--db",
              db,
              "-f",
              "json",
            ];
            if (repeater) {
              args.push("--repeater", repeater);
            }
            if (delay) {
              args.push("--delay", delay);
            }
            const { stdout } = await runOrg(cfg.orgBin, args);
            return {
              content: [{ type: "text" as const, text: stdout }],
              details: { action: "rescheduled", customId, date },
            };
          } catch (err) {
            return {
              content: [
                { type: "text" as const, text: `Reschedule failed: ${String(err)}` },
              ],
              details: { error: true },
            };
          }
        },
      },
      { optional: true },
    );

    // ======================================================================
    // Service
    // ======================================================================

    api.registerService({
      id: "org-memory",
      start: () => {
        api.logger.info(
          `org-memory: started (agent: ${cfg.agentDir}, agentRoam: ${cfg.agentRoamDir}, human: ${cfg.humanDir}, humanRoam: ${cfg.humanRoamDir})`,
        );
      },
      stop: () => {
        api.logger.info("org-memory: stopped");
      },
    });
  },
};

export default orgMemoryPlugin;
