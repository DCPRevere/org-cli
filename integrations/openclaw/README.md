# org-memory

An [OpenClaw](https://github.com/openclaw/openclaw) skill that gives your agent structured, linked, human-readable memory using org-mode files.

## Install

1. Put `org` on your PATH ([releases](https://github.com/dcprevere/org-cli/releases)).

2. Copy the skill into your OpenClaw skills directory:

```sh
cp -r integrations/openclaw ~/.openclaw/skills/org-memory
```

3. Ask your agent to "refresh skills" or restart the gateway.

## Quick start

Once installed, just talk to your agent naturally:

- **"Remember: Sarah prefers morning meetings"** → Agent saves to its knowledge base
- **"Note: Buy groceries"** → Agent adds TODO to your inbox
- **"What do you know about Sarah?"** → Agent queries its knowledge

## When to use org-memory

OpenClaw's default memory (`MEMORY.md` + semantic search) works well for simple setups. org-memory is worth the added complexity when you need more.

### org-memory vs MEMORY.md

| Capability | MEMORY.md | org-memory |
|------------|-----------|------------|
| Store facts about one person | ✓ Works great | Overkill |
| Store 20+ entities (people, projects, companies) | Gets messy | Each entity = separate file |
| "List all my clients" | Grep through text | `tag find client` → structured list |
| Track relationships | Text references | Graph links with backlinks |
| "What's due this week?" | ❌ Not possible | `agenda week` → parsed dates |
| Task management | ❌ No date support | SCHEDULED, DEADLINE, repeaters |

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

The real differentiator is **agenda queries**. The moment you want:
- "What's due today?"
- "Schedule this for next Monday"
- "Show me overdue tasks"

...MEMORY.md and Obsidian can't help. org-memory handles this natively because org-mode was built for it.

## What it does

The skill teaches the agent to use `org` for:

- **Knowledge graph**: create nodes, link them, query by tag/title/backlink/search
- **Task management**: create/complete/schedule/refile tasks in the human's org files
- **Structured memory**: record entities, relationships, and constraints as linked org-roam nodes instead of flat text
- **Batch mutations**: apply multiple changes atomically

By default the agent maintains two directories: its own knowledge base and the human's files. Either feature can be disabled independently. All files are plain text, human-readable, and version-controllable.

## Configuration

| Variable | Default | Purpose |
|---|---|---|
| `ORG_MEMORY_USE_FOR_AGENT` | `true` | Enable the agent's own knowledge base |
| `ORG_MEMORY_AGENT_DIR` | `~/org/agent` | Agent's org directory |
| `ORG_MEMORY_AGENT_DATABASE_LOCATION` | `~/.local/share/org-memory/agent/.org.db` | Agent's database |
| `ORG_MEMORY_USE_FOR_HUMAN` | `true` | Enable task management in the human's org files |
| `ORG_MEMORY_HUMAN_DIR` | `~/org/human` | Human's org directory |
| `ORG_MEMORY_HUMAN_DATABASE_LOCATION` | `~/.local/share/org-memory/human/.org.db` | Human's database |

All are optional. If unset, the defaults apply. Set `ORG_MEMORY_USE_FOR_AGENT` or `ORG_MEMORY_USE_FOR_HUMAN` to anything other than `true` to disable that feature.

The databases are stored under `~/.local/share/org-memory/` by default, separate from both the org files and the emacs org-roam database (`~/.emacs.d/org-roam.db`) to avoid concurrent-write conflicts. The `org` CLI itself defaults to the emacs database; the skill overrides this via `--db`.

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
