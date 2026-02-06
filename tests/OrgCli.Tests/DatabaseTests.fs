module DatabaseTests

open System
open System.IO
open Xunit
open OrgCli.Roam

[<Fact>]
let ``Database initializes with correct version`` () =
    let dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".db")
    try
        use db = new Database.OrgRoamDb(dbPath)
        db.Initialize()
        // Version should be 20
        Assert.True(File.Exists(dbPath))
    finally
        if File.Exists(dbPath) then File.Delete(dbPath)

[<Fact>]
let ``Insert and retrieve file`` () =
    let dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".db")
    try
        use db = new Database.OrgRoamDb(dbPath)
        db.Initialize()

        let file = {
            DbFile.File = "/test/file.org"
            Title = Some "Test Title"
            Hash = "abc123"
            Atime = DateTime(2024, 1, 15, 10, 30, 0)
            Mtime = DateTime(2024, 1, 15, 10, 30, 0)
        }
        db.InsertFile(file)

        let hash = db.GetFileHash("/test/file.org")
        Assert.Equal(Some "abc123", hash)
    finally
        if File.Exists(dbPath) then File.Delete(dbPath)

[<Fact>]
let ``Insert and retrieve node`` () =
    let dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".db")
    try
        use db = new Database.OrgRoamDb(dbPath)
        db.Initialize()

        // Must insert file first due to foreign key
        db.InsertFile({
            File = "/test/file.org"
            Title = Some "Test"
            Hash = "abc"
            Atime = DateTime.UtcNow
            Mtime = DateTime.UtcNow
        })

        let node = {
            DbNode.Id = "test-id-123"
            File = "/test/file.org"
            Level = 0
            Pos = 1
            Todo = Some "TODO"
            Priority = Some "A"
            Scheduled = None
            Deadline = None
            Title = "Test Node"
            Properties = """(("ID" . "test-id-123"))"""
            Olp = "nil"
        }
        db.InsertNode(node)

        let retrieved = db.GetNode("test-id-123")
        Assert.True(retrieved.IsSome)
        Assert.Equal("Test Node", retrieved.Value.Title)
        Assert.Equal(Some "TODO", retrieved.Value.Todo)
    finally
        if File.Exists(dbPath) then File.Delete(dbPath)

[<Fact>]
let ``Foreign key cascade deletes nodes when file deleted`` () =
    let dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".db")
    try
        use db = new Database.OrgRoamDb(dbPath)
        db.Initialize()

        db.InsertFile({
            File = "/test/file.org"
            Title = Some "Test"
            Hash = "abc"
            Atime = DateTime.UtcNow
            Mtime = DateTime.UtcNow
        })

        db.InsertNode({
            Id = "node-1"
            File = "/test/file.org"
            Level = 0
            Pos = 1
            Todo = None
            Priority = None
            Scheduled = None
            Deadline = None
            Title = "Node 1"
            Properties = "nil"
            Olp = "nil"
        })

        // Verify node exists
        Assert.True((db.GetNode("node-1")).IsSome)

        // Delete file
        db.ClearFile("/test/file.org")

        // Node should be gone too
        Assert.True((db.GetNode("node-1")).IsNone)
    finally
        if File.Exists(dbPath) then File.Delete(dbPath)

[<Fact>]
let ``Tags are stored and retrieved correctly`` () =
    let dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".db")
    try
        use db = new Database.OrgRoamDb(dbPath)
        db.Initialize()

        db.InsertFile({
            File = "/test/file.org"
            Title = Some "Test"
            Hash = "abc"
            Atime = DateTime.UtcNow
            Mtime = DateTime.UtcNow
        })

        db.InsertNode({
            Id = "node-1"
            File = "/test/file.org"
            Level = 0
            Pos = 1
            Todo = None
            Priority = None
            Scheduled = None
            Deadline = None
            Title = "Node"
            Properties = "nil"
            Olp = "nil"
        })

        db.InsertTag({ NodeId = "node-1"; Tag = "tag1" })
        db.InsertTag({ NodeId = "node-1"; Tag = "tag2" })

        let tags = db.GetTags("node-1")
        Assert.Equal(2, tags.Length)
        Assert.Contains("tag1", tags)
        Assert.Contains("tag2", tags)
    finally
        if File.Exists(dbPath) then File.Delete(dbPath)

[<Fact>]
let ``Elisp alist parsing works correctly`` () =
    let input = """(("ID" . "abc-123") ("CATEGORY" . "test"))"""
    let result = Domain.parseElispAlist input

    Assert.Equal(2, result.Length)
    Assert.Contains(("ID", "abc-123"), result)
    Assert.Contains(("CATEGORY", "test"), result)

[<Fact>]
let ``Elisp alist formatting works correctly`` () =
    let input = [("ID", "abc-123"); ("CATEGORY", "test")]
    let result = Domain.formatElispAlist input

    Assert.Contains("\"ID\"", result)
    Assert.Contains("\"abc-123\"", result)
    Assert.Contains("\"CATEGORY\"", result)
    Assert.Contains("\"test\"", result)

[<Fact>]
let ``Elisp list parsing works correctly`` () =
    let input = """("Parent" "Child")"""
    let result = Domain.parseElispList input

    Assert.Equal(2, result.Length)
    Assert.Equal("Parent", result.[0])
    Assert.Equal("Child", result.[1])

[<Fact>]
let ``Elisp nil parsing returns empty list`` () =
    let result = Domain.parseElispList "nil"
    Assert.Empty(result)

[<Fact>]
let ``Links are stored and retrieved correctly`` () =
    let dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".db")
    try
        use db = new Database.OrgRoamDb(dbPath)
        db.Initialize()

        db.InsertFile({
            File = "/test/file.org"
            Title = Some "Test"
            Hash = "abc"
            Atime = DateTime.UtcNow
            Mtime = DateTime.UtcNow
        })

        db.InsertNode({
            Id = "source-node"
            File = "/test/file.org"
            Level = 0
            Pos = 1
            Todo = None
            Priority = None
            Scheduled = None
            Deadline = None
            Title = "Source"
            Properties = "nil"
            Olp = "nil"
        })

        db.InsertLink({
            Pos = 100
            Source = "source-node"
            Dest = "target-node"
            Type = "id"
            Properties = "nil"
        })

        let linksFrom = db.GetLinksFrom("source-node")
        Assert.Equal(1, linksFrom.Length)
        Assert.Equal("target-node", linksFrom.[0].Dest)

        let linksTo = db.GetLinksTo("target-node")
        Assert.Equal(1, linksTo.Length)
        Assert.Equal("source-node", linksTo.[0].Source)
    finally
        if File.Exists(dbPath) then File.Delete(dbPath)
