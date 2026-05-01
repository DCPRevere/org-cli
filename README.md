<div align="center">

<pre>
                                                 
                                       ,,    ,,  
                                     `7MM    db  
                                       MM        
 ,pW"Wq.`7Mb,od8 .P"Ybmmm      ,p6"bo  MM  `7MM  
6W'   `Wb MM' "':MI  I8       6M'  OO  MM    MM  
8M     M8 MM     WmmmP" mmmmm 8M       MM    MM  
YA.   ,A9 MM    8M            YM.    , MM    MM  
 `Ybmd9'.JMML.   YMMMMMb       YMbmd'.JMML..JMML.
                6'     dP                        
                Ybmmmd'                          
</pre>

</div>

<p align="center">
  <strong>An org-mode CLI for scripts and AI agents.</strong><br>
  Query and mutate org files without running Emacs.
</p>

<p align="center">
  <a href="https://dotnet.microsoft.com/"><img src="https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet" alt=".NET 9" /></a>
  <a href="https://github.com/dcprevere/org-cli/releases/latest"><img src="https://img.shields.io/github/v/release/dcprevere/org-cli?label=org-cli" alt="latest release" /></a>
</p>

---

## What it is

A parser and CLI for org-mode files: headlines, TODO states, priorities, tags, timestamps, property drawers, clock entries, links. Output is structured (text or JSON) and mutations are atomic.

Intended for scripts and AI agents that need to read or edit an org-mode corpus without running Emacs.

## Installation

Pre-built, self-contained binaries for Linux, macOS, and Windows are attached to every [GitHub release](https://github.com/dcprevere/org-cli/releases). No .NET runtime required.

### Linux / macOS

One-line install (detects OS/arch, verifies sha256, installs to `~/.local/bin`):

```sh
curl -fsSL https://raw.githubusercontent.com/dcprevere/org-cli/master/scripts/install.sh | bash
```

Options:

```sh
# Specific version
curl -fsSL https://raw.githubusercontent.com/dcprevere/org-cli/master/scripts/install.sh | ORG_VERSION=1.0.0 bash

# System-wide install (uses sudo if needed)
curl -fsSL https://raw.githubusercontent.com/dcprevere/org-cli/master/scripts/install.sh | bash -s -- --prefix /usr/local/bin
```

Upgrade by re-running the one-liner — it overwrites the existing binary in place.

Manual install:

```sh
# Pick the asset for your platform:
#   org-linux-x64.tar.gz   org-linux-arm64.tar.gz
#   org-osx-x64.tar.gz     org-osx-arm64.tar.gz
ASSET=org-linux-x64.tar.gz
curl -L "https://github.com/dcprevere/org-cli/releases/latest/download/${ASSET}" | tar xz
install -m 755 org ~/.local/bin/org    # or: sudo mv org /usr/local/bin/
org --version
```

Verify checksums (optional):

```sh
curl -LO https://github.com/dcprevere/org-cli/releases/latest/download/sha256sums.txt
sha256sum -c --ignore-missing sha256sums.txt
```

### Windows

Download `org-win-x64.zip` from the [releases page](https://github.com/dcprevere/org-cli/releases), extract `org.exe`, and add its directory to `PATH`.

### Other install methods

```sh
# Global .NET tool (requires .NET 9.0 SDK)
dotnet tool install --global OrgCli

# Build from source
git clone https://github.com/dcprevere/org-cli.git
cd org-cli
dotnet build OrgCli.slnx
```

## Quick start

```sh
# What do I need to do today? (TODOs due today + overdue)
org today -d ~/org

# View today's full agenda (all scheduled + deadlines)
org agenda today -d ~/org

# List all headlines, filter by TODO state and tag
org headlines --todo TODO --tag work -d ~/org

# Set a headline to DONE (by short ID -- no file needed)
org todo k4t DONE

# Add a new headline (auto-assigns a short CUSTOM_ID)
org add tasks.org "New task" --todo TODO --tag project --scheduled 2026-03-01

# Assign short IDs to all existing headlines
org custom-id assign -d ~/org

# View all TODOs with filters
org todos --state TODO -d ~/org
org todos --state TODO --unscheduled -d ~/org
org todos --state TODO --overdue -d ~/org
org todos --search "meeting" --due-before 2026-03-01 -d ~/org

# Change a TODO state
org todo k4t DONE
org todo tasks.org "Pay rent" DONE

# Search across files
org search "meeting.*notes" -d ~/org

# JSON output for scripting
org today -d ~/org -f json

