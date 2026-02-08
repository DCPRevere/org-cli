module OrgCli.Tests.IndexIntegrationTests

open System
open System.IO
open Xunit
open OrgCli.Org
open OrgCli.Index
open OrgCli.Index.IndexDatabase
open OrgCli.Index.IndexSync

let private tempDbPath () =
    Path.Combine(Path.GetTempPath(), sprintf "org-index-integ-test-%s.db" (Guid.NewGuid().ToString("N")))

let private tempDir () =
    let d =
        Path.Combine(Path.GetTempPath(), sprintf "org-index-integ-test-%s" (Guid.NewGuid().ToString("N")))

    Directory.CreateDirectory(d) |> ignore
    d

let private cleanup (paths: string list) =
    for p in paths do
        if File.Exists(p) then
            File.Delete(p)

        if Directory.Exists(p) then
            Directory.Delete(p, true)

    for p in paths do
        let wal = p + "-wal"
        let shm = p + "-shm"

        if File.Exists(wal) then
            File.Delete(wal)

        if File.Exists(shm) then
            File.Delete(shm)

let private writeOrgFile (dir: string) (name: string) (content: string) =
    let path = Path.Combine(dir, name)
    File.WriteAllText(path, content)
    path

// ── Full workflow ──

[<Fact>]
let ``org index creates database from directory of org files`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        writeOrgFile dir "a.org" "* TODO Task A :work:\nBody of A\n" |> ignore
        writeOrgFile dir "b.org" "* DONE Task B\n** Subtask B1\n" |> ignore

        writeOrgFile dir "c.org" "#+filetags: :project:\n* Task C\nSCHEDULED: <2026-02-10 Tue>\n"
        |> ignore

        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        syncDirectory db dir
        let allFiles = db.GetAllFiles()
        Assert.Equal(3, allFiles.Length)
        // a.org has 1 headline, b.org has 2, c.org has 1
        let aHeadlines = db.GetHeadlines(Path.Combine(dir, "a.org"))
        let bHeadlines = db.GetHeadlines(Path.Combine(dir, "b.org"))
        let cHeadlines = db.GetHeadlines(Path.Combine(dir, "c.org"))
        Assert.Equal(1, aHeadlines.Length)
        Assert.Equal(2, bHeadlines.Length)
        Assert.Equal(1, cHeadlines.Length)
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``syncFile reflects changed TODO state after file rewrite`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        let filePath = writeOrgFile dir "test.org" "* TODO My task\nBody here\n"
        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        syncDirectory db dir
        let before = db.GetHeadlines(filePath)
        Assert.Equal(Some "TODO", before.[0].Todo)
        // Also verify body and FTS indexed
        Assert.True(before.[0].Body.IsSome)
        let ftsBefore = db.SearchFts("body")
        Assert.True(ftsBefore.Length > 0)

        // Rewrite file (simulating what a mutation would produce)
        File.WriteAllText(filePath, "* DONE My task\nBody here\n")
        syncFile db filePath

        let after = db.GetHeadlines(filePath)
        Assert.Equal(1, after.Length)
        Assert.Equal(Some "DONE", after.[0].Todo)
        Assert.Equal("My task", after.[0].Title)
        // FTS must reflect new state, not have stale duplicates
        let ftsAfter = db.SearchFts("body")
        Assert.Equal(1, ftsAfter.Length)
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``syncFile after multi-headline rewrite reflects final state`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        let filePath =
            writeOrgFile dir "test.org" "* TODO Task 1\n* TODO Task 2\n* TODO Task 3\n"

        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        syncDirectory db dir
        let before = db.GetHeadlines(filePath)
        Assert.Equal(3, before.Length)
        Assert.True(before |> List.forall (fun h -> h.Todo = Some "TODO"))

        // Rewrite with different states (simulating batch result)
        File.WriteAllText(filePath, "* DONE Task 1\n* NEXT Task 2\n* WAITING Task 3\n")
        syncFile db filePath

        let after = db.GetHeadlines(filePath)
        Assert.Equal(3, after.Length)
        // Verify each headline has its correct new state
        Assert.Equal(Some "DONE", after.[0].Todo)
        Assert.Equal(Some "NEXT", after.[1].Todo)
        Assert.Equal(Some "WAITING", after.[2].Todo)
        // No leftover TODO headlines
        Assert.True(after |> List.forall (fun h -> h.Todo <> Some "TODO"))
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``Pre-query sync catches external edit`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        let filePath = writeOrgFile dir "test.org" "* Original\n"
        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        syncDirectory db dir

        System.Threading.Thread.Sleep(1100)
        File.WriteAllText(filePath, "* Externally edited\n* New headline\n")

        // Pre-query sync
        syncDirectory db dir
        let headlines = db.GetHeadlines(filePath)
        Assert.Equal(2, headlines.Length)
        Assert.Equal("Externally edited", headlines.[0].Title)
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``Pre-query sync catches external file deletion`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        let filePath = writeOrgFile dir "test.org" "* Headline\n"
        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        syncDirectory db dir
        Assert.Equal(1, (db.GetHeadlines(filePath)).Length)

        File.Delete(filePath)
        syncDirectory db dir
        Assert.Equal(0, (db.GetHeadlines(filePath)).Length)
        Assert.True((db.GetFile(filePath)).IsNone)
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``syncFile on two files reflects cross-file headline move`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        let srcPath = writeOrgFile dir "source.org" "* Task to refile\nBody\n* Remaining\n"
        let dstPath = writeOrgFile dir "target.org" "* Existing target\n"
        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        syncDirectory db dir
        Assert.Equal(2, (db.GetHeadlines(srcPath)).Length)
        Assert.Equal(1, (db.GetHeadlines(dstPath)).Length)
        // Verify FTS has entries for both files
        let ftsBefore = db.SearchFts("refile")
        Assert.Equal(1, ftsBefore.Length)
        Assert.Equal(srcPath, ftsBefore.[0].File)

        // Simulate refile result: headline moved from source to target
        File.WriteAllText(srcPath, "* Remaining\n")
        File.WriteAllText(dstPath, "* Existing target\n* Task to refile\nBody\n")
        syncFile db srcPath
        syncFile db dstPath

        Assert.Equal(1, (db.GetHeadlines(srcPath)).Length)
        Assert.Equal(2, (db.GetHeadlines(dstPath)).Length)
        // FTS must reflect the move: "refile" now in target, not source
        let ftsAfter = db.SearchFts("refile")
        Assert.Equal(1, ftsAfter.Length)
        Assert.Equal(dstPath, ftsAfter.[0].File)
        // Source must have no stale FTS entries
        let ftsSource = db.SearchFts("remaining")
        Assert.True(ftsSource.Length > 0)
        Assert.Equal(srcPath, ftsSource.[0].File)
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``Index with filetags propagates to all headlines`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        let filePath =
            writeOrgFile dir "test.org" "#+filetags: :project:urgent:\n* First\n* Second\n** Third\n"

        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        syncDirectory db dir
        let headlines = db.GetHeadlines(filePath)

        for h in headlines do
            let tags = db.GetTags(filePath, h.CharPos)
            let tagNames = tags |> List.map (fun t -> t.Tag)
            Assert.Contains("project", tagNames)
            Assert.Contains("urgent", tagNames)
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``Index with nested tag inheritance`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        let filePath =
            writeOrgFile dir "test.org" "* Parent :work:\n** Middle :coding:\n*** Child\n"

        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        syncDirectory db dir
        let filePath' = Path.Combine(dir, "test.org")
        let headlines = db.GetHeadlines(filePath')
        let child = headlines |> List.find (fun h -> h.Title = "Child")
        let tags = db.GetTags(filePath', child.CharPos)
        let tagNames = tags |> List.map (fun t -> t.Tag)
        Assert.Contains("work", tagNames)
        Assert.Contains("coding", tagNames)
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``Concurrent read during write does not block`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        writeOrgFile dir "test.org" "* Headline\n" |> ignore
        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        syncDirectory db dir

        // Open a second raw connection and hold an exclusive write lock
        use conn2 =
            new Microsoft.Data.Sqlite.SqliteConnection(sprintf "Data Source=%s" dbPath)

        conn2.Open()
        use walCmd = conn2.CreateCommand()
        walCmd.CommandText <- "PRAGMA journal_mode=WAL"
        walCmd.ExecuteNonQuery() |> ignore
        use beginCmd = conn2.CreateCommand()
        beginCmd.CommandText <- "BEGIN IMMEDIATE"
        beginCmd.ExecuteNonQuery() |> ignore
        use insertCmd = conn2.CreateCommand()
        insertCmd.CommandText <- "INSERT INTO index_files (path, hash, mtime) VALUES ('/dummy', 'x', 0)"
        insertCmd.ExecuteNonQuery() |> ignore
        // conn2 now holds a write lock (RESERVED state)

        // Read from first connection — must succeed under WAL
        let files = db.GetAllFiles()
        Assert.True(files.Length >= 1, "Reader must not be blocked by concurrent writer under WAL")

        use rollbackCmd = conn2.CreateCommand()
        rollbackCmd.CommandText <- "ROLLBACK"
        rollbackCmd.ExecuteNonQuery() |> ignore
    finally
        cleanup [ dir; dbPath ]

// ── Edge cases ──

[<Fact>]
let ``Empty org file produces no headlines`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        let filePath = writeOrgFile dir "empty.org" ""
        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        syncDirectory db dir
        let headlines = db.GetHeadlines(filePath)
        Assert.Equal(0, headlines.Length)
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``File with only file-level content and no headlines`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        let filePath =
            writeOrgFile dir "preamble.org" "#+title: Just a title\n\nSome paragraph text.\n"

        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        syncDirectory db dir
        let headlines = db.GetHeadlines(filePath)
        Assert.Equal(0, headlines.Length)
        // File should still be tracked
        let file = db.GetFile(filePath)
        Assert.True(file.IsSome)
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``Unicode content in titles and body`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        let filePath =
            writeOrgFile
                dir
                "unicode.org"
                "* \u4F60\u597D\u4E16\u754C\n\u3053\u3093\u306B\u3061\u306F body text \U0001F680\n"

        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        syncDirectory db dir
        let headlines = db.GetHeadlines(filePath)
        Assert.Equal(1, headlines.Length)
        Assert.Equal("\u4F60\u597D\u4E16\u754C", headlines.[0].Title)
        Assert.True(headlines.[0].Body.IsSome)
        Assert.Contains("\u3053\u3093\u306B\u3061\u306F", headlines.[0].Body.Value)
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``Very large body text`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        let largeBody = String.replicate 10000 "This is a line of body text.\n"

        let filePath =
            writeOrgFile dir "large.org" (sprintf "* Large headline\n%s" largeBody)

        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        syncDirectory db dir
        let headlines = db.GetHeadlines(filePath)
        Assert.Equal(1, headlines.Length)
        Assert.True(headlines.[0].Body.IsSome)
        Assert.True(headlines.[0].Body.Value.Length > 100000)
        // Verify FTS also indexed it
        db.RebuildFtsForFile(filePath)
        let results = db.SearchFts("body text")
        Assert.True(results.Length > 0)
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``Headline with all optional fields null`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        let filePath = writeOrgFile dir "minimal.org" "* Just a title\n"
        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        syncDirectory db dir
        let headlines = db.GetHeadlines(filePath)
        Assert.Equal(1, headlines.Length)
        let h = headlines.[0]
        Assert.Equal("Just a title", h.Title)
        Assert.True(h.Todo.IsNone)
        Assert.True(h.Priority.IsNone)
        Assert.True(h.Scheduled.IsNone)
        Assert.True(h.ScheduledDt.IsNone)
        Assert.True(h.Deadline.IsNone)
        Assert.True(h.DeadlineDt.IsNone)
        Assert.True(h.Closed.IsNone)
        Assert.True(h.ClosedDt.IsNone)
    finally
        cleanup [ dir; dbPath ]

// ── Mutation + auto-sync ──

[<Fact>]
let ``syncFile after setTodoState mutation reflects new state`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        let filePath = writeOrgFile dir "test.org" "* TODO My task\nBody here\n"
        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        syncDirectory db dir
        let before = db.GetHeadlines(filePath)
        Assert.Equal(Some "TODO", before.[0].Todo)

        let content = File.ReadAllText(filePath)

        let cfg =
            { Types.defaultConfig with
                LogDone = LogAction.NoLog }

        let newContent = Mutations.setTodoState cfg content 0L (Some "DONE") DateTime.Now
        File.WriteAllText(filePath, newContent)
        syncFile db filePath

        let after = db.GetHeadlines(filePath)
        Assert.Equal(1, after.Length)
        Assert.Equal(Some "DONE", after.[0].Todo)
        Assert.Equal("My task", after.[0].Title)
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``syncFile after addTag mutation reflects new tag`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        let filePath = writeOrgFile dir "test.org" "* Headline\nBody\n"
        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        syncDirectory db dir

        let content = File.ReadAllText(filePath)
        let newContent = Mutations.addTag content 0L "work"
        File.WriteAllText(filePath, newContent)
        syncFile db filePath

        let headlines = db.GetHeadlines(filePath)
        Assert.Equal(1, headlines.Length)
        let tags = db.GetTags(filePath, headlines.[0].CharPos)
        Assert.True(tags |> List.exists (fun t -> t.Tag = "work" && not t.Inherited))
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``syncFile after setScheduled mutation reflects timestamp`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        let filePath = writeOrgFile dir "test.org" "* My task\n"
        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        syncDirectory db dir
        let before = db.GetHeadlines(filePath)
        Assert.True(before.[0].Scheduled.IsNone)

        let content = File.ReadAllText(filePath)
        let ts = Utils.parseDate "2026-03-15"

        let newContent =
            Mutations.setScheduled Types.defaultConfig content 0L (Some ts) DateTime.Now

        File.WriteAllText(filePath, newContent)
        syncFile db filePath

        let after = db.GetHeadlines(filePath)
        Assert.Equal(1, after.Length)
        Assert.True(after.[0].Scheduled.IsSome)
        Assert.True(after.[0].ScheduledDt.IsSome)
        Assert.True(after.[0].ScheduledDt.Value.StartsWith("2026-03-15"))
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``syncFile after refile mutation updates both files`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        let srcPath =
            writeOrgFile dir "source.org" "* TODO Refile me\nBody text\n* Stay here\n"

        let dstPath = writeOrgFile dir "target.org" "* Existing\n"
        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        syncDirectory db dir
        Assert.Equal(2, (db.GetHeadlines(srcPath)).Length)
        Assert.Equal(1, (db.GetHeadlines(dstPath)).Length)

        let srcContent = File.ReadAllText(srcPath)
        let dstContent = File.ReadAllText(dstPath)
        let dstDoc = Document.parse dstContent

        let dstPos =
            match dstDoc.Headlines |> List.tryLast with
            | Some h -> h.Position
            | None -> 0L

        let (newSrc, newDst) =
            Mutations.refile Types.defaultConfig srcContent 0L dstContent dstPos false DateTime.Now

        File.WriteAllText(srcPath, newSrc)
        File.WriteAllText(dstPath, newDst)
        syncFile db srcPath
        syncFile db dstPath

        let srcAfter = db.GetHeadlines(srcPath)
        let dstAfter = db.GetHeadlines(dstPath)
        Assert.Equal(1, srcAfter.Length)
        Assert.Equal("Stay here", srcAfter.[0].Title)
        Assert.True(dstAfter.Length >= 2)
        Assert.True(dstAfter |> List.exists (fun h -> h.Title = "Refile me"))
        // FTS reflects the move
        let fts = db.SearchFts("Refile")
        Assert.True(fts |> List.exists (fun r -> r.File = dstPath))
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``syncFile after archive mutation removes headline from source`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        let filePath =
            writeOrgFile dir "test.org" "* Keep this\n* DONE Archive me\nBody\n* Also keep\n"

        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        syncDirectory db dir
        Assert.Equal(3, (db.GetHeadlines(filePath)).Length)

        let content = File.ReadAllText(filePath)
        let doc = Document.parse content
        let archiveMe = doc.Headlines |> List.find (fun h -> h.Title = "Archive me")

        let (newSrc, _) =
            Mutations.archive content archiveMe.Position "" filePath [] DateTime.Now

        File.WriteAllText(filePath, newSrc)
        syncFile db filePath

        let after = db.GetHeadlines(filePath)
        Assert.Equal(2, after.Length)
        Assert.True(after |> List.forall (fun h -> h.Title <> "Archive me"))
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``syncFile after addNote mutation preserves existing body`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        let filePath = writeOrgFile dir "test.org" "* My task\nSome body text\n"
        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        syncDirectory db dir

        let content = File.ReadAllText(filePath)
        let newContent = Mutations.addNote content 0L "Important note" DateTime.Now
        File.WriteAllText(filePath, newContent)
        syncFile db filePath

        // addNote inserts into :LOGBOOK: drawer which HeadlineEdit.split
        // strips from the body (metadata position). Verify existing body
        // is preserved and the file content contains the note.
        let after = db.GetHeadlines(filePath)
        Assert.Equal(1, after.Length)
        Assert.True(after.[0].Body.IsSome)
        Assert.Contains("Some body text", after.[0].Body.Value)
        Assert.Contains("Important note", newContent)
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``syncFile after setProperty mutation reflects in properties JSON`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        let filePath = writeOrgFile dir "test.org" "* My task\nBody\n"
        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        syncDirectory db dir

        let content = File.ReadAllText(filePath)
        let newContent = Mutations.setProperty content 0L "CATEGORY" "work"
        File.WriteAllText(filePath, newContent)
        syncFile db filePath

        let after = db.GetHeadlines(filePath)
        Assert.Equal(1, after.Length)
        Assert.True(after.[0].Properties.IsSome)
        Assert.Contains("CATEGORY", after.[0].Properties.Value)
        Assert.Contains("work", after.[0].Properties.Value)
    finally
        cleanup [ dir; dbPath ]

// ── FTS error handling ──

[<Fact>]
let ``Malformed FTS query throws catchable exception`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        writeOrgFile dir "test.org" "* Headline\nBody\n" |> ignore
        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        syncDirectory db dir

        let ex =
            Assert.ThrowsAny<Exception>(fun () -> db.SearchFts("\"unclosed phrase") |> ignore)

        Assert.True(ex.Message.Length > 0)
    finally
        cleanup [ dir; dbPath ]
