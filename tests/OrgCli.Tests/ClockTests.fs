module ClockTests

open System
open Xunit
open OrgCli.Org

[<Fact>]
let ``parse completed clock line`` () =
    let line = "CLOCK: [2026-02-05 Thu 09:00]--[2026-02-05 Thu 10:30] =>  1:30"
    match Parsers.runParser Parsers.pClockEntry (line + "\n") with
    | Result.Ok entry ->
        Assert.Equal(DateTime(2026, 2, 5, 9, 0, 0), entry.Start.Date)
        Assert.True(entry.End.IsSome)
        Assert.Equal(DateTime(2026, 2, 5, 10, 30, 0), entry.End.Value.Date)
        Assert.True(entry.Duration.IsSome)
        Assert.Equal(TimeSpan(1, 30, 0), entry.Duration.Value)
    | Result.Error e -> failwithf "Parse failed: %s" e

[<Fact>]
let ``parse running clock line`` () =
    let line = "CLOCK: [2026-02-05 Thu 14:00]"
    match Parsers.runParser Parsers.pClockEntry (line + "\n") with
    | Result.Ok entry ->
        Assert.Equal(DateTime(2026, 2, 5, 14, 0, 0), entry.Start.Date)
        Assert.True(entry.End.IsNone)
        Assert.True(entry.Duration.IsNone)
    | Result.Error e -> failwithf "Parse failed: %s" e

[<Fact>]
let ``collectClockEntriesFromDocs finds clock entries in headlines`` () =
    let content =
        "* Task A\n" +
        "CLOCK: [2026-02-05 Thu 09:00]--[2026-02-05 Thu 10:00] =>  1:00\n" +
        "CLOCK: [2026-02-05 Thu 11:00]--[2026-02-05 Thu 12:00] =>  1:00\n" +
        "* Task B\n" +
        "No clocks here.\n"
    let doc = Document.parse content
    let results = Clock.collectClockEntriesFromDocs [("test.org", doc, content)]
    Assert.Equal(1, results.Length)
    let (headline, _, entries) = results.[0]
    Assert.Equal("Task A", headline.Title)
    Assert.Equal(2, entries.Length)

[<Fact>]
let ``totalDuration sums completed clock entries`` () =
    let entries : ClockEntry list = [
        { Start = { Type = TimestampType.Inactive; Date = DateTime(2026, 2, 5, 9, 0, 0); HasTime = true; Repeater = None; Delay = None; RangeEnd = None }
          End = Some { Type = TimestampType.Inactive; Date = DateTime(2026, 2, 5, 10, 0, 0); HasTime = true; Repeater = None; Delay = None; RangeEnd = None }
          Duration = Some (TimeSpan(1, 0, 0)) }
        { Start = { Type = TimestampType.Inactive; Date = DateTime(2026, 2, 5, 11, 0, 0); HasTime = true; Repeater = None; Delay = None; RangeEnd = None }
          End = Some { Type = TimestampType.Inactive; Date = DateTime(2026, 2, 5, 12, 30, 0); HasTime = true; Repeater = None; Delay = None; RangeEnd = None }
          Duration = Some (TimeSpan(1, 30, 0)) }
    ]
    let total = Clock.totalDuration entries
    Assert.Equal(TimeSpan(2, 30, 0), total)

[<Fact>]
let ``totalDuration ignores running clocks`` () =
    let entries : ClockEntry list = [
        { Start = { Type = TimestampType.Inactive; Date = DateTime(2026, 2, 5, 9, 0, 0); HasTime = true; Repeater = None; Delay = None; RangeEnd = None }
          End = Some { Type = TimestampType.Inactive; Date = DateTime(2026, 2, 5, 10, 0, 0); HasTime = true; Repeater = None; Delay = None; RangeEnd = None }
          Duration = Some (TimeSpan(1, 0, 0)) }
        { Start = { Type = TimestampType.Inactive; Date = DateTime(2026, 2, 5, 14, 0, 0); HasTime = true; Repeater = None; Delay = None; RangeEnd = None }
          End = None
          Duration = None }
    ]
    let total = Clock.totalDuration entries
    Assert.Equal(TimeSpan(1, 0, 0), total)

[<Fact>]
let ``collectClockEntriesFromDocs across multiple files`` () =
    let content1 = "* Task A\nCLOCK: [2026-02-05 Thu 09:00]--[2026-02-05 Thu 10:00] =>  1:00\n"
    let content2 = "* Task B\nCLOCK: [2026-02-05 Thu 11:00]--[2026-02-05 Thu 12:00] =>  1:00\n"
    let doc1 = Document.parse content1
    let doc2 = Document.parse content2
    let results = Clock.collectClockEntriesFromDocs [("a.org", doc1, content1); ("b.org", doc2, content2)]
    Assert.Equal(2, results.Length)

[<Fact>]
let ``clock entries under sub-headline attributed to sub-headline`` () =
    let content =
        "* Parent\n" +
        "** Child\n" +
        "CLOCK: [2026-02-05 Thu 09:00]--[2026-02-05 Thu 10:00] =>  1:00\n"
    let doc = Document.parse content
    let results = Clock.collectClockEntriesFromDocs [("test.org", doc, content)]
    let childEntries = results |> List.filter (fun (h, _, _) -> h.Title = "Child")
    Assert.Equal(1, childEntries.Length)
    let (_, _, entries) = childEntries.[0]
    Assert.Equal(1, entries.Length)

[<Fact>]
let ``clock entries only in parent body not attributed to child`` () =
    let content =
        "* Parent\n" +
        "CLOCK: [2026-02-05 Thu 09:00]--[2026-02-05 Thu 10:00] =>  1:00\n" +
        "** Child\n" +
        "Child body\n"
    let doc = Document.parse content
    let results = Clock.collectClockEntriesFromDocs [("test.org", doc, content)]
    let parentEntries = results |> List.filter (fun (h, _, _) -> h.Title = "Parent")
    let childEntries = results |> List.filter (fun (h, _, _) -> h.Title = "Child")
    Assert.Equal(1, parentEntries.Length)
    Assert.Empty(childEntries)
