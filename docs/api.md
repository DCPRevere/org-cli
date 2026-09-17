# Optional API and MCP

There is one executable. Normal `org` commands run and exit. Server modes run in the foreground until Ctrl-C; no installation or background service is required.

```sh
org serve -d ~/org                        # HTTP API on 127.0.0.1:8765
org serve -d ~/org --port 9000 --mcp       # Same API, plus MCP at /mcp
org mcp --stdio -d ~/org                  # Local client starts this on demand
```

Both server modes accept `--db`, `--config`, and `--read-only`. The selected directory is the workspace boundary. Capture writes to its `inbox.org`; requests cannot choose arbitrary file paths. `--read-only` disables mutations and removes them from discovery. Source files and the CLI-owned index are shared with ordinary CLI use; org-roam is unnecessary.

## External edits and cache freshness

Both `org serve` and `org mcp --stdio` watch the physical workspace recursively. File notifications are coalesced and processed in the background approximately every 200 ms; a request also processes pending notifications before using the index. Unchanged requests reuse parsed snapshots and do not scan or hash the corpus. Writes made through the service update the affected projection immediately, without waiting for a watcher event.

External edits are eventually visible, not transactionally synchronized with an editor: a request can race an event that has not arrived yet. Atomic saves, file creation/deletion, and renames are supported. Directory changes, watcher errors/overflow, and startup trigger full reconciliation. The server also verifies content every 60 seconds to recover from silently missed events (including on filesystems with unreliable notifications). These full passes can delay requests while refreshing a large workspace. Failed refreshes remain pending and are retried; requests requiring that refresh fail rather than silently returning the old cache.

The watcher is owned by the server process and stops with it. Ordinary CLI commands retain their existing scan-based freshness. Virtual-host tests inject notifications directly; they never attach a watcher to the user's filesystem.

## HTTP

The listener is loopback-only. Set `ORG_API_TOKEN` before starting the process to require `Authorization: Bearer <token>` on data and tool endpoints. Without it, local clients can call the API without credentials. Requests with unrelated Host or Origin headers are rejected; no cross-origin browser access is enabled. Request bodies are limited to 1 MiB. This is a local bridge, not a public multi-user service: OAuth and remote binding are not implemented.

- `GET /health`: readiness of the process.
- `GET /api/v1/tools`: operation names, input schemas, descriptions, and annotations.
- `POST /api/v1/<operation>`: JSON argument object.
- `/mcp`: available only with `--mcp`, using the official .NET SDK's Streamable HTTP transport. Legacy HTTP+SSE endpoints are not enabled.

Success: `{"ok":true,"data":...}`. Errors: `{"ok":false,"error":{"code":"conflict","message":"..."}}`, with a corresponding HTTP status. Common statuses are 400 for invalid inputs, 403 for read-only writes, 404 for missing entries/operations, and 409 for conflicts or ambiguous identities.

```sh
curl -s http://127.0.0.1:8765/api/v1/search \
  -H 'Content-Type: application/json' \
  -d '{"query":"renovation","limit":10}'

curl -s http://127.0.0.1:8765/api/v1/capture \
  -H 'Content-Type: application/json' \
  -d '{"request_id":"523172ad-29f3-49ba-b8a4-46e1a81b725f","title":"Call the builder","state":"TODO"}'
```

Generate a new UUID for each new capture. Reuse it only when retrying the identical request. Replays survive process restarts because capture identity and a request fingerprint are stored in the Org heading. Reusing a UUID with a different request is a conflict.

## Task workflow

The built-in browser board is at `/`. The empty page shell loads without a token; all data requests still require the configured bearer token. `tasks`, `task_create`, `task_update`, and `task_action` expose the same workflow to HTTP and MCP clients. See the [task workflow guide](task-workflow.md) for schemas, leases, dependency rules, review, and examples.

## Note operations

