# org-memory

An [OpenClaw](https://github.com/openclaw/openclaw) skill that gives your agent structured, linked, human-readable memory using org-mode files.

## Install

From [ClawHub](https://clawhub.ai/):

```sh
openclaw skill install org-memory
```

Or manually from a repo checkout:

1. Put `org` on your PATH ([releases](https://github.com/dcprevere/org-cli/releases)).
2. Copy the skill: `cp -r integrations/openclaw ~/.openclaw/skills/org-memory`
3. Refresh skills or restart the gateway.

## Quick start

If you ask, the agent can migrate OpenClaw's default memory (MEMORY.md) to org format and disable the default memory plugin. This is optional — if you don't migrate, both systems coexist (org-memory handles knowledge graph and tasks while MEMORY.md stays as your agent's primary memory).

Once installed, just talk to your agent naturally:

- **"k: Sarah prefers morning meetings"** → Agent saves to its knowledge base
- **"t: submit taxes in 3 weeks"** → Agent adds scheduled TODO to your inbox
- **"r: Buy groceries"** → Agent adds TODO to your inbox
- **"d: groceries"** → Agent marks the task complete
- **"s: taxes to next Friday"** → Agent reschedules the task
- **"What do you know about Sarah?"** → Agent queries its knowledge

### Shortcuts

Org mutations:

| Prefix | Alias | Action |
|---|---|---|
| `t:` | `Todo:` | Create TODO in your org (extracts dates) |
| `d:` | `Done:` / `Finished:` | Mark a TODO as DONE |
| `s:` | | Reschedule a TODO |
| `r:` | `Note:` | Save to your roam (knowledge/info) |
| `k:` | `Know:` / `Remember:` | Agent learns it (agent's knowledge base) |

Behaviour modifiers:

| Prefix | Action |
|---|---|
| `v:` | Voice reply (TTS) |
| `?` | Research (web + files) |
| `@` | Roam lookup |
| `w:` | Work context (Remundo) |
| `!` | Urgent — act now |
| `q:` | Quick answer, no tools |

## When to use org-memory

OpenClaw's default memory (`MEMORY.md` + semantic search) works well for simple setups. org-memory is worth the added complexity when you need more.

### org-memory vs MEMORY.md

| Capability | MEMORY.md | org-memory |
|------------|-----------|------------|
| Store facts about one person | Works great | Overkill |
| Store 20+ entities (people, projects, companies) | Gets messy | Each entity = separate file |
| "List all my clients" | Grep through text | `tag find client` → structured list |
| Track relationships | Text references | Graph links with backlinks |
| "What do I need to do today?" | Not possible | `org today` → due + overdue TODOs |
| "What's due this week?" | Not possible | `agenda week` → parsed dates |
| Task management | No date support | SCHEDULED, DEADLINE, repeaters |
| "Mark that task done" | Find file, find line | `org todo set k4t DONE` |

**Use MEMORY.md** for: personal preferences, key dates, simple facts (<100 items).

**Use org-memory** for: CRM-like knowledge, task management, relationship graphs, or any knowledge base that will grow beyond 100 entities.

### org-memory vs Obsidian

Both are linked knowledge graphs. Key differences:

| | Obsidian | org-memory |
|---|----------|------------|
| Format | Markdown + YAML frontmatter | Org-mode |
| Task management | Limited (no native dates) | Full agenda: SCHEDULED, DEADLINE, repeaters, clock |
| Query language | Dataview plugin (JS-based) | CLI with JSON output |
| Human editing | Obsidian app or any editor | Emacs or any editor |
| Agent integration | Needs custom tooling | Built for CLI/agent use |

**Use Obsidian** if: you already live in Obsidian and want your agent to share that vault.

**Use org-memory** if: you need real task management with dates, prefer CLI-native tooling, or use Emacs.

### The killer feature

The real differentiator is **agenda queries** and **short IDs**. The moment you want:
- "What's due today?" → `org today` shows all non-done TODOs due today or overdue
- "Schedule this for next Monday"
- "Show me overdue tasks" → `org today` catches these automatically
- "d: groceries" → `org todo set k4t DONE` (no file path needed)

...MEMORY.md and Obsidian can't help. org-memory handles this natively because org-mode was built for it. Every headline gets a short CUSTOM_ID (like `k4t`) that works across files without remembering paths.

## What it does

The skill teaches the agent to use `org` for:

- **Knowledge graph**: create nodes, link them, query by tag/title/backlink/search
- **Task management**: create/complete/schedule/refile tasks in the human's org files
- **Structured memory**: record entities, relationships, and constraints as linked org-roam nodes instead of flat text
- **Batch mutations**: apply multiple changes atomically

By default the agent maintains two directories: its own knowledge base and the human's files. Either feature can be disabled independently. All files are plain text, human-readable, and version-controllable.

## Configuration

Set these to match your directory layout:

| Variable | Default | Purpose |
|---|---|---|
| `ORG_MEMORY_AGENT_DIR` | `~/org/agent` | Agent's org workspace directory |
| `ORG_MEMORY_AGENT_ROAM_DIR` | `~/org/agent/roam` | Agent's roam node directory |
| `ORG_MEMORY_AGENT_DATABASE_LOCATION` | `~/org/agent/.org.db` | Agent's database |
| `ORG_MEMORY_HUMAN_DIR` | `~/org/human` | Human's org workspace directory |
| `ORG_MEMORY_HUMAN_ROAM_DIR` | `~/org/human/roam` | Human's roam node directory |
| `ORG_MEMORY_HUMAN_DATABASE_LOCATION` | `~/org/human/.org.db` | Human's database |

Optional:

| Variable | Default | Purpose |
|---|---|---|
| `ORG_MEMORY_USE_FOR_AGENT` | `true` | Enable the agent's own knowledge base |
| `ORG_MEMORY_USE_FOR_HUMAN` | `true` | Enable task management in the human's org files |
| `ORG_MEMORY_ORG_BIN` | `org` | Path to the org CLI binary |
| `ORG_MEMORY_INBOX_FILE` | `inbox.org` | Filename for new tasks (relative to humanDir) |

Workspace dirs (`*_DIR`) hold tasks, inbox, and daily files. Roam dirs (`*_ROAM_DIR`) hold knowledge graph nodes. Databases are collocated with roam dirs by default. Roam dirs default to `<workspace>/roam` — roam nodes are never created in the workspace root. Set `ORG_MEMORY_USE_FOR_AGENT` or `ORG_MEMORY_USE_FOR_HUMAN` to anything other than `true` to disable that feature.

To override, set them in `~/.openclaw/openclaw.json`:

```json
{
  "skills": {
    "entries": {
      "org-memory": {
        "env": {
          "ORG_MEMORY_USE_FOR_HUMAN": "false",
          "ORG_MEMORY_AGENT_DIR": "/path/to/agent",
          "ORG_MEMORY_AGENT_DATABASE_LOCATION": "/path/to/agent.db"
        }
      }
    }
  }
}
```

Or export them in your shell. Shell env takes precedence over `openclaw.json`.
