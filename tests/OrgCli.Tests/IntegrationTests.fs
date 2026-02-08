module IntegrationTests

open System
open System.IO
open Xunit
open OrgCli.Roam
open OrgCli.Org

/// Create a temporary directory for tests
let createTempDir () =
    let path =
        Path.Combine(Path.GetTempPath(), "org-roam-test-" + Guid.NewGuid().ToString())

    Directory.CreateDirectory(path) |> ignore
    path

/// Clean up temporary directory
let cleanupTempDir (path: string) =
    if Directory.Exists(path) then
        Directory.Delete(path, true)

[<Fact>]
let ``Sync creates database from org files`` () =
    let tempDir = createTempDir ()

    try
        let orgContent =
            ":PROPERTIES:\n:ID: test-node-001\n:END:\n#+title: Test Note\n\nSome content here.\n"

        File.WriteAllText(Path.Combine(tempDir, "test.org"), orgContent)

        let dbPath = Path.Combine(tempDir, ".org.db")
        use db = new Database.OrgRoamDb(dbPath)
        let errors = Sync.sync db tempDir false
        Assert.Empty(errors)

        let node = db.GetNode("test-node-001")
        Assert.True(node.IsSome)
        Assert.Equal("Test Note", node.Value.Title)
    finally
        cleanupTempDir tempDir

[<Fact>]
let ``Sync handles multiple files with links`` () =
    let tempDir = createTempDir ()

    try
        let file1 =
            ":PROPERTIES:\n:ID: node-a\n:END:\n#+title: Node A\n\nThis links to [[id:node-b][Node B]].\n"

        let file2 = ":PROPERTIES:\n:ID: node-b\n:END:\n#+title: Node B\n\nContent here.\n"
        File.WriteAllText(Path.Combine(tempDir, "a.org"), file1)
        File.WriteAllText(Path.Combine(tempDir, "b.org"), file2)

        let dbPath = Path.Combine(tempDir, ".org.db")
        use db = new Database.OrgRoamDb(dbPath)
        let errors = Sync.sync db tempDir false
        Assert.Empty(errors)

        Assert.True((db.GetNode("node-a")).IsSome)
        Assert.True((db.GetNode("node-b")).IsSome)

        let links = db.GetLinksFrom("node-a")
        Assert.Equal(1, links.Length)
        Assert.Equal("node-b", links.[0].Dest)
        Assert.Equal("id", links.[0].Type)

        let backlinks = Sync.getBacklinks db "node-b"
        Assert.Equal(1, backlinks.Length)
        Assert.Equal("node-a", backlinks.[0].SourceNode.Id)
    finally
        cleanupTempDir tempDir

[<Fact>]
let ``Sync handles file modifications`` () =
    let tempDir = createTempDir ()

    try
        let filePath = Path.Combine(tempDir, "test.org")
        let original = ":PROPERTIES:\n:ID: node-1\n:END:\n#+title: Original Title\n"
        File.WriteAllText(filePath, original)

        let dbPath = Path.Combine(tempDir, ".org.db")
        use db = new Database.OrgRoamDb(dbPath)
        let errors = Sync.sync db tempDir false
        Assert.Empty(errors)

        let node1 = db.GetNode("node-1")
        Assert.Equal("Original Title", node1.Value.Title)

        let updated = ":PROPERTIES:\n:ID: node-1\n:END:\n#+title: Updated Title\n"
        File.WriteAllText(filePath, updated)

        let errors2 = Sync.sync db tempDir false
        Assert.Empty(errors2)

        let node2 = db.GetNode("node-1")
        Assert.Equal("Updated Title", node2.Value.Title)
    finally
        cleanupTempDir tempDir

[<Fact>]
let ``Sync handles file deletion`` () =
    let tempDir = createTempDir ()

    try
        let filePath = Path.Combine(tempDir, "test.org")
        let content = ":PROPERTIES:\n:ID: node-to-delete\n:END:\n#+title: Will Be Deleted\n"
        File.WriteAllText(filePath, content)

        let dbPath = Path.Combine(tempDir, ".org.db")
        use db = new Database.OrgRoamDb(dbPath)
        let errors = Sync.sync db tempDir false
        Assert.Empty(errors)

        Assert.True((db.GetNode("node-to-delete")).IsSome)

        File.Delete(filePath)

        let errors2 = Sync.sync db tempDir false
        Assert.Empty(errors2)

        Assert.True((db.GetNode("node-to-delete")).IsNone)
    finally
        cleanupTempDir tempDir

