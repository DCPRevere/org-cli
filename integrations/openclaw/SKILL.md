---
name: org-memory
description: "Structured knowledge base and task management using org-mode files. Query, mutate, link, and search org files and org-roam databases with the `org` CLI."
metadata: {"openclaw":{"emoji":"🦄","requires":{"bins":["org"]},"install":[{"id":"github-release","kind":"manual","label":"Download from GitHub releases: https://github.com/dcprevere/org-cli/releases"}]}}
---

# org-memory

Use the `org` CLI to maintain structured, linked, human-readable knowledge in org-mode files. Org files are plain text with rich structure: headlines, TODO states, tags, properties, timestamps, and links. Combined with org-roam, they form a knowledge graph backed by a SQLite database.

## Shortcuts

When your human uses these patterns, act immediately — no confirmation needed.

### NOTE — for the human

`Note: <text>` means "add this to MY org files." It is always a task or reminder for the *human*, not for the agent.

Action: `org add $ORG_MEMORY_HUMAN_DIR/inbox.org "<text>" --todo TODO -f json`

If the note includes a date or deadline, add `--scheduled <date>` or `--deadline <date>`. If there's no date, add it without one (the human will schedule it themselves, or ask you to).

Examples:
- "Note: Buy groceries" → `org add .../inbox.org "Buy groceries" --todo TODO`
- "Note: Review PR #42 by Friday" → `org add .../inbox.org "Review PR #42" --todo TODO --deadline 2026-02-28`
- "Note: we could add feature X to the app" → `org add .../inbox.org "Add feature X to the app" --todo TODO`
- "Note: send email to Donna about safeguarding" → `org add .../inbox.org "Send email to Donna about safeguarding" --todo TODO`

**Edge case — ideas and observations:** If the human says "Note: we could do X" or "Note: idea for Y", it's still a NOTE. They're telling you to write it down for them. Add it as a TODO. Don't create a roam node, don't put it in the agent's knowledge base.

### REMEMBER — for the agent

`Remember: <info>` means "store this in YOUR knowledge base for future recall." This is information the agent should retain across sessions.

Action: Search for an existing node first (`org roam node find`), then create or update.

Examples:
- "Remember: Sarah prefers morning meetings" → Create/update a node for Sarah in `$ORG_MEMORY_AGENT_DIR`
- "Remember: The API uses OAuth2, not API keys" → Create/update a node for the API in `$ORG_MEMORY_AGENT_DIR`

### After every write — confirm

After every mutation to either directory, print a line in this exact format:

```
org-memory: <action> <file-path>
```

Examples: `org-memory: added TODO to ~/org/human/inbox.org`, `org-memory: created node ~/org/agent/sarah.org`, `org-memory: updated ~/org/agent/sarah.org`.

**This is mandatory.** Never silently write to either directory. The human should always see what you did and where.

## Output format

All commands accept `-f json` for structured output with `{"ok":true,"data":...}` envelopes. Errors return `{"ok":false,"error":{"type":"...","message":"..."}}`. Always use `-f json`.

## Discovery

Run `org schema` once to get a machine-readable description of all commands, arguments, and flags. Use this to construct valid commands without memorizing the interface.

## Setup

Configuration is via environment variables. Set them in `openclaw.json` so they are injected into every command automatically.

| Variable | Default | Purpose |
|---|---|---|
| `ORG_MEMORY_USE_FOR_AGENT` | `true` | Enable the agent's own knowledge base |
| `ORG_MEMORY_AGENT_DIR` | `~/org/agent` | Agent's org directory |
| `ORG_MEMORY_AGENT_DATABASE_LOCATION` | `~/.local/share/org-memory/agent/.org.db` | Agent's database |
| `ORG_MEMORY_USE_FOR_HUMAN` | `true` | Enable task management in the human's org files |
| `ORG_MEMORY_HUMAN_DIR` | `~/org/human` | Human's org directory |
| `ORG_MEMORY_HUMAN_DATABASE_LOCATION` | `~/.local/share/org-memory/human/.org.db` | Human's database |

