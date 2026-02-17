[<Xunit.Collection("ConsoleCapture")>]
module OrgCli.Tests.CustomIdTests

open System
open System.IO
open System.Text.Json.Nodes
open Xunit
open OrgCli.Org
open OrgCli.Index
open OrgCli.Index.IndexDatabase

// ── CustomId.generate tests ──

[<Fact>]
let ``generate produces string of correct length`` () =
    for len in [ 3; 4; 5; 8 ] do
        let id = CustomId.generate len
        Assert.Equal(len, id.Length)

[<Fact>]
let ``generate produces only base36 characters`` () =
    let valid = set "abcdefghijklmnopqrstuvwxyz0123456789"

    for _ in 1..100 do
        let id = CustomId.generate 5
        Assert.All(id.ToCharArray(), fun c -> Assert.Contains(c, valid))

[<Fact>]
let ``generate produces different values on successive calls`` () =
    let ids = [ for _ in 1..20 -> CustomId.generate 6 ] |> Set.ofList
    // With 36^6 = 2.18 billion possible values, 20 calls should yield 20 distinct values
    Assert.True(ids.Count >= 18, sprintf "Expected mostly distinct IDs but got %d unique out of 20" ids.Count)

// ── CustomId.recommendedLength tests ──

[<Fact>]
let ``recommendedLength returns 3 for small counts`` () =
    Assert.Equal(3, CustomId.recommendedLength 0 0.01)
    Assert.Equal(3, CustomId.recommendedLength 100 0.01)
    Assert.Equal(3, CustomId.recommendedLength 400 0.01)

[<Fact>]
let ``recommendedLength returns 4 when count exceeds 1 percent of 36 cubed`` () =
    // 36^3 = 46656, 1% = 466
    Assert.Equal(4, CustomId.recommendedLength 467 0.01)

[<Fact>]
let ``recommendedLength never returns less than 3`` () =
    Assert.Equal(3, CustomId.recommendedLength 0 0.5)

// ── Database: custom_id schema ──

let private tempDbPath () =
    Path.Combine(Path.GetTempPath(), sprintf "org-customid-test-%s.db" (Guid.NewGuid().ToString("N")))

let private withDb (f: OrgIndexDb -> unit) =
    let path = tempDbPath ()

    try
        use db = new OrgIndexDb(path)
        db.Initialize()
        f db
    finally
        if File.Exists(path) then
            File.Delete(path)

        let wal = path + "-wal"
        let shm = path + "-shm"

        if File.Exists(wal) then
            File.Delete(wal)

        if File.Exists(shm) then
            File.Delete(shm)

let private mkHeadline file pos title customId =
    { File = file
      CharPos = pos
      Level = 1
      Title = title
      Todo = None
      Priority = None
      Scheduled = None
      ScheduledDt = None
      Deadline = None
      DeadlineDt = None
      Closed = None
      ClosedDt = None
      Properties = None
      Body = None
      CustomId = customId
      OutlinePath = None }

[<Fact>]
let ``FindByCustomId returns correct file and position`` () =
    withDb (fun db ->
        db.InsertFile(
            { Path = "/test.org"
              Hash = "h"
              Mtime = 1L }
        )

        db.InsertHeadline(mkHeadline "/test.org" 42L "My task" (Some "abc"))
        let result = db.FindByCustomId("abc")
        Assert.True(result.IsSome)
        let (file, pos) = result.Value
        Assert.Equal("/test.org", file)
        Assert.Equal(42L, pos))

[<Fact>]
let ``FindByCustomId returns None for unknown id`` () =
    withDb (fun db ->
        let result = db.FindByCustomId("zzz")
        Assert.True(result.IsNone))

[<Fact>]
let ``CustomIdExists returns true for existing id`` () =
    withDb (fun db ->
        db.InsertFile(
            { Path = "/test.org"
              Hash = "h"
              Mtime = 1L }
        )

        db.InsertHeadline(mkHeadline "/test.org" 0L "Task" (Some "xyz"))
        Assert.True(db.CustomIdExists("xyz")))

[<Fact>]
let ``CustomIdExists returns false for missing id`` () =
    withDb (fun db -> Assert.False(db.CustomIdExists("nope")))

[<Fact>]
let ``CountCustomIds counts only non-null custom_ids`` () =
    withDb (fun db ->
        db.InsertFile(
            { Path = "/test.org"
              Hash = "h"
              Mtime = 1L }
        )

        db.InsertHeadline(mkHeadline "/test.org" 0L "Has ID" (Some "a1b"))
        db.InsertHeadline(mkHeadline "/test.org" 50L "No ID" None)
        db.InsertHeadline(mkHeadline "/test.org" 100L "Also has" (Some "c2d"))
        Assert.Equal(2, db.CountCustomIds()))

[<Fact>]
let ``UNIQUE constraint on custom_id prevents duplicates`` () =
    withDb (fun db ->
        db.InsertFile(
            { Path = "/test.org"
              Hash = "h"
              Mtime = 1L }
        )

        db.InsertHeadline(mkHeadline "/test.org" 0L "First" (Some "dup"))

        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(fun () ->
            db.InsertHeadline(mkHeadline "/test.org" 50L "Second" (Some "dup")))
        |> ignore)

[<Fact>]
let ``Multiple NULL custom_ids are allowed`` () =
    withDb (fun db ->
        db.InsertFile(
            { Path = "/test.org"
              Hash = "h"
              Mtime = 1L }
        )

        db.InsertHeadline(mkHeadline "/test.org" 0L "A" None)
        db.InsertHeadline(mkHeadline "/test.org" 50L "B" None)
        // Should not throw
        Assert.Equal(0, db.CountCustomIds()))

// ── CustomIdService.generateUnique tests ──

[<Fact>]
let ``generateUnique produces a valid id not in the database`` () =
    withDb (fun db ->
        db.InsertFile(
            { Path = "/test.org"
              Hash = "h"
              Mtime = 1L }
        )

        let id = CustomIdService.generateUnique db
        Assert.True(id.Length >= 3)
        Assert.False(db.CustomIdExists(id)))

[<Fact>]
let ``generateUnique produces unique ids across multiple calls`` () =
    withDb (fun db ->
        db.InsertFile(
            { Path = "/test.org"
              Hash = "h"
              Mtime = 1L }
        )

        let ids =
            [ for i in 0..19 ->
                  let id = CustomIdService.generateUnique db
                  // Simulate inserting it so next call avoids it
                  db.InsertHeadline(mkHeadline "/test.org" (int64 (i * 100)) (sprintf "H%d" i) (Some id))
                  id ]

        let unique = Set.ofList ids
        Assert.Equal(20, unique.Count))

