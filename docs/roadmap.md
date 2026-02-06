# Roadmap

## Current State

Three-project F# solution targeting .NET 9:

- **OrgCli.Org** (17 files) -- org-mode parser library. FParsec for timestamps/links/planning, regex for headline splitting/section editing. Config system, mutations, agenda, batch mode, JSON output. No external dependencies beyond FParsec.
- **OrgCli.Roam** (4 files) -- SQLite-backed org-roam v20 database layer. Sync, CRUD, backlinks.
- **OrgCli** (1 file, ~1080 lines) -- CLI entry point. Hand-rolled arg parser, ~30 subcommands.

435 tests (427 pass, 8 skipped emacs interop). Packaged as a dotnet tool (`org`).

## Architecture Assessment

### What works

The Org/Roam/CLI split is correct. The parser library has no roam dependency, so it can be used standalone. Compile order in the fsproj is clean -- Types flows into everything, no circular references.

The `HeadlineEdit` module is well-factored: it owns the split/reassemble representation of a headline section, and `Mutations` delegates all structural editing to it. This keeps the mutation logic focused on semantics (what to change) rather than mechanics (how to edit text).

Config precedence (defaults -> XDG file -> env vars -> CLI flags -> per-file #+STARTUP/#+TODO) is solid and matches how org-mode users expect configuration to work.

### Problems

**Program.fs is a monolith.** 1080 lines in a single file doing arg parsing, output formatting, all command dispatch, and error handling. The hand-rolled arg parser is fragile -- it silently drops malformed flags, doesn't validate required arguments, and treats any `--foo bar` pair as a flag even when `bar` is a positional argument. Missing argument errors surface as index-out-of-bounds exceptions caught by the top-level `try/with`.

**No argument validation layer.** Every command pattern-matches on positional args with fallthrough to `Unknown command`. If you write `org todo myfile.org` and forget the state argument, you get an unhelpful crash instead of a usage hint. There is no per-command validation before dispatch.

**Duplicated headline regex construction.** `HeadlineEdit` and `Mutations` both bind `Types.defaultHeadlineRegex` at module init time. Any code path using config-aware keywords (via `Document.parseWithConfig`) builds a different regex, but the section-editing code always uses the default. This means mutations on files with custom `#+TODO:` keywords will fail to parse headline lines if those keywords aren't in the default set.

**JSON output is hand-assembled.** `JsonOutput.fs` and `Program.fs` build JSON by string interpolation. This works until someone's headline title contains an unescaped character the manual `escapeJson` misses (it handles the obvious ones, but there's no systematic test for all Unicode edge cases). Using `System.Text.Json.JsonSerializer` would eliminate this class of bugs.

**Roam layer does N+1 queries.** `getAllNodes` fetches all DbNodes, then calls `PopulateNode` for each one -- each of which does 4 separate queries (node, tags, aliases, refs, file). For a typical org-roam database with 500+ nodes, this will be noticeably slow. Should be a single joined query.

**No error handling in mutations.** `Mutations.setTodoState` and friends operate on raw `string * int64` and return `string`. If the position doesn't point at a headline (e.g., file was edited between read and mutation), `HeadlineEdit.split` will silently produce garbage. These should return `Result<string, error>`.

**Writer.fs has two concerns.** It contains both serialization logic (formatTimestamp, formatHeadline) and file mutation logic (addProperty, removeProperty, addToMultiValueProperty). The mutation functions in Writer.fs do index-based string surgery that duplicates what HeadlineEdit does more robustly. These should be consolidated.

### Structural observations

The compile order forces `Agenda.fs` last in OrgCli.Org, but Agenda only depends on Types, Document, and Headlines. It could compile earlier if needed. Not a problem now, but indicates the file order was chosen chronologically (order of implementation) rather than by dependency.

`Utils.fs` mixes three concerns: ID generation, file hashing, XDG paths, and date parsing. Should be split if it grows further.

## Parser Gaps

### Not parsed at all

- **Source blocks** (`#+BEGIN_SRC ... #+END_SRC`). No AST node, no extraction, no metadata.
- **Other block types**: QUOTE, EXAMPLE, VERSE, CENTER, EXPORT, COMMENT blocks.
- **Tables**. No table parsing whatsoever.
- **Footnotes** (`[fn:1]` definitions and references).
- **Inline markup** (bold, italic, underline, strikethrough, code, verbatim). The parser treats body text as opaque strings.
- **Lists** (ordered, unordered, definition lists). Completely ignored by the parser.
- **Diary timestamps** (`<%%(...)>`).
- **Effort property** parsing for clock reports.
- **Drawers** other than PROPERTIES and LOGBOOK (e.g., custom named drawers).
- **Comments** (`# line` and `#+BEGIN_COMMENT`).
- **LaTeX fragments** and `\begin{} \end{}` environments.
- **Radio targets** and target links (`<<<target>>>`, `<<target>>`).
- **Macros** (`{{{macro(args)}}}`).
- **Org-cite** syntax (`[cite:@key]`) -- parsed as regular links but not semantically understood.
- **Column view / COLUMNS property** format specifiers.
- **Habit tracking** properties (STYLE, LAST_REPEAT, etc. are stored as properties but not interpreted).