If `ORG_MEMORY_USE_FOR_AGENT` is not `true`, skip the Knowledge management section. If `ORG_MEMORY_USE_FOR_HUMAN` is not `true`, skip the Task management and Batch operations sections.

Always pass `--db` to point at the correct database. The CLI auto-syncs the roam database after every mutation using the `--db` value. Without `--db`, the CLI defaults to the emacs org-roam database (`~/.emacs.d/org-roam.db`), which is not what you want.

Initialize each enabled directory by creating a first node and building the headline index:

```bash
org roam node create "Index" -d "$ORG_MEMORY_AGENT_DIR" --db "$ORG_MEMORY_AGENT_DATABASE_LOCATION" -f json
org index -d "$ORG_MEMORY_AGENT_DIR" --db "$ORG_MEMORY_AGENT_DATABASE_LOCATION"
org index -d "$ORG_MEMORY_HUMAN_DIR" --db "$ORG_MEMORY_HUMAN_DATABASE_LOCATION"
```

The roam response includes the node's ID, file path, title, and tags. The index enables CUSTOM_ID auto-assignment and file-less commands.

## Knowledge management

This section applies when `ORG_MEMORY_USE_FOR_AGENT` is `true`.

### ⚠️ Always search before creating

Before creating a node or link, check if the entity already exists:

```bash
org roam node find "Sarah" -d "$ORG_MEMORY_AGENT_DIR" --db "$ORG_MEMORY_AGENT_DATABASE_LOCATION" -f json
```

- If found: use the existing node's ID and file path
- If not found (`headline_not_found` error): create a new node

**Never create a node without searching first.** Duplicates fragment your knowledge graph.

### Record an entity

Only after confirming no existing node:

```bash
org roam node create "Sarah" -d "$ORG_MEMORY_AGENT_DIR" --db "$ORG_MEMORY_AGENT_DATABASE_LOCATION" -t person -t work -f json
```

### Add structure to a node

Use the file path returned by create/find commands:

```bash
# Add a headline to the node (response includes auto-assigned custom_id)
org add <file> "Unavailable March 2026" --tag scheduling --db "$ORG_MEMORY_AGENT_DATABASE_LOCATION" -f json
# → {"ok":true,"data":{"custom_id":"k4t","title":"Unavailable March 2026",...}}

# Use the custom_id for follow-up commands
org note k4t "Out all of March per human." -d "$ORG_MEMORY_AGENT_DIR" --db "$ORG_MEMORY_AGENT_DATABASE_LOCATION" -f json

# Append body text to an existing headline
org append k4t "Confirmed by email on 2026-02-20." -d "$ORG_MEMORY_AGENT_DIR" --db "$ORG_MEMORY_AGENT_DATABASE_LOCATION" -f json

# Append multi-line text via stdin
echo "First paragraph.\n\nSecond paragraph." | org append k4t --stdin -d "$ORG_MEMORY_AGENT_DIR" --db "$ORG_MEMORY_AGENT_DATABASE_LOCATION" -f json
```

**`org note` vs `org append`:** `note` adds a timestamped entry to the LOGBOOK drawer (metadata). `append` adds text to the headline body (visible content). Use `note` for audit trail, `append` for building up content.

**Note:** Both commands attach to *headlines*, not file-level nodes. If a roam node is file-level (no headlines yet), first add a headline with `org add`, then use `note` or `append` on it.

### Link two nodes

**Always search for both nodes first** to get their IDs:

```bash
# Find source node
org roam node find "Bob" -d "$ORG_MEMORY_AGENT_DIR" --db "$ORG_MEMORY_AGENT_DATABASE_LOCATION" -f json
# → Returns {"ok":true,"data":{"id":"e5f6a7b8-...","file":"/path/to/bob.org",...}}

# Find target node  
org roam node find "Alice" -d "$ORG_MEMORY_AGENT_DIR" --db "$ORG_MEMORY_AGENT_DATABASE_LOCATION" -f json
# → Returns {"ok":true,"data":{"id":"a1b2c3d4-...",...}}
```

