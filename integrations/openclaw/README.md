# OpenClaw plugins

This directory ships two [OpenClaw](https://github.com/openclaw/openclaw) plugins built on the `org` CLI.

- **[`org-cli`](./org-cli)** — manage the user's org-mode workspace on their behalf. Task capture, scheduling, plain notes, and a linked knowledge graph (org-roam). Install this for the day-to-day agent-as-secretary use case.
- **[`org-memory`](./org-memory)** — extends `org-cli` so the agent also persists its own memory (knowledge, observations, daily notes) into a second org workspace. Install on top of `org-cli` if you want the agent to build up its own org-based knowledge graph alongside the user's.

Both plugins ship from the same release. Each is versioned in lockstep with the `org` binary.

## Relationship between the two plugins

`org-memory` is a strict extension of `org-cli` — you install both if you want the agent-memory layer. The user-facing shortcut grammar (`t:`, `n:`, `k:`, `d:`, `s:`, `f:`) lives in `org-cli` and is unchanged when `org-memory` is added. `org-memory` adds `@a`-prefixed variants (`@at:`, `@ak:`, etc.) that target a separate agent workspace, plus a session-start hook that injects the agent's `memory.org` and recent daily notes.

If you only install `org-cli`, the agent has no `@a` shortcuts and no agent workspace — it just manages the user's org. If you only install `org-memory` (without `org-cli`), the agent will try to act on `@a` shortcuts but have no instructions for bare shortcuts; install `org-cli` too.

## Upgrading from 0.7.x

Both plugins are 1.0.0 and represent a clean break from the previous single-plugin `org-memory@0.7.x`. Env var names and tool signatures have changed. See each plugin's README for its migration table.