### Parsed but incomplete

- **Timestamps**: Active/inactive, date, time, repeater, delay, ranges all work. Missing: time ranges within a single day (`<2024-01-15 Mon 10:00-12:00>`), sexp timestamps, diary-style timestamps.
- **Links**: Bracket links work. Missing: angle links (`<URL>`), plain URL auto-detection, citation links.
- **Headline regex**: The regex approach for splitting sections (`^\*+ `) is fragile -- it will incorrectly match `*` at start of line inside source blocks or example blocks. The parser has no concept of "inside a block" context.
- **Property drawer**: Only parsed for the first :PROPERTIES: block after a headline. Nested or malformed drawers will confuse the parser.
- **Planning line**: Only recognizes SCHEDULED/DEADLINE/CLOSED at the immediate line after a headline. If there's whitespace or other content between the headline and planning line, it won't parse.

### Real-world breakage scenarios

1. A file with `#+BEGIN_SRC org` containing headline-like lines (`* Foo`) will be parsed as actual headlines, corrupting the document structure.
2. Very large property drawers (100+ properties) could be slow due to regex-based parsing on every operation.
3. Files with Windows line endings (`\r\n`) are not explicitly handled. The `\n` splits and regex patterns should work, but `\r` will remain in parsed values.
4. Unicode in tag names beyond `@` (e.g., non-ASCII characters) won't match the `[\w@:]` regex in some locales.

## Feature Priorities

### High (blocks daily use)

1. **Argument validation and error messages.** Every command should validate required args before dispatch and produce a usage hint on failure. This is the number one usability blocker.
2. **Source block awareness in parser.** At minimum, the headline splitter must skip `#+BEGIN_SRC ... #+END_SRC` ranges. Without this, any org file with embedded org examples will corrupt.
3. **Config-aware headline regex in mutations.** `HeadlineEdit` and `Mutations` must accept a keyword list parameter or use config-aware regex, not the default. Otherwise custom TODO keywords break.
4. **Proper JSON serialization.** Replace hand-rolled JSON with `System.Text.Json.JsonSerializer`.
5. **Roam sync performance.** Single-query population of nodes with JOINs instead of N+1.

### Medium (quality of life)

6. **Interactive timestamp input.** `org schedule file headline 2024-01-15` works, but org-mode users expect to type `+3d` or `next monday`. Relative date parsing.
7. **Repeatable flags.** `--tag` can be repeated, but `--files` is the only documented repeatable flag. Commands like `add` should support multiple `--tag` flags consistently.
8. **Table output.** For `headlines` and `clock report`, aligned columnar output instead of free-form text.
9. **Watch mode / file watcher.** `org agenda --watch` that re-renders on file changes.
10. **Capture templates.** `org capture --template meeting` that adds a headline from a configured template.
11. **Effort estimates** in clock reports.
12. **Custom agenda views.** A config-driven way to define named views (e.g., "work" = specific files + tag filter).

### Low (nice to have)

13. **Inline markup parsing.** Bold, italic, code. Mainly useful for richer export or rendering.
14. **Table parsing** and simple column operations.
15. **Org-babel execution** (or at least source block extraction).
16. **Tree-sitter integration** for more robust parsing.
17. **LSP server** for editor integration.

## 1.0 Checklist

### Must have

- [ ] All commands validate required arguments and print per-command usage on failure
- [ ] Source block ranges excluded from headline splitting
- [ ] Config-aware headline regex flows through to HeadlineEdit/Mutations
- [ ] Mutations return Result types, not bare strings
- [ ] Hand-rolled JSON replaced with System.Text.Json serialization
- [ ] Roam sync uses batch queries (no N+1)
- [ ] Windows line ending handling (normalize on read)
- [ ] Program.fs split into separate modules (arg parsing, command dispatch, output formatting)
- [ ] CI pipeline: build + test on push, release artifact generation
- [ ] Published to NuGet as a dotnet tool
- [ ] Man page or --help output for every command covers all flags
- [ ] Integration tests that exercise the CLI binary end-to-end (not just library functions)

### Should have

- [ ] Relative date parsing ("+3d", "next monday", "tomorrow")
- [ ] Capture templates from config
- [ ] Clock report with effort estimates
- [ ] Aligned table output for headlines/clock
- [ ] Emacs interop test suite passing (currently 8 skipped)
- [ ] Property inheritance tested against emacs behavior
- [ ] Changelog and semantic versioning

### Nice to have

- [ ] Shell completion that's context-aware (completes file paths, headline titles)
- [ ] Watch mode for agenda
- [ ] Custom agenda views from config
- [ ] Export without pandoc dependency (native markdown/HTML writer)
