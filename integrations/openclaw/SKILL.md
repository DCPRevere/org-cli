---
name: org-memory
version: 0.7.0
description: "Structured knowledge base and task management using org-mode files. Query, mutate, link, and search org files and org-roam databases with the `org` CLI."
metadata: {"openclaw":{"emoji":"🦄","homepage":"https://github.com/dcprevere/org-cli","requires":{"bins":["org"],"env":["ORG_MEMORY_AGENT_DIR","ORG_MEMORY_HUMAN_DIR"]},"install":[{"kind":"download","label":"Download from GitHub releases: https://github.com/dcprevere/org-cli/releases"}],"scope":{"reads":["$ORG_MEMORY_AGENT_DIR","$ORG_MEMORY_HUMAN_DIR","$ORG_MEMORY_AGENT_ROAM_DIR","$ORG_MEMORY_HUMAN_ROAM_DIR","$ORG_MEMORY_AGENT_DATABASE_LOCATION","$ORG_MEMORY_HUMAN_DATABASE_LOCATION"],"writes":["$ORG_MEMORY_AGENT_DIR","$ORG_MEMORY_HUMAN_DIR","$ORG_MEMORY_AGENT_ROAM_DIR","$ORG_MEMORY_HUMAN_ROAM_DIR","$ORG_MEMORY_AGENT_DATABASE_LOCATION","$ORG_MEMORY_HUMAN_DATABASE_LOCATION"],"migrationReads":["~/.openclaw/workspace/MEMORY.md","~/.openclaw/workspace/memory/"],"migrationWrites":["~/.openclaw/openclaw.json"]}}}
---

# org-memory

Maintain structured, linked, human-readable knowledge in org-mode files via the `org` CLI. Org files combine plain text with rich structure — headlines, TODO states, tags, properties, timestamps, links — and back onto a SQLite knowledge graph via org-roam.

## The prime directive

**Everything persistent goes in org.** Tasks, notes, facts, preferences, relationships, decisions — if the human says it and it has lasting value, it lives in an org file.

- Never hold long-term information in chat context alone.
- Never write to `MEMORY.md` or call `memory_search` / `memory_get` (unless `ORG_MEMORY_USE_FOR_AGENT` is not `true`).
- Never reply "got it" without a corresponding write when a write is warranted.
- If a mutation fails, retry or surface the error — do not silently drop it.

**Always surface the ID when you mention an item.** When you create, find, reference, or list anything in org, include its short CUSTOM_ID (or UUID for roam nodes) in your reply so the human can act on it by ID. If an item has no ID (index missing — see Stable identifiers), say so and offer to backfill.

After every write, print exactly:

```
org-memory: <action> [<id>] <file-path>
```

Examples:

```
org-memory: added TODO [k4t] ~/org/human/inbox.org
org-memory: added note [k4t] ~/org/human/inbox.org
org-memory: marked DONE [k4t] ~/org/human/inbox.org
org-memory: created node [3f2a-…] ~/org/agent/roam/sarah.org
```

If the response JSON has no `custom_id` (and no `id` for roam nodes), print `[no-id]` and flag it.

No silent writes. Ever.

## Shortcuts

When the human uses these prefixes, act immediately.

Targets are explicit: `@h` (human) or `@a` (agent). The letter after is the action.

| Action | Human | Agent | Target | Does |
|---|---|---|---|---|
| `n` note | `@hn` | `@an` | inbox | Create plain headline (no TODO, no date) |
| `k` knowledge | `@hk` | `@ak` | roam | Store/update a knowledge node |
| `f` find | `@hf` | `@af` | all | Full-text search across headlines and roam nodes, return IDs |
| `t` todo | `@ht` | `@at` | inbox | Create TODO, extract any date |
| `s` schedule | `@hs` | `@as` | workspace | Reschedule matching TODO |
| `d` done | `@hd` | `@ad` | workspace | Mark matching TODO DONE |

`t` vs `n`: `t` creates a TODO (appears in TODO lists, can be scheduled/done). `n` creates a plain captured headline — a thought parked under inbox, no TODO state. Use `n` when the human is noting something for themselves, not committing to action. A dateless TODO is still `@ht` with no date in the text; it just lands without `SCHEDULED`/`DEADLINE`.

