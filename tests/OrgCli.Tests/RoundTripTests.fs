module RoundTripTests

open System
open Xunit
open OrgCli.Org

/// Parse -> mutate -> re-parse helper. Verifies the mutation took effect
/// and nothing else changed unexpectedly.
let private now = DateTime(2026, 2, 5, 14, 30, 0)
let private config = Types.defaultConfig

let private roundTrip (content: string) (mutate: string -> string) (verify: OrgDocument -> OrgDocument -> unit) =
    let docBefore = Document.parse content
    let mutated = mutate content
    let docAfter = Document.parse mutated
    verify docBefore docAfter

// --- setTodoState round-trip ---

[<Fact>]
let ``round-trip: setTodoState TODO->DONE preserves title and properties`` () =
    let content =
        "* TODO My task\n:PROPERTIES:\n:ID: abc-123\n:CUSTOM: value\n:END:\nBody text.\n"

    roundTrip content (fun c -> Mutations.setTodoState config c 0L (Some "DONE") now) (fun before after ->
        let hBefore = before.Headlines.[0]
        let hAfter = after.Headlines.[0]
        Assert.Equal("My task", hAfter.Title)
        Assert.Equal(Some "DONE", hAfter.TodoKeyword)
        Assert.True(hAfter.Planning.IsSome)
        Assert.True(hAfter.Planning.Value.Closed.IsSome)
        // Properties preserved
        Assert.Equal(Types.tryGetId hBefore.Properties, Types.tryGetId hAfter.Properties)
        Assert.Equal(Types.tryGetProperty "CUSTOM" hBefore.Properties, Types.tryGetProperty "CUSTOM" hAfter.Properties))

[<Fact>]
let ``round-trip: setTodoState DONE->TODO removes CLOSED, preserves SCHEDULED`` () =
    let dn = now.ToString("ddd", Globalization.CultureInfo.InvariantCulture)

    let content =
        sprintf
            "* DONE My task\nSCHEDULED: <2026-02-05 %s> CLOSED: [2026-02-05 %s 14:30]\n:PROPERTIES:\n:ID: abc-123\n:END:\nBody\n"
            dn
            dn

    roundTrip content (fun c -> Mutations.setTodoState config c 0L (Some "TODO") now) (fun _ after ->
        let h = after.Headlines.[0]
        Assert.Equal(Some "TODO", h.TodoKeyword)
        Assert.True(h.Planning.IsSome)
        Assert.True(h.Planning.Value.Scheduled.IsSome)
        Assert.True(h.Planning.Value.Closed.IsNone))

// --- setPriority round-trip ---

[<Fact>]
let ``round-trip: setPriority preserves everything else`` () =
    let content = "* TODO My task\n:PROPERTIES:\n:ID: abc-123\n:END:\nBody text.\n"

    roundTrip content (fun c -> Mutations.setPriority c 0L (Some 'A')) (fun before after ->
        let h = after.Headlines.[0]
        Assert.Equal(Some(Priority 'A'), h.Priority)
        Assert.Equal("My task", h.Title)
        Assert.Equal(Some "TODO", h.TodoKeyword)
        Assert.Equal(Types.tryGetId before.Headlines.[0].Properties, Types.tryGetId h.Properties))

// --- setScheduled round-trip ---

[<Fact>]
let ``round-trip: setScheduled preserves title and properties`` () =
    let content = "* TODO My task\n:PROPERTIES:\n:ID: abc-123\n:END:\nBody.\n"
    let schedTs = Utils.parseDate "2026-03-01"

    roundTrip content (fun c -> Mutations.setScheduled config c 0L (Some schedTs) now) (fun _ after ->
        let h = after.Headlines.[0]
        Assert.True(h.Planning.IsSome)
        Assert.True(h.Planning.Value.Scheduled.IsSome)
        Assert.Equal(DateTime(2026, 3, 1).Date, h.Planning.Value.Scheduled.Value.Date.Date)
        Assert.Equal("My task", h.Title))

// --- setDeadline round-trip ---

