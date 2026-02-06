module HeadlineQueryTests

open Xunit
open OrgCli.Org

// --- Tag inheritance tests ---

module TagInheritance =

    let private parseDoc content = Document.parse content
    let private defaultCfg = Types.defaultConfig

    [<Fact>]
    let ``child inherits parent tags`` () =
        let content = "* Parent :work:\n** Child\n"
        let doc = parseDoc content
        let child = doc.Headlines.[1]
        let tags = Headlines.computeInheritedTags defaultCfg doc child
        Assert.Contains("work", tags)

    [<Fact>]
    let ``grandchild inherits through parent`` () =
        let content = "* Grandparent :project:\n** Parent :urgent:\n*** Child\n"
        let doc = parseDoc content
        let child = doc.Headlines.[2]
        let tags = Headlines.computeInheritedTags defaultCfg doc child
        Assert.Contains("project", tags)
        Assert.Contains("urgent", tags)

    [<Fact>]
    let ``own tags combined with inherited`` () =
        let content = "* Parent :work:\n** Child :home:\n"
        let doc = parseDoc content
        let child = doc.Headlines.[1]
        let tags = Headlines.computeInheritedTags defaultCfg doc child
        Assert.Contains("work", tags)
        Assert.Contains("home", tags)

    [<Fact>]
    let ``no duplicate tags`` () =
        let content = "* Parent :work:\n** Child :work:\n"
        let doc = parseDoc content
        let child = doc.Headlines.[1]
        let tags = Headlines.computeInheritedTags defaultCfg doc child
        let workCount = tags |> List.filter (fun t -> t = "work") |> List.length
        Assert.Equal(1, workCount)

    [<Fact>]
    let ``filetags inherited by all headlines`` () =
        let content = "#+FILETAGS: :project:draft:\n* Headline\n"
        let doc = parseDoc content
        let h = doc.Headlines.[0]
        let tags = Headlines.computeInheritedTags defaultCfg doc h
        Assert.Contains("project", tags)
        Assert.Contains("draft", tags)

    [<Fact>]
    let ``tags exclude from inheritance respected`` () =
        let cfg = { defaultCfg with TagsExcludeFromInheritance = ["noexport"] }
        let content = "* Parent :noexport:work:\n** Child\n"
        let doc = parseDoc content
        let child = doc.Headlines.[1]
        let tags = Headlines.computeInheritedTags cfg doc child
        Assert.DoesNotContain("noexport", tags)
        Assert.Contains("work", tags)

    [<Fact>]
    let ``tag inheritance disabled returns own tags only`` () =
        let cfg = { defaultCfg with TagInheritance = false }
        let content = "* Parent :work:\n** Child :home:\n"
        let doc = parseDoc content
        let child = doc.Headlines.[1]
        let tags = Headlines.computeInheritedTags cfg doc child
        Assert.DoesNotContain("work", tags)
        Assert.Contains("home", tags)

    [<Fact>]
    let ``sibling does not inherit from sibling`` () =
        let content = "* Sibling1 :work:\n* Sibling2\n"
        let doc = parseDoc content
        let s2 = doc.Headlines.[1]
        let tags = Headlines.computeInheritedTags defaultCfg doc s2
        Assert.DoesNotContain("work", tags)

    [<Fact>]
    let ``filterByTagWithInheritance finds inherited matches`` () =
        let content = "* Parent :work:\n** Child\n** Other :home:\n"
        let doc = parseDoc content
        let all = Headlines.collectHeadlinesFromDocs [("test.org", doc)]
        let filtered = Headlines.filterByTagWithInheritance defaultCfg [("test.org", doc)] "work" all
        // Parent + Child + Other all have "work" (Parent directly, children inherited)
        Assert.Equal(3, filtered.Length)

    [<Fact>]
    let ``top-level headline inherits filetags only`` () =
        let content = "#+FILETAGS: :project:\n* Top :work:\n"
        let doc = parseDoc content
        let top = doc.Headlines.[0]
        let tags = Headlines.computeInheritedTags defaultCfg doc top
        Assert.Contains("project", tags)
        Assert.Contains("work", tags)
        Assert.Equal(2, tags.Length)

// --- Property inheritance tests ---