// ── Headlines.resolveHeadlinePos with CUSTOM_ID ──

[<Fact>]
let ``resolveHeadlinePos finds headline by CUSTOM_ID`` () =
    let content = "* Task one\n:PROPERTIES:\n:CUSTOM_ID: k4t\n:END:\nBody\n* Task two\n"

    let result = Headlines.resolveHeadlinePos content "k4t"
    Assert.True(Result.isOk result)
    Assert.Equal(Ok 0L, result)

[<Fact>]
let ``resolveHeadlinePos prefers ID over CUSTOM_ID`` () =
    let content = "* Task\n:PROPERTIES:\n:ID: my-uuid\n:CUSTOM_ID: k4t\n:END:\n"

    // Searching for "my-uuid" should find it via :ID:
    let result = Headlines.resolveHeadlinePos content "my-uuid"
    Assert.True(Result.isOk result)
    Assert.Equal(Ok 0L, result)

[<Fact>]
let ``resolveHeadlinePos falls through from CUSTOM_ID to title`` () =
    let content = "* My Title\n"
    let result = Headlines.resolveHeadlinePos content "My Title"
    Assert.True(Result.isOk result)
    Assert.Equal(Ok 0L, result)

// ── HeadlineState.CustomId extraction ──

[<Fact>]
let ``extractState includes CustomId from property drawer`` () =
    let content = "* Task\n:PROPERTIES:\n:CUSTOM_ID: abc\n:END:\nBody\n"

    let state = HeadlineEdit.extractState content 0L
    Assert.Equal(Some "abc", state.CustomId)

[<Fact>]
let ``extractState returns None CustomId when not present`` () =
    let content = "* Task\nBody\n"
    let state = HeadlineEdit.extractState content 0L
    Assert.Equal(None, state.CustomId)

// ── JSON output includes custom_id ──

[<Fact>]
let ``formatHeadlineState includes custom_id field`` () =
    let state: HeadlineEdit.HeadlineState =
        { Pos = 0L
          Id = None
          CustomId = Some "k4t"
          Title = "Test"
          Todo = None
          Priority = None
          Tags = []
          Scheduled = None
          Deadline = None
          Closed = None }

    let result = JsonOutput.toJsonString (JsonOutput.formatHeadlineState state)
    Assert.Contains("\"custom_id\":\"k4t\"", result)

[<Fact>]
let ``formatHeadlineState has null custom_id when absent`` () =
    let state: HeadlineEdit.HeadlineState =
        { Pos = 0L
          Id = None
          CustomId = None
          Title = "Test"
          Todo = None
          Priority = None
          Tags = []
          Scheduled = None
          Deadline = None
          Closed = None }

    let result = JsonOutput.toJsonString (JsonOutput.formatHeadlineState state)
    Assert.Contains("\"custom_id\":null", result)

// ── IndexSync extracts CUSTOM_ID ──

[<Fact>]
let ``syncFile indexes CUSTOM_ID from property drawer`` () =
    withDb (fun db ->
        let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(dir) |> ignore
        let file = Path.Combine(dir, "test.org")

        File.WriteAllText(file, "* Task\n:PROPERTIES:\n:CUSTOM_ID: xyz\n:END:\nBody\n")

        try
            IndexSync.syncFile db file
            let h = db.GetHeadline(file, 0L)
            Assert.True(h.IsSome)
            Assert.Equal(Some "xyz", h.Value.CustomId)

            let found = db.FindByCustomId("xyz")
            Assert.True(found.IsSome)
            let (f, p) = found.Value
            Assert.Equal(file, f)
            Assert.Equal(0L, p)
        finally
            Directory.Delete(dir, true))

// ── File-less CLI: todo via CUSTOM_ID ──

[<Fact>]
let ``todo command resolves file-less CUSTOM_ID via index`` () =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    let file = Path.Combine(dir, "test.org")
    let dbPath = Path.Combine(dir, ".org-index.db")

    File.WriteAllText(file, "* TODO Buy groceries\n:PROPERTIES:\n:CUSTOM_ID: k4t\n:END:\n")

    try
        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        IndexSync.syncFile db file
        db.Close()

        let args =
            [| "todo"; "k4t"; "DONE"; "--directory"; dir; "--db"; dbPath; "--quiet" |]

        let exitCode = Program.main args
        Assert.Equal(0, exitCode)

        let content = File.ReadAllText(file)
        Assert.Contains("DONE Buy groceries", content)
        Assert.DoesNotContain("TODO Buy groceries", content)
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``todo command still works with explicit file path and CUSTOM_ID`` () =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    let file = Path.Combine(dir, "test.org")

    File.WriteAllText(file, "* TODO Task\n:PROPERTIES:\n:CUSTOM_ID: abc\n:END:\n")

    try
        let args = [| "todo"; file; "abc"; "DONE"; "--quiet" |]
        let exitCode = Program.main args
        Assert.Equal(0, exitCode)

        let content = File.ReadAllText(file)
        Assert.Contains("DONE Task", content)
        Assert.DoesNotContain("TODO Task", content)
    finally
        Directory.Delete(dir, true)

// ── Helper for temp dir with index ──

let private withTempDirAndIndex (orgContent: string) (f: string -> string -> string -> unit) =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    let file = Path.Combine(dir, "test.org")
    let dbPath = Path.Combine(dir, ".org-index.db")
    File.WriteAllText(file, orgContent)

    try
        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        IndexSync.syncFile db file
        db.Close()
        f dir file dbPath
    finally
        Directory.Delete(dir, true)

// ── org add stamps CUSTOM_ID ──

