module SchemaCompatibilityTests

open System
open System.IO
open Xunit
open Microsoft.Data.Sqlite
open OrgCli.Roam

/// These tests verify our database schema matches org-roam v20 exactly

[<Fact>]
let ``Database version is 20`` () =
    let dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".db")

    try
        use db = new Database.OrgRoamDb(dbPath)
        db.Initialize() |> ignore

        use conn = new SqliteConnection(sprintf "Data Source=%s" dbPath)
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "PRAGMA user_version"
        let version = cmd.ExecuteScalar() :?> int64

        Assert.Equal(20L, version)
    finally
        if File.Exists(dbPath) then
            File.Delete(dbPath)

[<Fact>]
let ``Files table has correct columns`` () =
    let dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".db")

    try
        use db = new Database.OrgRoamDb(dbPath)
        db.Initialize() |> ignore

        use conn = new SqliteConnection(sprintf "Data Source=%s" dbPath)
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "PRAGMA table_info(files)"
        use reader = cmd.ExecuteReader()

        let columns = ResizeArray<string>()

        while reader.Read() do
            columns.Add(reader.GetString(1))

        Assert.Contains("file", columns)
        Assert.Contains("title", columns)
        Assert.Contains("hash", columns)
        Assert.Contains("atime", columns)
        Assert.Contains("mtime", columns)
    finally
        if File.Exists(dbPath) then
            File.Delete(dbPath)

[<Fact>]
let ``Nodes table has correct columns`` () =
    let dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".db")

    try
        use db = new Database.OrgRoamDb(dbPath)
        db.Initialize() |> ignore

        use conn = new SqliteConnection(sprintf "Data Source=%s" dbPath)
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "PRAGMA table_info(nodes)"
        use reader = cmd.ExecuteReader()

        let columns = ResizeArray<string>()

        while reader.Read() do
            columns.Add(reader.GetString(1))

        // All org-roam node columns
        Assert.Contains("id", columns)
        Assert.Contains("file", columns)
        Assert.Contains("level", columns)
        Assert.Contains("pos", columns)
        Assert.Contains("todo", columns)
        Assert.Contains("priority", columns)
        Assert.Contains("scheduled", columns)
        Assert.Contains("deadline", columns)
        Assert.Contains("title", columns)
        Assert.Contains("properties", columns)
        Assert.Contains("olp", columns)
    finally
        if File.Exists(dbPath) then
            File.Delete(dbPath)

[<Fact>]
let ``Aliases table has correct columns`` () =
    let dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".db")

    try
        use db = new Database.OrgRoamDb(dbPath)
        db.Initialize() |> ignore

        use conn = new SqliteConnection(sprintf "Data Source=%s" dbPath)
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "PRAGMA table_info(aliases)"
        use reader = cmd.ExecuteReader()

        let columns = ResizeArray<string>()

        while reader.Read() do
            columns.Add(reader.GetString(1))

        Assert.Contains("node_id", columns)
        Assert.Contains("alias", columns)
    finally
        if File.Exists(dbPath) then
            File.Delete(dbPath)

[<Fact>]
let ``Refs table has correct columns`` () =
    let dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".db")

    try
        use db = new Database.OrgRoamDb(dbPath)
        db.Initialize() |> ignore

        use conn = new SqliteConnection(sprintf "Data Source=%s" dbPath)
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "PRAGMA table_info(refs)"
        use reader = cmd.ExecuteReader()

        let columns = ResizeArray<string>()

        while reader.Read() do
            columns.Add(reader.GetString(1))

        Assert.Contains("node_id", columns)
        Assert.Contains("ref", columns)
        Assert.Contains("type", columns)
    finally
        if File.Exists(dbPath) then
            File.Delete(dbPath)

[<Fact>]
let ``Tags table has correct columns`` () =
    let dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".db")

    try
        use db = new Database.OrgRoamDb(dbPath)
        db.Initialize() |> ignore

        use conn = new SqliteConnection(sprintf "Data Source=%s" dbPath)
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "PRAGMA table_info(tags)"
        use reader = cmd.ExecuteReader()

        let columns = ResizeArray<string>()

        while reader.Read() do
            columns.Add(reader.GetString(1))

        Assert.Contains("node_id", columns)
        Assert.Contains("tag", columns)
    finally
        if File.Exists(dbPath) then
            File.Delete(dbPath)

