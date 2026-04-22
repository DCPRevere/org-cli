---
name: org-memory
version: 1.0.2
description: "Extends the org-cli skill so the agent also persists its own memory (knowledge, observations, daily notes) to an org workspace."
metadata: {"openclaw":{"emoji":"🐘","homepage":"https://github.com/dcprevere/org-cli","requires":{"bins":["org"],"env":["ORG_MEMORY_DIR","ORG_MEMORY_ROAM_DIR","ORG_MEMORY_DB","ORG_CLI_BIN"]},"install":[{"kind":"download","label":"Download from GitHub releases: https://github.com/dcprevere/org-cli/releases"}],"scope":{"reads":["$ORG_CLI_DIR","$ORG_CLI_ROAM_DIR","$ORG_CLI_DB","$ORG_MEMORY_DIR","$ORG_MEMORY_ROAM_DIR","$ORG_MEMORY_DB"],"writes":["$ORG_CLI_DIR","$ORG_CLI_ROAM_DIR","$ORG_CLI_DB","$ORG_MEMORY_DIR","$ORG_MEMORY_ROAM_DIR","$ORG_MEMORY_DB"]}}}
---

# org-memory

**This skill extends [`org-cli`](../org-cli/SKILL.md).** You must have the `org-cli` skill installed and loaded — everything about the CLI contract, command safety, identifiers, shortcut grammar, and the user's workspace lives there. This skill only adds what's different.

## What this skill adds

Two things:

1. **A second workspace** — yours. Via `@a`-prefixed shortcuts, you can capture your own memory (observations, things you've learned, daily notes, a knowledge graph about the user or their work) into a dedicated org workspace. Bare shortcuts (`t:`, `n:`, `k:`, `d:`, `s:`, `f:`) still target the *user*'s workspace as defined by `org-cli`.

2. **Memory-wiki override.** When this skill is active, graph-structured knowledge routes through org-roam rather than OpenClaw's memory-wiki. See below.

## Memory-wiki override

When `org-memory` is active, **store all persistent graph-structured knowledge in your org workspace, not in memory-wiki.** The auto-memory types (`user`, `feedback`, `project`, `reference`) can still go to `MEMORY.md` as usual — those are flat, typed notes and memory-wiki's graph features don't add much. But anything you'd model as entities + relationships (a person's preferences, a project's stakeholders, the history of a system) goes to `@ak` instead of memory-wiki.

Why: one knowledge graph, not two. The user can browse your memory in the same tooling they use for their own. Your graph can link into their graph when the subject is shared.

## Targeting

`@a` = agent. Append any action letter: `@at:` `@an:` `@ak:` `@ad:` `@as:` `@af:`.

| Bare (user, from org-cli) | Agent extension | What it does |
|---|---|---|
| `t:` | `@at:` | Create TODO in your inbox |
| `n:` | `@an:` | Plain headline in your inbox |
| `k:` | `@ak:` | Knowledge in your roam graph |
| `d:` | `@ad:` | Mark one of your TODOs DONE |
| `s:` | `@as:` | Reschedule one of your TODOs |
| `f:` | `@af:` | Search your org files and roam |

## Action details

The agent-side commands are exact mirrors of the `org-cli` actions, with `$DIR`/`$ROAM`/`$DB` substituted to point at your workspace (`$ORG_MEMORY_DIR`, `$ORG_MEMORY_ROAM_DIR`, `$ORG_MEMORY_DB`). See the `org-cli` skill for the full specs of each action — the commands here are identical, just targeted differently.

Worked example for `@ak`:

```bash
# Find existing node for the subject
org roam node find '<subject>' -d "$ORG_MEMORY_ROAM_DIR" --db "$ORG_MEMORY_DB" -f json

# If it exists, append the fact
org append <custom_id> '<fact>' -d "$ORG_MEMORY_DIR" --db "$ORG_MEMORY_DB" -f json

# If not, create then append
org roam node create '<subject>' -d "$ORG_MEMORY_ROAM_DIR" --db "$ORG_MEMORY_DB" -f json
# then append
```

The same substitution pattern applies to every other action.

## Session-start context

When this skill is active, two things get injected into the agent's session-start context from your workspace:

- `$ORG_MEMORY_DIR/memory.org` — your persistent memory file. Read this first; it's the continuity you've built up.
- `$ORG_MEMORY_DIR/daily/YYYY-MM-DD.org` — today's and yesterday's daily notes, if they exist.

The `org-memory` plugin's `before_agent_start` hook loads these; you don't have to fetch them manually.

## Write-log format

Use `org-memory:` (not `org-cli:`) as the prefix when the write went to *your* workspace. The user reads both prefixes to know which workspace changed:

```
org-memory: added TODO [k4t] ~/org/agent/inbox.org
org-memory: created node [3f2a-…] ~/org/agent/roam/self.org
```

Bare shortcuts still produce `org-cli:` lines (they're handled by the `org-cli` skill). Only `@a*` shortcuts produce `org-memory:` lines.

## Configuration

Additional env vars beyond what `org-cli` declares:

| Variable | Default | Purpose |
|---|---|---|
| `ORG_MEMORY_DIR` | `~/org/agent` | Your workspace (memory.org, daily/, tasks) |
| `ORG_MEMORY_DB` | `$ORG_MEMORY_DIR/.org.db` | SQLite database |
| `ORG_MEMORY_ROAM_DIR` | `$ORG_MEMORY_DIR/roam` | Your roam nodes |

`ORG_CLI_BIN` (the org binary path) is shared — both skills use the same binary. `ORG_CLI_INBOX_FILE` is *not* shared — your inbox is always `inbox.org` in `$ORG_MEMORY_DIR` unless you set a separate plugin config.

## First-time setup

In addition to the `org-cli` first-time setup for the user's workspace, run:

```bash
# Sync any existing agent files
org roam sync -d "$ORG_MEMORY_DIR" --db "$ORG_MEMORY_DB"

# Seed the knowledge base
org roam node create 'Index' -d "$ORG_MEMORY_ROAM_DIR" --db "$ORG_MEMORY_DB" -f json

# Build the headline index
org index -d "$ORG_MEMORY_DIR" --db "$ORG_MEMORY_DB"
```

## References

- **Memory architecture** (`{baseDir}/references/memory-architecture.md`) — why the dual-workspace split exists, how memory.org + daily notes fit in, and the migration from MEMORY.md-only.
