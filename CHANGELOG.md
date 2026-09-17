# Changelog

## 2.0.0-rc.2 — 2026-09-17

Shared task management for humans and agents, with Org files authoritative across the CLI, browser board, API, MCP, and direct editor changes.

- Recover stale task workflows with an audited reset, respect manually reopened cancellations, and automatically refresh the board without overwriting drafts.
- Keep routine CI Linux-only; native macOS and Windows smoke tests were verified during development.

- Detect direct requirement edits during claims and review; block stale completion evidence, preserve claims across refiling, and show actionable recovery in the board.

- Add a shared human/agent task workflow: dependency-aware ready queues, project/owner assignment, expiring claims, handoffs, evidence-backed submission, and separate-actor review. Existing Org TODOs can be adopted without migration.
- Add `org task` commands, four task API/MCP operations, and a browser task board embedded in the same executable.
- Fix repeated directory traversal in CLI full-text search; avoid redundant timestamp writes and configuration formatting during cache refresh.
- Unify overdue scheduled work and file-local completion handling across CLI and API agendas.
- Fix empty Org properties/keywords consuming the following line, and rebuild older cached projections automatically.

- HTTP and stdio MCP servers now watch the workspace for external edits, update only affected projections, and reuse cached snapshots between changes. Startup, directory changes, watcher errors, and periodic reconciliation repair missed notifications.
- Add virtual-filesystem tests and live watcher tests for atomic saves, renames, and deletion.

## 2.0.0-rc.1

First release candidate for the standalone Org engine and optional API/MCP interfaces. Try it against a copy of your Org workspace before adopting it for daily use.

### Added

- One executable with optional `org serve`, `org serve --mcp`, and `org mcp --stdio` modes. Ordinary CLI commands still run and exit without a server.
- Search, fetch, agenda, capture, append, task updates, and backlinks through a shared workspace API, exposed over HTTP and MCP.
- Revision checks for API updates, persistent capture deduplication, read-only mode, optional bearer authentication, and a loopback-only HTTP listener.
- An injectable filesystem, environment, and clock with virtual-workspace tests and real-process HTTP/MCP smoke coverage.
- Recovery journals and `org recover` for interrupted multi-file changes.

### Changed — migration required

- Org files are authoritative. The CLI owns a rebuildable `.org-index.db` with parsed document snapshots, full-text search, and identity lookup. Queries refresh it automatically using content and parser-configuration fingerprints.
- Org-roam is an optional extension over Org documents. The CLI no longer reads or writes Emacs's org-roam database. Emacs should index the shared files independently.
- `org add` always assigns a standard UUID `ID`. Consumers should prefer JSON `id`; existing `custom_id` values and legacy short-ID helpers remain supported.
- Explicit character offsets now use `pos:<offset>`. Numeric text is no longer treated as a position; duplicate identities and ambiguous matches fail instead of silently selecting an entry.
- File-level notes support core search, read, append, and backlinks without org-roam.

### Fixed

- Changes with unchanged timestamps now invalidate cached projections; querying a subdirectory retains unrelated cached documents.
- Failed batch validation writes no source files. Refile and archive validate destinations and stage outputs before replacement.
- Conflicting writes, unfinished recovery operations, and recovery conflicts are detected. Source-block headline examples are preserved during subtree moves.

### Removed

- Retired the OpenClaw `org-cli` and `org-memory` plugins, skills, tests, npm packaging, and ClawHub publishing. Previously published packages remain available but are no longer updated by this repository.

### Upgrade

1. Keep existing Org files and IDs; no content conversion is required.
2. Point the CLI at a CLI-owned `.org-index.db`, not an Emacs `org-roam.db` or a previously shared database. The first query builds the new index.
3. Update consumers to use standard `id` values and explicit `pos:` selectors where applicable.
4. Use the CLI, the optional HTTP API, or MCP in place of the retired OpenClaw integration.

See [architecture and migration](https://github.com/DCPRevere/org-cli/blob/v2.0.0-rc.1/docs/architecture.md) and [API/MCP usage](https://github.com/DCPRevere/org-cli/blob/v2.0.0-rc.1/docs/api.md).

### RC validation and limits

- The .NET suite passes 838 tests, including virtual workspace and HTTP/MCP coverage. Eight legacy Emacs database interoperability tests are skipped; they do not validate the new extension.
- Linux CLI, NuGet installation, single-file publishing, HTTP, and stdio MCP smoke tests pass. macOS, Windows, and ARM artifacts are cross-built; native execution on those platforms is not yet verified. A direct ChatGPT account connection has not been tested.
- Cache freshness still reads the selected corpus. Multi-file writes are recoverable, not an OS-level atomic transaction; unrelated editors do not participate in CLI locks, and directory metadata is not explicitly fsynced.
- HTTP is a local bridge. OAuth and public multi-user hosting are not included.

### Security

- Update the bundled SQLite dependency to SQLitePCLRaw 2.1.13, addressing CVE-2025-6965.
