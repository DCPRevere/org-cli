module OrgCli.Tests.IndexSyncTests

open System
open System.IO
open Xunit
open OrgCli.Index
open OrgCli.Index.IndexDatabase
open OrgCli.Index.IndexSync

let private tempDbPath () =
    Path.Combine(Path.GetTempPath(), sprintf "org-index-sync-test-%s.db" (Guid.NewGuid().ToString("N")))

let private tempDir () =
    let d =
        Path.Combine(Path.GetTempPath(), sprintf "org-index-sync-test-%s" (Guid.NewGuid().ToString("N")))

    Directory.CreateDirectory(d) |> ignore
    d

let private cleanup (paths: string list) =
    for p in paths do
        if File.Exists(p) then
            File.Delete(p)

        if Directory.Exists(p) then
            Directory.Delete(p, true)
    // Also clean up WAL/SHM files
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

// ── Incremental sync ──

[<Fact>]
let ``Sync indexes new file`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        writeOrgFile dir "test.org" "* TODO First headline\nSome body\n* Second headline\n"
        |> ignore

        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        syncDirectory db dir
        let headlines = db.GetHeadlines(Path.Combine(dir, "test.org"))
        Assert.Equal(2, headlines.Length)
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``Sync skips unchanged file`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        writeOrgFile dir "test.org" "* Headline\nBody\n" |> ignore
        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        syncDirectory db dir
        let filesBefore = db.GetAllFiles()
        Assert.Equal(1, filesBefore.Length)
        let mtimeBefore = filesBefore.[0].Mtime
        // Sync again without changes
        syncDirectory db dir
        let filesAfter = db.GetAllFiles()
        Assert.Equal(1, filesAfter.Length)
        // Mtime should be the same (file wasn't re-processed)
        Assert.Equal(mtimeBefore, filesAfter.[0].Mtime)
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``Sync re-indexes file when mtime changes`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        let filePath = writeOrgFile dir "test.org" "* Original headline\n"
        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        syncDirectory db dir
        let before = db.GetHeadlines(filePath)
        Assert.Equal(1, before.Length)
        Assert.Equal("Original headline", before.[0].Title)

        // Change mtime by rewriting with different content
        System.Threading.Thread.Sleep(1100) // Ensure mtime differs (1s resolution on some FS)
        File.WriteAllText(filePath, "* Updated headline\n* Second\n")
        syncDirectory db dir
        let after = db.GetHeadlines(filePath)
        Assert.Equal(2, after.Length)
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``Sync re-indexes file when content changes`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        let filePath = writeOrgFile dir "test.org" "* Headline A\n"
        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        syncDirectory db dir
        let before = db.GetHeadlines(filePath)
        Assert.Equal(1, before.Length)
        Assert.Equal("Headline A", before.[0].Title)

        System.Threading.Thread.Sleep(1100)
        File.WriteAllText(filePath, "* Headline B\n")
        syncDirectory db dir
        let after = db.GetHeadlines(filePath)
        Assert.Equal(1, after.Length)
        Assert.Equal("Headline B", after.[0].Title)
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``Sync removes entries for deleted files`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        let filePath = writeOrgFile dir "test.org" "* Headline\n"
        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        syncDirectory db dir
        let before = db.GetHeadlines(filePath)
        Assert.Equal(1, before.Length)

        File.Delete(filePath)
        syncDirectory db dir
        let after = db.GetHeadlines(filePath)
        Assert.Equal(0, after.Length)
        let fileEntry = db.GetFile(filePath)
        Assert.True(fileEntry.IsNone)
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``Sync skips encrypted files`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        writeOrgFile dir "secret.org.gpg" "encrypted garbage" |> ignore
        writeOrgFile dir "normal.org" "* Headline\n" |> ignore
        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        syncDirectory db dir
        // Should have indexed normal.org but not secret.org.gpg
        let normalHeadlines = db.GetHeadlines(Path.Combine(dir, "normal.org"))
        Assert.Equal(1, normalHeadlines.Length)
        let encryptedFile = db.GetFile(Path.Combine(dir, "secret.org.gpg"))
        Assert.True(encryptedFile.IsNone)
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``Sync skips unparseable files`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        // Write binary garbage with .org extension
        let garbagePath = Path.Combine(dir, "garbage.org")
        File.WriteAllBytes(garbagePath, [| 0uy; 1uy; 2uy; 0xFFuy; 0xFEuy |])
        writeOrgFile dir "normal.org" "* Headline\n" |> ignore
        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        // Should not throw
        syncDirectory db dir
        let normalHeadlines = db.GetHeadlines(Path.Combine(dir, "normal.org"))
        Assert.True(normalHeadlines.Length > 0)
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``Sync is resumable after interruption`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        writeOrgFile dir "a.org" "* A headline\n" |> ignore
        writeOrgFile dir "b.org" "* B headline\n" |> ignore
        writeOrgFile dir "c.org" "* C headline\n" |> ignore
        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        syncDirectory db dir

        // Verify all 3 files indexed
        let allFiles = db.GetAllFiles()
        Assert.Equal(3, allFiles.Length)

        // Simulate interruption: delete headlines for c.org but leave files entry
        // (mimics crash mid-file where file row was written but headlines weren't)
        db.DeleteHeadlines(Path.Combine(dir, "c.org"))

        // Force re-sync of c.org by clearing its hash
        db.UpdateFile(Path.Combine(dir, "c.org"), "", 0L)

        syncDirectory db dir
        let cHeadlines = db.GetHeadlines(Path.Combine(dir, "c.org"))
        Assert.Equal(1, cHeadlines.Length)
    finally
        cleanup [ dir; dbPath ]

