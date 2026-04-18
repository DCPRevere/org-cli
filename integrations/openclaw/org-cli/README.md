# org-cli

An [OpenClaw](https://github.com/openclaw/openclaw) plugin that gives your agent structured, linked, human-readable tools for your org-mode files.

This is the user-facing piece. Install this if you want your agent to capture tasks and notes into your org workspace, mark them done, reschedule, search, and maintain a linked knowledge graph (org-roam) on your behalf. If you also want your agent to persist *its own* memory in org, install `org-memory` on top.

## Install

From [ClawHub](https://clawhub.ai/):

```sh
openclaw skill install org-cli
```

Or manually from a repo checkout:

1. Put `org` on your PATH ([releases](https://github.com/dcprevere/org-cli/releases)).
2. Copy the skill: `cp -r integrations/openclaw/org-cli ~/.openclaw/skills/org-cli`
3. Refresh skills or restart the gateway.

## Quick start

Once installed, just talk to your agent naturally:

- **"t: submit taxes in 3 weeks"** → scheduled TODO added to inbox
- **"n: think about hanging pictures up"** → plain note added, no TODO state
- **"k: Sarah prefers morning meetings"** → stored in your roam graph against `Sarah`
- **"d: groceries"** → matching TODO marked DONE
- **"s: taxes to next Friday"** → TODO rescheduled
- **"f: sacra"** → search across headlines and roam nodes
- **"What do I need to do today?"** → runs `org today`

### Shortcuts

| Prefix | Action |
|---|---|
| `t:` | Create TODO in inbox (extracts dates) |
| `n:` | Create plain headline (no TODO, no date) |
| `k:` | Store knowledge in the roam graph |
| `d:` | Mark matching TODO DONE |
| `s:` | Reschedule a TODO |
| `f:` | Find across headlines and roam |

## What it does

The skill teaches the agent to use `org` for:

- **Task capture and lifecycle**: add, schedule, reschedule, complete.
- **Plain notes**: capture thoughts without committing them to a TODO list.
- **Knowledge graph**: create and update roam nodes, link them, query by tag/title/backlink/search.
- **Search**: full-text search across all org content, filtered queries for TODOs.
- **Batch mutations**: apply multiple changes atomically.

All files are plain text, human-readable, and version-controllable.

## Configuration

| Variable | Default | Purpose |
|---|---|---|
| `ORG_CLI_DIR` | `~/org` | Your workspace (inbox.org, tasks, projects) |
| `ORG_CLI_DB` | `$ORG_CLI_DIR/.org.db` | SQLite database (roam + index) |
| `ORG_CLI_ROAM_DIR` | `$ORG_CLI_DIR/roam` | Roam nodes |
| `ORG_CLI_BIN` | `org` | Path to the `org` binary |
| `ORG_CLI_INBOX_FILE` | `inbox.org` | Filename new captures land in |

To override, set them in `~/.openclaw/openclaw.json`:

```json
{
  "skills": {
    "entries": {
      "org-cli": {
        "env": {
          "ORG_CLI_DIR": "/path/to/workspace",
          "ORG_CLI_DB": "/path/to/workspace/.org.db"
        }
      }
    }
  }
}
```

Or export them in your shell. Shell env takes precedence over `openclaw.json`.

## When to use org-cli

The sweet spot is real task management with dates plus a linked knowledge graph. If you just want a flat set of facts, OpenClaw's default memory is simpler. If you need agenda queries (`what's due today?`, `what's overdue?`), scheduling with repeaters, and CRM-like entity tracking with short stable IDs, this is the tool.

## Migrating from `org-memory@0.7.x`

If you were running the previous single-skill `org-memory@0.7.x`, the v1.0.0 split removed the human/agent dual-target model from this skill. The `org-cli` skill operates on one workspace; for agent-side memory install `org-memory@1.0.0` alongside.

Env var migration:

| Old (0.7.x) | New (1.0.0, `org-cli`) |
|---|---|
| `ORG_MEMORY_HUMAN_DIR` | `ORG_CLI_DIR` |
| `ORG_MEMORY_HUMAN_ROAM_DIR` | `ORG_CLI_ROAM_DIR` |
| `ORG_MEMORY_HUMAN_DATABASE_LOCATION` | `ORG_CLI_DB` |
| `ORG_MEMORY_ORG_BIN` | `ORG_CLI_BIN` |
| `ORG_MEMORY_INBOX_FILE` | `ORG_CLI_INBOX_FILE` |

The `@a` targeting prefix and the agent-side `ORG_MEMORY_AGENT_*` vars are handled by the `org-memory` skill.