| Name | Arguments | Result |
| --- | --- | --- |
| `search` | `query`, optional `limit` and `offset` | Ranked FTS5 results with excerpts, file/outline context, and entry references |
| `fetch` | `ref`, optional `limit` and `offset` | Entry/subtree text, outline, file revision, and continuation offset |
| `agenda` | Optional `from`, `through`, `limit`, `offset` | Unfinished scheduled/deadline items through the given date, including overdue work before `from` |
| `capture` | `request_id`, `title`, optional `text`, `state` | New or previously captured entry |
| `append_note` | `ref`, `text`, `expected_revision` | Entry after appending |
| `update_task` | `ref`, `expected_revision`, and one or more of `state`, `scheduled`, `deadline`, `priority` | Updated task |
| `related` | `ref`, optional `limit`, `offset` | Incoming standard Org ID links |

Search uses SQLite FTS5 syntax: words, quoted phrases, AND/OR, and prefix*. Result pages default to 20 items (agenda: 50), capped at 100. Fetch defaults to 16,000 characters, capped at 65,536. Follow `next_offset` until null for remaining results/text. Agenda defaults to today through seven days later using the host's clock; dates use `yyyy-MM-dd` and the range may span at most 366 days. File-local completion keywords are respected.

Entries with standard IDs return `id:<value>`. Entries without IDs return opaque `loc:` references, scoped to the workspace and a file revision. A changed file invalidates a positional reference: search again. Reads do not stamp IDs. Duplicate identities produce an error.

Before appending or updating, fetch and send the returned `revision` as `expected_revision`. Revisions cover the whole file, deliberately rejecting concurrent changes elsewhere in that file. A stale retry cannot append twice. Omitted task fields stay unchanged; an empty string clears the selected field. State changes preserve existing Org repeater/logging behavior. A successful file write with an index refresh failure returns success with a `warning`; the next query retries refresh.

## MCP clients

A local client can launch the executable directly:

```json
{
  "mcpServers": {
    "org": {
      "command": "/absolute/path/to/org",
      "args": ["mcp", "--stdio", "-d", "/absolute/path/to/org-files"]
    }
  }
}
```

The exact configuration file depends on the client. Stdio emits only protocol messages on stdout; logs go to stderr. Closing stdin shuts down the process. HTTP clients use `http://127.0.0.1:8765/mcp` after starting `org serve --mcp`; a remote assistant needs an appropriate local bridge/tunnel. Direct ChatGPT account connection has not been tested by this change.

MCP exposes the same operations and structured result envelopes as HTTP. Writes are annotated; tool-level failures set `isError`. The SDK handles initialization, protocol negotiation, message framing, cancellation messages, and transport lifecycle. No assistant-specific database is created.

## Implementation and tests

`OrgCli.Index.Application.WorkspaceService` owns request validation and workspace operations using the same parser, index, configuration, and checked file mutation primitives as the CLI. `OrgCli/Server.fs` adapts this service to ASP.NET Core and the official MCP SDK. Neither transport invokes subprocess commands or captures console output. The service explicitly scopes the supplied filesystem, environment, clock, and configuration for every call and serializes calls per server instance.

```sh
dotnet test OrgCli.slnx
python3 tests/server_smoke.py
# Test a self-contained binary:
python3 tests/server_smoke.py /path/to/published/org
```

The .NET service tests include an entirely virtual filesystem/environment and real in-memory SQLite, plus live HTTP/MCP requests against that virtual workspace. Process tests cover stdio framing, EOF shutdown, concurrent capture retries, HTTP access checks, and ordinary CLI use after shutdown. The server is also available in `EnableRoam=false` builds.

See the [MCP .NET SDK](https://github.com/modelcontextprotocol/csharp-sdk) and [transport specification](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports) for protocol details.

Task results also include `outline` (parent heading titles), `tags` (explicit
headline tags), and nullable `scheduled` / `deadline` objects. Each timestamp has
`date` (`YYYY-MM-DD`), nullable `time` (`HH:mm`, no timezone conversion), nullable
`repeater`, and nullable `end` (another timestamp object). Dates remain separate
from workflow status and ownership; these fields are read from the Org heading.
