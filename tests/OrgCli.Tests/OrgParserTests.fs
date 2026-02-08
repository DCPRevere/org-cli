module OrgParserTests

open System
open Xunit
open OrgCli.Org

[<Fact>]
let ``Parse simple file with title and ID`` () =
    let content =
        """
:PROPERTIES:
:ID: 12345678-1234-1234-1234-123456789012
:END:
#+title: My Test Note

Some content here.
"""

    let doc = Document.parse content

    Assert.Equal(Some "My Test Note", Types.tryGetTitle doc.Keywords)
    Assert.Equal(Some "12345678-1234-1234-1234-123456789012", Types.tryGetId doc.FileProperties)

[<Fact>]
let ``Parse headline with properties`` () =
    let content =
        """
* Test Headline
:PROPERTIES:
:ID: abcd-1234
:ROAM_ALIASES: "Alias One" AliasTwo
:END:

Content under headline.
"""

    let doc = Document.parse content

    Assert.Equal(1, doc.Headlines.Length)
    let headline = doc.Headlines.[0]
    Assert.Equal("Test Headline", headline.Title)
    Assert.Equal(1, headline.Level)
    Assert.Equal(Some "abcd-1234", Types.tryGetId headline.Properties)

    let aliases = Types.getRoamAliases headline.Properties
    Assert.Equal(2, aliases.Length)
    Assert.Contains("Alias One", aliases)
    Assert.Contains("AliasTwo", aliases)

[<Fact>]
let ``Parse headline with TODO and priority`` () =
    let content =
        """
* TODO [#A] Important Task :tag1:tag2:
:PROPERTIES:
:ID: task-id
:END:
"""

    let doc = Document.parse content

    Assert.Equal(1, doc.Headlines.Length)
    let headline = doc.Headlines.[0]
    Assert.Equal(Some "TODO", headline.TodoKeyword)
    Assert.Equal(Some(Priority 'A'), headline.Priority)
    Assert.Equal("Important Task", headline.Title)
    Assert.Equal<string list>([ "tag1"; "tag2" ], headline.Tags)

[<Fact>]
let ``Parse links in content`` () =
    let content =
        """
:PROPERTIES:
:ID: node-1
:END:
#+title: Node with links

Here is a link to [[id:other-node][Other Node]] and [[roam:By Title]].
"""

    let doc = Document.parse content

    Assert.Equal(2, doc.Links.Length)

    let idLink = doc.Links |> List.find (fun (l, _) -> l.LinkType = "id")
    Assert.Equal("other-node", (fst idLink).Path)
    Assert.Equal(Some "Other Node", (fst idLink).Description)

    let roamLink = doc.Links |> List.find (fun (l, _) -> l.LinkType = "roam")
    Assert.Equal("By Title", (fst roamLink).Path)

[<Fact>]
let ``splitQuotedString handles quoted and unquoted values`` () =
    let result = Types.splitQuotedString "foo \"bar baz\" qux"
    Assert.Equal<string list>([ "foo"; "bar baz"; "qux" ], result)

[<Fact>]
let ``splitQuotedString handles empty string`` () =
    let result = Types.splitQuotedString ""
    Assert.Equal<string list>([], result)

[<Fact>]
let ``splitQuotedString handles single value`` () =
    let result = Types.splitQuotedString "single"
    Assert.Equal<string list>([ "single" ], result)

[<Fact>]
let ``Parse nested headlines`` () =
    let content =
        """
* Level 1
:PROPERTIES:
:ID: level-1
:END:

** Level 2
:PROPERTIES:
:ID: level-2
:END:

*** Level 3
:PROPERTIES:
:ID: level-3
:END:
"""

    let doc = Document.parse content

    Assert.Equal(3, doc.Headlines.Length)
    Assert.Equal(1, doc.Headlines.[0].Level)
    Assert.Equal(2, doc.Headlines.[1].Level)
    Assert.Equal(3, doc.Headlines.[2].Level)

[<Fact>]
let ``Compute outline path`` () =
    let content =
        """
* Parent
:PROPERTIES:
:ID: parent
:END:

** Child
:PROPERTIES:
:ID: child
:END:
"""

    let doc = Document.parse content

    let child = doc.Headlines.[1]
    let olp = Document.computeOutlinePath doc.Headlines child

    Assert.Equal<string list>([ "Parent" ], olp)

[<Fact>]
let ``Parse ROAM_REFS`` () =
    let content =
        """
* Reference Node
:PROPERTIES:
:ID: ref-node
:ROAM_REFS: @citationKey "https://example.com"
:END:
"""

    let doc = Document.parse content

    let headline = doc.Headlines.[0]
    let refs = Types.getRoamRefs headline.Properties

    Assert.Equal(2, refs.Length)
    Assert.Contains("@citationKey", refs)
    Assert.Contains("https://example.com", refs)

