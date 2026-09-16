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
  <strong>📃 CLI access to your org files, for you and your agents.</strong><br>
  Query and mutate org files without running Emacs.
</p>

<p align="center">
  <a href="https://dotnet.microsoft.com/"><img src="https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet" alt=".NET 9" /></a>
  <a href="https://github.com/dcprevere/org-cli/releases/latest"><img src="https://img.shields.io/github/v/release/dcprevere/org-cli?label=org-cli" alt="latest release" /></a>
</p>

---

## What it is

A parser and CLI for org-mode files: headlines, TODO states, priorities, tags, timestamps, property drawers, clock entries, links. Output is structured (text or JSON) and file edits use checked, staged replacements. Multi-file edits have a recovery journal.

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

# Add a new headline (always assigns a standard UUID ID)
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

# Query the optional org-roam view over your Org files
org roam sync -d ~/org
org roam node list -d ~/org
```

## What it does

### Org file operations

- **Headlines** — list, filter by TODO state / tag / level / property, with tag and property inheritance. Each headline shows its short CUSTOM_ID for easy reference.
- **Mutations** — set TODO state, priority, tags, properties, SCHEDULED, DEADLINE; respects repeaters, per-keyword logging, LOGBOOK drawers. Commands accept a bare CUSTOM_ID instead of `<file> <identifier>` with automatic index refresh.
- **Clock** — clock in/out, clock reports with per-headline and grand totals.
- **Refile** — move subtrees within or across files, with level adjustment.
- **Archive** — move subtrees to `.org_archive` with metadata stamps.
- **Search** — regex search with context (containing headline, file, line number).
- **Links** — resolve `id:`, `file:`, fuzzy, and abbreviated links across the document set.
- **Export** — convert via pandoc to any supported format.
- **Batch mode** — validate multiple mutations from JSON before writing; interrupted multi-file commits are recoverable.

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

Uses cached parsed documents, refreshed from Org files before each query.

- `org today` — all non-done TODOs due today or overdue, split into sections
- Today/week views with overdue detection
- TODO list with state and tag filtering
- Timed items (`SCHEDULED: <2026-03-01 Mon 14:00>`) sort before untimed
- Timestamp range support (`<start>--<end>`)
- All list output uses aligned columns (like `docker ps`)

### Org-roam

Org-roam is an optional extension over the same documents and index used by the core. It interprets file and heading IDs, aliases, refs, tags, and node exclusions. It supports node create/find/read, backlinks, and link/property edits without Emacs.

It does **not** read or write Emacs's org-roam database. Emacs can index the shared Org files independently. Build without the extension using `dotnet build src/OrgCli/OrgCli.fsproj -p:EnableRoam=false`.

### Owned index and identity

Org files are authoritative. The CLI owns `.org-index.db` under the selected directory (`--db` overrides it). Queries create and refresh it automatically; deleting it loses no note data. Parsed document snapshots, identity lookup, and SQLite FTS5 share one projection.

Every refresh fingerprints file contents and effective parser configuration. Unchanged files reuse their parsed snapshot; changes with unchanged timestamps are still detected. This currently reads the selected corpus on each refresh, trading some I/O for reliable freshness without a watcher. `fts --no-sync` explicitly accepts stale results.

- `org add` always assigns a UUID `ID`, independent of index state.
- Use `id:<uuid>` for standard IDs, `custom:<value>` for existing CUSTOM_IDs, or `pos:<offset>` with a file for explicit character offsets.
- Bare IDs and exact titles remain compatibility selectors; ambiguous matches fail.
- `org read id:<uuid>` and `org append id:<uuid> "text"` support file-level notes as well as headings.
- `org backlinks id:<uuid>` is a core command.
- `org custom-id assign` / `org id stamp` remain legacy short-ID helpers.

### Safe edits and recovery

Edits validate the original content, acquire cooperating CLI locks, stage replacements, and then rename them into place. A failed batch command writes nothing. Refile validates its destination before changing its source. Dry runs leave source files unchanged.

Multiple file renames are not an OS-level atomic transaction. If a commit is interrupted, its `.org-operation-*/manifest.json` records original and replacement contents; `.org-pending` markers block overlapping edits. Run `org recover <manifest.json>` to finish. Recovery refuses files changed by another editor. Keep journals private: they contain note contents. Locks coordinate CLI processes; unrelated editors do not participate in these locks.

See [the architecture and migration notes](docs/architecture.md) for boundaries, cache guarantees, and remaining limits.

### For AI agents

- `org schema` outputs a machine-readable JSON description of all commands and their arguments
- `org batch` accepts a JSON command array on stdin for validated multi-step operations
- `-f json` on all commands for structured output with `{"ok":true,"data":...}` envelopes
- `--dry-run` previews mutations without writing
- `org completions bash|zsh|fish` for shell integration

See [docs/agents.org](docs/agents.org) for a guide to building a knowledge base with an AI agent.

### Optional API and MCP

Use the same binary when a client needs a live connection:

```sh
org serve -d ~/org                  # Local HTTP API
org serve --mcp -d ~/org            # API plus MCP endpoint
org mcp --stdio -d ~/org            # Launched on demand by a local MCP client
```

Search, fetch, agenda, capture, append, task updates, and backlinks share the owned index and file mutation primitives. No server is needed for ordinary CLI commands. See [API and MCP usage](docs/api.md) for schemas, revision checks, authentication, read-only mode, and client configuration.

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
src/OrgCli.Index/  Owned document cache, SQLite FTS5, identity lookup.
src/OrgCli.Roam.Extension/ Optional org-roam semantics over the core index.
src/OrgCli.Roam/   Legacy database API retained for compatibility tests; not used by the CLI.
src/OrgCli/        CLI entry point.
tests/OrgCli.Tests/
```

## Building and testing

```sh
dotnet build OrgCli.slnx
dotnet test OrgCli.slnx
python3 tests/server_smoke.py
```

`VirtualWorkspaceTests.fs` exercises the production CLI with an in-memory filesystem, environment, clock, and SQLite database. It includes stale timestamps, configuration changes, duplicate identities, dry runs, failed commits, recovery conflicts, and isolated workspaces. Legacy Emacs interoperability tests require Emacs and may be skipped; they do not validate the new extension.

## Non-goals

- Interactive or TUI features. This is a tool for scripts, not humans at a terminal.
- Tables, spreadsheets, babel/code block evaluation.
- Capture templates. Appending to a file is trivial; no tool needed.
- File watching. Queries refresh the owned index automatically.

## License

MIT