[<Fact>]
let ``add command stamps CUSTOM_ID when index DB exists`` () =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    let file = Path.Combine(dir, "test.org")
    let dbPath = Path.Combine(dir, ".org-index.db")
    File.WriteAllText(file, "")

    try
        // Create index first so stampCustomId finds it
        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        db.Close()

        let args = [| "add"; file; "New task"; "--db"; dbPath; "--quiet" |]
        let exitCode = Program.main args
        Assert.Equal(0, exitCode)

        let content = File.ReadAllText(file)
        Assert.Contains(":CUSTOM_ID:", content)
        Assert.Contains(":PROPERTIES:", content)
        Assert.Contains(":END:", content)
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``add command does not stamp CUSTOM_ID when no index DB`` () =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    let file = Path.Combine(dir, "test.org")
    File.WriteAllText(file, "")

    try
        let args =
            [| "add"; file; "New task"; "--db"; Path.Combine(dir, "nope.db"); "--quiet" |]

        let exitCode = Program.main args
        Assert.Equal(0, exitCode)

        let content = File.ReadAllText(file)
        Assert.DoesNotContain(":CUSTOM_ID:", content)
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``add command stamps unique CUSTOM_IDs for multiple headlines`` () =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    let file = Path.Combine(dir, "test.org")
    let dbPath = Path.Combine(dir, ".org-index.db")
    File.WriteAllText(file, "")

    try
        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        db.Close()

        for i in 1..5 do
            let args = [| "add"; file; sprintf "Task %d" i; "--db"; dbPath; "--quiet" |]
            let exitCode = Program.main args
            Assert.Equal(0, exitCode)

        let content = File.ReadAllText(file)
        // Extract all CUSTOM_ID values
        let re = System.Text.RegularExpressions.Regex(@":CUSTOM_ID:\s+(\S+)")
        let matches = re.Matches(content)
        Assert.Equal(5, matches.Count)
        let ids = [ for m in matches -> m.Groups.[1].Value ] |> Set.ofList
        Assert.Equal(5, ids.Count) // all unique
    finally
        Directory.Delete(dir, true)

// ── File-less CLI: commands beyond todo ──

[<Fact>]
let ``schedule command resolves file-less CUSTOM_ID`` () =
    withTempDirAndIndex "* Task\n:PROPERTIES:\n:CUSTOM_ID: s1x\n:END:\n" (fun dir _ dbPath ->
        let args =
            [| "schedule"
               "s1x"
               "2026-03-01"
               "--directory"
               dir
               "--db"
               dbPath
               "--quiet" |]

        let exitCode = Program.main args
        Assert.Equal(0, exitCode)

        let content = File.ReadAllText(Path.Combine(dir, "test.org"))
        Assert.Contains("SCHEDULED:", content)
        Assert.Contains("2026-03-01", content))

[<Fact>]
let ``deadline command resolves file-less CUSTOM_ID`` () =
    withTempDirAndIndex "* Task\n:PROPERTIES:\n:CUSTOM_ID: d2y\n:END:\n" (fun dir _ dbPath ->
        let args =
            [| "deadline"
               "d2y"
               "2026-04-15"
               "--directory"
               dir
               "--db"
               dbPath
               "--quiet" |]

        let exitCode = Program.main args
        Assert.Equal(0, exitCode)

        let content = File.ReadAllText(Path.Combine(dir, "test.org"))
        Assert.Contains("DEADLINE:", content)
        Assert.Contains("2026-04-15", content))

[<Fact>]
let ``priority command resolves file-less CUSTOM_ID`` () =
    withTempDirAndIndex "* Task\n:PROPERTIES:\n:CUSTOM_ID: p3z\n:END:\n" (fun dir _ dbPath ->
        let args =
            [| "priority"; "p3z"; "A"; "--directory"; dir; "--db"; dbPath; "--quiet" |]

        let exitCode = Program.main args
        Assert.Equal(0, exitCode)

        let content = File.ReadAllText(Path.Combine(dir, "test.org"))
        Assert.Contains("[#A]", content))

[<Fact>]
let ``tag add command resolves file-less CUSTOM_ID`` () =
    withTempDirAndIndex "* Task\n:PROPERTIES:\n:CUSTOM_ID: t4a\n:END:\n" (fun dir _ dbPath ->
        let args =
            [| "tag"
               "add"
               "t4a"
               "urgent"
               "--directory"
               dir
               "--db"
               dbPath
               "--quiet" |]

        let exitCode = Program.main args
        Assert.Equal(0, exitCode)

        let content = File.ReadAllText(Path.Combine(dir, "test.org"))
        Assert.Contains(":urgent:", content))

[<Fact>]
let ``tag remove command resolves file-less CUSTOM_ID`` () =
    withTempDirAndIndex "* Task :old:\n:PROPERTIES:\n:CUSTOM_ID: t5b\n:END:\n" (fun dir _ dbPath ->
        let args =
            [| "tag"
               "remove"
               "t5b"
               "old"
               "--directory"
               dir
               "--db"
               dbPath
               "--quiet" |]

        let exitCode = Program.main args
        Assert.Equal(0, exitCode)

        let content = File.ReadAllText(Path.Combine(dir, "test.org"))
        Assert.DoesNotContain(":old:", content))

[<Fact>]
let ``property set command resolves file-less CUSTOM_ID`` () =
    withTempDirAndIndex "* Task\n:PROPERTIES:\n:CUSTOM_ID: r6c\n:END:\n" (fun dir _ dbPath ->
        let args =
            [| "property"
               "set"
               "r6c"
               "EFFORT"
               "2h"
               "--directory"
               dir
               "--db"
               dbPath
               "--quiet" |]

        let exitCode = Program.main args
        Assert.Equal(0, exitCode)

        let content = File.ReadAllText(Path.Combine(dir, "test.org"))
        Assert.Contains(":EFFORT: 2h", content))

[<Fact>]
let ``property remove command resolves file-less CUSTOM_ID`` () =
    withTempDirAndIndex "* Task\n:PROPERTIES:\n:CUSTOM_ID: r7d\n:EFFORT: 1h\n:END:\n" (fun dir _ dbPath ->
        let args =
            [| "property"
               "remove"
               "r7d"
               "EFFORT"
               "--directory"
               dir
               "--db"
               dbPath
               "--quiet" |]

        let exitCode = Program.main args
        Assert.Equal(0, exitCode)

        let content = File.ReadAllText(Path.Combine(dir, "test.org"))
        Assert.DoesNotContain(":EFFORT:", content))

[<Fact>]
let ``note command resolves file-less CUSTOM_ID`` () =
    withTempDirAndIndex "* Task\n:PROPERTIES:\n:CUSTOM_ID: n8e\n:END:\n" (fun dir _ dbPath ->
        let args =
            [| "note"
               "n8e"
               "Remember this"
               "--directory"
               dir
               "--db"
               dbPath
               "--quiet" |]

        let exitCode = Program.main args
        Assert.Equal(0, exitCode)

        let content = File.ReadAllText(Path.Combine(dir, "test.org"))
        Assert.Contains("Remember this", content)
        Assert.Contains(":LOGBOOK:", content))

