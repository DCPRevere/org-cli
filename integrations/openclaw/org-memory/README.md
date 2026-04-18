# org-memory

An [OpenClaw](https://github.com/openclaw/openclaw) plugin that lets your agent persist its own memory — knowledge, observations, daily notes — into an org workspace.

`org-memory` extends [`org-cli`](../org-cli). You need both plugins installed. `org-cli` handles the user's workspace; `org-memory` adds a second workspace for the agent itself, reachable via `@a`-prefixed shortcuts.

## Install

Install **both** plugins from [ClawHub](https://clawhub.ai/):

```sh
openclaw skill install org-cli
openclaw skill install org-memory
```

Or manually from a repo checkout:

1. Put `org` on your PATH ([releases](https://github.com/dcprevere/org-cli/releases)).
2. Copy both skills: `cp -r integrations/openclaw/org-cli ~/.openclaw/skills/org-cli && cp -r integrations/openclaw/org-memory ~/.openclaw/skills/org-memory`
3. Refresh skills or restart the gateway.

## What this plugin adds over `org-cli`

1. **A second workspace for the agent.** Via `@at:`, `@an:`, `@ak:`, `@ad:`, `@as:`, `@af:`, the agent captures its own memory into a dedicated org directory. Bare shortcuts (`t:`, `n:`, etc.) still target the user's workspace per `org-cli`.

2. **Memory-wiki override.** When this plugin is active, graph-structured knowledge routes through org-roam, not OpenClaw's memory-wiki. Flat typed memory (user/feedback/project/reference in `MEMORY.md`) still works as usual.

3. **Session-start context.** The agent's `memory.org` and recent daily notes are injected automatically at session start.

## Shortcuts

| Prefix | Action |
|---|---|
| `@at:` | Create TODO in the agent's inbox |
| `@an:` | Create plain headline in the agent's workspace |
| `@ak:` | Store knowledge in the agent's roam graph |
| `@ad:` | Mark one of the agent's TODOs DONE |
| `@as:` | Reschedule one of the agent's TODOs |
| `@af:` | Search the agent's workspace |

Bare shortcuts (`t:`, `n:`, `k:`, `d:`, `s:`, `f:`) remain user-side — see the `org-cli` plugin.

## Configuration

`org-memory` adds these env vars on top of the `ORG_CLI_*` ones from `org-cli`:

| Variable | Default | Purpose |
|---|---|---|
| `ORG_MEMORY_DIR` | `~/org/agent` | Agent workspace (memory.org, daily/, tasks) |
| `ORG_MEMORY_DB` | `$ORG_MEMORY_DIR/.org.db` | Agent SQLite database |
| `ORG_MEMORY_ROAM_DIR` | `$ORG_MEMORY_DIR/roam` | Agent roam nodes |

`ORG_CLI_BIN` (the org binary path) is shared between both plugins — one binary, one env var.

To override, set them in `~/.openclaw/openclaw.json`:

```json
{
  "skills": {
    "entries": {
      "org-memory": {
        "env": {
          "ORG_MEMORY_DIR": "/path/to/agent",
          "ORG_MEMORY_DB": "/path/to/agent/.org.db"
        }
      }
    }
  }
}
```

Or export them in your shell. Shell env takes precedence over `openclaw.json`.

## File layout

```
$ORG_MEMORY_DIR/
├── memory.org          # curated long-term memory, loaded every session
├── daily/
│   ├── 2026-04-18.org  # today's raw log
│   └── ...
└── roam/*.org          # entity nodes (people, projects, concepts)
```

`memory.org` is the agent's permanent memory — curated and concise. Daily files are raw logs. Entity nodes are structured knowledge the agent builds up over time.

## Migrating from `org-memory@0.7.x`

The v1.0.0 split moved the user-facing half of the old `org-memory` skill into a separate `org-cli` plugin. This plugin (`org-memory@1.0.0`) is now agent-side only. You must also install `org-cli@1.0.0` to get task management for the user's own workspace.

Env var migration:

| Old (0.7.x) | New (1.0.0) |
|---|---|
| `ORG_MEMORY_AGENT_DIR` | `ORG_MEMORY_DIR` (this plugin) |
| `ORG_MEMORY_AGENT_ROAM_DIR` | `ORG_MEMORY_ROAM_DIR` (this plugin) |
| `ORG_MEMORY_AGENT_DATABASE_LOCATION` | `ORG_MEMORY_DB` (this plugin) |
| `ORG_MEMORY_HUMAN_DIR` | `ORG_CLI_DIR` (install `org-cli`) |
| `ORG_MEMORY_HUMAN_ROAM_DIR` | `ORG_CLI_ROAM_DIR` (install `org-cli`) |
| `ORG_MEMORY_HUMAN_DATABASE_LOCATION` | `ORG_CLI_DB` (install `org-cli`) |
| `ORG_MEMORY_ORG_BIN` | `ORG_CLI_BIN` (shared) |
| `ORG_MEMORY_USE_FOR_AGENT=false` | uninstall this plugin |
| `ORG_MEMORY_USE_FOR_HUMAN=false` | uninstall `org-cli` |

The `org_memory_*` tool names are unchanged, but their signatures lost the `dir: "human" | "agent"` parameter — they always target the agent workspace now. For user-workspace operations, call the `org_*` tools provided by `org-cli`.

Your existing org files and databases are **unchanged** — only the env var names and tool signatures moved. Point the new vars at your existing directories and everything works.