module PropertyInheritance =

    let private parseDoc content = Document.parse content
    let private defaultCfg = Types.defaultConfig

    [<Fact>]
    let ``CATEGORY always inherits from parent`` () =
        let content = "* Parent\n:PROPERTIES:\n:CATEGORY: work\n:END:\n** Child\n"
        let doc = parseDoc content
        let child = doc.Headlines.[1]
        let v = Headlines.resolveProperty defaultCfg doc child "CATEGORY"
        Assert.Equal(Some "work", v)

    [<Fact>]
    let ``ARCHIVE always inherits from parent`` () =
        let content = "* Parent\n:PROPERTIES:\n:ARCHIVE: archive.org::\n:END:\n** Child\n"
        let doc = parseDoc content
        let child = doc.Headlines.[1]
        let v = Headlines.resolveProperty defaultCfg doc child "ARCHIVE"
        Assert.Equal(Some "archive.org::", v)

    [<Fact>]
    let ``LOGGING always inherits from parent`` () =
        let content = "* Parent\n:PROPERTIES:\n:LOGGING: nil\n:END:\n** Child\n"
        let doc = parseDoc content
        let child = doc.Headlines.[1]
        let v = Headlines.resolveProperty defaultCfg doc child "LOGGING"
        Assert.Equal(Some "nil", v)

    [<Fact>]
    let ``regular property does NOT inherit when PropertyInheritance is false`` () =
        let cfg = { defaultCfg with PropertyInheritance = false }
        let content = "* Parent\n:PROPERTIES:\n:EFFORT: 1:00\n:END:\n** Child\n"
        let doc = parseDoc content
        let child = doc.Headlines.[1]
        let v = Headlines.resolveProperty cfg doc child "EFFORT"
        Assert.Equal(None, v)

    [<Fact>]
    let ``regular property inherits when PropertyInheritance is true`` () =
        let cfg = { defaultCfg with PropertyInheritance = true; InheritProperties = [] }
        let content = "* Parent\n:PROPERTIES:\n:EFFORT: 1:00\n:END:\n** Child\n"
        let doc = parseDoc content
        let child = doc.Headlines.[1]
        let v = Headlines.resolveProperty cfg doc child "EFFORT"
        Assert.Equal(Some "1:00", v)

    [<Fact>]
    let ``regular property inherits only if in InheritProperties list`` () =
        let cfg = { defaultCfg with PropertyInheritance = true; InheritProperties = ["CUSTOM"] }
        let content = "* Parent\n:PROPERTIES:\n:EFFORT: 1:00\n:CUSTOM: yes\n:END:\n** Child\n"
        let doc = parseDoc content
        let child = doc.Headlines.[1]
        Assert.Equal(None, Headlines.resolveProperty cfg doc child "EFFORT")
        Assert.Equal(Some "yes", Headlines.resolveProperty cfg doc child "CUSTOM")

    [<Fact>]
    let ``own property takes precedence over inherited`` () =
        let content = "* Parent\n:PROPERTIES:\n:CATEGORY: work\n:END:\n** Child\n:PROPERTIES:\n:CATEGORY: personal\n:END:\n"
        let doc = parseDoc content
        let child = doc.Headlines.[1]
        let v = Headlines.resolveProperty defaultCfg doc child "CATEGORY"
        Assert.Equal(Some "personal", v)

    [<Fact>]
    let ``CATEGORY falls back to file keyword`` () =
        let content = "#+CATEGORY: myproject\n* Headline\n"
        let doc = parseDoc content
        let h = doc.Headlines.[0]
        let v = Headlines.resolveProperty defaultCfg doc h "CATEGORY"
        Assert.Equal(Some "myproject", v)

    [<Fact>]
    let ``file-level PROPERTY keyword provides default`` () =
        let content = "#+PROPERTY: Effort_ALL 0 0:10 0:30\n* Headline\n"
        let doc = parseDoc content
        let h = doc.Headlines.[0]
        let v = Headlines.resolveProperty { defaultCfg with PropertyInheritance = true; InheritProperties = [] } doc h "Effort_ALL"
        Assert.Equal(Some "0 0:10 0:30", v)

let private parseDoc content = Document.parse content

[<Fact>]
let ``collectHeadlinesFromDocs returns headlines with file context`` () =
    let content = "* First\n* Second\n"
    let doc = parseDoc content
    let results = Headlines.collectHeadlinesFromDocs [("test.org", doc)]
    Assert.Equal(2, results.Length)
    Assert.Equal("First", results.[0].Headline.Title)
    Assert.Equal("test.org", results.[0].File)
    Assert.Equal("Second", results.[1].Headline.Title)

[<Fact>]
let ``collectHeadlinesFromDocs collects from multiple files`` () =
    let doc1 = parseDoc "* A\n"
    let doc2 = parseDoc "* B\n* C\n"
    let results = Headlines.collectHeadlinesFromDocs [("a.org", doc1); ("b.org", doc2)]
    Assert.Equal(3, results.Length)
    Assert.Equal("a.org", results.[0].File)
    Assert.Equal("b.org", results.[1].File)
    Assert.Equal("b.org", results.[2].File)