[<Fact>]
let ``clock in command resolves file-less CUSTOM_ID`` () =
    withTempDirAndIndex "* Task\n:PROPERTIES:\n:CUSTOM_ID: c9f\n:END:\n" (fun dir _ dbPath ->
        let args = [| "clock"; "in"; "c9f"; "--directory"; dir; "--db"; dbPath; "--quiet" |]

        let exitCode = Program.main args
        Assert.Equal(0, exitCode)

        let content = File.ReadAllText(Path.Combine(dir, "test.org"))
        Assert.Contains("CLOCK:", content))

[<Fact>]
let ``clock out command resolves file-less CUSTOM_ID`` () =
    withTempDirAndIndex "* Task\n:PROPERTIES:\n:CUSTOM_ID: c0g\n:END:\n" (fun dir file dbPath ->
        // Clock in first
        let inArgs = [| "clock"; "in"; file; "c0g"; "--db"; dbPath; "--quiet" |]
        Program.main inArgs |> ignore

        // Re-index after clock in modified the file
        use db2 = new OrgIndexDb(dbPath)
        db2.Initialize()
        IndexSync.syncFile db2 file
        db2.Close()

        let outArgs =
            [| "clock"; "out"; "c0g"; "--directory"; dir; "--db"; dbPath; "--quiet" |]

        let exitCode = Program.main outArgs
        Assert.Equal(0, exitCode)

        let content = File.ReadAllText(file)
        Assert.Contains("=>", content)) // clock duration marker

[<Fact>]
let ``archive command resolves file-less CUSTOM_ID`` () =
    withTempDirAndIndex "* Keep\n* Archive me\n:PROPERTIES:\n:CUSTOM_ID: a1h\n:END:\n" (fun dir _ dbPath ->
        let args = [| "archive"; "a1h"; "--directory"; dir; "--db"; dbPath; "--quiet" |]

        let exitCode = Program.main args
        Assert.Equal(0, exitCode)

        let content = File.ReadAllText(Path.Combine(dir, "test.org"))
        Assert.DoesNotContain("Archive me", content)
        let archiveContent = File.ReadAllText(Path.Combine(dir, "test.org_archive"))
        Assert.Contains("Archive me", archiveContent))

[<Fact>]
let ``read command resolves file-less CUSTOM_ID`` () =
    withTempDirAndIndex "* Headline\n:PROPERTIES:\n:CUSTOM_ID: r2i\n:END:\nBody text here\n" (fun dir _ dbPath ->
        // read prints to stdout -- just verify exit code
        let args = [| "read"; "r2i"; "--directory"; dir; "--db"; dbPath |]

        let exitCode = Program.main args
        Assert.Equal(0, exitCode))

// ── Duplicate CUSTOM_ID graceful degradation ──

[<Fact>]
let ``syncDirectory handles duplicate CUSTOM_IDs across files gracefully`` () =
    withDb (fun db ->
        let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(dir) |> ignore
        let file1 = Path.Combine(dir, "a.org")
        let file2 = Path.Combine(dir, "b.org")
        File.WriteAllText(file1, "* Task A\n:PROPERTIES:\n:CUSTOM_ID: dup\n:END:\n")
        File.WriteAllText(file2, "* Task B\n:PROPERTIES:\n:CUSTOM_ID: dup\n:END:\n")

        try
            // Should not throw -- second file's headline gets custom_id = NULL
            IndexSync.syncDirectory db dir

            // First file's CUSTOM_ID should be findable
            let found = db.FindByCustomId("dup")
            Assert.True(found.IsSome)

            // Both headlines should be indexed (one with custom_id, one without)
            let h1 = db.GetHeadlines(file1)
            let h2 = db.GetHeadlines(file2)
            Assert.Equal(1, h1.Length)
            Assert.Equal(1, h2.Length)

            // Exactly one has the custom_id
            let withId = [ h1.[0].CustomId; h2.[0].CustomId ] |> List.choose id

            Assert.Equal(1, withId.Length)
            Assert.Equal("dup", withId.[0])
        finally
            Directory.Delete(dir, true))

[<Fact>]
let ``syncFile re-indexes CUSTOM_ID after file change`` () =
    withDb (fun db ->
        let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(dir) |> ignore
        let file = Path.Combine(dir, "test.org")

        File.WriteAllText(file, "* Task\n:PROPERTIES:\n:CUSTOM_ID: old\n:END:\n")

        try
            IndexSync.syncFile db file
            Assert.True(db.FindByCustomId("old").IsSome)
            Assert.True(db.FindByCustomId("new1").IsNone)

            // Change the CUSTOM_ID
            File.WriteAllText(file, "* Task\n:PROPERTIES:\n:CUSTOM_ID: new1\n:END:\n")
            IndexSync.syncFile db file

            Assert.True(db.FindByCustomId("old").IsNone)
            Assert.True(db.FindByCustomId("new1").IsSome)
        finally
            Directory.Delete(dir, true))

// ── Schema migration: existing DB without custom_id column ──

[<Fact>]
let ``Initialize migrates existing DB without custom_id column`` () =
    let path = tempDbPath ()

    try
        // Create a DB with the old schema (no custom_id column)
        use conn = new Microsoft.Data.Sqlite.SqliteConnection(sprintf "Data Source=%s" path)
        conn.Open()
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            """
            CREATE TABLE IF NOT EXISTS index_files (
                path TEXT PRIMARY KEY,
                hash TEXT NOT NULL,
                mtime INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS index_headlines (
                file TEXT NOT NULL,
                char_pos INTEGER NOT NULL,
                level INTEGER NOT NULL,
                title TEXT NOT NULL,
                todo TEXT,
                priority TEXT,
                scheduled TEXT,
                scheduled_dt TEXT,
                deadline TEXT,
                deadline_dt TEXT,
                closed TEXT,
                closed_dt TEXT,
                properties TEXT,
                body TEXT,
                outline_path TEXT,
                PRIMARY KEY (file, char_pos),
                FOREIGN KEY (file) REFERENCES index_files(path) ON DELETE CASCADE
            );
            """

        cmd.ExecuteNonQuery() |> ignore

        // Insert a row without custom_id
        use insFile = conn.CreateCommand()
        insFile.CommandText <- "INSERT INTO index_files (path, hash, mtime) VALUES ('/old.org', 'h', 1)"
        insFile.ExecuteNonQuery() |> ignore
        use insH = conn.CreateCommand()

        insH.CommandText <-
            "INSERT INTO index_headlines (file, char_pos, level, title) VALUES ('/old.org', 0, 1, 'Old task')"

        insH.ExecuteNonQuery() |> ignore
        conn.Close()

        // Now open with OrgIndexDb and Initialize -- should add custom_id column
        use db = new OrgIndexDb(path)
        db.Initialize()

        // Should be able to read the old headline (custom_id = NULL)
        let h = db.GetHeadline("/old.org", 0L)
        Assert.True(h.IsSome)
        Assert.Equal("Old task", h.Value.Title)
        Assert.Equal(None, h.Value.CustomId)

        // Should be able to insert a new headline with custom_id
        db.InsertHeadline(mkHeadline "/old.org" 100L "New task" (Some "abc"))
        let found = db.FindByCustomId("abc")
        Assert.True(found.IsSome)

        // Calling Initialize again should not fail (idempotent)
        db.Initialize()
    finally
        if File.Exists(path) then
            File.Delete(path)

        if File.Exists(path + "-wal") then
            File.Delete(path + "-wal")

        if File.Exists(path + "-shm") then
            File.Delete(path + "-shm")

