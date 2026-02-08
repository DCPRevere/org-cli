#!/usr/bin/env bash
#
# Smoke test for org-cli. Creates a temp repo in /tmp, runs every
# subcommand, and prints a pass/fail summary.
#
# Usage:
#   ./test-cli.sh                 # build Release then test
#   ./test-cli.sh /path/to/org    # use an existing binary
#
set -euo pipefail

# ── locate binary ──

if [ $# -ge 1 ]; then
    ORG="$1"
else
    echo "Building org-cli (Release)..."
    dotnet build src/OrgCli/ -c Release
    ORG="$(cd "$(dirname "$0")" && pwd)/src/OrgCli/bin/Release/net9.0/org"
fi

if [ ! -f "$ORG" ]; then
    echo "Binary not found: $ORG" >&2
    exit 1
fi

# ── temp directory ──

D=$(mktemp -d /tmp/org-cli-smoke-XXXXXX)
trap 'rm -rf "$D"' EXIT

# ── counters ──

PASS=0
FAIL=0
FAILURES=""

ok() {
    local name="$1"
    shift
    local out rc
    out=$("$@" 2>&1) && rc=0 || rc=$?
    if [ "$rc" -eq 0 ]; then
        PASS=$((PASS + 1))
    else
        FAIL=$((FAIL + 1))
        FAILURES="${FAILURES}  FAIL  ${name}  (exit=${rc})\n"
        echo "FAIL  $name  (exit=$rc)"
        echo "$out" | sed 's/^/       /'
    fi
}

# expect non-zero exit
fail() {
    local name="$1"
    shift
    local out rc
    out=$("$@" 2>&1) && rc=0 || rc=$?
    if [ "$rc" -ne 0 ]; then
        PASS=$((PASS + 1))
    else
        FAIL=$((FAIL + 1))
        FAILURES="${FAILURES}  FAIL  ${name}  (expected non-zero, got 0)\n"
        echo "FAIL  $name  (expected non-zero, got 0)"
        echo "$out" | sed 's/^/       /'
    fi
}

# expect stdout to contain a substring
ok_contains() {
    local name="$1"
    local needle="$2"
    shift 2
    local out rc
    out=$("$@" 2>&1) && rc=0 || rc=$?
    if [ "$rc" -eq 0 ] && echo "$out" | grep -qF "$needle"; then
        PASS=$((PASS + 1))
    else
        FAIL=$((FAIL + 1))
        local reason=""
        [ "$rc" -ne 0 ] && reason="exit=${rc}" || reason="missing '${needle}'"
        FAILURES="${FAILURES}  FAIL  ${name}  (${reason})\n"
        echo "FAIL  $name  ($reason)"
        echo "$out" | sed 's/^/       /'
    fi
}

# ── seed files ──
# Note: planning lines (SCHEDULED/DEADLINE/CLOSED) come BEFORE property drawers.

cat > "$D/projects.org" << 'SEED'
#+title: Projects
#+filetags: :project:

* TODO Build CLI tool :dev:coding:
SCHEDULED: <2026-02-07 Sat>
:PROPERTIES:
:ID: cli-001
:EFFORT: 8h
:END:
This is the main project.

** TODO Write parser
DEADLINE: <2026-02-10 Tue>
:PROPERTIES:
:ID: parser-002
:END:
Implement the parser.

** DONE Design schema
CLOSED: [2026-02-05 Wed 14:30]
:PROPERTIES:
:ID: schema-003
:END:
Design the database schema.

* NEXT Review docs :review:
SCHEDULED: <2026-02-08 Sun>
:PROPERTIES:
:ID: review-004
:END:
Review documentation before release.

* WAITING Feedback :review:
:PROPERTIES:
:ID: feedback-005
:END:
Waiting for beta testers.
SEED

cat > "$D/notes.org" << 'SEED'
#+title: Notes

* Meeting notes
:PROPERTIES:
:ID: meeting-006
:END:
Discussed project timeline.

** Action items
- [ ] Send follow-up email

* Research: FTS5
:PROPERTIES:
:ID: fts5-007
:END:
Full-text search with FTS5.

#+begin_src sql
CREATE VIRTUAL TABLE fts USING fts5(title, body);
#+end_src

| Feature | Supported |
|---------+-----------|
| Boolean | Yes       |
| Prefix  | Yes       |

* Ideas for v2
Brainstorming notes.
SEED

cat > "$D/journal.org" << 'SEED'
#+title: Journal

* 2026-02-06 Thursday
Worked on parser.

* 2026-02-07 Friday
Testing the CLI.
SEED

echo "Running smoke tests against: $ORG"
echo "Temp repo: $D"
echo ""

# ══════════════════════════════════════════════
#  1. Meta
# ══════════════════════════════════════════════

ok          "version"                   "$ORG" --version
ok_contains "schema lists commands"     '"commands"' "$ORG" schema
ok_contains "help shows usage"          "Usage:" "$ORG" help

# ══════════════════════════════════════════════
#  2. Read-only queries
# ══════════════════════════════════════════════

# headlines
ok_contains "headlines text"            "Build CLI tool"    "$ORG" headlines -d "$D"
ok_contains "headlines json"            '"ok":true'         "$ORG" headlines -d "$D" --format json
ok_contains "headlines --todo TODO"     "Write parser"      "$ORG" headlines -d "$D" --todo TODO
ok_contains "headlines --tag review"    "Review docs"       "$ORG" headlines -d "$D" --tag review
ok_contains "headlines --level 2"       "Write parser"      "$ORG" headlines -d "$D" --level 2
ok_contains "headlines --property"      "Build CLI tool"    "$ORG" headlines -d "$D" --property "EFFORT=8h"

# agenda
ok_contains "agenda today"             "Review docs"        "$ORG" agenda today -d "$D"
ok          "agenda week"                                   "$ORG" agenda week -d "$D"
ok_contains "agenda todo"              "TODO"               "$ORG" agenda todo -d "$D"
ok_contains "agenda todo json"         '"ok":true'          "$ORG" agenda today -d "$D" --format json
ok_contains "agenda --tag dev"         "Build CLI tool"     "$ORG" agenda todo -d "$D" --tag dev
ok_contains "agenda --state NEXT"      "Review docs"        "$ORG" agenda todo -d "$D" --state NEXT

# read
ok_contains "read by id"               "Build CLI tool"     "$ORG" read "$D/projects.org" cli-001
ok_contains "read by title"            "FTS5"               "$ORG" read "$D/notes.org" "Research: FTS5"
ok          "read by position"                              "$ORG" read "$D/projects.org" 0

# search
ok_contains "search text"             "parser"              "$ORG" search "parser" -d "$D"
ok_contains "search json"             '"ok":true'           "$ORG" search "FTS5" -d "$D" --format json

# links
ok          "links"                                         "$ORG" links "$D/projects.org" -d "$D"

# ══════════════════════════════════════════════
#  3. Mutations
# ══════════════════════════════════════════════

# add
ok_contains "add headline"            "added"               "$ORG" add "$D/projects.org" "New task" --todo TODO --priority A --tag cli --scheduled 2026-02-15
ok_contains "add json"                '"ok":true'           "$ORG" add "$D/notes.org" "CLI note" --format json
ok_contains "add --under"             "added"               "$ORG" add "$D/projects.org" "Subtask" --todo TODO --under cli-001

# todo
ok_contains "todo set DONE"           "updated"             "$ORG" todo "$D/projects.org" "New task" DONE
ok_contains "todo by id"              "updated"             "$ORG" todo "$D/projects.org" review-004 TODO
ok_contains "todo clear"              "updated"             "$ORG" todo "$D/projects.org" feedback-005 ""

# priority
ok_contains "priority set"            "updated"             "$ORG" priority "$D/projects.org" parser-002 A
ok_contains "priority json"           '"priority":"B"'      "$ORG" priority "$D/projects.org" parser-002 B --format json
ok_contains "priority clear"          "updated"             "$ORG" priority "$D/projects.org" parser-002 ""

# tag
ok_contains "tag add"                 "added"               "$ORG" tag add "$D/projects.org" parser-002 urgent
ok_contains "tag remove"              "removed"             "$ORG" tag remove "$D/projects.org" parser-002 urgent
ok_contains "tag add json"            '"ok":true'           "$ORG" tag add "$D/notes.org" meeting-006 important --format json

# property
ok_contains "property set"            "set"                 "$ORG" property set "$D/projects.org" cli-001 STATUS active
ok_contains "property set json"       '"ok":true'           "$ORG" property set "$D/projects.org" cli-001 CATEGORY work --format json
ok_contains "property remove"         "removed"             "$ORG" property remove "$D/projects.org" cli-001 STATUS

# schedule
ok_contains "schedule set"            "updated"             "$ORG" schedule "$D/projects.org" parser-002 2026-03-01
ok_contains "schedule clear"          "updated"             "$ORG" schedule "$D/projects.org" parser-002 ""
ok_contains "schedule json"           '"scheduled"'         "$ORG" schedule "$D/notes.org" meeting-006 2026-02-20 --format json

# deadline
ok_contains "deadline set"            "updated"             "$ORG" deadline "$D/notes.org" fts5-007 2026-03-15
ok_contains "deadline clear"          "updated"             "$ORG" deadline "$D/notes.org" fts5-007 ""

# note
ok_contains "note add"                "added"               "$ORG" note "$D/projects.org" cli-001 "Started implementation"
ok_contains "note json"               '"ok":true'           "$ORG" note "$D/notes.org" meeting-006 "Follow-up" --format json

# clock
ok_contains "clock in"                "started"             "$ORG" clock in "$D/projects.org" parser-002
sleep 1
ok_contains "clock out"               "stopped"             "$ORG" clock out "$D/projects.org" parser-002
ok_contains "clock report"            "Write parser"        "$ORG" clock report -d "$D"
ok_contains "clock report json"       '"ok":true'           "$ORG" clock report -d "$D" --format json

# dry-run
ok_contains "dry-run"                 '"dry_run":true'      "$ORG" todo "$D/projects.org" cli-001 WAITING --dry-run --format json

# ══════════════════════════════════════════════
#  4. Index + FTS
# ══════════════════════════════════════════════

ok          "index"                                         "$ORG" index -d "$D" --quiet
ok          "index --force"                                 "$ORG" index -d "$D" --force --quiet
ok_contains "index json"              '"files"'             "$ORG" index -d "$D" --force --format json --quiet

ok_contains "fts simple"              "parser"              "$ORG" fts "parser" -d "$D" --no-sync
ok_contains "fts AND"                 "Build CLI"           "$ORG" fts "project AND CLI" -d "$D" --no-sync
ok_contains "fts prefix"              "Implement"           "$ORG" fts "implement*" -d "$D" --no-sync
ok_contains "fts phrase"              "project timeline"    "$ORG" fts '"project timeline"' -d "$D" --no-sync
ok_contains "fts json"                '"ok":true'           "$ORG" fts "parser" -d "$D" --no-sync --format json
ok_contains "fts no match"            "No results."         "$ORG" fts "xyznonexistent" -d "$D" --no-sync
fail        "fts malformed"                                 "$ORG" fts '"unclosed' -d "$D" --no-sync

# ══════════════════════════════════════════════
#  5. Structural operations
# ══════════════════════════════════════════════

ok_contains "refile"                  "complete"            "$ORG" refile "$D/notes.org" "Ideas for v2" "$D/projects.org" cli-001
ok_contains "archive"                 "Archived"            "$ORG" archive "$D/journal.org" "2026-02-06 Thursday"

# verify archive file was created
if [ -f "$D/journal.org_archive" ]; then
    PASS=$((PASS + 1))
else
    FAIL=$((FAIL + 1))
    FAILURES="${FAILURES}  FAIL  archive file exists\n"
    echo "FAIL  archive file exists"
fi

# ══════════════════════════════════════════════
#  6. Batch
# ══════════════════════════════════════════════

cat > "$D/batch.json" << EOF
{"commands":[
  {"command":"todo","file":"$D/projects.org","identifier":"cli-001","args":{"state":"NEXT"}},
  {"command":"tag-add","file":"$D/projects.org","identifier":"cli-001","args":{"tag":"batched"}}
]}
EOF
ok_contains "batch"                   '"ok":true'           bash -c "$ORG batch --files '$D/projects.org' --format json < '$D/batch.json'"

# ══════════════════════════════════════════════
#  7. Roam
# ══════════════════════════════════════════════

ROAM_DB="$D/.org.db"

ok_contains "roam sync"               "complete"            "$ORG" roam sync -d "$D" --db "$ROAM_DB"
ok_contains "roam node list"          '"ok":true'           "$ORG" roam node list --db "$ROAM_DB" --format json
ok_contains "roam node get"           "cli-001"             "$ORG" roam node get cli-001 --db "$ROAM_DB" --format json
ok_contains "roam node find"          "parser-002"          "$ORG" roam node find "Write parser" --db "$ROAM_DB" --format json
ok_contains "roam backlinks"          '"ok":true'           "$ORG" roam backlinks cli-001 --db "$ROAM_DB" --format json
ok_contains "roam tag list"           '"ok":true'           "$ORG" roam tag list --db "$ROAM_DB" --format json
ok_contains "roam tag find"           '"ok":true'           "$ORG" roam tag find "project" --db "$ROAM_DB" --format json

ok_contains "roam node create (headline)" "node"            "$ORG" roam node create "New roam node" --parent "$D/notes.org" --db "$ROAM_DB"
ok_contains "roam node create (file)"     "Standalone node" "$ORG" roam node create "Standalone node" -d "$D" --db "$ROAM_DB"

# verify file-level node was created as a new .org file with ID property
STANDALONE=$(ls "$D"/*standalone*node.org 2>/dev/null | head -1 || true)
if [ -n "$STANDALONE" ] && grep -q ":ID:" "$STANDALONE" && grep -q "#+title: Standalone node" "$STANDALONE"; then
    PASS=$((PASS + 1))
else
    FAIL=$((FAIL + 1))
    FAILURES="${FAILURES}  FAIL  roam file node structure\n"
    echo "FAIL  roam file node structure"
fi

# re-sync after mutations in section 3 changed the org files
ok          "roam re-sync"                                    "$ORG" roam sync -d "$D" --db "$ROAM_DB"
ok_contains "roam node read"          "Build CLI"           "$ORG" roam node read cli-001 --db "$ROAM_DB"

# alias and ref need <file> <node-id> <value>
ok_contains "roam alias add"          "added"               "$ORG" roam alias add "$D/projects.org" cli-001 "The CLI" --db "$ROAM_DB"
ok_contains "roam alias remove"       "removed"             "$ORG" roam alias remove "$D/projects.org" cli-001 "The CLI" --db "$ROAM_DB"
ok_contains "roam ref add"            "added"               "$ORG" roam ref add "$D/projects.org" cli-001 "https://example.com" --db "$ROAM_DB"
ok_contains "roam ref remove"         "removed"             "$ORG" roam ref remove "$D/projects.org" cli-001 "https://example.com" --db "$ROAM_DB"

# link: need two nodes that exist
ok_contains "roam link add"           "added"               "$ORG" roam link add "$D/projects.org" cli-001 parser-002 --db "$ROAM_DB"

# ══════════════════════════════════════════════
#  8. Export (requires pandoc)
# ══════════════════════════════════════════════

if command -v pandoc &>/dev/null; then
    ok_contains "export markdown"     "Meeting notes"       "$ORG" export "$D/notes.org" --to markdown
else
    echo "SKIP  export (pandoc not installed)"
fi

# ══════════════════════════════════════════════
#  9. Error paths
# ══════════════════════════════════════════════

fail        "file not found"                                "$ORG" read "$D/nonexistent.org" 0
fail        "headline not found"                            "$ORG" read "$D/projects.org" "No such headline"
fail        "fts no index"                                  "$ORG" fts "test" -d /tmp/org-cli-smoke-nonexistent
fail        "unknown command"                               "$ORG" nosuchcommand

# ══════════════════════════════════════════════
#  10. Completions (just check they don't crash)
# ══════════════════════════════════════════════

ok_contains "completions bash"        "complete"            "$ORG" completions bash
ok_contains "completions zsh"         "compdef"             "$ORG" completions zsh
ok_contains "completions fish"        "complete"            "$ORG" completions fish

# ══════════════════════════════════════════════
#  Summary
# ══════════════════════════════════════════════

echo ""
echo "════════════════════════════════════════"
TOTAL=$((PASS + FAIL))
if [ "$FAIL" -eq 0 ]; then
    echo "  ALL $TOTAL PASSED"
else
    echo "  $TOTAL total: $PASS passed, $FAIL failed"
    echo ""
    echo -e "$FAILURES"
fi
echo "════════════════════════════════════════"

exit "$FAIL"
