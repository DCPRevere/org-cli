---
name: org-memory
description: "Structured knowledge base and task management using org-mode files. Query, mutate, link, and search org files and org-roam databases with the `org` CLI."
metadata: {"openclaw":{"emoji":"🦄","requires":{"bins":["org"]},"install":[{"id":"github-release","kind":"manual","label":"Download from GitHub releases: https://github.com/dcprevere/org-cli/releases"}]}}
---

# org-memory

Use the `org` CLI to maintain structured, linked, human-readable knowledge in org-mode files. Org files are plain text with rich structure: headlines, TODO states, tags, properties, timestamps, and links. Combined with org-roam, they form a knowledge graph backed by a SQLite database.

## Shortcuts

When your human uses these patterns, act immediately:

| Pattern | Action |
|---------|--------|
| `Remember: <info>` | Save to your knowledge base (`$ORG_MEMORY_AGENT_DIR`). Create or update a node. This is for *your* future recall. |
| `Note: <task or info>` | Add to the human's org files (`$ORG_MEMORY_HUMAN_DIR/inbox.org`). This is for *them* to act on. |

Examples:
- "Remember: Sarah prefers morning meetings" → Create/update a node for Sarah in your repo
- "Note: Buy groceries" → Add a TODO to the human's inbox
- "Remember: The API uses OAuth2, not API keys" → Create/update a node for the API in your repo
- "Note: Review PR #42 by Friday" → Add a TODO with deadline to the human's inbox

Don't ask for confirmation on shortcuts — just do it. After every write, print a line in this exact format:

```
org-memory: <action> <file-path>
```

Examples: `org-memory: added TODO to ~/org/human/inbox.org`, `org-memory: created node ~/org/agent/sarah.org`, `org-memory: updated ~/org/agent/sarah.org`.

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

Initialize each enabled directory by creating a first node:

```bash
org roam node create "Index" -d "$ORG_MEMORY_AGENT_DIR" --db "$ORG_MEMORY_AGENT_DATABASE_LOCATION" -f json
```

The response includes the node's ID, file path, title, and tags. Use these values in subsequent commands.

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
# Add a headline to the node
org add <file> "Unavailable March 2026" --tag scheduling --db "$ORG_MEMORY_AGENT_DATABASE_LOCATION" -f json

# Add a note to an existing headline (NOT to file-level nodes)
org note <file> "Unavailable March 2026" "Out all of March per human." --db "$ORG_MEMORY_AGENT_DATABASE_LOCATION" -f json
```

**Note:** `org note` attaches notes to *headlines*, not file-level nodes. If a roam node is file-level (no headlines yet), first add a headline with `org add`, then attach notes to it.

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

```bash
org agenda today -d "$ORG_MEMORY_HUMAN_DIR" -f json
org agenda week -d "$ORG_MEMORY_HUMAN_DIR" -f json
org agenda todo -d "$ORG_MEMORY_HUMAN_DIR" -f json
org agenda todo --tag work -d "$ORG_MEMORY_HUMAN_DIR" -f json
```

### Make changes

```bash
org add $ORG_MEMORY_HUMAN_DIR/inbox.org "Review PR #42" --todo TODO --tag work --deadline 2026-02-10 --db "$ORG_MEMORY_HUMAN_DATABASE_LOCATION"
org todo $ORG_MEMORY_HUMAN_DIR/inbox.org "Review PR #42" DONE --db "$ORG_MEMORY_HUMAN_DATABASE_LOCATION" -f json
org schedule $ORG_MEMORY_HUMAN_DIR/projects.org "Quarterly review" 2026-03-15 --db "$ORG_MEMORY_HUMAN_DATABASE_LOCATION" -f json
org note $ORG_MEMORY_HUMAN_DIR/projects.org "Quarterly review" "Pushed back per manager request" --db "$ORG_MEMORY_HUMAN_DATABASE_LOCATION"
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
  {"command":"schedule","file":"tasks.org","identifier":"Write report","args":{"date":"2026-03-01"}}
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

## Stable identifiers

Always address headlines by org-id or exact title, not by position number. Positions change when files are edited. If you create a headline you'll refer to later, set an ID:

```bash
org property set file.org "My task" ID "$(uuidgen)" --db "$ORG_MEMORY_HUMAN_DATABASE_LOCATION" -f json
```

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
