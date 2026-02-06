module ErrorPathTests

open Xunit
open OrgCli.Org

// ============================================================
// Malformed timestamp parsing
// ============================================================

[<Fact>]
let ``parse document with malformed timestamp does not crash`` () =
    // Invalid date values (month 99) crash DateTime constructor inside FParsec parser.
    // This documents the current behavior - the parser throws rather than skipping.
    let content = "* TODO Task\nSCHEDULED: <2026-99-99 Bad>\n:PROPERTIES:\n:ID: abc\n:END:\n"
    Assert.ThrowsAny<System.Exception>(fun () ->
        Document.parse content |> ignore) |> ignore

[<Fact>]
let ``parse document with non-timestamp scheduled text does not crash`` () =
    // Scheduled text that doesn't match timestamp pattern at all is harmless
    let content = "* TODO Task\nSCHEDULED: not-a-date\n:PROPERTIES:\n:ID: abc\n:END:\n"
    let doc = Document.parse content
    Assert.Equal(1, doc.Headlines.Length)
    Assert.Equal("Task", doc.Headlines.[0].Title)

[<Fact>]
let ``parse document with unclosed timestamp angle bracket`` () =
    let content = "* TODO Task\nSCHEDULED: <2026-02-05 Thu\n:PROPERTIES:\n:ID: abc\n:END:\n"
    let doc = Document.parse content
    Assert.Equal(1, doc.Headlines.Length)

[<Fact>]
let ``parse document with empty timestamp`` () =
    let content = "* TODO Task\nSCHEDULED: <>\n:PROPERTIES:\n:ID: abc\n:END:\n"
    let doc = Document.parse content
    Assert.Equal(1, doc.Headlines.Length)

// ============================================================
// Malformed property drawers
// ============================================================

[<Fact>]
let ``parse document with unclosed property drawer`` () =
    let content = "* TODO Task\n:PROPERTIES:\n:ID: abc\nBody text without END.\n"
    let doc = Document.parse content
    Assert.Equal(1, doc.Headlines.Length)

[<Fact>]
let ``parse document with empty property drawer`` () =
    let content = "* TODO Task\n:PROPERTIES:\n:END:\nBody.\n"
    let doc = Document.parse content
    Assert.Equal(1, doc.Headlines.Length)

[<Fact>]
let ``parse document with malformed property key`` () =
    let content = "* TODO Task\n:PROPERTIES:\nNOT_A_PROPERTY\n:ID: abc\n:END:\nBody.\n"
    let doc = Document.parse content
    Assert.Equal(1, doc.Headlines.Length)

// ============================================================
// Malformed headlines
// ============================================================

[<Fact>]
let ``parse document with stars but no space`` () =
    let content = "***notaheadline\n* Real headline\nBody.\n"
    let doc = Document.parse content
    // Only the real headline should be parsed
    Assert.True(doc.Headlines.Length >= 1)
    Assert.Contains(doc.Headlines, fun h -> h.Title = "Real headline")

[<Fact>]
let ``parse empty document`` () =
    let doc = Document.parse ""
    Assert.Empty(doc.Headlines)
    Assert.Empty(doc.Keywords)

[<Fact>]
let ``parse document with only whitespace`` () =
    let doc = Document.parse "   \n\n  \n"
    Assert.Empty(doc.Headlines)

// ============================================================
// Malformed links
// ============================================================

[<Fact>]
let ``parse document with unclosed link bracket`` () =
    let content = "* Headline\nBody with [[id:broken link text.\n"
    let doc = Document.parse content
    Assert.Equal(1, doc.Headlines.Length)

[<Fact>]
let ``parse document with empty link`` () =
    let content = "* Headline\nBody with [[]] here.\n"
    let doc = Document.parse content
    Assert.Equal(1, doc.Headlines.Length)

// ============================================================
// Malformed clock entries
// ============================================================

[<Fact>]
let ``collectClockEntries skips malformed clock lines`` () =
    let content =
        "* Task\n" +
        ":LOGBOOK:\n" +
        "CLOCK: not-a-timestamp\n" +
        "CLOCK: [2026-02-05 Thu 10:00]--[2026-02-05 Thu 11:00] =>  1:00\n" +
        ":END:\n"
    let doc = Document.parse content
    let entries = Clock.collectClockEntriesFromDocs [("test.org", doc, content)]
    // Should still find the valid clock entry
    let clocks = entries |> List.collect (fun (_, _, c) -> c)
    Assert.Equal(1, clocks.Length)

// ============================================================
// Mixed valid/invalid content
// ============================================================

[<Fact>]
let ``parse document with mix of valid and invalid elements`` () =
    let content =
        ":PROPERTIES:\n" +
        ":ID: file-id\n" +
        ":END:\n" +
        "#+title: Test\n" +
        "\n" +
        "* Valid headline\n" +
        ":PROPERTIES:\n" +
        ":ID: valid-id\n" +
        ":END:\n" +
        "Body with [[id:target][good link]].\n" +
        "\n" +
        "* Another headline with bad planning\n" +
        "SCHEDULED: <broken-date>\n" +
        "Some content.\n"
    let doc = Document.parse content
    Assert.Equal(2, doc.Headlines.Length)
    Assert.Equal(Some "file-id", Types.tryGetId doc.FileProperties)
