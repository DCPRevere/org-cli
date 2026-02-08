module OrgCli.Tests.CliHandlerTests

open System
open System.IO
open System.Text.Json.Nodes
open Xunit
open OrgCli.Index

/// Capture stdout during f(), returning (captured text, f's return value).
let private captureStdout (f: unit -> 'a) : string * 'a =
    let sw = new StringWriter()
    let old = Console.Out
    Console.SetOut(sw)

    try
        let result = f ()
        Console.SetOut(old)
        sw.ToString(), result
    with ex ->
        Console.SetOut(old)
        reraise ()

/// Capture stderr during f(), returning (captured text, f's return value).
let private captureStderr (f: unit -> 'a) : string * 'a =
    let sw = new StringWriter()
    let old = Console.Error
    Console.SetError(sw)

    try
        let result = f ()
        Console.SetError(old)
        sw.ToString(), result
    with ex ->
        Console.SetError(old)
        reraise ()

/// Capture both stdout and stderr.
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

let private tempDir () =
    let d =
        Path.Combine(Path.GetTempPath(), sprintf "org-cli-handler-test-%s" (Guid.NewGuid().ToString("N")))

    Directory.CreateDirectory(d) |> ignore
    d

let private tempDbPath () =
    Path.Combine(Path.GetTempPath(), sprintf "org-cli-handler-test-%s.db" (Guid.NewGuid().ToString("N")))

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

let private makeOpts (pairs: (string * string) list) : Map<string, string list> =
    pairs
    |> List.groupBy fst
    |> List.map (fun (k, vs) -> k, vs |> List.map snd)
    |> Map.ofList

// ── resolveIndexDbPath ──

[<Fact>]
let ``resolveIndexDbPath defaults to .org-index.db in directory`` () =
    let dir = tempDir ()

    try
        let opts = makeOpts [ ("directory", dir) ]
        let path = Program.resolveIndexDbPath opts
        Assert.Equal(Path.Combine(dir, ".org-index.db"), path)
    finally
        cleanup [ dir ]

[<Fact>]
let ``resolveIndexDbPath respects --db flag`` () =
    let dir = tempDir ()
    let customDb = Path.Combine(dir, "custom.db")

    try
        let opts = makeOpts [ ("directory", dir); ("db", customDb) ]
        let path = Program.resolveIndexDbPath opts
        Assert.Equal(customDb, path)
    finally
        cleanup [ dir ]

// ── tryAutoSyncIndex ──

[<Fact>]
let ``tryAutoSyncIndex does nothing when no index db exists`` () =
    let dir = tempDir ()

    try
        let filePath = writeOrgFile dir "test.org" "* Headline\n"
        let opts = makeOpts [ ("directory", dir) ]
        // Should not throw, should not create a db
        Program.tryAutoSyncIndex opts [ filePath ]
        let dbPath = Path.Combine(dir, ".org-index.db")
        Assert.False(File.Exists(dbPath), "Should not create db when it doesn't exist")
    finally
        cleanup [ dir ]

[<Fact>]
let ``tryAutoSyncIndex silently handles corrupt db`` () =
    let dir = tempDir ()

    try
        let filePath = writeOrgFile dir "test.org" "* Headline\n"
        let dbPath = Path.Combine(dir, ".org-index.db")
        // Write garbage to the db file
        File.WriteAllBytes(dbPath, [| 0uy; 1uy; 2uy; 0xFFuy; 0xFEuy |])
        let opts = makeOpts [ ("directory", dir) ]
        // Should not throw
        Program.tryAutoSyncIndex opts [ filePath ]
    finally
        cleanup [ dir ]

[<Fact>]
let ``tryAutoSyncIndex syncs file when index db exists`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        let filePath = writeOrgFile dir "test.org" "* TODO Task\nBody\n"
        let opts = makeOpts [ ("directory", dir); ("db", dbPath) ]

        // Create the index db
        do
            use db = new IndexDatabase.OrgIndexDb(dbPath)
            db.Initialize()
            IndexSync.syncFile db filePath
            let before = db.GetHeadlines(filePath)
            Assert.Equal("TODO", before.[0].Todo.Value)

        // Mutate the file externally
        File.WriteAllText(filePath, "* DONE Task\nBody\n")

        // tryAutoSyncIndex should re-index
        Program.tryAutoSyncIndex opts [ filePath ]

        use db2 = new IndexDatabase.OrgIndexDb(dbPath)
        db2.Initialize()
        let after = db2.GetHeadlines(filePath)
        Assert.Equal(1, after.Length)
        Assert.Equal("DONE", after.[0].Todo.Value)
    finally
        cleanup [ dir; dbPath ]

// ── handleIndex ──

[<Fact>]
let ``handleIndex creates and populates index database`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        writeOrgFile dir "a.org" "* Alpha\n" |> ignore
        writeOrgFile dir "b.org" "* Bravo\n* Charlie\n" |> ignore
        let opts = makeOpts [ ("directory", dir); ("db", dbPath) ]

        let _, _, exitCode = captureBoth (fun () -> Program.handleIndex opts false true)
        Assert.Equal(0, exitCode)
        Assert.True(File.Exists(dbPath), "Database file should be created")

        use db = new IndexDatabase.OrgIndexDb(dbPath)
        db.Initialize()
        let files = db.GetAllFiles()
        Assert.Equal(2, files.Length)
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``handleIndex --force rebuilds index`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        let filePath = writeOrgFile dir "test.org" "* Original\n"
        let opts = makeOpts [ ("directory", dir); ("db", dbPath) ]
        captureBoth (fun () -> Program.handleIndex opts false true) |> ignore

        // Modify file but use same mtime trick: force should re-index regardless
        File.WriteAllText(filePath, "* Updated\n* Extra\n")
        let forceOpts = makeOpts [ ("directory", dir); ("db", dbPath); ("force", "true") ]
        captureBoth (fun () -> Program.handleIndex forceOpts false true) |> ignore

        use db = new IndexDatabase.OrgIndexDb(dbPath)
        db.Initialize()
        let headlines = db.GetHeadlines(filePath)
        Assert.Equal(2, headlines.Length)
        Assert.Equal("Updated", headlines.[0].Title)
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``handleIndex JSON output has ok, files, and db fields`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        writeOrgFile dir "test.org" "* Headline\n" |> ignore
        let opts = makeOpts [ ("directory", dir); ("db", dbPath) ]

        let stdout, _, exitCode = captureBoth (fun () -> Program.handleIndex opts true true)
        Assert.Equal(0, exitCode)

        let json = JsonNode.Parse(stdout.Trim())
        Assert.True(json["ok"].GetValue<bool>())
        let data = json["data"]
        Assert.Equal(1, data["files"].GetValue<int>())
        Assert.Equal(dbPath, data["db"].GetValue<string>())
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``handleIndex returns 0 for empty directory`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        let opts = makeOpts [ ("directory", dir); ("db", dbPath) ]
        let _, _, exitCode = captureBoth (fun () -> Program.handleIndex opts false true)
        Assert.Equal(0, exitCode)
    finally
        cleanup [ dir; dbPath ]

// ── handleFts ──

[<Fact>]
let ``handleFts returns error when no index exists`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        let opts = makeOpts [ ("directory", dir); ("db", dbPath) ]
        let stdout, _, exitCode = captureBoth (fun () -> Program.handleFts opts true "test")
        Assert.Equal(1, exitCode)

        let json = JsonNode.Parse(stdout.Trim())
        Assert.False(json["ok"].GetValue<bool>())
        let err = json["error"]
        Assert.Equal("invalid_args", err["type"].GetValue<string>())
        Assert.Contains("No index found", err["message"].GetValue<string>())
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``handleFts returns error for malformed query`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        writeOrgFile dir "test.org" "* Headline\nBody\n" |> ignore
        let opts = makeOpts [ ("directory", dir); ("db", dbPath) ]
        // Build the index first
        captureBoth (fun () -> Program.handleIndex opts false true) |> ignore

        let stdout, _, exitCode =
            captureBoth (fun () -> Program.handleFts opts true "\"unclosed phrase")

        Assert.Equal(1, exitCode)

        let json = JsonNode.Parse(stdout.Trim())
        Assert.False(json["ok"].GetValue<bool>())
        let err = json["error"]
        Assert.Equal("invalid_args", err["type"].GetValue<string>())
        Assert.Contains("Invalid FTS query", err["message"].GetValue<string>())
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``handleFts text output returns error for malformed query`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        writeOrgFile dir "test.org" "* Headline\nBody\n" |> ignore
        let opts = makeOpts [ ("directory", dir); ("db", dbPath) ]
        captureBoth (fun () -> Program.handleIndex opts false true) |> ignore

        let _, stderr, exitCode =
            captureBoth (fun () -> Program.handleFts opts false "\"unclosed phrase")

        Assert.Equal(1, exitCode)
        Assert.Contains("Invalid FTS query", stderr)
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``handleFts --no-sync skips pre-query sync`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        let filePath = writeOrgFile dir "test.org" "* Alpha topic\nAlpha body\n"
        let opts = makeOpts [ ("directory", dir); ("db", dbPath) ]
        captureBoth (fun () -> Program.handleIndex opts false true) |> ignore

        // Modify file externally — ensure mtime differs (1s FS resolution)
        System.Threading.Thread.Sleep(1100)
        File.WriteAllText(filePath, "* Beta topic\nBeta body\n")

        // With --no-sync, stale results should persist
        let noSyncOpts =
            makeOpts [ ("directory", dir); ("db", dbPath); ("no-sync", "true") ]

        let stdout, _, exitCode =
            captureBoth (fun () -> Program.handleFts noSyncOpts true "alpha")

        Assert.Equal(0, exitCode)

        let json = JsonNode.Parse(stdout.Trim())
        let results = json["data"] :?> JsonArray
        Assert.True(results.Count > 0, "Should still find 'alpha' because --no-sync skipped re-index")

        // Without --no-sync, sync should pick up the change
        let syncOpts = makeOpts [ ("directory", dir); ("db", dbPath) ]

        let stdout2, _, exitCode2 =
            captureBoth (fun () -> Program.handleFts syncOpts true "alpha")

        Assert.Equal(0, exitCode2)
        let json2 = JsonNode.Parse(stdout2.Trim())
        let results2 = json2["data"] :?> JsonArray
        Assert.Equal(0, results2.Count)
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``handleFts JSON output contains expected fields`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        writeOrgFile dir "test.org" "* Important meeting\nDiscuss project timeline\n"
        |> ignore

        let opts = makeOpts [ ("directory", dir); ("db", dbPath); ("no-sync", "true") ]
        captureBoth (fun () -> Program.handleIndex opts false true) |> ignore

        let stdout, _, exitCode =
            captureBoth (fun () -> Program.handleFts opts true "meeting")

        Assert.Equal(0, exitCode)

        let json = JsonNode.Parse(stdout.Trim())
        Assert.True(json["ok"].GetValue<bool>())
        let results = json["data"] :?> JsonArray
        Assert.True(results.Count > 0)
        let first = results.[0]
        // Verify all expected fields exist
        Assert.NotNull(first["file"])
        Assert.NotNull(first["char_pos"])
        Assert.NotNull(first["title"])
        Assert.NotNull(first["rank"])
        Assert.Equal("Important meeting", first["title"].GetValue<string>())
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``handleFts returns 0 with no results for valid query`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        writeOrgFile dir "test.org" "* Headline\nBody\n" |> ignore
        let opts = makeOpts [ ("directory", dir); ("db", dbPath); ("no-sync", "true") ]
        captureBoth (fun () -> Program.handleIndex opts false true) |> ignore

        let stdout, _, exitCode =
            captureBoth (fun () -> Program.handleFts opts true "xyznonexistent")

        Assert.Equal(0, exitCode)

        let json = JsonNode.Parse(stdout.Trim())
        Assert.True(json["ok"].GetValue<bool>())
        let results = json["data"] :?> JsonArray
        Assert.Equal(0, results.Count)
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``handleFts text output shows results`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        writeOrgFile dir "test.org" "* Important meeting\nProject timeline\n" |> ignore
        let opts = makeOpts [ ("directory", dir); ("db", dbPath); ("no-sync", "true") ]
        captureBoth (fun () -> Program.handleIndex opts true true) |> ignore

        let stdout, _, exitCode =
            captureBoth (fun () -> Program.handleFts opts false "meeting")

        Assert.Equal(0, exitCode)
        Assert.Contains("Important meeting", stdout)
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``handleFts text output shows No results for no match`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        writeOrgFile dir "test.org" "* Headline\nBody\n" |> ignore
        let opts = makeOpts [ ("directory", dir); ("db", dbPath); ("no-sync", "true") ]
        captureBoth (fun () -> Program.handleIndex opts true true) |> ignore

        let stdout, _, exitCode =
            captureBoth (fun () -> Program.handleFts opts false "xyznonexistent")

        Assert.Equal(0, exitCode)
        Assert.Contains("No results.", stdout)
    finally
        cleanup [ dir; dbPath ]

// ── CLI entry point argument routing ──

[<Fact>]
let ``main routes index command`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        writeOrgFile dir "test.org" "* Headline\n" |> ignore
        let args = [| "index"; "--directory"; dir; "--db"; dbPath; "--quiet" |]
        let _, _, exitCode = captureBoth (fun () -> Program.main args)
        Assert.Equal(0, exitCode)
        Assert.True(File.Exists(dbPath))
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``main routes fts command with query`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        writeOrgFile dir "test.org" "* Alpha topic\nAlpha body\n" |> ignore
        // First index
        Program.main [| "index"; "--directory"; dir; "--db"; dbPath; "--quiet" |]
        |> ignore

        let stdout, _, exitCode =
            captureBoth (fun () ->
                Program.main
                    [| "fts"
                       "alpha"
                       "--directory"
                       dir
                       "--db"
                       dbPath
                       "--no-sync"
                       "--format"
                       "json" |])

        Assert.Equal(0, exitCode)
        let json = JsonNode.Parse(stdout.Trim())
        let results = json["data"] :?> JsonArray
        Assert.True(results.Count > 0)
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``main returns 1 for fts without query`` () =
    let _, stderr, exitCode = captureBoth (fun () -> Program.main [| "fts" |])
    Assert.Equal(1, exitCode)

// ── tryAutoSyncRoam ──

[<Fact>]
let ``tryAutoSyncRoam does nothing when no roam db exists`` () =
    let dir = tempDir ()

    try
        let filePath = writeOrgFile dir "test.org" "* Headline\n"
        let dbPath = Path.Combine(dir, "nonexistent.db")
        let opts = makeOpts [ ("db", dbPath) ]
        // Should not throw, should not create a db
        Program.tryAutoSyncRoam opts [ filePath ]
        Assert.False(File.Exists(dbPath), "Should not create db when it doesn't exist")
    finally
        cleanup [ dir ]

[<Fact>]
let ``tryAutoSyncRoam silently handles corrupt db`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        let filePath =
            writeOrgFile dir "test.org" ":PROPERTIES:\n:ID: node-1\n:END:\n#+title: Test\n"

        // Write garbage to the db file
        File.WriteAllBytes(dbPath, [| 0uy; 1uy; 2uy; 0xFFuy; 0xFEuy |])
        let opts = makeOpts [ ("db", dbPath) ]
        // Should not throw
        Program.tryAutoSyncRoam opts [ filePath ]
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``tryAutoSyncRoam syncs file when roam db exists`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        let filePath =
            writeOrgFile dir "test.org" ":PROPERTIES:\n:ID: node-1\n:END:\n#+title: Original\n"

        let opts = makeOpts [ ("db", dbPath) ]

        // Create and populate the roam db via initial sync
        do
            use db = new OrgCli.Roam.Database.OrgRoamDb(dbPath)
            db.Initialize() |> ignore
            OrgCli.Roam.Sync.updateFile db dir filePath
            let node = db.GetNode("node-1")
            Assert.True(node.IsSome, "Node should exist after initial sync")

        // Modify the file externally (change title)
        File.WriteAllText(filePath, ":PROPERTIES:\n:ID: node-1\n:END:\n#+title: Updated\n")

        // tryAutoSyncRoam should re-sync
        Program.tryAutoSyncRoam opts [ filePath ]

        use db2 = new OrgCli.Roam.Database.OrgRoamDb(dbPath)
        db2.Initialize() |> ignore
        let node = db2.GetNode("node-1")
        Assert.True(node.IsSome, "Node should still exist after re-sync")
        Assert.Equal("Updated", node.Value.Title)
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``roam initializes on db created by index module`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        let filePath =
            writeOrgFile dir "test.org" ":PROPERTIES:\n:ID: node-1\n:END:\n#+title: Test\n"

        // Index creates the db first (user_version stays 0)
        do
            use indexDb = new OrgCli.Index.IndexDatabase.OrgIndexDb(dbPath)
            indexDb.Initialize()
            OrgCli.Index.IndexSync.syncFile indexDb filePath

        // Roam should initialize successfully on the same db
        use roamDb = new OrgCli.Roam.Database.OrgRoamDb(dbPath)

        match roamDb.Initialize() with
        | Error msg -> Assert.Fail(sprintf "Roam init should succeed on index-created db: %s" msg)
        | Ok() ->
            OrgCli.Roam.Sync.updateFile roamDb dir filePath
            let node = roamDb.GetNode("node-1")
            Assert.True(node.IsSome, "Roam should work in shared db")
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``mutation with --db auto-syncs roam database`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        let filePath =
            writeOrgFile
                dir
                "test.org"
                ":PROPERTIES:\n:ID: node-1\n:END:\n#+title: Tasks\n\n* TODO Buy groceries\n"

        // Create the roam db via initial sync
        do
            use db = new OrgCli.Roam.Database.OrgRoamDb(dbPath)
            db.Initialize() |> ignore
            OrgCli.Roam.Sync.updateFile db dir filePath

        // Run a mutation via CLI with --db
        let _, _, exitCode =
            captureBoth (fun () ->
                Program.main [| "todo"; filePath; "Buy groceries"; "DONE"; "--db"; dbPath; "--quiet" |])

        Assert.Equal(0, exitCode)

        // Verify the file was mutated
        let content = File.ReadAllText(filePath)
        Assert.Contains("DONE", content)

        // Verify the roam db was auto-synced (node still present with updated content)
        use db2 = new OrgCli.Roam.Database.OrgRoamDb(dbPath)
        db2.Initialize() |> ignore
        let node = db2.GetNode("node-1")
        Assert.True(node.IsSome, "Node should exist in roam db after mutation + auto-sync")
    finally
        cleanup [ dir; dbPath ]

// ── Refile without target via CLI ──

[<Fact>]
let ``refile without target headline appends at level 1`` () =
    let dir = tempDir ()

    try
        let srcFile = writeOrgFile dir "src.org" "* Source\nBody\n"
        let tgtFile = writeOrgFile dir "tgt.org" "* Existing\nBody\n"

        let _, _, exitCode =
            captureBoth (fun () -> Program.main [| "refile"; srcFile; "Source"; tgtFile; "--quiet" |])

        Assert.Equal(0, exitCode)

        let tgtContent = File.ReadAllText(tgtFile)
        let doc = OrgCli.Org.Document.parse tgtContent
        let source = doc.Headlines |> List.find (fun h -> h.Title = "Source")
        Assert.Equal(1, source.Level)
    finally
        cleanup [ dir ]