[<Fact>]
let ``ROAM_EXCLUDE is detected`` () =
    let content =
        """
* Excluded Node
:PROPERTIES:
:ID: excluded
:ROAM_EXCLUDE: t
:END:
"""

    let doc = Document.parse content

    let headline = doc.Headlines.[0]
    Assert.True(Types.isRoamExcluded headline.Properties)

[<Fact>]
let ``Parse filetags`` () =
    let content =
        """
:PROPERTIES:
:ID: file-node
:END:
#+title: Tagged Note
#+filetags: :tag1:tag2:tag3:
"""

    let doc = Document.parse content

    let tags = Types.getFileTags doc.Keywords
    Assert.Equal<string list>([ "tag1"; "tag2"; "tag3" ], tags)

// --- Timestamp formatting ---

[<Fact>]
let ``formatTimestamp with RangeEnd formats as range`` () =
    let ts: Timestamp =
        { Type = TimestampType.Active
          Date = DateTime(2026, 2, 5)
          HasTime = false
          Repeater = None
          Delay = None
          RangeEnd =
            Some
                { Type = TimestampType.Active
                  Date = DateTime(2026, 2, 10)
                  HasTime = false
                  Repeater = None
                  Delay = None
                  RangeEnd = None } }

    let result = Writer.formatTimestamp ts
    Assert.Equal("<2026-02-05 Thu>--<2026-02-10 Tue>", result)

[<Fact>]
let ``formatTimestamp without RangeEnd unchanged`` () =
    let ts: Timestamp =
        { Type = TimestampType.Active
          Date = DateTime(2026, 2, 5)
          HasTime = false
          Repeater = None
          Delay = None
          RangeEnd = None }

    let result = Writer.formatTimestamp ts
    Assert.Equal("<2026-02-05 Thu>", result)

// --- Timestamp range parsing ---

[<Fact>]
let ``pTimestampRange parses active date range`` () =
    let input = "<2026-02-05 Thu>--<2026-02-10 Tue>"

    match Parsers.runParser Parsers.pTimestampRange input with
    | Result.Ok ts ->
        Assert.Equal(DateTime(2026, 2, 5), ts.Date)
        Assert.True(ts.RangeEnd.IsSome)
        Assert.Equal(DateTime(2026, 2, 10), ts.RangeEnd.Value.Date)
        Assert.Equal(TimestampType.Active, ts.RangeEnd.Value.Type)
    | Result.Error e -> failwithf "Parse failed: %s" e

[<Fact>]
let ``pTimestampRange parses active date-time range`` () =
    let input = "<2026-02-05 Thu 10:00>--<2026-02-10 Tue 14:00>"

    match Parsers.runParser Parsers.pTimestampRange input with
    | Result.Ok ts ->
        Assert.True(ts.HasTime)
        Assert.Equal(10, ts.Date.Hour)
        Assert.True(ts.RangeEnd.IsSome)
        Assert.True(ts.RangeEnd.Value.HasTime)
        Assert.Equal(14, ts.RangeEnd.Value.Date.Hour)
    | Result.Error e -> failwithf "Parse failed: %s" e

[<Fact>]
let ``pTimestampRange parses inactive date range`` () =
    let input = "[2026-02-05 Thu]--[2026-02-10 Tue]"

    match Parsers.runParser Parsers.pTimestampRange input with
    | Result.Ok(ts: Timestamp) ->
        Assert.Equal(TimestampType.Inactive, ts.Type)
        Assert.True(ts.RangeEnd.IsSome)
        Assert.Equal(TimestampType.Inactive, ts.RangeEnd.Value.Type)
    | Result.Error e -> failwithf "Parse failed: %s" e

[<Fact>]
let ``pTimestampRange parses single timestamp as no range`` () =
    let input = "<2026-02-05 Thu>"

    match Parsers.runParser Parsers.pTimestampRange input with
    | Result.Ok ts ->
        Assert.Equal(DateTime(2026, 2, 5), ts.Date)
        Assert.True(ts.RangeEnd.IsNone)
    | Result.Error e -> failwithf "Parse failed: %s" e

[<Fact>]
let ``Planning line with range parses correctly`` () =
    let content = "* TODO My task\nSCHEDULED: <2026-02-05 Thu>--<2026-02-10 Tue>\n"
    let doc = Document.parse content
    Assert.Equal(1, doc.Headlines.Length)
    let h = doc.Headlines.[0]
    Assert.True(h.Planning.IsSome)
    Assert.True(h.Planning.Value.Scheduled.IsSome)
    let sched = h.Planning.Value.Scheduled.Value
    Assert.Equal(DateTime(2026, 2, 5), sched.Date)
    Assert.True(sched.RangeEnd.IsSome)
    Assert.Equal(DateTime(2026, 2, 10), sched.RangeEnd.Value.Date)