// ── resolveFileFromIndex error paths ──

[<Fact>]
let ``file-less command with no index DB returns error`` () =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore

    try
        let args =
            [| "todo"
               "k4t"
               "DONE"
               "--directory"
               dir
               "--db"
               Path.Combine(dir, "nope.db")
               "--quiet" |]

        let exitCode = Program.main args
        Assert.Equal(1, exitCode)
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``file-less command with unknown identifier returns error`` () =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    let dbPath = Path.Combine(dir, ".org-index.db")
    let file = Path.Combine(dir, "test.org")
    File.WriteAllText(file, "* Task\n:PROPERTIES:\n:CUSTOM_ID: abc\n:END:\n")

    try
        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        IndexSync.syncFile db file
        db.Close()

        let args =
            [| "todo"; "zzz999"; "DONE"; "--directory"; dir; "--db"; dbPath; "--quiet" |]

        let exitCode = Program.main args
        Assert.Equal(1, exitCode)
    finally
        Directory.Delete(dir, true)

// ── CUSTOM_ID case sensitivity ──

[<Fact>]
let ``resolveHeadlinePos matches CUSTOM_ID case-sensitively`` () =
    let content = "* Task\n:PROPERTIES:\n:CUSTOM_ID: AbC\n:END:\n"
    // Exact case matches
    let result = Headlines.resolveHeadlinePos content "AbC"
    Assert.True(Result.isOk result)
    // Wrong case does not match (falls through to title, also no match)
    let result2 = Headlines.resolveHeadlinePos content "abc"
    Assert.True(Result.isError result2)
    let result3 = Headlines.resolveHeadlinePos content "ABC"
    Assert.True(Result.isError result3)

[<Fact>]
let ``FindByCustomId is case-sensitive`` () =
    withDb (fun db ->
        db.InsertFile(
            { Path = "/test.org"
              Hash = "h"
              Mtime = 1L }
        )

        db.InsertHeadline(mkHeadline "/test.org" 0L "Task" (Some "AbC"))
        Assert.True(db.FindByCustomId("AbC").IsSome)
        Assert.True(db.FindByCustomId("abc").IsNone)
        Assert.True(db.FindByCustomId("ABC").IsNone))

// ── Headline without CUSTOM_ID indexed with custom_id = NULL ──

[<Fact>]
let ``syncFile indexes headline without CUSTOM_ID as NULL`` () =
    withDb (fun db ->
        let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(dir) |> ignore
        let file = Path.Combine(dir, "test.org")
        File.WriteAllText(file, "* No properties\nJust body\n")

        try
            IndexSync.syncFile db file
            let h = db.GetHeadline(file, 0L)
            Assert.True(h.IsSome)
            Assert.Equal(None, h.Value.CustomId)
        finally
            Directory.Delete(dir, true))

// ── custom-id assign command ──

[<Fact>]
let ``custom-id assign adds CUSTOM_IDs to headlines without them`` () =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    let file = Path.Combine(dir, "test.org")
    File.WriteAllText(file, "* First\n* Second\n* Third\n")

    try
        let args = [| "custom-id"; "assign"; "--directory"; dir; "--quiet" |]
        let exitCode = Program.main args
        Assert.Equal(0, exitCode)

        let content = File.ReadAllText(file)
        let re = System.Text.RegularExpressions.Regex(@":CUSTOM_ID:\s+(\S+)")
        let matches = re.Matches(content)
        Assert.Equal(3, matches.Count)
        let ids = [ for m in matches -> m.Groups.[1].Value ] |> Set.ofList
        Assert.Equal(3, ids.Count)
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``custom-id assign skips headlines that already have CUSTOM_ID`` () =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    let file = Path.Combine(dir, "test.org")

    File.WriteAllText(file, "* Has ID\n:PROPERTIES:\n:CUSTOM_ID: existing\n:END:\n* No ID\n")

    try
        let args = [| "custom-id"; "assign"; "--directory"; dir; "--quiet" |]
        let exitCode = Program.main args
        Assert.Equal(0, exitCode)

        let content = File.ReadAllText(file)
        // Original CUSTOM_ID preserved
        Assert.Contains(":CUSTOM_ID: existing", content)
        // New one assigned to second headline
        let re = System.Text.RegularExpressions.Regex(@":CUSTOM_ID:\s+(\S+)")
        let matches = re.Matches(content)
        Assert.Equal(2, matches.Count)
        let ids = [ for m in matches -> m.Groups.[1].Value ]
        Assert.Contains("existing", ids)
        Assert.True(ids |> List.exists (fun id -> id <> "existing"))
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``custom-id assign works across multiple files`` () =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    let file1 = Path.Combine(dir, "a.org")
    let file2 = Path.Combine(dir, "b.org")
    File.WriteAllText(file1, "* Task A1\n* Task A2\n")
    File.WriteAllText(file2, "* Task B1\n")

    try
        let args = [| "custom-id"; "assign"; "--directory"; dir; "--quiet" |]
        let exitCode = Program.main args
        Assert.Equal(0, exitCode)

        let re = System.Text.RegularExpressions.Regex(@":CUSTOM_ID:\s+(\S+)")
        let content1 = File.ReadAllText(file1)
        let content2 = File.ReadAllText(file2)
        let m1 = re.Matches(content1)
        let m2 = re.Matches(content2)
        Assert.Equal(2, m1.Count)
        Assert.Equal(1, m2.Count)

        // All 3 IDs are unique across both files
        let allIds =
            [ for m in m1 -> m.Groups.[1].Value ] @ [ for m in m2 -> m.Groups.[1].Value ]
            |> Set.ofList

        Assert.Equal(3, allIds.Count)
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``custom-id assign with dry-run does not modify files`` () =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    let file = Path.Combine(dir, "test.org")
    let original = "* Task\n"
    File.WriteAllText(file, original)

    try
        let args = [| "custom-id"; "assign"; "--directory"; dir; "--dry-run"; "--quiet" |]
        let exitCode = Program.main args
        Assert.Equal(0, exitCode)

        let content = File.ReadAllText(file)
        Assert.Equal(original, content)
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``custom-id assign JSON output reports counts`` () =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    let file = Path.Combine(dir, "test.org")

    File.WriteAllText(file, "* Has ID\n:PROPERTIES:\n:CUSTOM_ID: old\n:END:\n* No ID\n")

    try
        let oldOut = Console.Out
        use sw = new StringWriter()
        Console.SetOut(sw)

        try
            let args = [| "custom-id"; "assign"; "--directory"; dir; "--format"; "json" |]
            Program.main args |> ignore
        finally
            Console.SetOut(oldOut)

        let output = sw.ToString()
        Assert.Contains("\"assigned\"", output)
        Assert.Contains("\"skipped\"", output)
    finally
        Directory.Delete(dir, true)

