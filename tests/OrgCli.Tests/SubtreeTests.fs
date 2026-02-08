module SubtreeTests

open Xunit
open OrgCli.Org

[<Fact>]
let ``extractSubtree returns single headline with no children`` () =
    let content = "* Headline One\nBody text\n* Headline Two\nMore text\n"
    let result = Subtree.extractSubtree content 0L
    Assert.Equal("* Headline One\nBody text", result)

[<Fact>]
let ``extractSubtree returns headline with deeper children`` () =
    let content =
        "* Parent\nBody\n** Child One\nChild body\n** Child Two\nAnother body\n* Sibling\n"

    let result = Subtree.extractSubtree content 0L
    Assert.Equal("* Parent\nBody\n** Child One\nChild body\n** Child Two\nAnother body", result)

[<Fact>]
let ``extractSubtree from middle of file`` () =
    let content =
        "* First\nBody\n* Second\nSecond body\n** Sub\nSub body\n* Third\nThird body\n"
    // Position of "* Second"
    let pos = content.IndexOf("* Second") |> int64
    let result = Subtree.extractSubtree content pos
    Assert.Equal("* Second\nSecond body\n** Sub\nSub body", result)

[<Fact>]
let ``extractSubtree at end of file`` () =
    let content = "* First\nBody\n* Last\nLast body\n"
    let pos = content.IndexOf("* Last") |> int64
    let result = Subtree.extractSubtree content pos
    Assert.Equal("* Last\nLast body", result)

[<Fact>]
let ``removeSubtree removes from beginning`` () =
    let content = "* First\nBody\n* Second\nSecond body\n"
    let result = Subtree.removeSubtree content 0L
    Assert.Equal("* Second\nSecond body\n", result)

[<Fact>]
let ``removeSubtree removes from middle`` () =
    let content =
        "* First\nBody\n* Second\nSecond body\n** Sub\nSub body\n* Third\nThird body\n"

    let pos = content.IndexOf("* Second") |> int64
    let result = Subtree.removeSubtree content pos
    Assert.Equal("* First\nBody\n* Third\nThird body\n", result)

[<Fact>]
let ``removeSubtree removes last headline`` () =
    let content = "* First\nBody\n* Last\nLast body\n"
    let pos = content.IndexOf("* Last") |> int64
    let result = Subtree.removeSubtree content pos
    Assert.Equal("* First\nBody\n", result)

[<Fact>]
let ``adjustLevels increases headline levels`` () =
    let subtree = "* Parent\nBody\n** Child\nChild body\n"
    let result = Subtree.adjustLevels subtree 1
    Assert.Equal("** Parent\nBody\n*** Child\nChild body\n", result)

[<Fact>]
let ``adjustLevels decreases headline levels`` () =
    let subtree = "** Parent\nBody\n*** Child\nChild body\n"
    let result = Subtree.adjustLevels subtree -1
    Assert.Equal("* Parent\nBody\n** Child\nChild body\n", result)

[<Fact>]
let ``adjustLevels does not go below level 1`` () =
    let subtree = "* Parent\nBody\n** Child\nChild body\n"
    let result = Subtree.adjustLevels subtree -1
    // Level 1 stays at 1, level 2 goes to 1
    Assert.Equal("* Parent\nBody\n* Child\nChild body\n", result)

[<Fact>]
let ``getHeadlineBody returns body without child headlines`` () =
    let content = "* Parent\nBody line 1\nBody line 2\n** Child\nChild body\n"
    let result = Subtree.getHeadlineBody content 0L
    Assert.Equal("Body line 1\nBody line 2", result)

[<Fact>]
let ``getHeadlineBody with no body`` () =
    let content = "* Parent\n** Child\nChild body\n"
    let result = Subtree.getHeadlineBody content 0L
    Assert.Equal("", result)

[<Fact>]
let ``insertSubtreeAsChild appends as child of parent`` () =
    let content = "* Parent\nBody\n* Sibling\n"
    let subtree = "* Inserted\nInserted body\n"
    let result = Subtree.insertSubtreeAsChild content 0L subtree
    // Should be inserted as ** (one deeper than parent) before sibling
    Assert.Contains("** Inserted", result)
    Assert.Contains("Inserted body", result)
    // Parent should still be there
    Assert.StartsWith("* Parent", result)
    // Sibling should still be there
    Assert.Contains("* Sibling", result)

[<Fact>]
let ``insertSubtreeAsChild adjusts levels of inserted subtree`` () =
    let content = "** Parent\nBody\n"
    let subtree = "* Inserted\n** Sub\n"
    let result = Subtree.insertSubtreeAsChild content 0L subtree
    Assert.Contains("*** Inserted", result)
    Assert.Contains("**** Sub", result)

[<Fact>]
let ``getSubtreeRange returns correct range`` () =
    let content = "* First\nBody\n* Second\nSecond body\n* Third\n"
    let pos = content.IndexOf("* Second") |> int64
    let (startIdx, endIdx) = Subtree.getSubtreeRange content pos
    Assert.Equal(content.IndexOf("* Second"), startIdx)
    Assert.Equal(content.IndexOf("* Third"), endIdx)

[<Fact>]
let ``getSubtreeRange at end of file`` () =
    let content = "* First\n* Last\nBody\n"
    let pos = content.IndexOf("* Last") |> int64
    let (_, endIdx) = Subtree.getSubtreeRange content pos
    Assert.Equal(content.Length, endIdx)

[<Fact>]
let ``extractSubtree with file preamble`` () =
    let content = "#+title: My File\n\n* Headline\nBody\n"
    let pos = content.IndexOf("* Headline") |> int64
    let result = Subtree.extractSubtree content pos
    Assert.Equal("* Headline\nBody", result)

[<Fact>]
let ``getSubtreeRange returns empty range when pos is not at headline`` () =
    let content = "Some text\n* Headline\nBody\n"
    let (s, e) = Subtree.getSubtreeRange content 0L
    Assert.Equal(0, s)
    Assert.Equal(0, e)

[<Fact>]
let ``extractSubtree returns empty string when pos is not at headline`` () =
    let content = "Some text\n* Headline\nBody\n"
    let result = Subtree.extractSubtree content 0L
    Assert.Equal("", result)

[<Fact>]
let ``adjustLevels with delta 0 returns unchanged content`` () =
    let subtree = "* Parent\nBody\n** Child\n"
    let result = Subtree.adjustLevels subtree 0
    Assert.Equal(subtree, result)

[<Fact>]
let ``insertSubtreeAsChild when content lacks trailing newline`` () =
    let content = "* Parent\nBody"
    let subtree = "* Inserted\n"
    let result = Subtree.insertSubtreeAsChild content 0L subtree
    Assert.Contains("** Inserted", result)
    Assert.Contains("Body", result)
    Assert.StartsWith("* Parent", result)

[<Fact>]
let ``removeSubtree returns content unchanged when pos is not at headline`` () =
    let content = "Some text\n* Headline\nBody\n"
    let result = Subtree.removeSubtree content 0L
    Assert.Equal(content, result)

[<Fact>]
let ``getHeadlineBody returns empty when pos is not at headline`` () =
    let content = "Some text\n* Headline\nBody\n"
    let result = Subtree.getHeadlineBody content 0L
    Assert.Equal("", result)