[<Fact>]
let ``Clock entry with -- separator still works`` () =
    let line = "CLOCK: [2026-02-05 Thu 09:00]--[2026-02-05 Thu 10:30] =>  1:30"

    match Parsers.runParser Parsers.pClockEntry (line + "\n") with
    | Result.Ok entry ->
        Assert.Equal(DateTime(2026, 2, 5, 9, 0, 0), entry.Start.Date)
        Assert.True(entry.End.IsSome)
        Assert.Equal(DateTime(2026, 2, 5, 10, 30, 0), entry.End.Value.Date)
    | Result.Error e -> failwithf "Parse failed: %s" e

// --- Dynamic TODO keyword parsing ---

[<Fact>]
let ``Document.parse respects custom TODO keywords from #+TODO line`` () =
    let content =
        "#+TODO: REVIEW APPROVE | MERGED REJECTED\n* REVIEW My PR\n* MERGED Old PR\n"

    let doc = Document.parse content
    Assert.Equal(2, doc.Headlines.Length)
    Assert.Equal(Some "REVIEW", doc.Headlines.[0].TodoKeyword)
    Assert.Equal(Some "MERGED", doc.Headlines.[1].TodoKeyword)

[<Fact>]
let ``Document.parse with no #+TODO uses default keywords`` () =
    let content = "* TODO Task\n* DONE Finished\n"
    let doc = Document.parse content
    Assert.Equal(Some "TODO", doc.Headlines.[0].TodoKeyword)
    Assert.Equal(Some "DONE", doc.Headlines.[1].TodoKeyword)

[<Fact>]
let ``Document.parse custom keywords: standard word not treated as keyword`` () =
    let content = "#+TODO: OPEN | CLOSED\n* TODO Not a keyword anymore\n"
    let doc = Document.parse content
    // "TODO" is no longer a keyword; it's part of the title
    Assert.Equal(None, doc.Headlines.[0].TodoKeyword)
    Assert.Equal("TODO Not a keyword anymore", doc.Headlines.[0].Title)

[<Fact>]
let ``Document.parseWithConfig accepts external config`` () =
    let cfg =
        { Types.defaultConfig with
            TodoKeywords =
                { ActiveStates =
                    [ { Keyword = "OPEN"
                        LogOnEnter = LogAction.NoLog
                        LogOnLeave = LogAction.NoLog } ]
                  DoneStates =
                    [ { Keyword = "CLOSED"
                        LogOnEnter = LogAction.NoLog
                        LogOnLeave = LogAction.NoLog } ] } }

    let content = "* OPEN Task\n* CLOSED Done\n* TODO Not keyword\n"
    let doc = Document.parseWithConfig cfg content
    Assert.Equal(Some "OPEN", doc.Headlines.[0].TodoKeyword)
    Assert.Equal(Some "CLOSED", doc.Headlines.[1].TodoKeyword)
    Assert.Equal(None, doc.Headlines.[2].TodoKeyword)

module SourceBlockAwareness =

    [<Fact>]
    let ``Headlines inside BEGIN_SRC are not parsed as headlines`` () =
        let content =
            "* Real headline\nSome text\n#+BEGIN_SRC org\n* Fake headline\n** Another fake\n#+END_SRC\n* Second real headline\n"

        let doc = Document.parse content
        Assert.Equal(2, doc.Headlines.Length)
        Assert.Equal("Real headline", doc.Headlines.[0].Title)
        Assert.Equal("Second real headline", doc.Headlines.[1].Title)

    [<Fact>]
    let ``Headlines inside BEGIN_EXAMPLE are not parsed as headlines`` () =
        let content =
            "* Before\n#+BEGIN_EXAMPLE\n* Not a headline\n#+END_EXAMPLE\n* After\n"

        let doc = Document.parse content
        Assert.Equal(2, doc.Headlines.Length)
        Assert.Equal("Before", doc.Headlines.[0].Title)
        Assert.Equal("After", doc.Headlines.[1].Title)

    [<Fact>]
    let ``Nested blocks are handled correctly`` () =
        let content =
            "* Top\n#+begin_src python\ndef foo():\n    pass\n#+end_src\nBody text\n"

        let doc = Document.parse content
        Assert.Equal(1, doc.Headlines.Length)
        Assert.Equal("Top", doc.Headlines.[0].Title)

    [<Fact>]
    let ``Block keywords are case-insensitive`` () =
        let content = "* A\n#+begin_SRC org\n* Not real\n#+END_src\n* B\n"
        let doc = Document.parse content
        Assert.Equal(2, doc.Headlines.Length)
        Assert.Equal("A", doc.Headlines.[0].Title)
        Assert.Equal("B", doc.Headlines.[1].Title)

    [<Fact>]
    let ``Multiple blocks in sequence`` () =
        let content =
            "* H1\n#+BEGIN_SRC\n* fake1\n#+END_SRC\n* H2\n#+BEGIN_EXAMPLE\n* fake2\n#+END_EXAMPLE\n* H3\n"

        let doc = Document.parse content
        Assert.Equal(3, doc.Headlines.Length)
        Assert.Equal("H1", doc.Headlines.[0].Title)
        Assert.Equal("H2", doc.Headlines.[1].Title)
        Assert.Equal("H3", doc.Headlines.[2].Title)