[<Fact>]
let ``Sync handles headline nodes`` () =
    let tempDir = createTempDir ()

    try
        let orgContent =
            ":PROPERTIES:\n:ID: file-node\n:END:\n#+title: File Level\n\n* Headline Node\n:PROPERTIES:\n:ID: headline-node\n:ROAM_ALIASES: \"Headline Alias\"\n:END:\n\nContent under headline.\n\n** Nested Headline\n:PROPERTIES:\n:ID: nested-node\n:END:\n\nNested content.\n"

        File.WriteAllText(Path.Combine(tempDir, "test.org"), orgContent)

        let dbPath = Path.Combine(tempDir, ".org.db")
        use db = new Database.OrgRoamDb(dbPath)
        let errors = Sync.sync db tempDir false
        Assert.Empty(errors)

        let fileNode = db.GetNode("file-node")
        let headlineNode = db.GetNode("headline-node")
        let nestedNode = db.GetNode("nested-node")

        Assert.True(fileNode.IsSome)
        Assert.True(headlineNode.IsSome)
        Assert.True(nestedNode.IsSome)

        Assert.Equal(0, fileNode.Value.Level)
        Assert.Equal(1, headlineNode.Value.Level)
        Assert.Equal(2, nestedNode.Value.Level)

        let aliases = db.GetAliases("headline-node")
        Assert.Contains("Headline Alias", aliases)
    finally
        cleanupTempDir tempDir

[<Fact>]
let ``Sync respects ROAM_EXCLUDE`` () =
    let tempDir = createTempDir ()

    try
        let orgContent =
            ":PROPERTIES:\n:ID: included-node\n:END:\n#+title: Included\n\n* Excluded Headline\n:PROPERTIES:\n:ID: excluded-node\n:ROAM_EXCLUDE: t\n:END:\n"

        File.WriteAllText(Path.Combine(tempDir, "test.org"), orgContent)

        let dbPath = Path.Combine(tempDir, ".org.db")
        use db = new Database.OrgRoamDb(dbPath)
        let errors = Sync.sync db tempDir false
        Assert.Empty(errors)

        Assert.True((db.GetNode("included-node")).IsSome)

        Assert.True((db.GetNode("excluded-node")).IsNone)
    finally
        cleanupTempDir tempDir

[<Fact>]
let ``NodeOperations creates valid file node`` () =
    let tempDir = createTempDir ()

    try
        let options =
            { NodeOperations.defaultCreateOptions "Test Create" with
                Tags = [ "tag1"; "tag2" ]
                Aliases = [ "Alias 1" ] }

        let filePath = NodeOperations.createFileNode tempDir options

        Assert.True(File.Exists(filePath))

        let doc = Document.parseFile filePath
        Assert.Equal(Some "Test Create", Types.tryGetTitle doc.Keywords)
        Assert.True((Types.tryGetId doc.FileProperties).IsSome)

        let tags = Types.getFileTags doc.Keywords
        Assert.Contains("tag1", tags)
        Assert.Contains("tag2", tags)
    finally
        cleanupTempDir tempDir

[<Fact>]
let ``Find by title works`` () =
    let tempDir = createTempDir ()

    try
        let content = ":PROPERTIES:\n:ID: find-me\n:END:\n#+title: Unique Title Here\n"
        File.WriteAllText(Path.Combine(tempDir, "test.org"), content)

        let dbPath = Path.Combine(tempDir, ".org.db")
        use db = new Database.OrgRoamDb(dbPath)
        let errors = Sync.sync db tempDir false
        Assert.Empty(errors)

        let found = Sync.findNodeByTitleOrAlias db "Unique Title Here"
        Assert.True(found.IsSome)
        Assert.Equal("find-me", found.Value.Id)
    finally
        cleanupTempDir tempDir

[<Fact>]
let ``Find by alias works`` () =
    let tempDir = createTempDir ()

    try
        let content =
            ":PROPERTIES:\n:ID: aliased-node\n:ROAM_ALIASES: \"My Alias\"\n:END:\n#+title: Real Title\n"

        File.WriteAllText(Path.Combine(tempDir, "test.org"), content)

        let dbPath = Path.Combine(tempDir, ".org.db")
        use db = new Database.OrgRoamDb(dbPath)
        let errors = Sync.sync db tempDir false
        Assert.Empty(errors)

        let found = Sync.findNodeByTitleOrAlias db "My Alias"
        Assert.True(found.IsSome)
        Assert.Equal("aliased-node", found.Value.Id)
    finally
        cleanupTempDir tempDir

[<Fact>]
let ``Find by tag works`` () =
    let tempDir = createTempDir ()

    try
        let tagged =
            ":PROPERTIES:\n:ID: tagged-node\n:END:\n#+title: Tagged Note\n#+filetags: :project:important:\n"

        let untagged = ":PROPERTIES:\n:ID: untagged-node\n:END:\n#+title: Untagged Note\n"
        File.WriteAllText(Path.Combine(tempDir, "tagged.org"), tagged)
        File.WriteAllText(Path.Combine(tempDir, "untagged.org"), untagged)

        let dbPath = Path.Combine(tempDir, ".org.db")
        use db = new Database.OrgRoamDb(dbPath)
        let errors = Sync.sync db tempDir false
        Assert.Empty(errors)

        let projectNodes = Sync.findNodesByTag db "project"
        Assert.Equal(1, projectNodes.Length)
        Assert.Equal("tagged-node", projectNodes.[0].Id)
    finally
        cleanupTempDir tempDir