If either node doesn't exist, create it first. Then link using the IDs from the responses:

```bash
org roam link add <source-file> "<source-id>" "<target-id>" -d "$ORG_MEMORY_AGENT_DIR" --db "$ORG_MEMORY_AGENT_DATABASE_LOCATION" --description "manages" -f json
```

The `--description` is optional metadata about the relationship.

### Query your knowledge

```bash
org roam node find "Sarah" -d "$ORG_MEMORY_AGENT_DIR" --db "$ORG_MEMORY_AGENT_DATABASE_LOCATION" -f json
org roam backlinks "a1b2c3d4-..." -d "$ORG_MEMORY_AGENT_DIR" --db "$ORG_MEMORY_AGENT_DATABASE_LOCATION" -f json
org roam tag find person -d "$ORG_MEMORY_AGENT_DIR" --db "$ORG_MEMORY_AGENT_DATABASE_LOCATION" -f json
org search "Sarah.*March" -d "$ORG_MEMORY_AGENT_DIR" -f json
```

### Add aliases and refs

Aliases let a node be found by multiple names. Refs associate URLs or external identifiers.

```bash
org roam alias add <file> "a1b2c3d4-..." "Sarah Chen" --db "$ORG_MEMORY_AGENT_DATABASE_LOCATION"
org roam ref add <file> "a1b2c3d4-..." "https://github.com/sarahchen" --db "$ORG_MEMORY_AGENT_DATABASE_LOCATION"
```

## Task management

This section applies when `ORG_MEMORY_USE_FOR_HUMAN` is `true`.

### Read the human's state

**Start here.** `org today` is the most useful query — it returns all non-done TODOs that are scheduled for today or overdue:

```bash
org today -d "$ORG_MEMORY_HUMAN_DIR" -f json
```

For broader views:

```bash
org agenda today -d "$ORG_MEMORY_HUMAN_DIR" -f json   # all scheduled + deadlines for today
org agenda week -d "$ORG_MEMORY_HUMAN_DIR" -f json    # next 7 days
org agenda todo -d "$ORG_MEMORY_HUMAN_DIR" -f json    # all TODOs with scheduled dates
org agenda todo --tag work -d "$ORG_MEMORY_HUMAN_DIR" -f json
```

For rich filtering across all TODOs (scheduled or not):

```bash
org todos --state TODO -d "$ORG_MEMORY_HUMAN_DIR" -f json                    # all open TODOs
org todos --state TODO --unscheduled -d "$ORG_MEMORY_HUMAN_DIR" -f json      # unscheduled only
org todos --state TODO --overdue -d "$ORG_MEMORY_HUMAN_DIR" -f json          # overdue items
org todos --state TODO --due-before 2026-03-01 -d "$ORG_MEMORY_HUMAN_DIR" -f json  # due before date
org todos --search "meeting" -d "$ORG_MEMORY_HUMAN_DIR" -f json              # search by title
org todos --state TODO --file "work" -d "$ORG_MEMORY_HUMAN_DIR" -f json      # filter by file
org todos --state TODO --tag urgent -d "$ORG_MEMORY_HUMAN_DIR" -f json       # filter by tag
org todos --state TODO --priority A -d "$ORG_MEMORY_HUMAN_DIR" -f json       # filter by priority
org todos --state TODO --sort priority -d "$ORG_MEMORY_HUMAN_DIR" -f json    # sort by priority
org todos --state TODO --sort scheduled --reverse -d "$ORG_MEMORY_HUMAN_DIR" -f json  # reverse sort
```

The `todos` command returns full data in JSON: title, todo state, priority, tags, file, pos, scheduled, deadline, level, path (parent headlines), and custom_id. All filters are combinable.

