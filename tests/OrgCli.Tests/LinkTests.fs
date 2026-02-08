module LinkTests

open Xunit
open OrgCli.Org

let private mkDoc content = Document.parse content

let private docs =
    [ ("notes/main.org",
       mkDoc
           "* Main Heading\n:PROPERTIES:\n:ID: id-main-123\n:CUSTOM_ID: custom-main\n:END:\nSome body with [[id:id-other-456][link to other]]\n** Sub Heading\n:PROPERTIES:\n:ID: id-sub-789\n:END:\nBody\n")
      ("notes/other.org",
       mkDoc "* Other Heading\n:PROPERTIES:\n:ID: id-other-456\n:END:\nBody\n* Second Heading\nNo properties\n")
      ("notes/deep/nested.org", mkDoc "* Nested\n:PROPERTIES:\n:ID: id-nested\n:END:\nBody\n") ]

// --- id link resolution ---

[<Fact>]
let ``resolve id link finds headline with matching ID`` () =
    let link =
        { LinkType = "id"
          Path = "id-other-456"
          Description = None
          SearchOption = None
          Position = 0 }

    let result = Links.resolveLink link "notes/main.org" docs Map.empty
    Assert.Equal(Some "notes/other.org", result.TargetFile)
    Assert.Equal(Some "Other Heading", result.TargetHeadline)

[<Fact>]
let ``resolve id link returns None for unknown ID`` () =
    let link =
        { LinkType = "id"
          Path = "nonexistent"
          Description = None
          SearchOption = None
          Position = 0 }

    let result = Links.resolveLink link "notes/main.org" docs Map.empty
    Assert.Equal(None, result.TargetFile)
    Assert.Equal(None, result.TargetHeadline)

// --- file link resolution ---

[<Fact>]
let ``resolve file link resolves relative path`` () =
    let link =
        { LinkType = "file"
          Path = "other.org"
          Description = None
          SearchOption = None
          Position = 0 }

    let result = Links.resolveLink link "notes/main.org" docs Map.empty
    Assert.Equal(Some "notes/other.org", result.TargetFile)

[<Fact>]
let ``resolve file link with heading search option`` () =
    let link =
        { LinkType = "file"
          Path = "other.org"
          Description = None
          SearchOption = Some "*Second Heading"
          Position = 0 }

    let result = Links.resolveLink link "notes/main.org" docs Map.empty
    Assert.Equal(Some "notes/other.org", result.TargetFile)
    Assert.Equal(Some "Second Heading", result.TargetHeadline)

[<Fact>]
let ``resolve file link with custom-id search option`` () =
    let link =
        { LinkType = "file"
          Path = "main.org"
          Description = None
          SearchOption = Some "#custom-main"
          Position = 0 }

    let result = Links.resolveLink link "notes/main.org" docs Map.empty
    Assert.Equal(Some "notes/main.org", result.TargetFile)
    Assert.Equal(Some "Main Heading", result.TargetHeadline)

// --- fuzzy link resolution ---

[<Fact>]
let ``resolve fuzzy heading link finds heading in current file`` () =
    let link =
        { LinkType = "fuzzy"
          Path = "*Sub Heading"
          Description = None
          SearchOption = None
          Position = 0 }

    let result = Links.resolveLink link "notes/main.org" docs Map.empty
    Assert.Equal(Some "notes/main.org", result.TargetFile)
    Assert.Equal(Some "Sub Heading", result.TargetHeadline)

// --- external links ---

[<Fact>]
let ``resolve https link returns without resolution`` () =
    let link =
        { LinkType = "https"
          Path = "//example.com"
          Description = None
          SearchOption = None
          Position = 0 }

    let result = Links.resolveLink link "notes/main.org" docs Map.empty
    Assert.Equal(None, result.TargetFile)
    Assert.Equal(None, result.TargetHeadline)

[<Fact>]
let ``resolve http link returns without resolution`` () =
    let link =
        { LinkType = "http"
          Path = "//example.com"
          Description = None
          SearchOption = None
          Position = 0 }

    let result = Links.resolveLink link "notes/main.org" docs Map.empty
    Assert.Equal(None, result.TargetFile)

// --- resolveLinksInFile ---

[<Fact>]
let ``resolveLinksInFile resolves multiple links`` () =
    let results = Links.resolveLinksInFile "notes/main.org" docs
    Assert.True(results.Length >= 1)
    let idLink = results |> List.tryFind (fun r -> r.Link.LinkType = "id")
    Assert.True(idLink.IsSome)
    Assert.Equal(Some "notes/other.org", idLink.Value.TargetFile)

[<Fact>]
let ``unresolvable link returns None for target fields`` () =
    let link =
        { LinkType = "magnet"
          Path = "something"
          Description = None
          SearchOption = None
          Position = 0 }

    let result = Links.resolveLink link "notes/main.org" docs Map.empty
    Assert.Equal(None, result.TargetFile)
    Assert.Equal(None, result.TargetHeadline)
    Assert.Equal(None, result.TargetPos)

// --- link abbreviation expansion ---

[<Fact>]
let ``link abbreviation expands %s in URL`` () =
    let abbrevs = Map.ofList [ ("bug", "http://bugs.example.com/show_bug.cgi?id=%s") ]

    let link =
        { LinkType = "bug"
          Path = "42"
          Description = None
          SearchOption = None
          Position = 0 }

    let result = Links.resolveLink link "notes/main.org" docs abbrevs
    Assert.Equal(Some "http://bugs.example.com/show_bug.cgi?id=42", result.TargetFile)

[<Fact>]
let ``link abbreviation without %s appends path`` () =
    let abbrevs = Map.ofList [ ("wiki", "http://wiki.example.com/") ]

    let link =
        { LinkType = "wiki"
          Path = "OrgMode"
          Description = None
          SearchOption = None
          Position = 0 }

    let result = Links.resolveLink link "notes/main.org" docs abbrevs
    Assert.Equal(Some "http://wiki.example.com/OrgMode", result.TargetFile)

[<Fact>]
let ``resolveLinksInFile uses document link abbreviations`` () =
    let content =
        "#+LINK: bug http://bugs.example.com/%s\n* Heading\nSee [[bug:99][bug 99]]\n"

    let docsWithAbbrev = [ ("test.org", Document.parse content) ]
    let results = Links.resolveLinksInFile "test.org" docsWithAbbrev
    Assert.Equal(1, results.Length)
    Assert.Equal(Some "http://bugs.example.com/99", results.[0].TargetFile)