[<Fact>]
let ``round-trip: setDeadline then remove preserves body`` () =
    let content = "* TODO My task\n:PROPERTIES:\n:ID: abc-123\n:END:\nImportant body.\n"
    let deadTs = Utils.parseDate "2026-04-15"
    let withDeadline = Mutations.setDeadline config content 0L (Some deadTs) now
    let docWithDeadline = Document.parse withDeadline
    Assert.True(docWithDeadline.Headlines.[0].Planning.Value.Deadline.IsSome)

    let withoutDeadline = Mutations.setDeadline config withDeadline 0L None now
    let docWithout = Document.parse withoutDeadline

    Assert.True(
        docWithout.Headlines.[0].Planning.IsNone
        || docWithout.Headlines.[0].Planning.Value.Deadline.IsNone
    )

    Assert.Contains("Important body", withoutDeadline)

// --- addTag/removeTag round-trip ---

[<Fact>]
let ``round-trip: addTag preserves properties and body`` () =
    let content = "* TODO My task\n:PROPERTIES:\n:ID: abc-123\n:END:\nBody.\n"
    let withTag1 = Mutations.addTag content 0L "tag1"
    let withTag2 = Mutations.addTag withTag1 0L "tag2"
    let doc = Document.parse withTag2
    let h = doc.Headlines.[0]
    Assert.Contains("tag1", h.Tags)
    Assert.Contains("tag2", h.Tags)
    Assert.Equal("My task", h.Title)
    Assert.Contains("Body.", withTag2)

[<Fact>]
let ``round-trip: addTag then removeTag roundtrip`` () =
    let content = "* TODO My task :old:\n:PROPERTIES:\n:ID: abc-123\n:END:\nBody.\n"
    let withTag = Mutations.addTag content 0L "new1"
    let doc = Document.parse withTag
    Assert.Contains("new1", doc.Headlines.[0].Tags)
    let removed = Mutations.removeTag withTag 0L "old"
    let doc2 = Document.parse removed
    Assert.DoesNotContain("old", doc2.Headlines.[0].Tags)
    Assert.Contains("new1", doc2.Headlines.[0].Tags)
    Assert.Equal("My task", doc2.Headlines.[0].Title)

// --- Multiple mutations preserve each other ---

[<Fact>]
let ``round-trip: multiple mutations compose correctly`` () =
    let content = "* My task\n:PROPERTIES:\n:ID: abc-123\n:END:\nBody.\n"
    let step1 = Mutations.setTodoState config content 0L (Some "TODO") now
    let step2 = Mutations.setPriority step1 0L (Some 'B')
    let schedTs = Utils.parseDate "2026-03-01"
    let step3 = Mutations.setScheduled config step2 0L (Some schedTs) now
    let step4 = Mutations.addTag step3 0L "important"

    let doc = Document.parse step4
    let h = doc.Headlines.[0]
    Assert.Equal(Some "TODO", h.TodoKeyword)
    Assert.Equal(Some(Priority 'B'), h.Priority)
    Assert.True(h.Planning.Value.Scheduled.IsSome)
    Assert.Contains("important", h.Tags)
    Assert.Equal("My task", h.Title)
    Assert.Contains("Body.", step4)

// --- Multi-headline document stability ---

[<Fact>]
let ``round-trip: mutating one headline does not affect siblings`` () =
    let content =
        "* TODO First task\n:PROPERTIES:\n:ID: first\n:END:\nFirst body.\n\n"
        + "* DONE Second task :tag1:\n:PROPERTIES:\n:ID: second\n:END:\nSecond body.\n\n"
        + "** Child of second\nChild body.\n"

    let docBefore = Document.parse content
    let firstPos = docBefore.Headlines.[0].Position

    let mutated = Mutations.setTodoState config content firstPos (Some "DONE") now
    let docAfter = Document.parse mutated

    // First headline changed
    Assert.Equal(Some "DONE", docAfter.Headlines.[0].TodoKeyword)
    // Second headline unchanged
    Assert.Equal(Some "DONE", docAfter.Headlines.[1].TodoKeyword)
    Assert.Contains("tag1", docAfter.Headlines.[1].Tags)
    Assert.Equal("Second task", docAfter.Headlines.[1].Title)
    // Child unchanged
    Assert.Equal("Child of second", docAfter.Headlines.[2].Title)
