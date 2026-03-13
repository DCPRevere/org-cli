# Memory architecture

`$ORG_MEMORY_AGENT_DIR` is the agent's primary long-term memory. It replaces flat memory files (like MEMORY.md) with a structured, searchable knowledge graph.

**When org-memory is the primary memory system** (i.e. the user said yes during migration and `plugins.slots.memory` is `"none"`): do not read or write `MEMORY.md`, and do not call `memory_search` or `memory_get`. All long-term memory goes through org files — `memory.org`, daily files, and entity nodes.

## Memory migration (optional, user-initiated)

This migration is **entirely optional** and only runs when the user explicitly asks for it. It reads and writes files outside the declared `$ORG_MEMORY_*` directories:

- **Reads:** `~/.openclaw/workspace/MEMORY.md`, `~/.openclaw/workspace/memory/*.md`
- **Writes:** `$ORG_MEMORY_AGENT_DIR/memory.org`, `$ORG_MEMORY_AGENT_DIR/daily/*.org`, `~/.openclaw/openclaw.json`

**Never start migration automatically.** Only proceed if the user explicitly requests it.

1. **Ask the user:** "Would you like org-memory to be your primary memory system? This replaces MEMORY.md and daily files with org-mode equivalents. It will read `~/.openclaw/workspace/MEMORY.md` and modify `~/.openclaw/openclaw.json`."

2. **If yes:**

   a. **Back up first.** Tell the user to back up `~/.openclaw/openclaw.json` and `~/.openclaw/workspace/MEMORY.md` before proceeding.

   b. **Migrate MEMORY.md.** Read `~/.openclaw/workspace/MEMORY.md`. Convert markdown headings to org headings (`# ` → `* `, `## ` → `** `, etc.) and write the result to `$ORG_MEMORY_AGENT_DIR/memory.org`. If the file is empty or missing, create a minimal `memory.org` with a single top-level heading.

   c. **Migrate daily files.** For each `~/.openclaw/workspace/memory/YYYY-MM-DD.md`, convert headings the same way and write to `$ORG_MEMORY_AGENT_DIR/daily/YYYY-MM-DD.org`. Create the `daily/` directory if needed.

   d. **Disable default memory plugin.** Update `~/.openclaw/openclaw.json` — set `plugins.slots.memory` to `"none"`. This stops OpenClaw from loading `memory_search`/`memory_get` and prevents auto-flush to MEMORY.md during compaction.

   ```json
   {
     "plugins": {
       "slots": {
         "memory": "none"
       }
     }
   }
   ```

   Merge this into the existing JSON — don't overwrite other keys.

   e. **Sync.** Run `org roam sync` and `org index` against `$ORG_MEMORY_AGENT_DIR` so the new files are in the database.

3. **If no:** Leave the default memory system active. The skill still provides knowledge graph (roam nodes, entity linking) and task management, but MEMORY.md remains the agent's primary curated memory. Do not write to `memory.org` or daily org files — those sections of the skill only apply when org-memory is the primary memory system.

## File structure

```
$ORG_MEMORY_AGENT_DIR/
├── memory.org          # Curated long-term memory (read every session)
├── daily/
│   ├── 2026-02-21.org # Today's raw log
│   ├── 2026-02-20.org # Yesterday's raw log
│   └── ...
└── *.org              # Entity nodes (people, projects, etc.)
```

**`memory.org`** — the agent's permanent memory. Curated, concise, always loaded. Contains who the human is, active projects, lessons learned, conventions, and anything needed every session. Keep it tight — move detail into entity nodes and keep memory.org as a summary with links.

**`daily/YYYY-MM-DD.org`** — raw daily logs. What happened, decisions made, ambient facts captured, things learned. Working notes, not curated. Write freely.

**Entity nodes** (`*.org`) — structured roam nodes for people, projects, concepts. Tagged, linked, and queryable on demand.

## Session start routine

At the start of every session:

1. **Read `memory.org`** — your permanent memory, always relevant
2. **Read today and yesterday's daily files** (`daily/YYYY-MM-DD.org`) — recent context
3. **Load today's agenda**: `org today -d "$ORG_MEMORY_HUMAN_DIR" -f json`

That's it. Don't load everything. Query entity nodes on demand when the conversation needs them.

## During the session

- **Ambient facts** → append to today's daily file (`daily/YYYY-MM-DD.org`)
- **New entity** → create a roam node, then link from today's daily file
- **Update to existing entity** → `org roam node find`, then `org append` to the node
- **Something worth keeping permanently** → also update `memory.org`

## Ambient capture

Not everything the human tells you is a command. Sometimes they mention facts in passing — a person's preference, a date, a technical detail, a relationship. When information has lasting value (people, decisions, technical details, preferences), offer to save it to `$ORG_MEMORY_AGENT_DIR`. Don't capture routine operational chatter.

**Process:** Complete the human's explicit request first. Then tell the human what ambient fact you noticed and confirm before writing. If they agree, search for an existing node and update it (or create one if new). Always print `org-memory: <action> <file-path>` after every write. Never write to the agent's knowledge base without the human's confirmation.

**Example:** "Cancel my Thursday meeting with Sarah and reschedule the API migration review to next week. Sarah is going to be out all of March."
- Cancel and reschedule: explicit requests → execute in `$ORG_MEMORY_HUMAN_DIR`
- Sarah out all of March: ambient information → offer to record in `$ORG_MEMORY_AGENT_DIR`
- Do both. Don't choose one or the other.

## Memory maintenance

Periodically (every few days, during a quiet moment):

1. Review recent daily files
2. Promote important facts to entity nodes or `memory.org`
3. Remove outdated info from `memory.org`
4. Daily files can accumulate — they're cheap and searchable via `org fts`

This is like reviewing a journal and updating your mental model. Daily files are raw notes; memory.org is curated wisdom; entity nodes are structured knowledge.
