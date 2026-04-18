# Memory architecture

`$ORG_MEMORY_DIR` is your primary long-term memory. It replaces flat memory files (like MEMORY.md) with a structured, searchable knowledge graph.

When `org-memory` is loaded, persistent graph-structured knowledge routes through org-roam — not memory-wiki, not ad-hoc appending to MEMORY.md. See the SKILL.md "Memory-wiki override" section for the boundary (flat typed memory can still go to MEMORY.md; graph-structured memory goes here).

## File structure

```
$ORG_MEMORY_DIR/
├── memory.org          # Curated long-term memory (read every session)
├── daily/
│   ├── 2026-04-18.org  # Today's raw log
│   ├── 2026-04-17.org  # Yesterday's raw log
│   └── ...
└── roam/*.org          # Entity nodes (people, projects, etc.)
```

**`memory.org`** — your permanent memory. Curated, concise, always loaded at session start. Contains who the user is, active projects, lessons learned, conventions, and anything needed every session. Keep it tight — move detail into entity nodes and keep memory.org as a summary with links.

**`daily/YYYY-MM-DD.org`** — raw daily logs. What happened, decisions made, ambient facts captured, things learned. Working notes, not curated. Write freely.

**Entity nodes** (`roam/*.org`) — structured roam nodes for people, projects, concepts. Tagged, linked, and queryable on demand.

## Session start routine

The `org-memory` plugin's `before_agent_start` hook injects `memory.org` and today's + yesterday's daily files into your session context automatically. In addition:

1. **Load today's agenda** from the user's workspace: `org today -d "$ORG_CLI_DIR" --db "$ORG_CLI_DB" -f json`
2. **Query entity nodes on demand** as the conversation surfaces them. Don't preload everything.

## During the session

- **Ambient facts** → append to today's daily file (`@an` into `daily/YYYY-MM-DD.org`)
- **New entity** → `@ak <subject> <fact>` (creates or updates a roam node)
- **Update to existing entity** → `@ak <subject> <new fact>` (upserts into the existing node)
- **Something worth keeping permanently** → also update `memory.org`

## Memory maintenance

Periodically (every few days, during a quiet moment):

1. Review recent daily files
2. Promote important facts to entity nodes or `memory.org`
3. Remove outdated info from `memory.org`
4. Daily files can accumulate — they're cheap and searchable via `@af`

Daily files are raw notes; memory.org is curated wisdom; entity nodes are structured knowledge.

## Memory migration (optional, user-initiated)

If the user has been using OpenClaw's default memory (MEMORY.md) and wants to move it into org, this migration reads and writes files outside the declared `$ORG_MEMORY_*` directories:

- **Reads:** `~/.openclaw/workspace/MEMORY.md`, `~/.openclaw/workspace/memory/*.md`
- **Writes:** `$ORG_MEMORY_DIR/memory.org`, `$ORG_MEMORY_DIR/daily/*.org`, `~/.openclaw/openclaw.json`

**Never start migration automatically.** Only proceed if the user explicitly requests it.

1. **Ask the user**: confirm they want to migrate and that `~/.openclaw/openclaw.json` and `~/.openclaw/workspace/MEMORY.md` are backed up.

2. **Migrate MEMORY.md** → `$ORG_MEMORY_DIR/memory.org`. Convert markdown headings (`# ` → `* `, `## ` → `** `, etc.). If empty or missing, create a minimal `memory.org` with one top-level heading.

3. **Migrate daily files**. For each `~/.openclaw/workspace/memory/YYYY-MM-DD.md`, convert headings and write to `$ORG_MEMORY_DIR/daily/YYYY-MM-DD.org`.

4. **Disable the default memory plugin**. Update `~/.openclaw/openclaw.json` — set `plugins.slots.memory` to `"none"`. This stops OpenClaw from auto-flushing to MEMORY.md during compaction.

   ```json
   {
     "plugins": {
       "slots": {
         "memory": "none"
       }
     }
   }
   ```

   Merge into the existing JSON — don't overwrite other keys.

5. **Sync**. Run `org roam sync` and `org index` against `$ORG_MEMORY_DIR`.

Ambient capture still applies — when the user mentions durable facts in passing, offer to save them. Process: complete the explicit request first, then flag the ambient fact and confirm before writing.
