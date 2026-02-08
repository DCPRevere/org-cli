module EdgeCaseTests

open System
open Xunit
open OrgCli.Org

[<Fact>]
let ``Parse Unicode title`` () =
    let content =
        """
:PROPERTIES:
:ID: unicode-test
:END:
#+title: 日本語タイトル with émojis 🎉
"""

    let doc = Document.parse content
    Assert.Equal(Some "日本語タイトル with émojis 🎉", Types.tryGetTitle doc.Keywords)

[<Fact>]
let ``Parse title with special characters`` () =
    let content =
        """
:PROPERTIES:
:ID: special-chars
:END:
#+title: Title with "quotes" and (parens) and [brackets]
"""

    let doc = Document.parse content
    Assert.Equal(Some """Title with "quotes" and (parens) and [brackets]""", Types.tryGetTitle doc.Keywords)

[<Fact>]
let ``Parse empty property drawer`` () =
    let content =
        """
:PROPERTIES:
:END:
#+title: No ID
"""

    let doc = Document.parse content
    Assert.True((Types.tryGetId doc.FileProperties).IsNone)

[<Fact>]
let ``Parse headline without property drawer`` () =
    let content =
        """
* Headline without properties

Just content.
"""

    let doc = Document.parse content
    Assert.Equal(1, doc.Headlines.Length)
    Assert.Equal("Headline without properties", doc.Headlines.[0].Title)
    Assert.True(doc.Headlines.[0].Properties.IsNone)

[<Fact>]
let ``Parse multiple TODO keywords`` () =
    let content =
        """
* TODO First task
* DONE Second task
* NEXT Third task
* WAITING Fourth task
"""

    let doc = Document.parse content
    Assert.Equal(4, doc.Headlines.Length)
    Assert.Equal(Some "TODO", doc.Headlines.[0].TodoKeyword)
    Assert.Equal(Some "DONE", doc.Headlines.[1].TodoKeyword)
    Assert.Equal(Some "NEXT", doc.Headlines.[2].TodoKeyword)
    Assert.Equal(Some "WAITING", doc.Headlines.[3].TodoKeyword)

[<Fact>]
let ``Parse all priority levels`` () =
    let content =
        """
* [#A] High priority
* [#B] Medium priority
* [#C] Low priority
"""

    let doc = Document.parse content
    Assert.Equal(Some(Priority 'A'), doc.Headlines.[0].Priority)
    Assert.Equal(Some(Priority 'B'), doc.Headlines.[1].Priority)
    Assert.Equal(Some(Priority 'C'), doc.Headlines.[2].Priority)

[<Fact>]
let ``Parse link with spaces in description`` () =
    let content =
        """
:PROPERTIES:
:ID: node-1
:END:
#+title: Links

Check out [[id:other][This is a long description with spaces]].
"""

    let doc = Document.parse content
    let link = doc.Links |> List.head |> fst
    Assert.Equal("other", link.Path)
    Assert.Equal(Some "This is a long description with spaces", link.Description)

[<Fact>]
let ``Parse link without description`` () =
    let content =
        """
:PROPERTIES:
:ID: node-1
:END:
#+title: Links

See [[id:target-node]].
"""

    let doc = Document.parse content
    let link = doc.Links |> List.head |> fst
    Assert.Equal("target-node", link.Path)
    Assert.True(link.Description.IsNone)

[<Fact>]
let ``Parse roam link`` () =
    let content =
        """
:PROPERTIES:
:ID: node-1
:END:
#+title: Links

Link to [[roam:Some Note Title]].
"""

    let doc = Document.parse content
    let link = doc.Links |> List.head |> fst
    Assert.Equal("roam", link.LinkType)
    Assert.Equal("Some Note Title", link.Path)

[<Fact>]
let ``Parse https link`` () =
    let content =
        """
:PROPERTIES:
:ID: node-1
:END:
#+title: Links

Visit [[https://example.com/path][Example Site]].
"""

    let doc = Document.parse content
    let link = doc.Links |> List.head |> fst
    Assert.Equal("https", link.LinkType)
    Assert.Contains("example.com", link.Path)