## Action details

In the examples below, `$DIR` is the target workspace (`$ORG_MEMORY_HUMAN_DIR` for `@h*`, `$ORG_MEMORY_AGENT_DIR` for `@a*`), `$ROAM` is the matching roam dir, and `$DB` is the matching database. Substitute per the `@h`/`@a` prefix.

### `n` — create a plain note (`@hn`, `@an`)

A plain headline, no TODO keyword, no date. Does not appear in TODO lists. Use for captured thoughts, ideas, observations the human wants parked.

```bash
org add "$DIR/inbox.org" '<text>' --db "$DB" -f json
```

If the human later wants to promote it to a TODO, use the ID: `org todo set <id> TODO -d "$DIR" --db "$DB"`.

### `k` — knowledge in the roam graph (`@hk`, `@ak`)

Store in the roam graph for the given target. Never create duplicates.

1. Find: `org roam node find '<subject>' -d "$ROAM" --db "$DB" -f json`
2. Exists → `org append <custom_id> '<fact>' -d "$DIR" --db "$DB" -f json`
3. New → `org roam node create '<subject>' -d "$ROAM" --db "$DB" -f json` then append.

Roam nodes live in the matching roam dir, never in the workspace root.

Roam nodes are identified by UUID (`data.id`); headlines inside them by CUSTOM_ID (`data.custom_id`). Surface whichever applies: `Noted against Sarah [3f2a-…]: prefers morning meetings.`

### `f` — find across headlines and knowledge (`@hf`, `@af`)

Search the target's org files and roam graph, return matches with IDs. No mutation. Matches any headline — TODO, done, or plain — and any roam node.

1. `org fts '<query>' -d "$DIR" --db "$DB" -f json` (full-text across titles and bodies)
2. `org roam node find '<query>' -d "$ROAM" --db "$DB" -f json`
3. Merge results, group by kind, print as `[<id>] <title>` lines.

If the human is specifically asking for TODOs only, use `org todo list --search '<query>' -d "$DIR" --db "$DB" -f json` instead. If they're asking for a headline by exact title or a filter like tag/state, use `org headlines` with the relevant flags.

Return the list even when empty — say "no matches" rather than retrying silently.

### `t` — create a TODO (`@ht`, `@at`)

Extract any relative or absolute date from the text. Use `--deadline` for hard dates ("by Friday", "due March 1st"); `--scheduled` for softer timing ("in 3 weeks", "next month"). If no date, omit both — the TODO is still created, just without a date.

```bash
org add "$DIR/inbox.org" '<title>' --todo TODO [--scheduled <date> | --deadline <date>] --db "$DB" -f json
```

- `@ht submit taxes in 3 weeks` → `--scheduled 2026-05-07`
- `@ht renew passport by June` → `--deadline 2026-06-01`
- `@ht call dentist tomorrow` → `--scheduled 2026-04-17`
- `@ht book flights` → no date flag

Read `data.custom_id` from the JSON response and include it in your reply. Example: `Added TODO [k4t]: Submit taxes, scheduled 2026-05-07.`

### `s` — reschedule (`@hs`, `@as`)

Same search flow as `d`, then:

```bash
org schedule <custom_id> <date> -d "$DIR" --db "$DB" -f json
```

### `d` — mark DONE (`@hd`, `@ad`)

1. Search: `org todo list --search '<text>' -d "$DIR" --db "$DB" -f json`
2. One match → `org todo set <custom_id> DONE -d "$DIR" --db "$DB" -f json`
3. Multiple → show each as `[<custom_id>] <title>` so the human can pick by ID. Ask which.
4. None → say so. If you searched with `--state TODO` and got nothing, retry without it (the match may be in another state).

Reply with the ID: `Marked DONE [k4t]: Submit taxes.`

## Ambient capture

When the human mentions a durable fact in passing — a preference, a relationship, a date, a constraint — offer to save it. Complete the explicit request first; then say "I'd like to note: <X>" and wait for confirmation.

Bias toward capturing. Under-capture is worse than over-capture: you can always prune, but you can't recover what was never written.

## Stable identifiers