[<Fact>]
let ``filterByTodo filters by TODO state`` () =
    let content = "* TODO Active\n* DONE Finished\n* Plain\n"
    let doc = parseDoc content
    let all = Headlines.collectHeadlinesFromDocs [("test.org", doc)]
    let filtered = Headlines.filterByTodo "TODO" all
    Assert.Equal(1, filtered.Length)
    Assert.Equal("Active", filtered.[0].Headline.Title)

[<Fact>]
let ``filterByTag filters by tag`` () =
    let content = "* Tagged :work:\n* Untagged\n* Also tagged :work:home:\n"
    let doc = parseDoc content
    let all = Headlines.collectHeadlinesFromDocs [("test.org", doc)]
    let filtered = Headlines.filterByTag "work" all
    Assert.Equal(2, filtered.Length)

[<Fact>]
let ``filterByLevel filters by headline level`` () =
    let content = "* Level 1\n** Level 2\n*** Level 3\n"
    let doc = parseDoc content
    let all = Headlines.collectHeadlinesFromDocs [("test.org", doc)]
    let filtered = Headlines.filterByLevel 2 all
    Assert.Equal(1, filtered.Length)
    Assert.Equal("Level 2", filtered.[0].Headline.Title)

[<Fact>]
let ``filterByProperty filters by property key=value`` () =
    let content = "* Has prop\n:PROPERTIES:\n:CATEGORY: work\n:END:\n* No prop\n"
    let doc = parseDoc content
    let all = Headlines.collectHeadlinesFromDocs [("test.org", doc)]
    let filtered = Headlines.filterByProperty "CATEGORY" "work" all
    Assert.Equal(1, filtered.Length)
    Assert.Equal("Has prop", filtered.[0].Headline.Title)

[<Fact>]
let ``combined filters work together`` () =
    let content = "* TODO Task one :work:\n* TODO Task two :home:\n* DONE Task three :work:\n"
    let doc = parseDoc content
    let all = Headlines.collectHeadlinesFromDocs [("test.org", doc)]
    let filtered =
        all
        |> Headlines.filterByTodo "TODO"
        |> Headlines.filterByTag "work"
    Assert.Equal(1, filtered.Length)
    Assert.Equal("Task one", filtered.[0].Headline.Title)

[<Fact>]
let ``outline path is computed for nested headlines`` () =
    let content = "* Parent\n** Child\n*** Grandchild\n"
    let doc = parseDoc content
    let results = Headlines.collectHeadlinesFromDocs [("test.org", doc)]
    Assert.Equal<string list>([], results.[0].OutlinePath)
    Assert.Equal<string list>(["Parent"], results.[1].OutlinePath)
    Assert.Equal<string list>(["Parent"; "Child"], results.[2].OutlinePath)

[<Fact>]
let ``outline path handles multiple top-level headlines`` () =
    let content = "* A\n** A1\n* B\n** B1\n"
    let doc = parseDoc content
    let results = Headlines.collectHeadlinesFromDocs [("test.org", doc)]
    Assert.Equal<string list>([], results.[0].OutlinePath) // A
    Assert.Equal<string list>(["A"], results.[1].OutlinePath) // A1
    Assert.Equal<string list>([], results.[2].OutlinePath) // B
    Assert.Equal<string list>(["B"], results.[3].OutlinePath) // B1

// --- resolveHeadlinePos tests ---

[<Fact>]
let ``resolveHeadlinePos resolves by position number`` () =
    let content = "* First\n* Second\n"
    let result = Headlines.resolveHeadlinePos content "8"
    Assert.Equal(Ok 8L, result)

[<Fact>]
let ``resolveHeadlinePos resolves by exact title`` () =
    let content = "* First\n* Second\n"
    let result = Headlines.resolveHeadlinePos content "Second"
    Assert.Equal(Ok 8L, result)

[<Fact>]
let ``resolveHeadlinePos resolves by org-id`` () =
    let content = "* My headline\n:PROPERTIES:\n:ID: 550e8400-e29b-41d4-a716-446655440000\n:END:\nBody\n"
    let result = Headlines.resolveHeadlinePos content "550e8400-e29b-41d4-a716-446655440000"
    Assert.Equal(Ok 0L, result)

[<Fact>]
let ``resolveHeadlinePos resolves org-id when multiple headlines`` () =
    let content = "* First\nBody\n* Second\n:PROPERTIES:\n:ID: abc-def-123\n:END:\nBody\n"
    let result = Headlines.resolveHeadlinePos content "abc-def-123"
    let expectedPos = content.IndexOf("* Second") |> int64
    Assert.Equal(Ok expectedPos, result)

