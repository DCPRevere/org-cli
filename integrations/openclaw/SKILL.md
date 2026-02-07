---
name: org-cli
description: "Structured knowledge base and task management using org-mode files. Query, mutate, link, and search org files and org-roam databases with the `org` CLI."
metadata:
  {
    "openclaw":
      {
        "emoji": "🦄",
        "requires": { "bins": ["org"] },
        "install":
          [
            {
              "id": "github-release",
              "kind": "manual",
              "label": "Download from GitHub releases: https://github.com/dcprevere/org-cli/releases"
            }
          ]
      }
  }
---

# org-cli Skill

Use the `org` CLI to maintain structured, linked, human-readable knowledge in org-mode files. Org files are plain text with rich structure: headlines, TODO states, tags, properties, timestamps, and links. Combined with org-roam, they form a knowledge graph backed by a SQLite database.

All commands accept `-f json` for structured output with `{"ok":true,"data":...}` envelopes. Errors return `{"ok":false,"error":{"type":"...","message":"..."}}`. Always use `-f json`.

## Discovery

Run `org schema` once to get a machine-readable description of all commands, arguments, and flags. Use this to construct valid commands without memorizing the interface.

## Setup

Maintain two separate org directories:

- **Your knowledge base** (`~/org/agent`): What you learn, entities you track, relationships between concepts. You own this. The human rarely looks at it.
- **The human's files** (`~/org/human`): Their tasks, projects, notes. You read and write here when asked, but they own it.

Each directory has its own org-roam database. Initialize by creating your first node:

```bash
org roam node create "Index" -d ~/org/agent -f json
```

The response includes the node's ID, file path, title, and tags. Use these values in subsequent commands.

## Knowledge management

### Record an entity

```bash
org roam node create "Sarah" -d ~/org/agent -t person -t work -f json
```

### Add structure to a node

Use the file path from the create response:

```bash
org add ~/org/agent/sarah.org "Unavailable March 2026" --tag scheduling
org note ~/org/agent/sarah.org "Unavailable March 2026" "Out all of March per human."
```

### Link two nodes

Look up both nodes, then link using their IDs and the source file path:

```bash
org roam link add ~/org/agent/api_migration.org "e5f6a7b8-..." "a1b2c3d4-..." -d ~/org/agent --description "stakeholder"
```

### Query your knowledge

```bash
# Find a node by name or alias
org roam node find "Sarah" -d ~/org/agent -f json

# What links to this node?
org roam backlinks "a1b2c3d4-..." -d ~/org/agent -f json

# All nodes with a tag
org roam tag find person -d ~/org/agent -f json

# Regex search across all files
org search "Sarah.*March" -d ~/org/agent -f json
```

### Add aliases and refs

Aliases let a node be found by multiple names. Refs associate URLs or external identifiers.

```bash
org roam alias add ~/org/agent/sarah.org "a1b2c3d4-..." "Sarah Chen"
org roam ref add ~/org/agent/sarah.org "a1b2c3d4-..." "https://github.com/sarahchen"
```

## Task management

### Read the human's state

```bash
# Today's agenda
org agenda today -d ~/org/human -f json

# This week
org agenda week -d ~/org/human -f json

# All open tasks, optionally filtered by tag
org agenda todo -d ~/org/human -f json
org agenda todo --tag work -d ~/org/human -f json
```

### Make changes

```bash
# Create a task
org add ~/org/human/inbox.org "Review PR #42" --todo TODO --tag work --deadline 2026-02-10

# Complete it
org todo ~/org/human/inbox.org "Review PR #42" DONE -f json

# Reschedule
org schedule ~/org/human/projects.org "Quarterly review" 2026-03-15 -f json

# Add a note
org note ~/org/human/projects.org "Quarterly review" "Pushed back per manager request"

# Move a task between files
org refile ~/org/human/inbox.org "Review PR #42" ~/org/human/work.org "Code reviews" -f json
```

### Preview before writing

Use `--dry-run` to see what a mutation would produce without modifying the file:

```bash
org todo tasks.org "Buy groceries" DONE --dry-run -f json
```

## Batch operations

Apply multiple mutations atomically. Commands execute sequentially against in-memory state. Files are written only if all succeed.

```bash
echo '{"commands":[
  {"command":"todo","file":"tasks.org","identifier":"Buy groceries","args":{"state":"DONE"}},
  {"command":"tag-add","file":"tasks.org","identifier":"Write report","args":{"tag":"urgent"}},
  {"command":"schedule","file":"tasks.org","identifier":"Write report","args":{"date":"2026-03-01"}}
]}' | org batch -d ~/org/human -f json
```

## When to record knowledge

When the human tells you something, distinguish between requests and ambient information. Fulfill requests in their repo. Record what you learned in yours.

Example: "Cancel my Thursday meeting with Sarah and reschedule the API migration review to next week. Sarah is going to be out all of March."

- Cancel and reschedule: explicit requests, execute in `~/org/human`
- Sarah out all of March: ambient information, record in `~/org/agent`

Check whether a node already exists before creating it. Use the returned data from mutations rather than making follow-up queries.

## Stable identifiers

Always address headlines by org-id or exact title, not by position number. Positions change when files are edited. If you create a headline you'll refer to later, set an ID:

```bash
org property set ~/org/human/tasks.org "My task" ID "$(uuidgen)" -f json
```

## Error handling

Branch on the `ok` field. Handle errors by `type`:

- `file_not_found`: wrong path or deleted file
- `headline_not_found`: identifier doesn't match; re-query to get current state
- `parse_error`: file has syntax the parser can't handle; don't retry
- `invalid_args`: check `org schema` or `org <command> --help`