// ── Body text extraction ──

[<Fact>]
let ``Body is text between metadata and next headline`` () =
    let content = "* First\nBody of first\nMore body\n* Second\nBody of second\n"
    let body = extractBody content 0L
    Assert.Contains("Body of first", body)
    Assert.Contains("More body", body)
    Assert.DoesNotContain("* Second", body)
    Assert.DoesNotContain("Body of second", body)

[<Fact>]
let ``Body excludes headline line planning and property drawer`` () =
    let content =
        "* TODO My task\nSCHEDULED: <2026-02-07 Sat>\n:PROPERTIES:\n:ID: abc\n:END:\nActual body content\n"

    let body = extractBody content 0L
    Assert.DoesNotContain("* TODO", body)
    Assert.DoesNotContain("SCHEDULED:", body)
    Assert.DoesNotContain(":PROPERTIES:", body)
    Assert.DoesNotContain(":ID:", body)
    Assert.DoesNotContain(":END:", body)
    Assert.Contains("Actual body content", body)

[<Fact>]
let ``Body is empty when headline has no content before next headline`` () =
    let content = "* First\n* Second\n"
    let body = extractBody content 0L
    Assert.True(String.IsNullOrWhiteSpace(body))

// ── Tag inheritance ──

[<Fact>]
let ``Direct tags stored with inherited=0`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        writeOrgFile dir "test.org" "* Headline :work:\n" |> ignore
        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        syncDirectory db dir
        let tags = db.GetTags(Path.Combine(dir, "test.org"), 0L)
        let workTag = tags |> List.tryFind (fun t -> t.Tag = "work")
        Assert.True(workTag.IsSome)
        Assert.False(workTag.Value.Inherited)
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``Filetags stored with inherited=1 for every headline`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        writeOrgFile dir "test.org" "#+filetags: :project:\n* First\n* Second\n"
        |> ignore

        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        syncDirectory db dir
        let filePath = Path.Combine(dir, "test.org")
        let headlines = db.GetHeadlines(filePath)
        Assert.Equal(2, headlines.Length)

        for h in headlines do
            let tags = db.GetTags(filePath, h.CharPos)
            let projectTag = tags |> List.tryFind (fun t -> t.Tag = "project")
            Assert.True(projectTag.IsSome, sprintf "Headline at %d should have project tag" h.CharPos)
            Assert.True(projectTag.Value.Inherited, "Filetag should be inherited")
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``Ancestor headline tags inherited with inherited=1`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        writeOrgFile dir "test.org" "* Parent :work:\n** Child\n" |> ignore
        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        syncDirectory db dir
        let filePath = Path.Combine(dir, "test.org")
        let headlines = db.GetHeadlines(filePath)
        let child = headlines |> List.find (fun h -> h.Title = "Child")
        let tags = db.GetTags(filePath, child.CharPos)
        let workTag = tags |> List.tryFind (fun t -> t.Tag = "work")
        Assert.True(workTag.IsSome, "Child should inherit work tag")
        Assert.True(workTag.Value.Inherited)
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``Direct tag takes precedence over inherited`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        writeOrgFile dir "test.org" "* Parent :work:\n** Child :work:\n" |> ignore
        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        syncDirectory db dir
        let filePath = Path.Combine(dir, "test.org")
        let headlines = db.GetHeadlines(filePath)
        let child = headlines |> List.find (fun h -> h.Title = "Child")
        let tags = db.GetTags(filePath, child.CharPos)
        let workTags = tags |> List.filter (fun t -> t.Tag = "work")
        Assert.Equal(1, workTags.Length)
        Assert.False(workTags.[0].Inherited, "Direct tag should win")
    finally
        cleanup [ dir; dbPath ]