[<Fact>]
let ``resolveHeadlinePos ID match takes precedence over title match`` () =
    let content = "* abc-def-123\nBody\n* Other\n:PROPERTIES:\n:ID: abc-def-123\n:END:\nBody\n"
    let result = Headlines.resolveHeadlinePos content "abc-def-123"
    let expectedPos = content.IndexOf("* Other") |> int64
    Assert.Equal(Ok expectedPos, result)

[<Fact>]
let ``resolveHeadlinePos returns error when nothing matches`` () =
    let content = "* First\n* Second\n"
    let result = Headlines.resolveHeadlinePos content "Nonexistent"
    match result with
    | Error e ->
        Assert.Equal(CliErrorType.HeadlineNotFound, e.Type)
        Assert.Contains("not found", e.Message.ToLowerInvariant())
    | Ok _ -> Assert.Fail("Expected Error")

// --- Virtual / special properties ---

module VirtualProperties =
    open System
    open OrgCli.Org

    let private defaultCfg = Types.defaultConfig

    let private content = "#+CATEGORY: myproject\n* TODO [#A] Task One :work:urgent:\nSCHEDULED: <2026-02-05 Thu>\n:PROPERTIES:\n:ID: abc-123\n:EFFORT: 1:00\n:END:\nBody\n** Sub task\nBody\n"
    let private doc = Document.parse content

    [<Fact>]
    let ``ITEM returns headline title`` () =
        let h = doc.Headlines.[0]
        Assert.Equal(Some "Task One", Headlines.resolveVirtualProperty defaultCfg doc h "ITEM" None)

    [<Fact>]
    let ``TODO returns todo keyword`` () =
        let h = doc.Headlines.[0]
        Assert.Equal(Some "TODO", Headlines.resolveVirtualProperty defaultCfg doc h "TODO" None)

    [<Fact>]
    let ``TODO returns None for plain headline`` () =
        let h = doc.Headlines.[1]
        Assert.Equal(None, Headlines.resolveVirtualProperty defaultCfg doc h "TODO" None)

    [<Fact>]
    let ``PRIORITY returns priority letter`` () =
        let h = doc.Headlines.[0]
        Assert.Equal(Some "A", Headlines.resolveVirtualProperty defaultCfg doc h "PRIORITY" None)

    [<Fact>]
    let ``LEVEL returns headline level as string`` () =
        let h = doc.Headlines.[0]
        Assert.Equal(Some "1", Headlines.resolveVirtualProperty defaultCfg doc h "LEVEL" None)
        let sub = doc.Headlines.[1]
        Assert.Equal(Some "2", Headlines.resolveVirtualProperty defaultCfg doc sub "LEVEL" None)

    [<Fact>]
    let ``TAGS returns local tags`` () =
        let h = doc.Headlines.[0]
        let result = Headlines.resolveVirtualProperty defaultCfg doc h "TAGS" None
        Assert.Equal(Some ":work:urgent:", result)

    [<Fact>]
    let ``ALLTAGS returns all tags including inherited`` () =
        let sub = doc.Headlines.[1]
        let result = Headlines.resolveVirtualProperty defaultCfg doc sub "ALLTAGS" None
        // Sub task inherits work and urgent from parent
        Assert.True(result.IsSome)
        Assert.Contains("work", result.Value)
        Assert.Contains("urgent", result.Value)

    [<Fact>]
    let ``CATEGORY returns document category`` () =
        let h = doc.Headlines.[0]
        Assert.Equal(Some "myproject", Headlines.resolveVirtualProperty defaultCfg doc h "CATEGORY" None)

    [<Fact>]
    let ``FILE returns file path`` () =
        let h = doc.Headlines.[0]
        Assert.Equal(Some "test.org", Headlines.resolveVirtualProperty defaultCfg doc h "FILE" (Some "test.org"))

    [<Fact>]
    let ``SCHEDULED returns timestamp string`` () =
        let h = doc.Headlines.[0]
        let result = Headlines.resolveVirtualProperty defaultCfg doc h "SCHEDULED" None
        Assert.True(result.IsSome)
        Assert.Contains("2026-02-05", result.Value)

    [<Fact>]
    let ``regular property falls through to drawer`` () =
        let h = doc.Headlines.[0]
        Assert.Equal(Some "1:00", Headlines.resolveVirtualProperty defaultCfg doc h "EFFORT" None)

    [<Fact>]
    let ``unknown property returns None`` () =
        let h = doc.Headlines.[0]
        Assert.Equal(None, Headlines.resolveVirtualProperty defaultCfg doc h "NONEXISTENT" None)
