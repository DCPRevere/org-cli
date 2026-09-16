# Standalone Org architecture

The implementation remains F#. The language change would not resolve the ownership and consistency problems; the new boundaries do.

## Dependencies

`OrgCli` → `OrgCli.Index` → `OrgCli.Org`

The optional `OrgCli.Roam.Extension` depends on the same index and Org library. The core does not reference it when built with `EnableRoam=false`. The old `OrgCli.Roam` database implementation remains as a legacy library with compatibility tests, but is absent from the CLI dependency graph.

## Authority and projections

Org files contain note data and identities. SQLite contains replaceable parsed document snapshots, FTS tables, and identity locations. A cache is never a source for allocating an entry's identity. New headings get a UUID `ID` before writing.

Refresh hashes the selected file contents and effective configuration, including a projection version. It reuses unchanged snapshots and replaces changed file projections transactionally. Missing files are removed. Queries filter by their selected file set; querying a subdirectory does not discard unrelated cached documents. Duplicate IDs remain visible and resolving them fails instead of silently selecting a row.

Freshness currently requires reading the selected corpus. Warm queries avoid parsing unchanged Org text, but are not constant-time filesystem operations. There is no watcher, daemon, mtime-only shortcut, or separate roam cache. `fts --no-sync` is an explicit stale-read option. There is no claim of a simultaneous snapshot across external editors and all files: files are refreshed individually.

## Org-roam extension

The extension projects nodes from standard Org IDs and adds interpretations for aliases, refs, exclusions, and tags. File roots and headings remain distinct. Core search, backlinks, identity lookup, reads, and append do not depend on roam. Compatibility is through shared Org files; Emacs owns and refreshes its own database independently.

## Mutations

Edits carry expected original contents and replacement bytes. Cooperating processes acquire locks in sorted path order. All outputs are staged before replacement. Refile writes its destination before removing its source. Failed validation or a failed batch operation leaves source files unchanged.

A recovery manifest stores original/replacement bytes and stage paths. Pending markers prevent later CLI mutations from overlapping an unfinished commit. `org recover <manifest.json>` completes it only if every target still matches either original or intended content. This is recoverable multi-file writing, not an atomic filesystem transaction. Nonparticipating editors can still race a CLI write; directory metadata is not explicitly fsynced, so crash durability is not a general power-loss guarantee. Preserve the manifest until recovery succeeds.

## Runtime and tests

`Runtime.IHost` owns filesystem operations, directory enumeration, environment variables, working/home directories, the clock, locking, and the SQLite connection target. `Program.runWithHost` invokes the production command dispatcher within a scoped host. The physical implementation is used by the executable.

`VirtualWorkspaceTests.fs` supplies in-memory files, fixed timestamps, isolated environment/clock values, and actual SQLite in shared-memory mode. It drives production commands, including cache reuse/invalidation, root nodes, dry runs, duplicate IDs, source blocks, failed batches, failed renames, recovery, and external-change conflicts. Other parser and compatibility tests remain useful, but the skipped legacy Emacs tests are not evidence of extension interoperability.

## Migration

1. Keep Org files and their existing IDs. No content migration is required.
2. Use a CLI-owned `.org-index.db`, not an Emacs `org-roam.db` or a formerly shared database. The first query populates it.
3. Consumers should use JSON `id` for new entries. `custom_id` and legacy short-ID stamping remain available for existing workflows.
4. Use `pos:<offset>` for explicit positional selectors. Numeric text can now be a title or identity.
5. Org-roam users keep Emacs's normal file indexing. Build the CLI without the extension when it is unnecessary.
6. The OpenClaw plugins and skills have been retired. Use the CLI directly or connect an assistant through the optional [API and MCP interfaces](api.md). Existing Org files remain usable without a plugin.

The parser is still an Org subset, not Emacs's complete grammar. Unterminated source blocks/drawers are rejected for structural edits. Encrypted files and symlink traversal are excluded from automatic discovery. Full grammar coverage and directory-level crash durability remain separate work; this redesign does not claim them.

## Optional interfaces in one binary

`org serve` runs a loopback HTTP API; `--mcp` adds the MCP endpoint. `org mcp --stdio` is an on-demand subprocess mode. Both use `Application.WorkspaceService`, an explicit host-scoped application boundary using the same index and mutation primitives as ordinary CLI commands. There is no separate server executable and no listener starts during ordinary CLI use. See [API documentation](api.md).