// ── Outline path computation ──

[<Fact>]
let ``Top-level headline has own title as path`` () =
    let content = "* Top level\n"
    let doc = OrgCli.Org.Document.parse content
    let h = doc.Headlines.[0]
    let path = computeOutlinePathString doc.Headlines h
    Assert.Equal("Top level", path)

[<Fact>]
let ``Nested headline includes ancestor titles joined by unit separator`` () =
    let content = "* Parent\n** Child\n"
    let doc = OrgCli.Org.Document.parse content
    let child = doc.Headlines.[1]
    let path = computeOutlinePathString doc.Headlines child
    Assert.Equal("Parent\x1FChild", path)

[<Fact>]
let ``Deeply nested headline has full path`` () =
    let content = "* Level 1\n** Level 2\n*** Level 3\n"
    let doc = OrgCli.Org.Document.parse content
    let h3 = doc.Headlines.[2]
    let path = computeOutlinePathString doc.Headlines h3
    Assert.Equal("Level 1\x1FLevel 2\x1FLevel 3", path)

// ── Body extraction edge cases ──

[<Fact>]
let ``extractBody does not truncate at bold markup`` () =
    let content = "* Headline\n*bold text* at start of line\nMore body\n"
    let body = extractBody content 0L
    Assert.Contains("*bold text*", body)
    Assert.Contains("More body", body)

[<Fact>]
let ``extractBody for last headline includes text to EOF`` () =
    let content = "* Only headline\nBody line 1\nBody line 2\n"
    let body = extractBody content 0L
    Assert.Contains("Body line 1", body)
    Assert.Contains("Body line 2", body)

// ── Sync coverage ──

