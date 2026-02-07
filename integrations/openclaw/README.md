# OpenClaw skill

An [OpenClaw](https://github.com/openclaw/openclaw) skill that gives your agent structured, linked, human-readable memory using org-mode files.

## Install

1. Put `org` on your PATH ([releases](https://github.com/dcprevere/org-cli/releases)).

2. Copy the skill into your OpenClaw workspace:

```sh
cp -r integrations/openclaw ~/.openclaw/workspace/skills/org-cli
```

3. Ask your agent to "refresh skills" or restart the gateway.

## What it does

The skill teaches the agent to use `org` for:

- **Knowledge graph**: create nodes, link them, query by tag/title/backlink/search
- **Task management**: create/complete/schedule/refile tasks in the human's org files
- **Structured memory**: record entities, relationships, and constraints as linked org-roam nodes instead of flat text
- **Batch mutations**: apply multiple changes atomically

The agent maintains two directories: its own knowledge base (`~/org/agent`) and the human's files (`~/org/human`). Both are plain text, human-readable, and version-controllable.