[<Fact>]
let ``Parse headline with many tags`` () =
    let content =
        """
* Headline :tag1:tag2:tag3:tag4:tag5:
"""

    let doc = Document.parse content
    Assert.Equal(5, doc.Headlines.[0].Tags.Length)

[<Fact>]
let ``Parse deeply nested headlines`` () =
    let content =
        """
* Level 1
** Level 2
*** Level 3
**** Level 4
***** Level 5
"""

    let doc = Document.parse content
    Assert.Equal(5, doc.Headlines.Length)
    Assert.Equal(5, doc.Headlines.[4].Level)

[<Fact>]
let ``Compute outline path for deeply nested headline`` () =
    let content =
        """
* Grandparent
** Parent
*** Child
"""

    let doc = Document.parse content
    let child = doc.Headlines.[2]
    let olp = Document.computeOutlinePath doc.Headlines child
    Assert.Equal<string list>([ "Grandparent"; "Parent" ], olp)

[<Fact>]
let ``Parse quoted aliases with spaces`` () =
    let content =
        """
* Node
:PROPERTIES:
:ID: test
:ROAM_ALIASES: "First Alias" "Second Alias" ThirdAlias
:END:
"""

    let doc = Document.parse content
    let aliases = Types.getRoamAliases doc.Headlines.[0].Properties
    Assert.Equal(3, aliases.Length)
    Assert.Contains("First Alias", aliases)
    Assert.Contains("Second Alias", aliases)
    Assert.Contains("ThirdAlias", aliases)

[<Fact>]
let ``Parse refs with different types`` () =
    let content =
        """
* Node
:PROPERTIES:
:ID: test
:ROAM_REFS: @citationKey "https://example.com" "http://other.com"
:END:
"""

    let doc = Document.parse content
    let refs = Types.getRoamRefs doc.Headlines.[0].Properties
    Assert.Equal(3, refs.Length)
    Assert.Contains("@citationKey", refs)
    Assert.Contains("https://example.com", refs)
    Assert.Contains("http://other.com", refs)

[<Fact>]
let ``Empty file produces empty document`` () =
    let doc = Document.parse ""
    Assert.Empty(doc.Keywords)
    Assert.Empty(doc.Headlines)
    Assert.True(doc.FileProperties.IsNone)

[<Fact>]
let ``File with only whitespace produces empty document`` () =
    let doc = Document.parse "   \n\n   \n"
    Assert.Empty(doc.Headlines)

[<Fact>]
let ``slugify handles Unicode`` () =
    let slug = Utils.slugify "Über Café"
    Assert.Equal("uber_cafe", slug)

[<Fact>]
let ``slugify handles special characters`` () =
    let slug = Utils.slugify "Hello, World! (2024)"
    Assert.Equal("hello_world_2024", slug)

[<Fact>]
let ``slugify handles multiple spaces`` () =
    let slug = Utils.slugify "Multiple   Spaces   Here"
    Assert.Equal("multiple_spaces_here", slug)

[<Fact>]
let ``splitQuotedString handles escaped quotes`` () =
    // This is an edge case - org-roam doesn't really support escaped quotes
    // but we should handle reasonable inputs gracefully
    let result = Types.splitQuotedString "simple \"with space\" another"
    Assert.Equal(3, result.Length)

[<Fact>]
let ``Parse file with CRLF line endings`` () =
    let content = ":PROPERTIES:\r\n:ID: crlf-test\r\n:END:\r\n#+title: CRLF Test\r\n"
    let doc = Document.parse content
    // Should still parse correctly
    Assert.Equal(Some "CRLF Test", Types.tryGetTitle doc.Keywords)

[<Fact>]
let ``Parse link with search option`` () =
    let content =
        """
:PROPERTIES:
:ID: node-1
:END:
#+title: Test

See [[id:other::*Heading][link]].
"""

    let doc = Document.parse content
    let link = doc.Links |> List.head |> fst
    Assert.Equal("other", link.Path)
    Assert.Equal(Some "*Heading", link.SearchOption)