Every headline added via `org add` gets a short CUSTOM_ID (e.g. `k4t`) — **but only when the index DB exists.** If you ran the first-time setup, it does. If a headline was created by hand, by `batch`, or by `add` before the index was built, it won't have one.

Every roam node created via `org roam node create` gets a UUID `:ID:` unconditionally. Nodes also carry a CUSTOM_ID on their file-level headline if the index exists.

Use IDs in subsequent commands — no file path needed:

```bash
org todo set k4t DONE  -d "$ORG_MEMORY_HUMAN_DIR" --db "$ORG_MEMORY_HUMAN_DATABASE_LOCATION" -f json
org schedule k4t 2026-05-01 -d "$ORG_MEMORY_HUMAN_DIR" --db "$ORG_MEMORY_HUMAN_DATABASE_LOCATION" -f json
org append k4t 'Note' -d "$ORG_MEMORY_HUMAN_DIR" --db "$ORG_MEMORY_HUMAN_DATABASE_LOCATION" -f json
```

Preference order: CUSTOM_ID > org-id (UUID) > exact title. **Never use `pos`** — it changes on every edit. For multiple mutations in one file, use `org batch`.

**Always include the ID when you mention an item to the human.** Read `data.custom_id` from any JSON response; for roam nodes, also read `data.id`. Echo it in brackets: `[k4t]` or `[3f2a-…]`. This is how the human follows up — "reschedule k4t to Friday" is only possible if you told them the ID existed.

If a response has no `custom_id`, tell the human: "This item has no short ID — run `org custom-id assign -d <dir> --db <db>` to backfill, or refer by title." Never silently proceed with just a title — the next lookup may match the wrong item.

Backfill missing IDs: `org custom-id assign -d <dir> --db <db>`.

## Output

All commands accept `-f json`. Always pass it. Envelopes:

- Success: `{"ok":true,"data":...}`
- Error: `{"ok":false,"error":{"type":"...","message":"..."}}`

