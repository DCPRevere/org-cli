[<Xunit.Collection("ConsoleCapture")>]
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

// ── Config-based directory resolution in handlers ──

/// Helper: set up a temp config.json with directories pointing to dir, clear env var.
let private withConfigDir (dir: string) (dbPath: string) (f: Map<string, string list> -> 'a) : 'a =
    let oldEnv = Environment.GetEnvironmentVariable("ORG_CLI_DIRECTORY")
    let oldXdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")

    try
        Environment.SetEnvironmentVariable("ORG_CLI_DIRECTORY", null)

        let tmpConfig =
            Path.Combine(Path.GetTempPath(), sprintf "org-cli-cfg-test-%s" (Guid.NewGuid().ToString("N")))

        let configDir = Path.Combine(tmpConfig, "org-cli")
        Directory.CreateDirectory(configDir) |> ignore
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", tmpConfig)

        File.WriteAllText(
            Path.Combine(configDir, "config.json"),
            sprintf """{"directories": ["%s"]}""" (dir.Replace("\\", "\\\\"))
        )

        try
            let opts = makeOpts [ ("db", dbPath) ]
            f opts
        finally
            Directory.Delete(tmpConfig, true)
    finally
        Environment.SetEnvironmentVariable("ORG_CLI_DIRECTORY", oldEnv)
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", oldXdg)

[<Fact>]
let ``handleIndex uses directory from config.json when no -d flag`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        writeOrgFile dir "cfg.org" "* From Config\n" |> ignore

        withConfigDir dir dbPath (fun opts ->
            let _, _, exitCode = captureBoth (fun () -> Program.handleIndex opts false true)
            Assert.Equal(0, exitCode)

            use db = new IndexDatabase.OrgIndexDb(dbPath)
            db.Initialize()
            let files = db.GetAllFiles()
            Assert.Equal(1, files.Length)
            Assert.Contains("cfg.org", files.[0].Path))
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``handleFts uses directory from config.json when no -d flag`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        writeOrgFile dir "cfg.org" "* Config Topic\nConfig body text\n" |> ignore

        withConfigDir dir dbPath (fun opts ->
            captureBoth (fun () -> Program.handleIndex opts false true) |> ignore

            let stdout, _, exitCode =
                captureBoth (fun () -> Program.handleFts opts true "config")

            Assert.Equal(0, exitCode)
            let json = JsonNode.Parse(stdout.Trim())
            let results = json["data"] :?> JsonArray
            Assert.True(results.Count > 0))
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``handleCustomIdAssign uses directory from config.json when no -d flag`` () =
    let dir = tempDir ()
    let dbPath = tempDbPath ()

    try
        writeOrgFile dir "cfg.org" "* Needs ID\n" |> ignore

        withConfigDir dir dbPath (fun opts ->
            let _, _, exitCode =
                captureBoth (fun () -> Program.handleCustomIdAssign opts true true false)

            Assert.Equal(0, exitCode))
    finally
        cleanup [ dir; dbPath ]

[<Fact>]
let ``resolveIndexDbPath uses directory from config.json for default db location`` () =
    let oldEnv = Environment.GetEnvironmentVariable("ORG_CLI_DIRECTORY")
    let oldXdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")

    try
        Environment.SetEnvironmentVariable("ORG_CLI_DIRECTORY", null)

        let tmpConfig =
            Path.Combine(Path.GetTempPath(), sprintf "org-cli-cfg-test-%s" (Guid.NewGuid().ToString("N")))

        let configDir = Path.Combine(tmpConfig, "org-cli")
        Directory.CreateDirectory(configDir) |> ignore
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", tmpConfig)

        File.WriteAllText(Path.Combine(configDir, "config.json"), """{"directories": ["/tmp/my-org-dir"]}""")

        try
            let opts = Map.empty
            let path = Program.resolveIndexDbPath opts
            Assert.Equal(Path.Combine("/tmp/my-org-dir", ".org-index.db"), path)
        finally
            Directory.Delete(tmpConfig, true)
    finally
        Environment.SetEnvironmentVariable("ORG_CLI_DIRECTORY", oldEnv)
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", oldXdg)

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
            writeOrgFile dir "test.org" ":PROPERTIES:\n:ID: node-1\n:END:\n#+title: Tasks\n\n* TODO Buy groceries\n"

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

// ── Append via CLI ──

[<Fact>]
let ``append command appends text to headline body`` () =
    let dir = tempDir ()

    try
        let filePath = writeOrgFile dir "test.org" "* My Headline\nExisting body\n"

        let _, _, exitCode =
            captureBoth (fun () -> Program.main [| "append"; filePath; "My Headline"; "appended text"; "--quiet" |])

        Assert.Equal(0, exitCode)
        let content = File.ReadAllText(filePath)
        Assert.Contains("Existing body", content)
        Assert.Contains("appended text", content)
    finally
        cleanup [ dir ]

[<Fact>]
let ``append command with --stdin reads from stdin`` () =
    let dir = tempDir ()

    try
        let filePath = writeOrgFile dir "test.org" "* My Headline\nExisting body\n"
        let oldIn = Console.In
        use sr = new StringReader("stdin text")
        Console.SetIn(sr)

        try
            let _, _, exitCode =
                captureBoth (fun () -> Program.main [| "append"; filePath; "My Headline"; "--stdin"; "--quiet" |])

            Assert.Equal(0, exitCode)
            let content = File.ReadAllText(filePath)
            Assert.Contains("stdin text", content)
        finally
            Console.SetIn(oldIn)
    finally
        cleanup [ dir ]

// ── handleTodos ──

[<Fact>]
let ``handleTodos returns all TODO items across files`` () =
    let dir = tempDir ()

    try
        writeOrgFile dir "a.org" "* TODO Task A\n* DONE Finished A\n" |> ignore
        writeOrgFile dir "b.org" "* TODO Task B\n* Not a todo\n" |> ignore
        let opts = makeOpts [ ("directory", dir) ]

        let stdout, _, exitCode =
            captureBoth (fun () -> Program.handleTodos OrgCli.Org.Types.defaultConfig opts true)

        Assert.Equal(0, exitCode)
        let json = JsonNode.Parse(stdout.Trim())
        let results = json["data"] :?> JsonArray
        // Should find 3 TODO items: TODO Task A, DONE Finished A, TODO Task B
        Assert.Equal(3, results.Count)
    finally
        cleanup [ dir ]

[<Fact>]
let ``handleTodos --state filters by TODO state`` () =
    let dir = tempDir ()

    try
        writeOrgFile dir "test.org" "* TODO Active\n* DONE Finished\n* WAITING Blocked\n"
        |> ignore

        let opts = makeOpts [ ("directory", dir); ("state", "TODO") ]

        let stdout, _, exitCode =
            captureBoth (fun () -> Program.handleTodos OrgCli.Org.Types.defaultConfig opts true)

        Assert.Equal(0, exitCode)
        let json = JsonNode.Parse(stdout.Trim())
        let results = json["data"] :?> JsonArray
        Assert.Equal(1, results.Count)
        let first = results.[0]
        Assert.Equal("Active", first["title"].GetValue<string>())
    finally
        cleanup [ dir ]

[<Fact>]
let ``handleTodos --tag filters by tag`` () =
    let dir = tempDir ()

    try
        writeOrgFile
            dir
            "test.org"
            "* TODO Task A                                        :work:\n* TODO Task B                                        :personal:\n"
        |> ignore

        let opts = makeOpts [ ("directory", dir); ("tag", "work") ]

        let stdout, _, exitCode =
            captureBoth (fun () -> Program.handleTodos OrgCli.Org.Types.defaultConfig opts true)

        Assert.Equal(0, exitCode)
        let json = JsonNode.Parse(stdout.Trim())
        let results = json["data"] :?> JsonArray
        Assert.Equal(1, results.Count)
        let first = results.[0]
        Assert.Equal("Task A", first["title"].GetValue<string>())
    finally
        cleanup [ dir ]

[<Fact>]
let ``handleTodos --tag with multiple tags uses OR`` () =
    let dir = tempDir ()

    try
        writeOrgFile
            dir
            "test.org"
            "* TODO Task A                                        :work:\n* TODO Task B                                        :personal:\n* TODO Task C                                        :other:\n"
        |> ignore

        let opts = makeOpts [ ("directory", dir); ("tag", "work"); ("tag", "personal") ]

        let stdout, _, exitCode =
            captureBoth (fun () -> Program.handleTodos OrgCli.Org.Types.defaultConfig opts true)

        Assert.Equal(0, exitCode)
        let json = JsonNode.Parse(stdout.Trim())
        let results = json["data"] :?> JsonArray
        Assert.Equal(2, results.Count)
    finally
        cleanup [ dir ]

[<Fact>]
let ``handleTodos --priority filters by priority`` () =
    let dir = tempDir ()

    try
        writeOrgFile dir "test.org" "* TODO [#A] High\n* TODO [#C] Low\n* TODO Normal\n"
        |> ignore

        let opts = makeOpts [ ("directory", dir); ("priority", "A") ]

        let stdout, _, exitCode =
            captureBoth (fun () -> Program.handleTodos OrgCli.Org.Types.defaultConfig opts true)

        Assert.Equal(0, exitCode)
        let json = JsonNode.Parse(stdout.Trim())
        let results = json["data"] :?> JsonArray
        Assert.Equal(1, results.Count)
        let first = results.[0]
        Assert.Equal("High", first["title"].GetValue<string>())
    finally
        cleanup [ dir ]

[<Fact>]
let ``handleTodos --scheduled filters to items with scheduled date`` () =
    let dir = tempDir ()

    try
        writeOrgFile dir "test.org" "* TODO Scheduled\nSCHEDULED: <2026-03-01 Sun>\n* TODO No date\n"
        |> ignore

        let opts = makeOpts [ ("directory", dir); ("scheduled", "true") ]

        let stdout, _, exitCode =
            captureBoth (fun () -> Program.handleTodos OrgCli.Org.Types.defaultConfig opts true)

        Assert.Equal(0, exitCode)
        let json = JsonNode.Parse(stdout.Trim())
        let results = json["data"] :?> JsonArray
        Assert.Equal(1, results.Count)
        let first = results.[0]
        Assert.Equal("Scheduled", first["title"].GetValue<string>())
    finally
        cleanup [ dir ]

[<Fact>]
let ``handleTodos --unscheduled filters to items without scheduled date`` () =
    let dir = tempDir ()

    try
        writeOrgFile dir "test.org" "* TODO Scheduled\nSCHEDULED: <2026-03-01 Sun>\n* TODO No date\n"
        |> ignore

        let opts = makeOpts [ ("directory", dir); ("unscheduled", "true") ]

        let stdout, _, exitCode =
            captureBoth (fun () -> Program.handleTodos OrgCli.Org.Types.defaultConfig opts true)

        Assert.Equal(0, exitCode)
        let json = JsonNode.Parse(stdout.Trim())
        let results = json["data"] :?> JsonArray
        Assert.Equal(1, results.Count)
        let first = results.[0]
        Assert.Equal("No date", first["title"].GetValue<string>())
    finally
        cleanup [ dir ]

[<Fact>]
let ``handleTodos --search filters by title substring`` () =
    let dir = tempDir ()

    try
        writeOrgFile dir "test.org" "* TODO Buy groceries\n* TODO Clean house\n* TODO Buy milk\n"
        |> ignore

        let opts = makeOpts [ ("directory", dir); ("search", "buy") ]

        let stdout, _, exitCode =
            captureBoth (fun () -> Program.handleTodos OrgCli.Org.Types.defaultConfig opts true)

        Assert.Equal(0, exitCode)
        let json = JsonNode.Parse(stdout.Trim())
        let results = json["data"] :?> JsonArray
        Assert.Equal(2, results.Count)
    finally
        cleanup [ dir ]

[<Fact>]
let ``handleTodos --file filters by filename`` () =
    let dir = tempDir ()

    try
        writeOrgFile dir "work.org" "* TODO Work task\n" |> ignore
        writeOrgFile dir "personal.org" "* TODO Personal task\n" |> ignore
        let opts = makeOpts [ ("directory", dir); ("file", "work") ]

        let stdout, _, exitCode =
            captureBoth (fun () -> Program.handleTodos OrgCli.Org.Types.defaultConfig opts true)

        Assert.Equal(0, exitCode)
        let json = JsonNode.Parse(stdout.Trim())
        let results = json["data"] :?> JsonArray
        Assert.Equal(1, results.Count)
        let first = results.[0]
        Assert.Equal("Work task", first["title"].GetValue<string>())
    finally
        cleanup [ dir ]

[<Fact>]
let ``handleTodos file field is relative path`` () =
    let dir = tempDir ()

    try
        let subDir = Path.Combine(dir, "sub")
        Directory.CreateDirectory(subDir) |> ignore
        writeOrgFile subDir "deep.org" "* TODO Deep task\n" |> ignore
        let opts = makeOpts [ ("directory", dir) ]

        let stdout, _, exitCode =
            captureBoth (fun () -> Program.handleTodos OrgCli.Org.Types.defaultConfig opts true)

        Assert.Equal(0, exitCode)
        let json = JsonNode.Parse(stdout.Trim())
        let results = json["data"] :?> JsonArray
        Assert.Equal(1, results.Count)
        let filePath = results.[0].["file"].GetValue<string>()
        Assert.Equal("sub/deep.org", filePath)
    finally
        cleanup [ dir ]

[<Fact>]
let ``handleTodos JSON output includes all required fields`` () =
    let dir = tempDir ()

    try
        writeOrgFile
            dir
            "test.org"
            "* TODO [#A] Important task                          :work:\nSCHEDULED: <2026-03-01 Sun> DEADLINE: <2026-03-15 Sun>\n"
        |> ignore

        let opts = makeOpts [ ("directory", dir) ]

        let stdout, _, exitCode =
            captureBoth (fun () -> Program.handleTodos OrgCli.Org.Types.defaultConfig opts true)

        Assert.Equal(0, exitCode)
        let json = JsonNode.Parse(stdout.Trim())
        let results = json["data"] :?> JsonArray
        Assert.Equal(1, results.Count)
        let item = results.[0]
        Assert.Equal("Important task", item["title"].GetValue<string>())
        Assert.Equal("TODO", item["todo"].GetValue<string>())
        Assert.Equal("A", item["priority"].GetValue<string>())
        Assert.NotNull(item["tags"])
        Assert.NotNull(item["file"])
        Assert.NotNull(item["pos"])
        Assert.Equal("2026-03-01", item["scheduled"].GetValue<string>())
        Assert.Equal("2026-03-15", item["deadline"].GetValue<string>())
        Assert.Equal(1, item["level"].GetValue<int>())
        Assert.NotNull(item["path"])
    finally
        cleanup [ dir ]

[<Fact>]
let ``handleTodos --sort priority sorts by priority`` () =
    let dir = tempDir ()

    try
        writeOrgFile dir "test.org" "* TODO [#C] Low\n* TODO [#A] High\n* TODO [#B] Medium\n"
        |> ignore

        let opts = makeOpts [ ("directory", dir); ("sort", "priority") ]

        let stdout, _, exitCode =
            captureBoth (fun () -> Program.handleTodos OrgCli.Org.Types.defaultConfig opts true)

        Assert.Equal(0, exitCode)
        let json = JsonNode.Parse(stdout.Trim())
        let results = json["data"] :?> JsonArray
        let r0 = results.[0]
        let r1 = results.[1]
        let r2 = results.[2]
        Assert.Equal("High", r0["title"].GetValue<string>())
        Assert.Equal("Medium", r1["title"].GetValue<string>())
        Assert.Equal("Low", r2["title"].GetValue<string>())
    finally
        cleanup [ dir ]

[<Fact>]
let ``handleTodos --reverse reverses sort order`` () =
    let dir = tempDir ()

    try
        writeOrgFile dir "test.org" "* TODO [#A] High\n* TODO [#C] Low\n" |> ignore

        let opts =
            makeOpts [ ("directory", dir); ("sort", "priority"); ("reverse", "true") ]

        let stdout, _, exitCode =
            captureBoth (fun () -> Program.handleTodos OrgCli.Org.Types.defaultConfig opts true)

        Assert.Equal(0, exitCode)
        let json = JsonNode.Parse(stdout.Trim())
        let results = json["data"] :?> JsonArray
        let r0 = results.[0]
        let r1 = results.[1]
        Assert.Equal("Low", r0["title"].GetValue<string>())
        Assert.Equal("High", r1["title"].GetValue<string>())
    finally
        cleanup [ dir ]

[<Fact>]
let ``handleTodos text output shows No TODO items for empty`` () =
    let dir = tempDir ()

    try
        writeOrgFile dir "test.org" "* Just a headline\n" |> ignore
        let opts = makeOpts [ ("directory", dir) ]

        let stdout, _, exitCode =
            captureBoth (fun () -> Program.handleTodos OrgCli.Org.Types.defaultConfig opts false)

        Assert.Equal(0, exitCode)
        Assert.Contains("No TODO items found.", stdout)
    finally
        cleanup [ dir ]

[<Fact>]
let ``handleTodos text output shows table for results`` () =
    let dir = tempDir ()

    try
        writeOrgFile dir "test.org" "* TODO My task\n" |> ignore
        let opts = makeOpts [ ("directory", dir) ]

        let stdout, _, exitCode =
            captureBoth (fun () -> Program.handleTodos OrgCli.Org.Types.defaultConfig opts false)

        Assert.Equal(0, exitCode)
        Assert.Contains("My task", stdout)
        Assert.Contains("STATE", stdout)
        Assert.Contains("TITLE", stdout)
    finally
        cleanup [ dir ]

[<Fact>]
let ``main routes todo list command`` () =
    let dir = tempDir ()

    try
        writeOrgFile dir "test.org" "* TODO Task\n" |> ignore

        let stdout, _, exitCode =
            captureBoth (fun () -> Program.main [| "todo"; "list"; "--directory"; dir; "--format"; "json" |])

        Assert.Equal(0, exitCode)
        let json = JsonNode.Parse(stdout.Trim())
        Assert.True(json["ok"].GetValue<bool>())
        let results = json["data"] :?> JsonArray
        Assert.Equal(1, results.Count)
    finally
        cleanup [ dir ]

[<Fact>]
let ``main routes todo set command`` () =
    let dir = tempDir ()

    try
        let file = writeOrgFile dir "test.org" "* TODO Task\nBody\n"

        let _, _, exitCode =
            captureBoth (fun () -> Program.main [| "todo"; "set"; file; "0"; "DONE"; "--quiet" |])

        Assert.Equal(0, exitCode)
        let content = File.ReadAllText(file)
        Assert.Contains("DONE", content)
    finally
        cleanup [ dir ]

[<Fact>]
let ``main routes implicit todo set (without subcommand)`` () =
    let dir = tempDir ()

    try
        let file = writeOrgFile dir "test.org" "* TODO Task\nBody\n"

        let _, _, exitCode =
            captureBoth (fun () -> Program.main [| "todo"; file; "0"; "DONE"; "--quiet" |])

        Assert.Equal(0, exitCode)
        let content = File.ReadAllText(file)
        Assert.Contains("DONE", content)
    finally
        cleanup [ dir ]

[<Fact>]
let ``handleTodos --overdue filters overdue items`` () =
    let dir = tempDir ()

    try
        writeOrgFile
            dir
            "test.org"
            "* TODO Overdue\nSCHEDULED: <2020-01-01 Wed>\n* TODO Future\nSCHEDULED: <2099-01-01 Tue>\n* TODO No date\n"
        |> ignore

        let opts = makeOpts [ ("directory", dir); ("overdue", "true") ]

        let stdout, _, exitCode =
            captureBoth (fun () -> Program.handleTodos OrgCli.Org.Types.defaultConfig opts true)

        Assert.Equal(0, exitCode)
        let json = JsonNode.Parse(stdout.Trim())
        let results = json["data"] :?> JsonArray
        Assert.Equal(1, results.Count)
        let first = results.[0]
        Assert.Equal("Overdue", first["title"].GetValue<string>())
    finally
        cleanup [ dir ]

[<Fact>]
let ``handleTodos JSON path field shows outline path`` () =
    let dir = tempDir ()

    try
        writeOrgFile dir "test.org" "* Parent\n** TODO Child task\n" |> ignore
        let opts = makeOpts [ ("directory", dir) ]

        let stdout, _, exitCode =
            captureBoth (fun () -> Program.handleTodos OrgCli.Org.Types.defaultConfig opts true)

        Assert.Equal(0, exitCode)
        let json = JsonNode.Parse(stdout.Trim())
        let results = json["data"] :?> JsonArray
        Assert.Equal(1, results.Count)
        let first = results.[0]
        let path = first["path"] :?> JsonArray
        Assert.Equal(1, path.Count)
        Assert.Equal("Parent", path.[0].GetValue<string>())
    finally
        cleanup [ dir ]

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

// ── Schedule/Deadline with repeater via CLI ──

[<Fact>]
let ``schedule with repeater flag writes repeater to file`` () =
    let dir = tempDir ()

    try
        let file = writeOrgFile dir "test.org" "* TODO My task\nBody\n"

        let stdout, _, exitCode =
            captureBoth (fun () ->
                Program.main [| "schedule"; file; "0"; "2026-03-10"; "--repeater"; "+1w"; "-f"; "json" |])

        Assert.Equal(0, exitCode)
        let content = File.ReadAllText(file)
        Assert.Contains("+1w", content)
        let json = JsonNode.Parse(stdout.Trim())
        Assert.True(json["ok"].GetValue<bool>())
    finally
        cleanup [ dir ]

[<Fact>]
let ``deadline with repeater and delay flags writes both to file`` () =
    let dir = tempDir ()

    try
        let file = writeOrgFile dir "test.org" "* TODO My task\nBody\n"

        let stdout, _, exitCode =
            captureBoth (fun () ->
                Program.main
                    [| "deadline"
                       file
                       "0"
                       "2026-04-01"
                       "--repeater"
                       "++1m"
                       "--delay"
                       "2d"
                       "-f"
                       "json" |])

        Assert.Equal(0, exitCode)
        let content = File.ReadAllText(file)
        Assert.Contains("++1m", content)
        Assert.Contains("-2d", content)
    finally
        cleanup [ dir ]

[<Fact>]
let ``schedule with invalid repeater returns error`` () =
    let dir = tempDir ()

    try
        let file = writeOrgFile dir "test.org" "* TODO My task\nBody\n"

        let stdout, _, exitCode =
            captureBoth (fun () ->
                Program.main [| "schedule"; file; "0"; "2026-03-10"; "--repeater"; "bad"; "-f"; "json" |])

        Assert.Equal(1, exitCode)
        let json = JsonNode.Parse(stdout.Trim())
        Assert.False(json["ok"].GetValue<bool>())
        let errObj = json["error"]
        let msg = errObj["message"].GetValue<string>()
        Assert.Contains("Invalid repeater", msg)
    finally
        cleanup [ dir ]

[<Fact>]
let ``schedule clear ignores repeater flag`` () =
    let dir = tempDir ()

    try
        let file =
            writeOrgFile dir "test.org" "* TODO My task\nSCHEDULED: <2026-03-10 Tue +1w>\nBody\n"

        let _, _, exitCode =
            captureBoth (fun () -> Program.main [| "schedule"; file; "0"; ""; "--repeater"; "+1w"; "--quiet" |])

        Assert.Equal(0, exitCode)
        let content = File.ReadAllText(file)
        Assert.DoesNotContain("SCHEDULED:", content)
    finally
        cleanup [ dir ]