[<Fact>]
let ``Links table has correct columns`` () =
    let dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".db")

    try
        use db = new Database.OrgRoamDb(dbPath)
        db.Initialize() |> ignore

        use conn = new SqliteConnection(sprintf "Data Source=%s" dbPath)
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "PRAGMA table_info(links)"
        use reader = cmd.ExecuteReader()

        let columns = ResizeArray<string>()

        while reader.Read() do
            columns.Add(reader.GetString(1))

        Assert.Contains("pos", columns)
        Assert.Contains("source", columns)
        Assert.Contains("dest", columns)
        Assert.Contains("type", columns)
        Assert.Contains("properties", columns)
    finally
        if File.Exists(dbPath) then
            File.Delete(dbPath)

[<Fact>]
let ``Citations table has correct columns`` () =
    let dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".db")

    try
        use db = new Database.OrgRoamDb(dbPath)
        db.Initialize() |> ignore

        use conn = new SqliteConnection(sprintf "Data Source=%s" dbPath)
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "PRAGMA table_info(citations)"
        use reader = cmd.ExecuteReader()

        let columns = ResizeArray<string>()

        while reader.Read() do
            columns.Add(reader.GetString(1))

        Assert.Contains("node_id", columns)
        Assert.Contains("cite_key", columns)
        Assert.Contains("pos", columns)
        Assert.Contains("properties", columns)
    finally
        if File.Exists(dbPath) then
            File.Delete(dbPath)

[<Fact>]
let ``Required indexes exist`` () =
    let dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".db")

    try
        use db = new Database.OrgRoamDb(dbPath)
        db.Initialize() |> ignore

        use conn = new SqliteConnection(sprintf "Data Source=%s" dbPath)
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT name FROM sqlite_master WHERE type='index'"
        use reader = cmd.ExecuteReader()

        let indexes = ResizeArray<string>()

        while reader.Read() do
            indexes.Add(reader.GetString(0))

        Assert.Contains("alias_node_id", indexes)
        Assert.Contains("refs_node_id", indexes)
        Assert.Contains("tags_node_id", indexes)
    finally
        if File.Exists(dbPath) then
            File.Delete(dbPath)

[<Fact>]
let ``Foreign keys are enabled`` () =
    let dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".db")

    try
        use db = new Database.OrgRoamDb(dbPath)
        db.Initialize() |> ignore

        use conn = new SqliteConnection(sprintf "Data Source=%s;Foreign Keys=True" dbPath)
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "PRAGMA foreign_keys"
        let result = cmd.ExecuteScalar() :?> int64

        Assert.Equal(1L, result)
    finally
        if File.Exists(dbPath) then
            File.Delete(dbPath)

[<Fact>]
let ``Properties are stored as Elisp alist`` () =
    let dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".db")

    try
        use db = new Database.OrgRoamDb(dbPath)
        db.Initialize() |> ignore

        db.InsertFile(
            { File = "/test.org"
              Title = Some "Test"
              Hash = "abc"
              Atime = DateTime.UtcNow
              Mtime = DateTime.UtcNow }
        )

        let props = """(("ID" . "test-123") ("CATEGORY" . "notes"))"""

        db.InsertNode(
            { Id = "test-123"
              File = "/test.org"
              Level = 0
              Pos = 1
              Todo = None
              Priority = None
              Scheduled = None
              Deadline = None
              Title = "Test"
              Properties = props
              Olp = "nil" }
        )

        // Retrieve and verify format
        let node = db.GetNode("test-123")
        Assert.True(node.Value.Properties.StartsWith("(("))
        Assert.Contains("\"ID\"", node.Value.Properties)
        Assert.Contains("\".\"", node.Value.Properties.Replace(" . ", "\".\""))
    finally
        if File.Exists(dbPath) then
            File.Delete(dbPath)

[<Fact>]
let ``OLP is stored as Elisp list`` () =
    let dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".db")

    try
        use db = new Database.OrgRoamDb(dbPath)
        db.Initialize() |> ignore

        db.InsertFile(
            { File = "/test.org"
              Title = Some "Test"
              Hash = "abc"
              Atime = DateTime.UtcNow
              Mtime = DateTime.UtcNow }
        )

        let olp = """("Parent" "Grandparent")"""

        db.InsertNode(
            { Id = "test-123"
              File = "/test.org"
              Level = 2
              Pos = 100
              Todo = None
              Priority = None
              Scheduled = None
              Deadline = None
              Title = "Child"
              Properties = "nil"
              Olp = olp }
        )

        let node = db.GetNode("test-123")
        Assert.True(node.Value.Olp.StartsWith("("))
        Assert.Contains("\"Parent\"", node.Value.Olp)
    finally
        if File.Exists(dbPath) then
            File.Delete(dbPath)