Branch on `ok`. Handle by `type`: `file_not_found`, `headline_not_found` (re-query), `parse_error` (don't retry), `invalid_args` (check `org schema`).

## Command safety

**Only environment variable paths go in double quotes. User text always goes in single quotes.** Double quotes expand `$(...)`, backticks, and variables — that's shell injection when the text came from the human.

```bash
# Right
org add "$ORG_MEMORY_HUMAN_DIR/inbox.org" 'User provided text' --todo TODO -f json

# Wrong — user text in double quotes is an injection vector
org add "$ORG_MEMORY_HUMAN_DIR/inbox.org" "User provided text" --todo TODO -f json
```

Embedded single quote → `'\''`. Multi-line → pipe via stdin:

```bash
printf '%s' 'Long text' | org append k4t --stdin -d "$ORG_MEMORY_AGENT_DIR" --db "$ORG_MEMORY_AGENT_DATABASE_LOCATION" -f json
```

(If you are calling the org-memory OpenClaw plugin's `org_*` tools instead of shelling out, pass raw text — those tools use `execFile` and do no shell interpolation.)

Never bypass `org` for org-file mutations — no `echo >>`, no direct file edits, no `sed`/`awk` rewrites. The CLI maintains formatting, timestamps, index sync, and ID allocation; ad-hoc edits silently break all of that.

## Configuration

Required:

| Variable | Default | Purpose |
|---|---|---|
| `ORG_MEMORY_AGENT_DIR` | `~/org/agent` | Agent's workspace (memory.org, daily/) |
| `ORG_MEMORY_HUMAN_DIR` | `~/org/human` | Human's workspace (inbox.org, tasks) |
| `ORG_MEMORY_AGENT_DATABASE_LOCATION` | `$ORG_MEMORY_AGENT_DIR/.org.db` | Agent's DB |
| `ORG_MEMORY_HUMAN_DATABASE_LOCATION` | `$ORG_MEMORY_HUMAN_DIR/.org.db` | Human's DB |

Optional:

| Variable | Default | Purpose |
|---|---|---|
| `ORG_MEMORY_AGENT_ROAM_DIR` | `$ORG_MEMORY_AGENT_DIR/roam` | Agent's roam nodes |
| `ORG_MEMORY_HUMAN_ROAM_DIR` | `$ORG_MEMORY_HUMAN_DIR/roam` | Human's roam nodes |
| `ORG_MEMORY_USE_FOR_AGENT` | `true` | Enable agent knowledge base |
| `ORG_MEMORY_USE_FOR_HUMAN` | `true` | Enable human task management |
| `ORG_MEMORY_ORG_BIN` | `org` | Path to the `org` binary |
| `ORG_MEMORY_INBOX_FILE` | `inbox.org` | Inbox filename |

Always pass `--db`. Without it, the CLI defaults to `<directory>/.org.db`, which may diverge from configured locations.

If `ORG_MEMORY_USE_FOR_AGENT` ≠ `true`: skip `@a*` shortcuts.
If `ORG_MEMORY_USE_FOR_HUMAN` ≠ `true`: skip `@h*` shortcuts.

## First-time setup

Run only for directories whose `USE_FOR_*` flag is `true`:

```bash
# Sync existing files (skip if starting fresh)
org roam sync -d "$ORG_MEMORY_HUMAN_DIR" --db "$ORG_MEMORY_HUMAN_DATABASE_LOCATION"
org roam sync -d "$ORG_MEMORY_AGENT_DIR" --db "$ORG_MEMORY_AGENT_DATABASE_LOCATION"

# Seed the agent's knowledge base (skip if files exist)
org roam node create 'Index' -d "$ORG_MEMORY_AGENT_ROAM_DIR" --db "$ORG_MEMORY_AGENT_DATABASE_LOCATION" -f json

# Build the headline index — enables CUSTOM_IDs and file-less commands
org index -d "$ORG_MEMORY_HUMAN_DIR" --db "$ORG_MEMORY_HUMAN_DATABASE_LOCATION"
org index -d "$ORG_MEMORY_AGENT_DIR" --db "$ORG_MEMORY_AGENT_DATABASE_LOCATION"
```

## Discovery

`org schema` dumps the full command catalog as JSON. Use it to construct commands without memorising flags.

Any mutation can be previewed with `--dry-run` — the command runs its logic and reports what *would* change without touching the file. Use it when the human asks to preview, when a batch is large, or when you're uncertain which headline will match.

## Useful commands

Read-only queries the shortcuts don't cover directly. All accept `-f json`.

**Today / agenda** — "what do I need to do":

```bash
org today -d "$DIR" --db "$DB" -f json                     # TODOs due today + overdue
org agenda today -d "$DIR" --db "$DB" -f json              # scheduled + deadlines + overdue for today
org agenda week -d "$DIR" --db "$DB" -f json               # next 7 days
org agenda todo --state TODO -d "$DIR" --db "$DB" -f json  # every open TODO
```

**TODO list filters** — narrower than `@hf`:

```bash
org todo list --state TODO --unscheduled -d "$DIR" --db "$DB" -f json  # TODOs with no date
org todo list --state TODO --overdue -d "$DIR" --db "$DB" -f json      # past their scheduled/deadline
org todo list --state TODO --due-before 2026-05-01 -d "$DIR" --db "$DB" -f json
org todo list --state TODO --tag work -d "$DIR" --db "$DB" -f json
org todo list --state TODO --priority A -d "$DIR" --db "$DB" -f json
org todo list --state TODO --sort priority -d "$DIR" --db "$DB" -f json
```

**Headlines** — any headline (TODO, done, or plain), filter by tag/level/property:

```bash
org headlines --todo TODO -d "$DIR" --db "$DB" -f json
org headlines --tag project -d "$DIR" --db "$DB" -f json
org headlines --property CATEGORY=admin -d "$DIR" --db "$DB" -f json
```

**Roam** — list, get, sync:

```bash
org roam node list -d "$ROAM" --db "$DB" -f json
org roam node get <uuid> -d "$ROAM" --db "$DB" -f json
org roam sync -d "$DIR" --db "$DB"
```

## References

Read on demand:

- **Knowledge management** (`{baseDir}/references/knowledge-management.md`) — when working with the agent's roam graph.
- **Task management** (`{baseDir}/references/task-management.md`) — for batch ops or translating natural language to queries.
- **Memory architecture** (`{baseDir}/references/memory-architecture.md`) — at session start. Also contains the optional MEMORY.md → org migration; only run if the user asks.