### Make changes

```bash
# Add a headline (response includes the auto-assigned custom_id)
org add $ORG_MEMORY_HUMAN_DIR/inbox.org "Review PR #42" --todo TODO --tag work --deadline 2026-02-10 --db "$ORG_MEMORY_HUMAN_DATABASE_LOCATION" -f json

# Subsequent commands use the custom_id — no file path needed
org todo k4t DONE -d "$ORG_MEMORY_HUMAN_DIR" --db "$ORG_MEMORY_HUMAN_DATABASE_LOCATION" -f json
org schedule a1b 2026-03-15 -d "$ORG_MEMORY_HUMAN_DIR" --db "$ORG_MEMORY_HUMAN_DATABASE_LOCATION" -f json
org note a1b "Pushed back per manager request" -d "$ORG_MEMORY_HUMAN_DIR" --db "$ORG_MEMORY_HUMAN_DATABASE_LOCATION"
org append a1b "Meeting notes from standup." -d "$ORG_MEMORY_HUMAN_DIR" --db "$ORG_MEMORY_HUMAN_DATABASE_LOCATION" -f json

# Refile still requires explicit file paths
org refile $ORG_MEMORY_HUMAN_DIR/inbox.org "Review PR #42" $ORG_MEMORY_HUMAN_DIR/work.org "Code reviews" --db "$ORG_MEMORY_HUMAN_DATABASE_LOCATION" -f json
```

### Preview before writing

Use `--dry-run` to see what a mutation would produce without modifying the file:

```bash
org todo tasks.org "Buy groceries" DONE --dry-run -f json
```

## Batch operations

This section applies when `ORG_MEMORY_USE_FOR_HUMAN` is `true`.

Apply multiple mutations atomically. Commands execute sequentially against in-memory state. Files are written only if all succeed.

```bash
echo '{"commands":[
  {"command":"todo","file":"tasks.org","identifier":"Buy groceries","args":{"state":"DONE"}},
  {"command":"tag-add","file":"tasks.org","identifier":"Write report","args":{"tag":"urgent"}},
  {"command":"schedule","file":"tasks.org","identifier":"Write report","args":{"date":"2026-03-01"}},
  {"command":"append","file":"tasks.org","identifier":"Write report","args":{"text":"Include Q1 metrics."}}
]}' | org batch -d "$ORG_MEMORY_HUMAN_DIR" --db "$ORG_MEMORY_HUMAN_DATABASE_LOCATION" -f json
```

## When to record knowledge

When both features are enabled and the human tells you something, distinguish between requests and ambient information. Fulfill requests in `$ORG_MEMORY_HUMAN_DIR`. Record what you learned in `$ORG_MEMORY_AGENT_DIR`.

Example: "Cancel my Thursday meeting with Sarah and reschedule the API migration review to next week. Sarah is going to be out all of March."

- Cancel and reschedule: explicit requests, execute in `$ORG_MEMORY_HUMAN_DIR`
- Sarah out all of March: ambient information, record in `$ORG_MEMORY_AGENT_DIR`

If only agent memory is enabled, record everything relevant in `$ORG_MEMORY_AGENT_DIR`. If only human file management is enabled, only act on explicit requests.

Check whether a node already exists before creating it. Use the returned data from mutations rather than making follow-up queries.

**Always report writes.** After every mutation to either directory, print `org-memory: <action> <file-path>`. Never silently write to either directory.

## Stable identifiers (CUSTOM_ID)

Every headline created with `org add` is auto-assigned a short CUSTOM_ID (e.g. `k4t`) when an index database exists. This ID appears in the `custom_id` field of all JSON responses and as a column in text output.

Use CUSTOM_IDs to refer to headlines in subsequent commands — they are stable across edits and don't require a file path:

```bash
org todo k4t DONE -d "$ORG_MEMORY_HUMAN_DIR" --db "$ORG_MEMORY_HUMAN_DATABASE_LOCATION" -f json
org schedule k4t 2026-03-15 -d "$ORG_MEMORY_HUMAN_DIR" --db "$ORG_MEMORY_HUMAN_DATABASE_LOCATION" -f json
org note k4t "Pushed back per manager request" -d "$ORG_MEMORY_HUMAN_DIR" --db "$ORG_MEMORY_HUMAN_DATABASE_LOCATION" -f json
org append k4t "Updated scope per review." -d "$ORG_MEMORY_HUMAN_DIR" --db "$ORG_MEMORY_HUMAN_DATABASE_LOCATION" -f json
```

To backfill CUSTOM_IDs on existing headlines that don't have them:

```bash
org custom-id assign -d "$ORG_MEMORY_HUMAN_DIR" --db "$ORG_MEMORY_HUMAN_DATABASE_LOCATION"
```

**Never address headlines by position number (`pos`).** Positions change when files are edited — a mutation on one headline shifts the byte positions of everything after it. This WILL corrupt your file if you use stale `pos` values for subsequent mutations.

Safe identifiers (in order of preference):
1. **CUSTOM_ID** (e.g. `k4t`) — stable, short, unique
2. **org-id** (UUID) — stable, unique
3. **Exact title** — stable as long as the title doesn't change

If you need to mutate multiple headlines in the same file, either:
- Use `org batch` for atomic multi-step operations (recommended)
- Use CUSTOM_IDs or titles, never `pos`
- If you must use `pos`, re-query after each mutation to get fresh positions

## Error handling

Branch on the `ok` field. Handle errors by `type`:

- `file_not_found`: wrong path or deleted file
- `headline_not_found`: identifier doesn't match; re-query to get current state
- `parse_error`: file has syntax the parser can't handle; don't retry
- `invalid_args`: check `org schema` or `org <command> --help`

## Troubleshooting

### Duplicate nodes created
You didn't search before creating. Always run `node find` first. If duplicates exist, manually delete the newer file and run `org roam sync`.

### "headline_not_found" when using org note
You tried to add a note to a file-level node (level 0). Use `org add` to create a headline first, then `org note` on that headline.

### Links show wrong display text
The `--description` parameter sets relationship metadata, not display text. The link displays the target node's title. This is correct org-roam behavior.

### Database out of sync
Run `org roam sync -d <dir> --db <db-path>` to rebuild the database from files.

## Common mistakes

These are real errors agents have made. Read them and don't repeat them.

### Confusing NOTE and REMEMBER
- `Note:` → human's org files, always a TODO for them
- `Remember:` → agent's knowledge base, information for agent's recall
- If in doubt: does the human need to *do* something? → NOTE. Is this something the agent should *know*? → REMEMBER.
- "Note: we could add X" is still a NOTE — the human is dictating a task/idea for their own list.

### Using `pos` after mutations
Position values (`pos`) are byte offsets. They shift after every edit. If you add a SCHEDULED line to headline at pos 100, the next headline's pos has changed. Using the old pos will target the wrong headline or corrupt the file (e.g. inserting duplicate SCHEDULED lines inside a headline body).

**Fix:** Use CUSTOM_ID, org-id, or exact title. Or use `org batch` for multiple mutations.

### Not confirming writes
After every write, print `org-memory: <action> <file-path>`. This is not optional. The human needs to know what you changed and where.

### Creating duplicate roam nodes
Always `org roam node find` before `org roam node create`. If you skip the search and create duplicates, you fragment the knowledge graph.

### Putting human tasks in the agent's knowledge base (or vice versa)
- Human says "Note: buy milk" → `$ORG_MEMORY_HUMAN_DIR/inbox.org`, NOT the agent's roam
- Human says "Remember: the WiFi password is X" → `$ORG_MEMORY_AGENT_DIR`, NOT the human's inbox
- An idea for a feature the human wants to track → human's org (NOTE)
- A fact the agent learned that might be useful later → agent's roam (REMEMBER)
