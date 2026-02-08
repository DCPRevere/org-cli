---
name: org-memory
description: "Structured knowledge base and task management using org-mode files. Query, mutate, link, and search org files and org-roam databases with the `org` CLI."
metadata: {"openclaw":{"emoji":"🦄","requires":{"bins":["org"]},"install":[{"id":"github-release","kind":"manual","label":"Download from GitHub releases: https://github.com/dcprevere/org-cli/releases"}]}}
---

# org-memory

Use the `org` CLI to maintain structured, linked, human-readable knowledge in org-mode files. Org files are plain text with rich structure: headlines, TODO states, tags, properties, timestamps, and links. Combined with org-roam, they form a knowledge graph backed by a SQLite database.

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

### Record an entity

```bash
org roam node create "Sarah" -d "$ORG_MEMORY_AGENT_DIR" --db "$ORG_MEMORY_AGENT_DATABASE_LOCATION" -t person -t work -f json
```

### Add structure to a node

Use the file path returned by the create command:

```bash
org add <file-from-response> "Unavailable March 2026" --tag scheduling --db "$ORG_MEMORY_AGENT_DATABASE_LOCATION"
org note <file-from-response> "Unavailable March 2026" "Out all of March per human." --db "$ORG_MEMORY_AGENT_DATABASE_LOCATION"
```

### Link two nodes

Look up both nodes, then link using their IDs and the source file path:

```bash
org roam link add <source-file> "e5f6a7b8-..." "a1b2c3d4-..." -d "$ORG_MEMORY_AGENT_DIR" --db "$ORG_MEMORY_AGENT_DATABASE_LOCATION" --description "stakeholder"
```

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
