module SearchTests

open Xunit
open OrgCli.Org

let private parseDoc content = Document.parse content

let private unwrap result =
    match result with
    | Result.Ok v -> v
    | Result.Error e -> failwithf "Unexpected error: %s" e

[<Fact>]
let ``search finds match in headline body and returns containing headline`` () =
    let content = "* My Headline\nThis has a keyword here.\n"
    let doc = parseDoc content
    let results = Search.searchDocs "keyword" [ ("test.org", doc, content) ] |> unwrap
    Assert.Equal(1, results.Length)
    Assert.Equal("My Headline", results.[0].Headline.Value.Title)
    Assert.Contains("keyword", results.[0].MatchLine)

[<Fact>]
let ``search with regex pattern`` () =
    let content = "* Headline\nError code: E1234\n"
    let doc = parseDoc content
    let results = Search.searchDocs @"E\d{4}" [ ("test.org", doc, content) ] |> unwrap
    Assert.Equal(1, results.Length)
    Assert.Contains("E1234", results.[0].MatchLine)

[<Fact>]
let ``search returns empty for no matches`` () =
    let content = "* Headline\nSome body text.\n"
    let doc = parseDoc content

    let results =
        Search.searchDocs "nonexistent" [ ("test.org", doc, content) ] |> unwrap

    Assert.Empty(results)

[<Fact>]
let ``search deduplicates multiple matches in same headline`` () =
    let content = "* Headline\nfoo bar foo baz\nfoo again\n"
    let doc = parseDoc content
    let results = Search.searchDocs "foo" [ ("test.org", doc, content) ] |> unwrap
    Assert.Equal(2, results.Length)
    Assert.True(results |> List.forall (fun r -> r.Headline.Value.Title = "Headline"))

[<Fact>]
let ``search matches in file-level content before first headline`` () =
    let content = "Some preamble with keyword.\n\n* Headline\nBody\n"
    let doc = parseDoc content
    let results = Search.searchDocs "keyword" [ ("test.org", doc, content) ] |> unwrap
    Assert.Equal(1, results.Length)
    Assert.True(results.[0].Headline.IsNone)

[<Fact>]
let ``search across multiple files`` () =
    let content1 = "* A\nfoo here\n"
    let content2 = "* B\nfoo there\n"
    let doc1 = parseDoc content1
    let doc2 = parseDoc content2

    let results =
        Search.searchDocs "foo" [ ("a.org", doc1, content1); ("b.org", doc2, content2) ]
        |> unwrap

    Assert.Equal(2, results.Length)
    let files = results |> List.map (fun r -> r.File)
    Assert.Contains("a.org", files)
    Assert.Contains("b.org", files)

[<Fact>]
let ``search returns correct line numbers`` () =
    let content = "* Headline\nLine one\nLine two has target\nLine three\n"
    let doc = parseDoc content
    let results = Search.searchDocs "target" [ ("test.org", doc, content) ] |> unwrap
    Assert.Equal(1, results.Length)
    Assert.Equal(3, results.[0].LineNumber)

[<Fact>]
let ``search with invalid regex returns Error`` () =
    let content = "* Headline\nSome text\n"
    let doc = parseDoc content
    let result = Search.searchDocs "foo(bar" [ ("test.org", doc, content) ]

    match result with
    | Result.Error msg -> Assert.Contains("Invalid regex", msg)
    | Result.Ok _ -> failwith "Expected Error for invalid regex"
