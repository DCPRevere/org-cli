import { Type } from "@sinclair/typebox";
import { definePluginEntry } from "openclaw/plugin-sdk/plugin-entry";
import type { OpenClawPluginApi } from "openclaw/plugin-sdk/plugin-entry";
import {
  DATE_PATTERN,
  DATE_OR_EMPTY_PATTERN,
  READ_TIMEOUT_MS,
  buildAddTodoArgs,
  buildAddNoteArgs,
  formatAddedTodo,
  formatAddedNote,
  formatCreatedNode,
  resolveConfig,
  runOrg,
} from "./lib.ts";

// Re-export helpers so external consumers and tests can import from the
// package root without depending on the SDK.
export {
  resolveConfig,
  formatAddedTodo,
  formatAddedNote,
  formatCreatedNode,
  formatOrgError,
  buildAddTodoArgs,
  buildAddNoteArgs,
} from "./lib.ts";
export type { OrgCliConfig } from "./lib.ts";

export default definePluginEntry({
  id: "org-cli",
  name: "org-cli",
  description:
    "Task capture, scheduling, and linked knowledge in org-mode files via the org CLI.",

  register(api: OpenClawPluginApi) {
    const cfg = resolveConfig(api.pluginConfig);

    api.logger.info(
      `org-cli: dir=${cfg.dir}, roamDir=${cfg.roamDir}, orgBin=${cfg.orgBin}`,
    );

    // ======================================================================
    // Session-start hook: inject the shortcut grammar so the agent knows
    // how to handle quick-capture prefixes the moment a session starts.
    // ======================================================================

    api.on("before_agent_start", async () => {
      const instructions = `<org-cli-instructions>
## Quick-capture shortcuts

These shortcuts trigger org-cli actions on the user's workspace. Act on them immediately, confirm briefly.

| Prefix | Target | Action |
|---|---|---|
| \`t:\` | inbox | Create TODO in inbox.org (extract dates → --scheduled/--deadline) |
| \`n:\` | inbox | Create plain headline (no TODO, no date) |
| \`k:\` | roam | Save knowledge/info to the roam graph |
| \`d:\` | workspace | Mark matching TODO as DONE |
| \`s:\` | workspace | Reschedule a TODO to a new date |
| \`f:\` | all | Find matching headlines and roam nodes |

### Rules
- **Todo vs Note**: \`t:\` creates a TODO (dated or dateless). \`n:\` creates a plain headline, no TODO keyword — use for captured thoughts, not commitments.
- **Done**: Search for a matching TODO first. If multiple matches, ask which. If no match, say so.
- **Know**: Find an existing node first, append if found, create+append if not. Never duplicate.
- **Schedule**: \`--deadline\` for hard dates ("by Friday"); \`--scheduled\` for softer timing ("next week").
- **After every write**: print \`org-cli: <action> [<id>] <file-path>\`.
- **Argument passing**: These plugin tools invoke org via execFile — pass raw text, do not quote or escape. Use the tools' typed parameters rather than constructing shell strings.

### Workspace
- Directory: \`${cfg.dir}\` (inbox.org, tasks, projects)
- Roam nodes: \`${cfg.roamDir}\` (knowledge, people, concepts)

**Important:** Roam nodes are ONLY created in the roam directory, never in the workspace root. The plugin tools handle this automatically.

### Ambient capture
When the user mentions durable facts in passing (a preference, a date, a relationship) that have lasting value, offer to save them. Complete the explicit request first, then tell the user what you'd like to capture and confirm before writing.
</org-cli-instructions>`;

      return {
        prependContext: `<org-cli>\n${instructions}\n</org-cli>`,
      };
    });

    // ======================================================================
    // Tool: org_find — full-text search across org files + roam
    // ======================================================================

    api.registerTool(
      {
        name: "org_find",
        label: "Org Find",
        description:
          "Full-text search across the user's org files (titles + bodies). Returns matching headlines with IDs. No mutation.",
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
    // Tool: org_read_node — read a roam node by title or id
    // ======================================================================

    api.registerTool(
      {
        name: "org_read_node",
        label: "Org Read Node",
        description:
          "Read a roam node by title or ID. Returns the full node content.",
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

            // `org read` resolves the file via the workspace-level index DB,
            // not the roam subdir, so pass the workspace dir here.
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
    // Tool: org_add_todo — add a TODO (t: shortcut)
    // ======================================================================

    api.registerTool(
      {
        name: "org_add_todo",
        label: "Org Add TODO",
        description:
          "Add a TODO headline to a workspace file (defaults to inbox.org). Optionally schedule or set a deadline.",
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
    // Tool: org_add_note — add a plain note (n: shortcut)
    // ======================================================================

    api.registerTool(
      {
        name: "org_add_note",
        label: "Org Add Note",
        description:
          "Add a plain headline (no TODO, no date) to a workspace file. Use for captured thoughts that aren't action items.",
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
    // Tool: org_mark_done — mark a TODO DONE by CUSTOM_ID
    // ======================================================================

    api.registerTool(
      {
        name: "org_mark_done",
        label: "Org Mark Done",
        description:
          "Mark a TODO as DONE by its CUSTOM_ID. For other states use org_set_state.",
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
    // Tool: org_set_state — set any TODO state by CUSTOM_ID
    // ======================================================================

    api.registerTool(
      {
        name: "org_set_state",
        label: "Org Set State",
        description:
          "Set a TODO to any state (DONE, CANCELLED, WAITING, or custom #+TODO keywords) by its CUSTOM_ID.",
        parameters: Type.Object({
          customId: Type.String({
            description: "The CUSTOM_ID of the headline",
          }),
          state: Type.String({
            description: "Target TODO state (e.g. DONE, CANCELLED, WAITING)",
          }),
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
    // Tool: org_reschedule — reschedule a TODO by CUSTOM_ID
    // ======================================================================

    api.registerTool(
      {
        name: "org_reschedule",
        label: "Org Reschedule",
        description:
          'Reschedule a TODO by its CUSTOM_ID. Pass a date in YYYY-MM-DD, or "" to clear the schedule.',
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
    // Tool: org_list_todos — today's agenda
    // ======================================================================

    api.registerTool(
      {
        name: "org_list_todos",
        label: "Org List TODOs",
        description:
          "Show today's agenda and overdue tasks. Returns scheduled, deadline, and active TODO items.",
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
    // Tool: org_append — append text to a headline body by CUSTOM_ID
    // ======================================================================

    api.registerTool(
      {
        name: "org_append",
        label: "Org Append",
        description:
          "Append text to a headline's body by its CUSTOM_ID. Use for adding notes, observations, or content to existing items.",
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
    // Tool: org_roam_upsert — find-or-create a roam node, then append
    // ======================================================================

    api.registerTool(
      {
        name: "org_roam_upsert",
        label: "Org Roam Upsert",
        description:
          "Store a fact in the roam graph against a subject. If a node for the subject exists, appends to it; otherwise creates a new node and appends. This is the `k:` shortcut.",
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
            // 1. Find existing node
            const { stdout: findOut } = await runOrg(
              cfg.orgBin,
              ["roam", "node", "find", subject, "-d", cfg.roamDir, "--db", cfg.db, "-f", "json"],
              READ_TIMEOUT_MS,
            );
            const found = JSON.parse(findOut);

            let customId: string | undefined = found?.data?.custom_id;

            // 2. If not found, create it
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

            // 3. Append the fact
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