# Sync and query an org-roam database
org roam sync -d ~/org
org roam node list -d ~/org
```

## What it does

### Org file operations

- **Headlines** — list, filter by TODO state / tag / level / property, with tag and property inheritance. Each headline shows its short CUSTOM_ID for easy reference.
- **Mutations** — set TODO state, priority, tags, properties, SCHEDULED, DEADLINE; respects repeaters, per-keyword logging, LOGBOOK drawers. Commands accept a bare CUSTOM_ID instead of `<file> <identifier>` when an index exists.
- **Clock** — clock in/out, clock reports with per-headline and grand totals.
- **Refile** — move subtrees within or across files, with level adjustment.
- **Archive** — move subtrees to `.org_archive` with metadata stamps.
- **Search** — regex search with context (containing headline, file, line number).
- **Links** — resolve `id:`, `file:`, fuzzy, and abbreviated links across the document set.
- **Export** — convert via pandoc to any supported format.
- **Batch mode** — execute multiple mutations atomically from JSON on stdin.

### Todos

View and filter all TODO headlines across your org files.

- `org todos` — list all headlines with a TODO state
- `--state TODO` / `--state DONE` — filter by state
- `--scheduled` / `--unscheduled` — filter by presence of SCHEDULED date
- `--overdue` — items where SCHEDULED date is before today
- `--due-before <date>` / `--due-after <date>` — date range filtering
- `--priority A` — filter by priority
- `--tag work` — filter by tag
- `--file "personal"` — filter by filename substring
- `--search "meeting"` — case-insensitive title search
- `--sort scheduled|deadline|priority|title|file` — sort output (default: scheduled)
- `--reverse` — reverse sort order
- All filters are combinable

### Agenda

Queries org files directly (no database needed).

- `org today` — all non-done TODOs due today or overdue, split into sections
- Today/week views with overdue detection
- TODO list with state and tag filtering
- Timed items (`SCHEDULED: <2026-03-01 Mon 14:00>`) sort before untimed
- Timestamp range support (`<start>--<end>`)
- All list output uses aligned columns (like `docker ps`)

### Org-roam

Manages an org-roam v2 SQLite database, compatible with Emacs org-roam (schema version 20).

- Sync files to database (incremental or forced)
- Node CRUD (file-level and headline-level nodes)
- Backlinks, tags, aliases, refs
- Link management

### Index and CUSTOM_ID

`org index` builds a SQLite headline index for fast full-text search and CUSTOM_ID resolution.

- `org add` auto-assigns a short base36 CUSTOM_ID (e.g. `k4t`) to new headlines when an index exists
- `org custom-id assign` backfills CUSTOM_IDs on all existing headlines that lack one
- With CUSTOM_IDs, most commands accept a bare ID instead of `<file> <identifier>`: `org todo k4t DONE`
- `org fts` provides FTS5 full-text search over indexed headlines

### For AI agents

- `org schema` outputs a machine-readable JSON description of all commands and their arguments
- `org batch` accepts a JSON command array on stdin for atomic multi-step operations
- `-f json` on all commands for structured output with `{"ok":true,"data":...}` envelopes
- `--dry-run` previews mutations without writing
- `org completions bash|zsh|fish` for shell integration

See [docs/agents.org](docs/agents.org) for a guide to building a knowledge base with an AI agent.

### OpenClaw integration

Two [OpenClaw](https://github.com/openclaw/openclaw) plugins ship with this repo:

- `org-cli` — task capture, scheduling, and knowledge graph operations against your org files.
- `org-memory` — extends `org-cli` with a separate workspace for the agent's own notes.

```sh
# Manage your own org files
openclaw skill install org-cli

# Optional: also let the agent persist its own memory in org
openclaw skill install org-memory
```

The plugins map short prefixes to `org` commands:

- `t: submit taxes in 3 weeks` — scheduled TODO in your inbox
- `n: think about hanging pictures up` — plain captured headline
- `k: Sarah prefers morning meetings` — roam fact against `Sarah`
- `d: groceries` — resolves by CUSTOM_ID and marks DONE
- `s: taxes to next Friday` — reschedule
- `f: sacra` — search headlines and roam nodes
- `"What's due today?"` — runs `org today`

With `org-memory` loaded, `@a`-prefixed shortcuts (`@at:`, `@an:`, `@ak:`, `@ad:`, `@as:`, `@af:`) target the agent's own workspace.

See [integrations/openclaw/README.md](integrations/openclaw/README.md) for an overview.

## Configuration

Configuration is resolved in order (later overrides earlier):

1. Built-in defaults
2. XDG config file (`$XDG_CONFIG_HOME/org-cli/config.json`)
3. Environment variables (`ORG_CLI_LOG_DONE`, `ORG_CLI_DEADLINE_WARNING_DAYS`, etc.)
4. CLI flags (`--config`, `--log-done`, `--deadline-warning-days`)
5. Per-file in-buffer settings (`#+TODO:`, `#+STARTUP:`, `#+PRIORITIES:`)

See [docs/usage.org](docs/usage.org) for the complete configuration reference.

## Project structure

```
src/OrgCli.Org/    Parser library. Types, parsers, writer, mutations, agenda, config, batch mode.
src/OrgCli.Index/  Headline index. SQLite FTS5, CUSTOM_ID generation and resolution.
src/OrgCli.Roam/   Roam DB layer. Database, sync, node operations.
src/OrgCli/        CLI entry point.
tests/OrgCli.Tests/
```

## Building and testing

```sh
dotnet build OrgCli.slnx
dotnet test OrgCli.slnx
```

## Non-goals

- Interactive or TUI features. This is a tool for scripts, not humans at a terminal.
- Tables, spreadsheets, babel/code block evaluation.
- Capture templates. Appending to a file is trivial; no tool needed.
- File watching. Sync is explicit.

## License

MIT