// ── Output formatting: custom_id visibility ──

let private captureBoth (f: unit -> 'a) : string * string * 'a =
    let outSw = new StringWriter()
    let errSw = new StringWriter()
    let oldOut = Console.Out
    let oldErr = Console.Error
    Console.SetOut(outSw)
    Console.SetError(errSw)

    try
        let result = f ()
        Console.SetOut(oldOut)
        Console.SetError(oldErr)
        outSw.ToString(), errSw.ToString(), result
    with ex ->
        Console.SetOut(oldOut)
        Console.SetError(oldErr)
        reraise ()

let private withTempOrgDir (files: (string * string) list) (f: string -> unit) =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore

    for (name, content) in files do
        File.WriteAllText(Path.Combine(dir, name), content)

    try
        f dir
    finally
        Directory.Delete(dir, true)

// -- Headlines output --

[<Fact>]
let ``headlines JSON output includes custom_id field`` () =
    withTempOrgDir [ ("test.org", "* Task\n:PROPERTIES:\n:CUSTOM_ID: k4t\n:END:\n") ] (fun dir ->
        let stdout, _, exitCode =
            captureBoth (fun () -> Program.main [| "headlines"; "--directory"; dir; "--format"; "json" |])

        Assert.Equal(0, exitCode)
        let json = JsonNode.Parse(stdout.Trim())
        let items = json["data"] :?> JsonArray
        Assert.Equal(1, items.Count)
        let cid = items.[0]["custom_id"]
        Assert.Equal("k4t", cid.GetValue<string>()))

[<Fact>]
let ``headlines JSON output has null custom_id when absent`` () =
    withTempOrgDir [ ("test.org", "* Task\n") ] (fun dir ->
        let stdout, _, _ =
            captureBoth (fun () -> Program.main [| "headlines"; "--directory"; dir; "--format"; "json" |])

        let json = JsonNode.Parse(stdout.Trim())
        let items = json["data"] :?> JsonArray
        let cid = items.[0]["custom_id"]
        Assert.True(isNull cid || cid.GetValue<string>() = null))

[<Fact>]
let ``headlines text output shows custom_id as identifier`` () =
    withTempOrgDir [ ("test.org", "* Task\n:PROPERTIES:\n:CUSTOM_ID: k4t\n:END:\n") ] (fun dir ->
        let stdout, _, _ =
            captureBoth (fun () -> Program.main [| "headlines"; "--directory"; dir |])

        Assert.Contains("k4t", stdout))

[<Fact>]
let ``headlines text output falls back to position when no custom_id`` () =
    withTempOrgDir [ ("test.org", "* Task\n") ] (fun dir ->
        let stdout, _, _ =
            captureBoth (fun () -> Program.main [| "headlines"; "--directory"; dir |])

        Assert.Contains("0", stdout))

// -- Agenda output --

[<Fact>]
let ``agenda JSON output includes custom_id`` () =
    let today = DateTime.Today.ToString("yyyy-MM-dd")

    let dayName =
        DateTime.Today.ToString("ddd", System.Globalization.CultureInfo.InvariantCulture)

    let content =
        sprintf "* TODO Task\nSCHEDULED: <%s %s>\n:PROPERTIES:\n:CUSTOM_ID: a1b\n:END:\n" today dayName

    withTempOrgDir [ ("test.org", content) ] (fun dir ->
        let stdout, _, exitCode =
            captureBoth (fun () -> Program.main [| "agenda"; "today"; "--directory"; dir; "--format"; "json" |])

        Assert.Equal(0, exitCode)
        let json = JsonNode.Parse(stdout.Trim())
        let items = json["data"] :?> JsonArray
        Assert.True(items.Count > 0)
        let cid = items.[0]["custom_id"]
        Assert.Equal("a1b", cid.GetValue<string>()))

[<Fact>]
let ``agenda text output includes custom_id in parentheses`` () =
    let today = DateTime.Today.ToString("yyyy-MM-dd")

    let dayName =
        DateTime.Today.ToString("ddd", System.Globalization.CultureInfo.InvariantCulture)

    let content =
        sprintf "* TODO Task\nSCHEDULED: <%s %s>\n:PROPERTIES:\n:CUSTOM_ID: a1b\n:END:\n" today dayName

    withTempOrgDir [ ("test.org", content) ] (fun dir ->
        let stdout, _, _ =
            captureBoth (fun () -> Program.main [| "agenda"; "today"; "--directory"; dir |])

        Assert.Contains("a1b", stdout)
        Assert.Contains("ID", stdout))

[<Fact>]
let ``agenda todo text output includes custom_id`` () =
    withTempOrgDir [ ("test.org", "* TODO Task\n:PROPERTIES:\n:CUSTOM_ID: t1x\n:END:\n") ] (fun dir ->
        let stdout, _, _ =
            captureBoth (fun () -> Program.main [| "agenda"; "todo"; "--directory"; dir |])

        Assert.Contains("t1x", stdout)
        Assert.Contains("ID", stdout))

// -- FTS output --

[<Fact>]
let ``fts JSON output includes custom_id`` () =
    withTempOrgDir
        [ ("test.org", "* Meeting notes\n:PROPERTIES:\n:CUSTOM_ID: m2y\n:END:\nDiscuss budget\n") ]
        (fun dir ->
            let dbPath = Path.Combine(dir, ".org-index.db")

            captureBoth (fun () -> Program.main [| "index"; "--directory"; dir; "--db"; dbPath; "--quiet" |])
            |> ignore

            let stdout, _, exitCode =
                captureBoth (fun () ->
                    Program.main
                        [| "fts"
                           "meeting"
                           "--directory"
                           dir
                           "--db"
                           dbPath
                           "--no-sync"
                           "--format"
                           "json" |])

            Assert.Equal(0, exitCode)
            let json = JsonNode.Parse(stdout.Trim())
            let items = json["data"] :?> JsonArray
            Assert.True(items.Count > 0)
            let cid = items.[0]["custom_id"]
            Assert.Equal("m2y", cid.GetValue<string>()))

[<Fact>]
let ``fts text output includes custom_id in parentheses`` () =
    withTempOrgDir
        [ ("test.org", "* Meeting notes\n:PROPERTIES:\n:CUSTOM_ID: m2y\n:END:\nDiscuss budget\n") ]
        (fun dir ->
            let dbPath = Path.Combine(dir, ".org-index.db")

            captureBoth (fun () -> Program.main [| "index"; "--directory"; dir; "--db"; dbPath; "--quiet" |])
            |> ignore

            let stdout, _, _ =
                captureBoth (fun () ->
                    Program.main [| "fts"; "meeting"; "--directory"; dir; "--db"; dbPath; "--no-sync" |])

            Assert.Contains("m2y", stdout)
            Assert.Contains("ID", stdout))

[<Fact>]
let ``SearchFts returns CustomId from database`` () =
    withDb (fun db ->
        let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(dir) |> ignore
        let file = Path.Combine(dir, "test.org")

        File.WriteAllText(file, "* Topic\n:PROPERTIES:\n:CUSTOM_ID: f3z\n:END:\nSearchable body\n")

        try
            IndexSync.syncFile db file
            let results = db.SearchFts("searchable")
            Assert.True(results.Length > 0)
            Assert.Equal(Some "f3z", results.[0].CustomId)
        finally
            Directory.Delete(dir, true))

[<Fact>]
let ``SearchFts returns None CustomId when headline has none`` () =
    withDb (fun db ->
        let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(dir) |> ignore
        let file = Path.Combine(dir, "test.org")
        File.WriteAllText(file, "* Topic\nSearchable body\n")

        try
            IndexSync.syncFile db file
            let results = db.SearchFts("searchable")
            Assert.True(results.Length > 0)
            Assert.Equal(None, results.[0].CustomId)
        finally
            Directory.Delete(dir, true))

// -- Search output --

[<Fact>]
let ``search JSON output includes custom_id`` () =
    withTempOrgDir [ ("test.org", "* Task\n:PROPERTIES:\n:CUSTOM_ID: s4w\n:END:\nfind this line\n") ] (fun dir ->
        let stdout, _, exitCode =
            captureBoth (fun () -> Program.main [| "search"; "find this"; "--directory"; dir; "--format"; "json" |])

        Assert.Equal(0, exitCode)
        let json = JsonNode.Parse(stdout.Trim())
        let items = json["data"] :?> JsonArray
        Assert.True(items.Count > 0)
        let cid = items.[0]["custom_id"]
        Assert.Equal("s4w", cid.GetValue<string>()))

[<Fact>]
let ``search text output includes custom_id in parentheses`` () =
    withTempOrgDir [ ("test.org", "* Task\n:PROPERTIES:\n:CUSTOM_ID: s4w\n:END:\nfind this line\n") ] (fun dir ->
        let stdout, _, _ =
            captureBoth (fun () -> Program.main [| "search"; "find this"; "--directory"; dir |])

        Assert.Contains("s4w", stdout)
        Assert.Contains("ID", stdout))

// -- Clock output --

[<Fact>]
let ``clock report JSON output includes custom_id`` () =
    let now = DateTime.Now
    let ts1 = now.AddHours(-1.0).ToString("yyyy-MM-dd ddd HH:mm")
    let ts2 = now.ToString("yyyy-MM-dd ddd HH:mm")

    let content =
        sprintf "* Task\n:PROPERTIES:\n:CUSTOM_ID: c5v\n:END:\n:LOGBOOK:\nCLOCK: [%s]--[%s] =>  1:00\n:END:\n" ts1 ts2

    withTempOrgDir [ ("test.org", content) ] (fun dir ->
        let stdout, _, exitCode =
            captureBoth (fun () -> Program.main [| "clock"; "--directory"; dir; "--format"; "json" |])

        Assert.Equal(0, exitCode)
        let json = JsonNode.Parse(stdout.Trim())
        let items = json["data"] :?> JsonArray
        Assert.True(items.Count > 0)
        let cid = items.[0]["custom_id"]
        Assert.Equal("c5v", cid.GetValue<string>()))

[<Fact>]
let ``clock report text output includes custom_id in parentheses`` () =
    let now = DateTime.Now
    let ts1 = now.AddHours(-1.0).ToString("yyyy-MM-dd ddd HH:mm")
    let ts2 = now.ToString("yyyy-MM-dd ddd HH:mm")

    let content =
        sprintf "* Task\n:PROPERTIES:\n:CUSTOM_ID: c5v\n:END:\n:LOGBOOK:\nCLOCK: [%s]--[%s] =>  1:00\n:END:\n" ts1 ts2

    withTempOrgDir [ ("test.org", content) ] (fun dir ->
        let stdout, _, _ =
            captureBoth (fun () -> Program.main [| "clock"; "--directory"; dir |])

        Assert.Contains("c5v", stdout)
        Assert.Contains("ID", stdout))

// -- org today command --

[<Fact>]
let ``today command shows TODO scheduled for today`` () =
    let today = DateTime.Today.ToString("yyyy-MM-dd")

    let dayName =
        DateTime.Today.ToString("ddd", System.Globalization.CultureInfo.InvariantCulture)

    let content =
        sprintf "* TODO Buy milk\nSCHEDULED: <%s %s>\n:PROPERTIES:\n:CUSTOM_ID: t1a\n:END:\n" today dayName

    withTempOrgDir [ ("test.org", content) ] (fun dir ->
        let stdout, _, exitCode =
            captureBoth (fun () -> Program.main [| "today"; "--directory"; dir |])

        Assert.Equal(0, exitCode)
        Assert.Contains("Buy milk", stdout)
        Assert.Contains("t1a", stdout))

[<Fact>]
let ``today command shows overdue scheduled TODO`` () =
    let content =
        "* TODO Overdue task\nSCHEDULED: <2020-01-01 Wed>\n:PROPERTIES:\n:CUSTOM_ID: t2b\n:END:\n"

    withTempOrgDir [ ("test.org", content) ] (fun dir ->
        let stdout, _, exitCode =
            captureBoth (fun () -> Program.main [| "today"; "--directory"; dir |])

        Assert.Equal(0, exitCode)
        Assert.Contains("Overdue task", stdout)
        Assert.Contains("t2b", stdout))

[<Fact>]
let ``today command excludes DONE items`` () =
    let today = DateTime.Today.ToString("yyyy-MM-dd")

    let dayName =
        DateTime.Today.ToString("ddd", System.Globalization.CultureInfo.InvariantCulture)

    let content =
        sprintf
            "* DONE Finished task\nSCHEDULED: <%s %s>\n:PROPERTIES:\n:CUSTOM_ID: t3c\n:END:\n* TODO Active task\nSCHEDULED: <%s %s>\n:PROPERTIES:\n:CUSTOM_ID: t4d\n:END:\n"
            today
            dayName
            today
            dayName

    withTempOrgDir [ ("test.org", content) ] (fun dir ->
        let stdout, _, exitCode =
            captureBoth (fun () -> Program.main [| "today"; "--directory"; dir |])

        Assert.Equal(0, exitCode)
        Assert.Contains("Active task", stdout)
        Assert.DoesNotContain("Finished task", stdout))

[<Fact>]
let ``today command excludes future scheduled items`` () =
    let content =
        "* TODO Future task\nSCHEDULED: <2099-01-01 Thu>\n:PROPERTIES:\n:CUSTOM_ID: t5e\n:END:\n"

    withTempOrgDir [ ("test.org", content) ] (fun dir ->
        let stdout, _, exitCode =
            captureBoth (fun () -> Program.main [| "today"; "--directory"; dir |])

        Assert.Equal(0, exitCode)
        Assert.DoesNotContain("Future task", stdout))

[<Fact>]
let ``today command shows deadline due today`` () =
    let today = DateTime.Today.ToString("yyyy-MM-dd")

    let dayName =
        DateTime.Today.ToString("ddd", System.Globalization.CultureInfo.InvariantCulture)

    let content =
        sprintf "* TODO Deadline task\nDEADLINE: <%s %s>\n:PROPERTIES:\n:CUSTOM_ID: t6f\n:END:\n" today dayName

    withTempOrgDir [ ("test.org", content) ] (fun dir ->
        let stdout, _, exitCode =
            captureBoth (fun () -> Program.main [| "today"; "--directory"; dir |])

        Assert.Equal(0, exitCode)
        Assert.Contains("Deadline task", stdout))

[<Fact>]
let ``today command JSON output`` () =
    let today = DateTime.Today.ToString("yyyy-MM-dd")

    let dayName =
        DateTime.Today.ToString("ddd", System.Globalization.CultureInfo.InvariantCulture)

    let content =
        sprintf "* TODO Task\nSCHEDULED: <%s %s>\n:PROPERTIES:\n:CUSTOM_ID: t7g\n:END:\n" today dayName

    withTempOrgDir [ ("test.org", content) ] (fun dir ->
        let stdout, _, exitCode =
            captureBoth (fun () -> Program.main [| "today"; "--directory"; dir; "--format"; "json" |])

        Assert.Equal(0, exitCode)
        let json = JsonNode.Parse(stdout.Trim())
        let items = json["data"] :?> JsonArray
        Assert.True(items.Count > 0)
        let item = items.[0]
        Assert.Equal("t7g", item["custom_id"].GetValue<string>())
        Assert.Equal("TODO", item["todo"].GetValue<string>()))

[<Fact>]
let ``today command deduplicates headline with both scheduled and deadline`` () =
    let today = DateTime.Today.ToString("yyyy-MM-dd")

    let dayName =
        DateTime.Today.ToString("ddd", System.Globalization.CultureInfo.InvariantCulture)

    let content =
        sprintf
            "* TODO Both dates\nSCHEDULED: <%s %s> DEADLINE: <%s %s>\n:PROPERTIES:\n:CUSTOM_ID: t8h\n:END:\n"
            today
            dayName
            today
            dayName

    withTempOrgDir [ ("test.org", content) ] (fun dir ->
        let stdout, _, exitCode =
            captureBoth (fun () -> Program.main [| "today"; "--directory"; dir |])

        Assert.Equal(0, exitCode)
        // Should appear exactly once, not twice
        let lines = stdout.Split('\n') |> Array.filter (fun l -> l.Contains("Both dates"))
        Assert.Equal(1, lines.Length))

[<Fact>]
let ``today command sorts timed items before untimed`` () =
    let today = DateTime.Today.ToString("yyyy-MM-dd")

    let dayName =
        DateTime.Today.ToString("ddd", System.Globalization.CultureInfo.InvariantCulture)

    let content =
        sprintf
            "* TODO Untimed task\nSCHEDULED: <%s %s>\n:PROPERTIES:\n:CUSTOM_ID: t9i\n:END:\n* TODO Timed task\nSCHEDULED: <%s %s 14:00>\n:PROPERTIES:\n:CUSTOM_ID: t0j\n:END:\n"
            today
            dayName
            today
            dayName

    withTempOrgDir [ ("test.org", content) ] (fun dir ->
        let stdout, _, exitCode =
            captureBoth (fun () -> Program.main [| "today"; "--directory"; dir |])

        Assert.Equal(0, exitCode)
        let timedPos = stdout.IndexOf("Timed task")
        let untimedPos = stdout.IndexOf("Untimed task")
        Assert.True(timedPos < untimedPos, "Timed items should sort before untimed"))

// -- QueryHeadlines includes CustomId --

[<Fact>]
let ``QueryHeadlines returns CustomId`` () =
    withDb (fun db ->
        let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(dir) |> ignore
        let file = Path.Combine(dir, "test.org")
        File.WriteAllText(file, "* TODO Task\n:PROPERTIES:\n:CUSTOM_ID: q6u\n:END:\n")

        try
            IndexSync.syncFile db file
            let results = db.QueryHeadlines(todo = "TODO")
            Assert.True(results.Length > 0)
            Assert.Equal(Some "q6u", results.[0].CustomId)
        finally
            Directory.Delete(dir, true))
