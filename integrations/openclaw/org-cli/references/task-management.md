# Task management

All commands operate against `$ORG_CLI_DIR` and `$ORG_CLI_DB`.

## Read the user's state

**Start here.** `org today` is the most useful query — it returns all non-done TODOs that are scheduled for today or overdue:

```bash
org today -d "$ORG_CLI_DIR" --db "$ORG_CLI_DB" -f json
```

For broader views:

```bash
org agenda today -d "$ORG_CLI_DIR" --db "$ORG_CLI_DB" -f json   # all scheduled + deadlines for today
org agenda week -d "$ORG_CLI_DIR" --db "$ORG_CLI_DB" -f json    # next 7 days
org agenda todo -d "$ORG_CLI_DIR" --db "$ORG_CLI_DB" -f json    # all TODOs with scheduled dates
org agenda todo --tag work -d "$ORG_CLI_DIR" --db "$ORG_CLI_DB" -f json
```

For rich filtering across all TODOs (scheduled or not):

```bash
org todo list --state TODO -d "$ORG_CLI_DIR" --db "$ORG_CLI_DB" -f json                    # all open TODOs
org todo list --state TODO --unscheduled -d "$ORG_CLI_DIR" --db "$ORG_CLI_DB" -f json      # unscheduled only
org todo list --state TODO --overdue -d "$ORG_CLI_DIR" --db "$ORG_CLI_DB" -f json          # overdue items
org todo list --state TODO --due-before 2026-05-01 -d "$ORG_CLI_DIR" --db "$ORG_CLI_DB" -f json
org todo list --search 'meeting' -d "$ORG_CLI_DIR" --db "$ORG_CLI_DB" -f json              # search by title
org todo list --state TODO --file 'work' -d "$ORG_CLI_DIR" --db "$ORG_CLI_DB" -f json      # filter by file
org todo list --state TODO --tag urgent -d "$ORG_CLI_DIR" --db "$ORG_CLI_DB" -f json       # filter by tag
org todo list --state TODO --priority A -d "$ORG_CLI_DIR" --db "$ORG_CLI_DB" -f json       # filter by priority
org todo list --state TODO --sort priority -d "$ORG_CLI_DIR" --db "$ORG_CLI_DB" -f json    # sort by priority
org todo list --state TODO --sort scheduled --reverse -d "$ORG_CLI_DIR" --db "$ORG_CLI_DB" -f json
```

The `todo list` command returns full data in JSON: title, todo state, priority, tags, file, pos, scheduled, deadline, level, path (parent headlines), and custom_id. All filters are combinable.

## Make changes

```bash
# Add a headline (response includes the auto-assigned custom_id)
org add "$ORG_CLI_DIR/inbox.org" 'Review PR #42' --todo TODO --tag work --deadline 2026-05-10 --db "$ORG_CLI_DB" -f json

# Subsequent commands use the custom_id — no file path needed
org todo set k4t DONE -d "$ORG_CLI_DIR" --db "$ORG_CLI_DB" -f json
org schedule a1b 2026-05-15 -d "$ORG_CLI_DIR" --db "$ORG_CLI_DB" -f json
org note a1b 'Pushed back per manager request' -d "$ORG_CLI_DIR" --db "$ORG_CLI_DB"
org append a1b 'Meeting notes from standup.' -d "$ORG_CLI_DIR" --db "$ORG_CLI_DB" -f json

# Refile still requires explicit file paths
org refile "$ORG_CLI_DIR/inbox.org" 'Review PR #42' "$ORG_CLI_DIR/work.org" 'Code reviews' --db "$ORG_CLI_DB" -f json
```

## Preview before writing

Use `--dry-run` to see what a mutation would produce without modifying the file:

```bash
org todo set tasks.org 'Buy groceries' DONE --dry-run -f json
```

## Batch operations

Apply multiple mutations atomically. Commands execute sequentially against in-memory state. Files are written only if all succeed.

```bash
echo '{"commands":[
  {"command":"todo","file":"tasks.org","identifier":"Buy groceries","args":{"state":"DONE"}},
  {"command":"tag-add","file":"tasks.org","identifier":"Write report","args":{"tag":"urgent"}},
  {"command":"schedule","file":"tasks.org","identifier":"Write report","args":{"date":"2026-05-01"}},
  {"command":"append","file":"tasks.org","identifier":"Write report","args":{"text":"Include Q1 metrics."}}
]}' | org batch -d "$ORG_CLI_DIR" --db "$ORG_CLI_DB" -f json
```

## Query shortcuts

When the user asks about their tasks or knowledge, map natural language to the right query. Don't ask "what do you mean?" — just run the query.

| User says | Action |
|---|---|
| "What do I need to do?" / "What's on my plate?" | `org today -d "$ORG_CLI_DIR" --db "$ORG_CLI_DB" -f json` (today + overdue) |
| "What's overdue?" | `org todo list --state TODO --overdue -d "$ORG_CLI_DIR" --db "$ORG_CLI_DB" -f json` |
| "What's coming up this week?" | `org agenda week -d "$ORG_CLI_DIR" --db "$ORG_CLI_DB" -f json` |
| "Show me everything tagged work" | `org todo list --state TODO --tag work -d "$ORG_CLI_DIR" --db "$ORG_CLI_DB" -f json` |
| "What do I have unscheduled?" | `org todo list --state TODO --unscheduled -d "$ORG_CLI_DIR" --db "$ORG_CLI_DB" -f json` |
| "Find all tasks about X" | `org todo list --search 'X' -d "$ORG_CLI_DIR" --db "$ORG_CLI_DB" -f json` |
| "What do you know about Sarah?" | `org roam node find 'Sarah' -d "$ORG_CLI_ROAM_DIR" --db "$ORG_CLI_DB"` → `org read <file> 'Sarah'` → `org roam backlinks <id>` |
| "What's the status of project X?" | `org fts 'X' -d "$ORG_CLI_DIR" --db "$ORG_CLI_DB"` + `org todo list --search 'X' -d "$ORG_CLI_DIR" --db "$ORG_CLI_DB"` |
| "Search my notes for Y" | `org fts 'Y' -d "$ORG_CLI_DIR" --db "$ORG_CLI_DB" -f json` |

Present results in a clean, readable format. Don't dump raw JSON at the user — summarise it.