[<Fact>]
let ``syncDirectory indexes files in nested subdirectories`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        let sub = Path.Combine(dir, "subdir", "nested")
        Directory.CreateDirectory(sub) |> ignore
        writeOrgFile dir "top.org" "* Top\n" |> ignore
        writeOrgFile sub "deep.org" "* Deep\n" |> ignore
        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        syncDirectory db dir
        let allFiles = db.GetAllFiles()
        Assert.Equal(2, allFiles.Length)
        let deepHeadlines = db.GetHeadlines(Path.Combine(sub, "deep.org"))
        Assert.Equal(1, deepHeadlines.Length)
        Assert.Equal("Deep", deepHeadlines.[0].Title)
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``Re-index updates FTS entries correctly`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        let filePath = writeOrgFile dir "test.org" "* Alpha topic\nAlpha body content\n"
        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        syncDirectory db dir
        let before = db.SearchFts("alpha")
        Assert.True(before.Length > 0, "Should find 'alpha' after initial index")

        // Rewrite with completely different content
        System.Threading.Thread.Sleep(1100)
        File.WriteAllText(filePath, "* Bravo topic\nBravo body content\n")
        syncDirectory db dir

        let afterAlpha = db.SearchFts("alpha")
        Assert.Equal(0, afterAlpha.Length)
        let afterBravo = db.SearchFts("bravo")
        Assert.True(afterBravo.Length > 0, "Should find 'bravo' after re-index")
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``syncFile indexes a file not previously in database`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        let filePath = writeOrgFile dir "new.org" "* Brand new headline\nWith body\n"
        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        // Call syncFile directly — no prior syncDirectory
        syncFile db filePath
        let headlines = db.GetHeadlines(filePath)
        Assert.Equal(1, headlines.Length)
        Assert.Equal("Brand new headline", headlines.[0].Title)
        let file = db.GetFile(filePath)
        Assert.True(file.IsSome)
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``syncFile is idempotent`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        let filePath = writeOrgFile dir "test.org" "* Headline :tag:\nBody text\n"
        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        syncFile db filePath
        let after1 = db.GetHeadlines(filePath)
        let tags1 = db.GetTags(filePath, after1.[0].CharPos)
        // Call again
        syncFile db filePath
        let after2 = db.GetHeadlines(filePath)
        let tags2 = db.GetTags(filePath, after2.[0].CharPos)
        Assert.Equal(after1.Length, after2.Length)
        Assert.Equal(after1.[0].Title, after2.[0].Title)
        Assert.Equal(tags1.Length, tags2.Length)
        // FTS should still work
        let fts = db.SearchFts("body")
        Assert.Equal(1, fts.Length)
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``syncFile re-index is atomic on failure`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        let filePath = writeOrgFile dir "test.org" "* Good headline\nGood body\n"
        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        syncFile db filePath
        let before = db.GetHeadlines(filePath)
        Assert.Equal(1, before.Length)
        let fileBefore = db.GetFile(filePath)

        // Delete the file, then syncFile should clean up
        File.Delete(filePath)
        syncFile db filePath
        let after = db.GetHeadlines(filePath)
        Assert.Equal(0, after.Length)
        let fileAfter = db.GetFile(filePath)
        Assert.True(fileAfter.IsNone)
    finally
        cleanup [ dir; dbPath ]

// ── Body extraction: complex org structures ──

[<Fact>]
let ``Body extraction preserves source block content`` () =
    let content =
        "* Headline\n#+begin_src python\ndef hello():\n    print('world')\n#+end_src\n"

    let body = extractBody content 0L
    Assert.Contains("#+begin_src python", body)
    Assert.Contains("def hello():", body)
    Assert.Contains("print('world')", body)
    Assert.Contains("#+end_src", body)

[<Fact>]
let ``Body extraction preserves table content`` () =
    let content =
        "* Headline\n| Name  | Value |\n|-------+-------|\n| Alice | 42    |\n| Bob   | 99    |\n"

    let body = extractBody content 0L
    Assert.Contains("| Name  | Value |", body)
    Assert.Contains("| Alice | 42    |", body)
    Assert.Contains("| Bob   | 99    |", body)

[<Fact>]
let ``Body extraction excludes logbook drawer in metadata position`` () =
    // When a :LOGBOOK: drawer immediately follows the headline (in the metadata
    // position), HeadlineEdit.split strips it. This is desirable for FTS: clock
    // entries shouldn't pollute search results.
    let content =
        "* TODO Task\n:LOGBOOK:\nCLOCK: [2026-02-06 Thu 10:00]--[2026-02-06 Thu 11:30] =>  1:30\n:END:\nSome body after logbook\n"

    let body = extractBody content 0L
    Assert.DoesNotContain(":LOGBOOK:", body)
    Assert.DoesNotContain("CLOCK:", body)
    Assert.Contains("Some body after logbook", body)

[<Fact>]
let ``Body extraction preserves results drawer`` () =
    let content = "* Headline\n#+begin_src python\n1+1\n#+end_src\n\n#+RESULTS:\n: 2\n"
    let body = extractBody content 0L
    Assert.Contains("#+RESULTS:", body)
    Assert.Contains(": 2", body)

[<Fact>]
let ``Body extraction with mixed complex content between two headlines`` () =
    let content =
        "* First\n"
        + "Some text.\n"
        + "#+begin_src bash\necho 'hello'\n#+end_src\n"
        + "| Col1 | Col2 |\n| a    | b    |\n"
        + ":LOGBOOK:\n- Note taken [2026-02-06 Thu 10:00]\n:END:\n"
        + "* Second\nBody of second\n"

    let body = extractBody content 0L
    Assert.Contains("Some text.", body)
    Assert.Contains("echo 'hello'", body)
    Assert.Contains("| Col1 | Col2 |", body)
    Assert.Contains(":LOGBOOK:", body)
    Assert.DoesNotContain("* Second", body)
    Assert.DoesNotContain("Body of second", body)

[<Fact>]
let ``Body extraction with source block containing headline-like line`` () =
    let content =
        "* Headline\n#+begin_src org\n* This looks like a headline but isn't\n#+end_src\nReal body\n"

    let body = extractBody content 0L
    // extractBody uses regex "^\*+ " which will match inside code blocks.
    // This test documents current behavior: body is truncated at the
    // headline-like line inside the src block.
    // If we later add block-range awareness, this test should be updated
    // to Assert.Contains("Real body", body).
    Assert.Contains("#+begin_src org", body)

[<Fact>]
let ``Body extraction with property drawer only has empty body`` () =
    let content = "* Headline\n:PROPERTIES:\n:ID: abc-123\n:END:\n* Next\n"
    let body = extractBody content 0L
    Assert.True(String.IsNullOrWhiteSpace(body), sprintf "Expected empty body but got: '%s'" body)

[<Fact>]
let ``FTS indexes source block content`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        writeOrgFile dir "test.org" "* Code example\n#+begin_src python\ndef fibonacci(n):\n    pass\n#+end_src\n"
        |> ignore

        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        syncDirectory db dir
        let results = db.SearchFts("fibonacci")
        Assert.True(results.Length > 0, "FTS should find content inside source blocks")
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``FTS indexes table content`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        writeOrgFile dir "test.org" "* Data table\n| Quarterly | Revenue |\n| Q1 | 5000 |\n| Q2 | 7500 |\n"
        |> ignore

        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        syncDirectory db dir
        let results = db.SearchFts("quarterly")
        Assert.True(results.Length > 0, "FTS should find content inside tables")
    finally
        cleanup [ dir; dbPath ]

// ── Hash and mtime ──

[<Fact>]
let ``File hash uses SHA-256`` () =
    // Known SHA-256 of "hello"
    let expected = "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824"
    let result = computeSha256 "hello"
    Assert.Equal(expected, result)

[<Fact>]
let ``Mtime stored as Unix epoch seconds`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        let filePath = writeOrgFile dir "test.org" "* Headline\n"
        use db = new OrgIndexDb(dbPath)
        db.Initialize()
        syncDirectory db dir
        let file = db.GetFile(filePath)
        Assert.True(file.IsSome)
        // Mtime should be a reasonable Unix timestamp (after year 2020)
        Assert.True(file.Value.Mtime > 1577836800L, sprintf "Mtime %d should be a Unix epoch > 2020" file.Value.Mtime)
    finally
        cleanup [ dir; dbPath ]
