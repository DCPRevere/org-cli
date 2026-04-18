import { join } from "node:path";
import { Type } from "@sinclair/typebox";
import { definePluginEntry } from "openclaw/plugin-sdk/plugin-entry";
import type { OpenClawPluginApi } from "openclaw/plugin-sdk/plugin-entry";
import {
  DATE_PATTERN,
  DATE_OR_EMPTY_PATTERN,
  READ_TIMEOUT_MS,
  MAX_FILE_BYTES,
  buildAddTodoArgs,
  buildAddNoteArgs,
  formatAddedTodo,
  formatAddedNote,
  formatCreatedNode,
  readOrgFile,
  resolveConfig,
  runOrg,
  todayStr,
  yesterdayStr,
} from "./lib.ts";

export {
  resolveConfig,
  formatAddedTodo,
  formatAddedNote,
  formatCreatedNode,
  formatOrgError,
  buildAddTodoArgs,
  buildAddNoteArgs,
} from "./lib.ts";
export type { OrgMemoryConfig } from "./lib.ts";

export default definePluginEntry({
  id: "org-memory",
  name: "org-memory",
  description:
    "Agent-memory extension for org-cli: persists agent knowledge and daily notes to an org workspace, overriding memory-wiki.",
  kind: "memory",

  register(api: OpenClawPluginApi) {
    const cfg = resolveConfig(api.pluginConfig);

    api.logger.info(
      `org-memory: dir=${cfg.dir}, roamDir=${cfg.roamDir}, orgBin=${cfg.orgBin}`,
    );

    // ======================================================================
    // Session-start hook: inject the @a shortcut grammar plus agent memory.
    //
    // Requires the org-cli skill for the base shortcut grammar and CLI
    // contract. This hook only adds the @a-prefixed extensions and the
    // agent-workspace context files (memory.org, daily/*.org).
    // ======================================================================

    api.on("before_agent_start", async () => {
      const parts: string[] = [];

      parts.push(`<org-memory-instructions>
## Agent-side shortcut extensions

These extend the \`org-cli\` shortcut grammar. Bare shortcuts (\`t:\`, \`n:\`, \`k:\`, \`d:\`, \`s:\`, \`f:\`) target the **user's** workspace (see org-cli). The \`@a\` prefix targets *your* workspace.

| Bare (user) | Agent extension | Action |
|---|---|---|
| \`t:\` | \`@at:\` | Create TODO in your inbox |
| \`n:\` | \`@an:\` | Plain headline in your inbox |
| \`k:\` | \`@ak:\` | Knowledge in your roam graph |
| \`d:\` | \`@ad:\` | Mark one of your TODOs DONE |
| \`s:\` | \`@as:\` | Reschedule one of your TODOs |
| \`f:\` | \`@af:\` | Search your org files and roam |

## Memory-wiki override

When this skill is active, store all persistent graph-structured knowledge (entities, relationships) in your org workspace — not in memory-wiki. Flat typed memory (user/feedback/project/reference) can still go to MEMORY.md as usual; graph-structured memory goes to \`@ak\`.

## Your workspace
- Directory: \`${cfg.dir}\` (memory.org, daily/, tasks)
- Roam nodes: \`${cfg.roamDir}\`
- Daily notes: \`${cfg.dir}/daily/YYYY-MM-DD.org\`

### Rules
- **After every write**: print \`org-memory: <action> [<id>] <file-path>\`.
- **Argument passing**: These plugin tools invoke org via execFile — pass raw text, do not quote or escape.
- **Ambient facts about the user** → offer to save via \`@ak\` against the relevant subject.
- **Working notes** → append to today's daily file.
- **Things worth keeping permanently** → also update \`memory.org\`.
</org-memory-instructions>`);

      // Load memory.org + today/yesterday daily so the agent starts with context.
      const memoryOrg = await readOrgFile(join(cfg.dir, "memory.org"), MAX_FILE_BYTES);
      if (memoryOrg) {
        parts.push(`<org-memory-file path="memory.org">\n${memoryOrg}\n</org-memory-file>`);
      }

      const today = todayStr();
      const yesterday = yesterdayStr();

      const todayContent = await readOrgFile(
        join(cfg.dir, "daily", `${today}.org`),
        MAX_FILE_BYTES,
      );
      if (todayContent) {
        parts.push(
          `<org-memory-file path="daily/${today}.org">\n${todayContent}\n</org-memory-file>`,
        );
      }

      const yesterdayContent = await readOrgFile(
        join(cfg.dir, "daily", `${yesterday}.org`),
        MAX_FILE_BYTES,
      );
      if (yesterdayContent) {
        parts.push(
          `<org-memory-file path="daily/${yesterday}.org">\n${yesterdayContent}\n</org-memory-file>`,
        );
      }

      return {
        prependContext: `<org-memory>\n${parts.join("\n")}\n</org-memory>`,
      };
    });

    // ======================================================================
    // Tool: org_memory_find — full-text search across agent org files
    // ======================================================================

    api.registerTool(
      {
        name: "org_memory_find",
        label: "Org Memory Find",
        description:
          "Full-text search across the agent's own org workspace (titles + bodies). Returns matching headlines with IDs. No mutation.",
        parameters: Type.Object({
          query: Type.String({ description: "FTS5 search query" }),
        }),
        async execute(_id, params) {
          const { query } = params as { query: string };
          try {
            const { stdout } = await runOrg(
              cfg.orgBin,
              ["fts", query, "-d", cfg.dir, "--db", cfg.db, "-f", "json"],
              READ_TIMEOUT_MS,
            );
            return {
              content: [{ type: "text" as const, text: stdout }],
              details: {},
            };
          } catch (err) {
            return {
              content: [
                { type: "text" as const, text: `Find failed: ${String(err)}` },
              ],
              details: { error: true },
            };
          }
        },
      },
      { optional: true },
    );

    // ======================================================================
    // Tool: org_memory_read_node — read a roam node
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
        }),
        async execute(_id, params) {
          const { identifier } = params as { identifier: string };

          try {
            const { stdout: findOut } = await runOrg(
              cfg.orgBin,
              ["roam", "node", "find", identifier, "-d", cfg.roamDir, "--db", cfg.db, "-f", "json"],
              READ_TIMEOUT_MS,
            );

            const result = JSON.parse(findOut);
            if (!result.ok || !result.data) {
              return {
                content: [
                  { type: "text" as const, text: `Node not found: ${identifier}` },
                ],
                details: { found: false },
              };
            }

            const filePath = result.data.file;
            if (filePath) {
              const { stdout: readOut } = await runOrg(
                cfg.orgBin,
                ["read", filePath, identifier, "-d", cfg.dir, "--db", cfg.db, "-f", "json"],
                READ_TIMEOUT_MS,
              );
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
                { type: "text" as const, text: `Read node failed: ${String(err)}` },
              ],
              details: { error: true },
            };
          }
        },
      },
      { optional: true },
    );

    // ======================================================================
    // Tool: org_memory_add_todo — @at
    // ======================================================================

    api.registerTool(
      {
        name: "org_memory_add_todo",
        label: "Org Memory Add TODO",
        description:
          "Add a TODO headline to the agent's workspace (defaults to the agent's inbox.org). Optionally schedule or set a deadline.",
        parameters: Type.Object({
          title: Type.String({ description: "TODO title" }),
          scheduled: Type.Optional(
            Type.String({
              description: "Scheduled date (YYYY-MM-DD)",
              pattern: DATE_PATTERN,
            }),
          ),
          deadline: Type.Optional(
            Type.String({
              description: "Deadline date (YYYY-MM-DD)",
              pattern: DATE_PATTERN,
            }),
          ),
          file: Type.Optional(
            Type.String({
              description: "Filename relative to the workspace dir (default: inboxFile)",
            }),
          ),
        }),
        async execute(_id, params) {
          const typed = params as {
            title: string;
            scheduled?: string;
            deadline?: string;
            file?: string;
          };
          const args = buildAddTodoArgs(cfg, typed);
          try {
            const { stdout } = await runOrg(cfg.orgBin, args);
            return {
              content: [{ type: "text" as const, text: formatAddedTodo(stdout) }],
              details: { action: "added-todo" },
            };
          } catch (err) {
            return {
              content: [
                { type: "text" as const, text: `Failed to add TODO: ${String(err)}` },
              ],
              details: { error: true },
            };
          }
        },
      },
      { optional: true },
    );

    // ======================================================================
    // Tool: org_memory_add_note — @an
    // ======================================================================

    api.registerTool(
      {
        name: "org_memory_add_note",
        label: "Org Memory Add Note",
        description:
          "Add a plain headline (no TODO, no date) to the agent's workspace. Use for captured thoughts, observations, working notes.",
        parameters: Type.Object({
          text: Type.String({ description: "Note text (becomes the headline title)" }),
          file: Type.Optional(
            Type.String({
              description: "Filename relative to the workspace dir (default: inboxFile)",
            }),
          ),
        }),
        async execute(_id, params) {
          const typed = params as { text: string; file?: string };
          const args = buildAddNoteArgs(cfg, typed);
          try {
            const { stdout } = await runOrg(cfg.orgBin, args);
            return {
              content: [{ type: "text" as const, text: formatAddedNote(stdout) }],
              details: { action: "added-note" },
            };
          } catch (err) {
            return {
              content: [
                { type: "text" as const, text: `Failed to add note: ${String(err)}` },
              ],
              details: { error: true },
            };
          }
        },
      },
      { optional: true },
    );

    // ======================================================================
    // Tool: org_memory_mark_done — @ad
    // ======================================================================

    api.registerTool(
      {
        name: "org_memory_mark_done",
        label: "Org Memory Mark Done",
        description:
          "Mark one of the agent's own TODOs as DONE by its CUSTOM_ID. For other states use org_memory_set_state.",
        parameters: Type.Object({
          customId: Type.String({
            description: "The CUSTOM_ID of the headline to mark DONE",
          }),
        }),
        async execute(_id, params) {
          const { customId } = params as { customId: string };
          try {
            const { stdout } = await runOrg(cfg.orgBin, [
              "todo", "set", customId, "DONE",
              "-d", cfg.dir, "--db", cfg.db, "-f", "json",
            ]);
            return {
              content: [{ type: "text" as const, text: stdout }],
              details: { action: "done", customId },
            };
          } catch (err) {
            return {
              content: [
                { type: "text" as const, text: `Failed to mark DONE: ${String(err)}` },
              ],
              details: { error: true },
            };
          }
        },
      },
      { optional: true },
    );

    // ======================================================================
    // Tool: org_memory_set_state — any state
    // ======================================================================

    api.registerTool(
      {
        name: "org_memory_set_state",
        label: "Org Memory Set State",
        description:
          "Set an agent TODO to any state (DONE, CANCELLED, WAITING, or custom #+TODO keywords) by CUSTOM_ID.",
        parameters: Type.Object({
          customId: Type.String({ description: "The CUSTOM_ID of the headline" }),
          state: Type.String({ description: "Target TODO state" }),
        }),
        async execute(_id, params) {
          const { customId, state } = params as { customId: string; state: string };
          try {
            const { stdout } = await runOrg(cfg.orgBin, [
              "todo", "set", customId, state,
              "-d", cfg.dir, "--db", cfg.db, "-f", "json",
            ]);
            return {
              content: [{ type: "text" as const, text: stdout }],
              details: { action: "state-set", customId, state },
            };
          } catch (err) {
            return {
              content: [
                { type: "text" as const, text: `Failed to set state: ${String(err)}` },
              ],
              details: { error: true },
            };
          }
        },
      },
      { optional: true },
    );

    // ======================================================================
    // Tool: org_memory_reschedule — @as
    // ======================================================================

    api.registerTool(
      {
        name: "org_memory_reschedule",
        label: "Org Memory Reschedule",
        description:
          'Reschedule one of the agent\'s TODOs by its CUSTOM_ID. Pass a date in YYYY-MM-DD, or "" to clear.',
        parameters: Type.Object({
          customId: Type.String({
            description: "The CUSTOM_ID of the headline to reschedule",
          }),
          date: Type.String({
            description: 'New scheduled date (YYYY-MM-DD) or "" to clear',
            pattern: DATE_OR_EMPTY_PATTERN,
          }),
          repeater: Type.Optional(
            Type.String({
              description: "Repeater: +N[hdwmy], ++N[hdwmy], or .+N[hdwmy]",
            }),
          ),
          delay: Type.Optional(
            Type.String({
              description: "Warning delay: N[hdwmy] (e.g. 2d for -2d)",
            }),
          ),
        }),
        async execute(_id, params) {
          const { customId, date, repeater, delay } = params as {
            customId: string;
            date: string;
            repeater?: string;
            delay?: string;
          };
          try {
            const args = [
              "schedule", customId, date,
              "-d", cfg.dir, "--db", cfg.db, "-f", "json",
            ];
            if (repeater) args.push("--repeater", repeater);
            if (delay) args.push("--delay", delay);
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
    // Tool: org_memory_list_todos — today's agenda (agent)
    // ======================================================================

    api.registerTool(
      {
        name: "org_memory_list_todos",
        label: "Org Memory List TODOs",
        description:
          "Show the agent's own TODOs for today + overdue.",
        parameters: Type.Object({}),
        async execute() {
          try {
            const { stdout } = await runOrg(
              cfg.orgBin,
              ["today", "-d", cfg.dir, "--db", cfg.db, "-f", "json"],
              READ_TIMEOUT_MS,
            );
            return {
              content: [{ type: "text" as const, text: stdout }],
              details: {},
            };
          } catch (err) {
            return {
              content: [
                { type: "text" as const, text: `List TODOs failed: ${String(err)}` },
              ],
              details: { error: true },
            };
          }
        },
      },
      { optional: true },
    );

    // ======================================================================
    // Tool: org_memory_append — append to any headline body
    // ======================================================================

    api.registerTool(
      {
        name: "org_memory_append",
        label: "Org Memory Append",
        description:
          "Append text to an agent headline's body by its CUSTOM_ID. Use for adding observations to existing knowledge nodes.",
        parameters: Type.Object({
          customId: Type.String({
            description: "The CUSTOM_ID of the headline to append to",
          }),
          text: Type.String({ description: "Text to append to the headline body" }),
        }),
        async execute(_id, params) {
          const { customId, text } = params as { customId: string; text: string };
          try {
            const { stdout } = await runOrg(cfg.orgBin, [
              "append", customId, text,
              "-d", cfg.dir, "--db", cfg.db, "-f", "json",
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
    // Tool: org_memory_roam_upsert — @ak — find-or-create node, append fact
    // ======================================================================

    api.registerTool(
      {
        name: "org_memory_roam_upsert",
        label: "Org Memory Roam Upsert",
        description:
          "Store a fact in the agent's roam graph against a subject. If a node for the subject exists, appends to it; otherwise creates a new node and appends. This is the `@ak` shortcut.",
        parameters: Type.Object({
          subject: Type.String({
            description: "Subject of the knowledge (e.g. person/concept name)",
          }),
          fact: Type.String({
            description: "The fact/note to record against the subject",
          }),
          tags: Type.Optional(
            Type.Array(Type.String(), {
              description: "Tags to apply if the node is newly created",
            }),
          ),
        }),
        async execute(_id, params) {
          const { subject, fact, tags } = params as {
            subject: string;
            fact: string;
            tags?: string[];
          };

          try {
            const { stdout: findOut } = await runOrg(
              cfg.orgBin,
              ["roam", "node", "find", subject, "-d", cfg.roamDir, "--db", cfg.db, "-f", "json"],
              READ_TIMEOUT_MS,
            );
            const found = JSON.parse(findOut);

            let customId: string | undefined = found?.data?.custom_id;

            if (!found?.ok || !found?.data) {
              const createArgs = ["roam", "node", "create", subject];
              if (tags) {
                for (const tag of tags) createArgs.push("-t", tag);
              }
              createArgs.push("-d", cfg.roamDir, "--db", cfg.db, "-f", "json");
              const { stdout: createOut } = await runOrg(cfg.orgBin, createArgs);
              const created = JSON.parse(createOut);
              customId = created?.data?.custom_id;
              if (!customId) {
                return {
                  content: [{ type: "text" as const, text: formatCreatedNode(createOut) }],
                  details: { action: "created-no-id" },
                };
              }
            }

            if (!customId) {
              return {
                content: [
                  { type: "text" as const, text: `Node lookup returned no custom_id for: ${subject}` },
                ],
                details: { error: true },
              };
            }

            const { stdout: appendOut } = await runOrg(cfg.orgBin, [
              "append", customId, fact,
              "-d", cfg.dir, "--db", cfg.db, "-f", "json",
            ]);
            return {
              content: [{ type: "text" as const, text: appendOut }],
              details: { action: "upserted", subject, customId },
            };
          } catch (err) {
            return {
              content: [
                { type: "text" as const, text: `Roam upsert failed: ${String(err)}` },
              ],
              details: { error: true },
            };
          }
        },
      },
      { optional: true },
    );
  },
});
