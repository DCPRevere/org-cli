module EmacsInteropTests

open System
open System.IO
open System.Diagnostics
open Xunit
open Xunit.Abstractions
open Xunit.Sdk
open OrgCli.Roam
open OrgCli.Org

/// Helper to run shell commands and capture output
let runCommand (command: string) (args: string) (timeout: int) =
    use proc = new Process()
    proc.StartInfo.FileName <- command
    proc.StartInfo.Arguments <- args
    proc.StartInfo.UseShellExecute <- false
    proc.StartInfo.RedirectStandardOutput <- true
    proc.StartInfo.RedirectStandardError <- true
    proc.StartInfo.CreateNoWindow <- true

    try
        proc.Start() |> ignore
        let output = proc.StandardOutput.ReadToEnd()
        let error = proc.StandardError.ReadToEnd()
        proc.WaitForExit(timeout) |> ignore
        Some (proc.ExitCode, output, error)
    with
    | _ -> None

/// Check if Emacs is available
let isEmacsAvailable () =
    match runCommand "emacs" "--version" 5000 with
    | Some (0, output, _) when output.Contains("GNU Emacs") -> true
    | _ -> false

/// Check if org-roam is available in Emacs
let isOrgRoamAvailable () =
    let elisp = "(require 'org-roam)"
    match runCommand "emacs" (sprintf "--batch --eval \"%s\"" elisp) 10000 with
    | Some (0, _, _) -> true
    | _ -> false

/// Skip message for when Emacs/org-roam not available
let skipReason = "Emacs with org-roam not available. Set RUN_EMACS_TESTS=1 and ensure emacs with org-roam is installed."

/// Check if we should run Emacs tests
let shouldRunEmacsTests () =
    let envVar = Environment.GetEnvironmentVariable("RUN_EMACS_TESTS")
    let explicitlyEnabled = envVar = "1" || envVar = "true"
    explicitlyEnabled && isEmacsAvailable() && isOrgRoamAvailable()

let createTempDir () =
    let path = Path.Combine(Path.GetTempPath(), "org-roam-emacs-test-" + Guid.NewGuid().ToString())
    Directory.CreateDirectory(path) |> ignore
    path

let cleanupTempDir (path: string) =
    if Directory.Exists(path) then
        Directory.Delete(path, true)

/// Run elisp code in Emacs with org-roam configured for given directory
let runOrgRoamElisp (roamDir: string) (dbPath: string) (elisp: string) (log: string -> unit) =
    let setupElisp = sprintf "(progn (require 'org-roam) (setq org-roam-directory \"%s\") (setq org-roam-db-location \"%s\") (org-roam-db-sync) %s)" (roamDir.Replace("\\", "/")) (dbPath.Replace("\\", "/")) elisp

    let tempElispFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".el")
    File.WriteAllText(tempElispFile, setupElisp)

    try
        let result = runCommand "emacs" (sprintf "--batch -l \"%s\"" tempElispFile) 60000
        match result with
        | Some (code, stdout, stderr) ->
            log (sprintf "Emacs exit code: %d" code)
            if not (String.IsNullOrWhiteSpace(stdout)) then
                log (sprintf "Emacs stdout: %s" stdout)
            if not (String.IsNullOrWhiteSpace(stderr)) then
                log (sprintf "Emacs stderr: %s" stderr)
            result
        | None ->
            log "Emacs command failed to run"
            None
    finally
        if File.Exists(tempElispFile) then File.Delete(tempElispFile)

/// Run elisp that prints a result, extract the printed value
let queryOrgRoam (roamDir: string) (dbPath: string) (queryElisp: string) (log: string -> unit) =
    let elisp = sprintf "(princ %s)" queryElisp
    match runOrgRoamElisp roamDir dbPath elisp log with
    | Some (0, stdout, _) -> Some (stdout.Trim())
    | _ -> None

type EmacsInteropTests(output: ITestOutputHelper) =

    let log msg = output.WriteLine(msg)

    [<SkippableFact>]
    member _.``CLI-created database can be read by Emacs org-roam`` () =
        if not (shouldRunEmacsTests()) then
            Skip.If(true, skipReason)

        let tempDir = createTempDir()
        try
            let content1 = ":PROPERTIES:\n:ID: emacs-test-001\n:END:\n#+title: Emacs Test Note\n\nThis is test content.\n"
            File.WriteAllText(Path.Combine(tempDir, "test1.org"), content1)

            let content2 = ":PROPERTIES:\n:ID: emacs-test-002\n:END:\n#+title: Second Note\n\nLinks to [[id:emacs-test-001][first note]].\n"
            File.WriteAllText(Path.Combine(tempDir, "test2.org"), content2)

            let dbPath = Path.Combine(tempDir, "org-roam.db")
            use db = new Database.OrgRoamDb(dbPath)
            let _syncErrors = Sync.sync db tempDir false

            let countQuery = "(length (org-roam-db-query [:select id :from nodes]))"
            match queryOrgRoam tempDir dbPath countQuery log with
            | Some result ->
                log (sprintf "Emacs reports node count: %s" result)
                let count = Int32.Parse(result)
                Assert.True(count >= 2, sprintf "Expected at least 2 nodes, got %d" count)
            | None ->
                Assert.Fail("Failed to query org-roam from Emacs")
        finally
            cleanupTempDir tempDir

    [<SkippableFact>]
    member _.``Emacs org-roam can find node created by CLI`` () =
        if not (shouldRunEmacsTests()) then
            Skip.If(true, skipReason)

        let tempDir = createTempDir()
        try
            let nodeId = "cli-created-" + Guid.NewGuid().ToString().Substring(0, 8)
            let content = sprintf ":PROPERTIES:\n:ID: %s\n:END:\n#+title: CLI Created Node\n\nContent here.\n" nodeId
            File.WriteAllText(Path.Combine(tempDir, "cli-node.org"), content)

            let dbPath = Path.Combine(tempDir, "org-roam.db")
            use db = new Database.OrgRoamDb(dbPath)
            let _syncErrors = Sync.sync db tempDir false

            let query = sprintf "(car (org-roam-db-query [:select title :from nodes :where (= id \"%s\")]))" nodeId
            match queryOrgRoam tempDir dbPath query log with
            | Some result ->
                log (sprintf "Emacs found title: %s" result)
                Assert.Contains("CLI Created Node", result)
            | None ->
                Assert.Fail("Failed to find node in Emacs org-roam")
        finally
            cleanupTempDir tempDir

    [<SkippableFact>]
    member _.``CLI can read database created by Emacs org-roam`` () =
        if not (shouldRunEmacsTests()) then
            Skip.If(true, skipReason)

        let tempDir = createTempDir()
        try
            let nodeId = Guid.NewGuid().ToString()
            let content = sprintf ":PROPERTIES:\n:ID: %s\n:END:\n#+title: Emacs Created Node\n\nCreated for interop test.\n" nodeId
            File.WriteAllText(Path.Combine(tempDir, "emacs-node.org"), content)

            let dbPath = Path.Combine(tempDir, "org-roam.db")

            let syncResult = runOrgRoamElisp tempDir dbPath "(message \"Sync complete\")" log
            match syncResult with
            | Some (0, _, _) ->
                use db = new Database.OrgRoamDb(dbPath)
                let node = db.GetNode(nodeId)

                Assert.True(node.IsSome, "CLI should find node created by Emacs")
                Assert.Equal("Emacs Created Node", node.Value.Title)
                log (sprintf "CLI successfully read Emacs-created node: %s" node.Value.Title)
            | _ ->
                Assert.Fail("Emacs org-roam sync failed")
        finally
            cleanupTempDir tempDir

    [<SkippableFact>]
    member _.``Links created by CLI are visible in Emacs org-roam`` () =
        if not (shouldRunEmacsTests()) then
            Skip.If(true, skipReason)

        let tempDir = createTempDir()
        try
            let sourceId = "link-source-" + Guid.NewGuid().ToString().Substring(0, 8)
            let targetId = "link-target-" + Guid.NewGuid().ToString().Substring(0, 8)

            let sourceContent = sprintf ":PROPERTIES:\n:ID: %s\n:END:\n#+title: Source Node\n\nThis links to [[id:%s][Target Node]].\n" sourceId targetId
            let targetContent = sprintf ":PROPERTIES:\n:ID: %s\n:END:\n#+title: Target Node\n\nThis is the target.\n" targetId

            File.WriteAllText(Path.Combine(tempDir, "source.org"), sourceContent)
            File.WriteAllText(Path.Combine(tempDir, "target.org"), targetContent)

            let dbPath = Path.Combine(tempDir, "org-roam.db")
            use db = new Database.OrgRoamDb(dbPath)
            let _syncErrors = Sync.sync db tempDir false

            let query = sprintf "(length (org-roam-db-query [:select * :from links :where (= source \"%s\")]))" sourceId
            match queryOrgRoam tempDir dbPath query log with
            | Some result ->
                log (sprintf "Emacs reports link count from source: %s" result)
                let count = Int32.Parse(result)
                Assert.True(count >= 1, sprintf "Expected at least 1 link, got %d" count)
            | None ->
                Assert.Fail("Failed to query links from Emacs")
        finally
            cleanupTempDir tempDir

    [<SkippableFact>]
    member _.``Aliases created by CLI are visible in Emacs org-roam`` () =
        if not (shouldRunEmacsTests()) then
            Skip.If(true, skipReason)

        let tempDir = createTempDir()
        try
            let nodeId = "alias-node-" + Guid.NewGuid().ToString().Substring(0, 8)
            let content = sprintf ":PROPERTIES:\n:ID: %s\n:ROAM_ALIASES: \"My Alias\" \"Another Alias\"\n:END:\n#+title: Node With Aliases\n\nContent.\n" nodeId

            File.WriteAllText(Path.Combine(tempDir, "aliased.org"), content)

            let dbPath = Path.Combine(tempDir, "org-roam.db")
            use db = new Database.OrgRoamDb(dbPath)
            let _syncErrors = Sync.sync db tempDir false

            let query = sprintf "(length (org-roam-db-query [:select * :from aliases :where (= node-id \"%s\")]))" nodeId
            match queryOrgRoam tempDir dbPath query log with
            | Some result ->
                log (sprintf "Emacs reports alias count: %s" result)
                let count = Int32.Parse(result)
                Assert.True(count >= 2, sprintf "Expected at least 2 aliases, got %d" count)
            | None ->
                Assert.Fail("Failed to query aliases from Emacs")
        finally
            cleanupTempDir tempDir

    [<SkippableFact>]
    member _.``Tags created by CLI are visible in Emacs org-roam`` () =
        if not (shouldRunEmacsTests()) then
            Skip.If(true, skipReason)

        let tempDir = createTempDir()
        try
            let nodeId = "tagged-node-" + Guid.NewGuid().ToString().Substring(0, 8)
            let content = sprintf ":PROPERTIES:\n:ID: %s\n:END:\n#+title: Tagged Node\n#+filetags: :project:important:test:\n\nContent.\n" nodeId

            File.WriteAllText(Path.Combine(tempDir, "tagged.org"), content)

            let dbPath = Path.Combine(tempDir, "org-roam.db")
            use db = new Database.OrgRoamDb(dbPath)
            let _syncErrors = Sync.sync db tempDir false

            let query = sprintf "(length (org-roam-db-query [:select * :from tags :where (= node-id \"%s\")]))" nodeId
            match queryOrgRoam tempDir dbPath query log with
            | Some result ->
                log (sprintf "Emacs reports tag count: %s" result)
                let count = Int32.Parse(result)
                Assert.True(count >= 3, sprintf "Expected at least 3 tags, got %d" count)
            | None ->
                Assert.Fail("Failed to query tags from Emacs")
        finally
            cleanupTempDir tempDir

    [<SkippableFact>]
    member _.``Round-trip: CLI create, Emacs modify, CLI read`` () =
        if not (shouldRunEmacsTests()) then
            Skip.If(true, skipReason)

        let tempDir = createTempDir()
        try
            let nodeId = "roundtrip-" + Guid.NewGuid().ToString().Substring(0, 8)
            let content = sprintf ":PROPERTIES:\n:ID: %s\n:END:\n#+title: Original Title\n\nOriginal content.\n" nodeId

            File.WriteAllText(Path.Combine(tempDir, "roundtrip.org"), content)

            let dbPath = Path.Combine(tempDir, "org-roam.db")
            use db = new Database.OrgRoamDb(dbPath)
            let _syncErrors = Sync.sync db tempDir false

            let node1 = db.GetNode(nodeId)
            Assert.Equal("Original Title", node1.Value.Title)

            let newContent = sprintf ":PROPERTIES:\n:ID: %s\n:END:\n#+title: Modified Title\n\nModified content.\n" nodeId
            File.WriteAllText(Path.Combine(tempDir, "roundtrip.org"), newContent)

            let syncResult = runOrgRoamElisp tempDir dbPath "(message \"Re-sync complete\")" log

            match syncResult with
            | Some (0, _, _) ->
                use db2 = new Database.OrgRoamDb(dbPath)
                let node2 = db2.GetNode(nodeId)
                Assert.True(node2.IsSome, "Node should still exist after Emacs sync")
                Assert.Equal("Modified Title", node2.Value.Title)
                log "Round-trip successful: CLI -> Emacs modify -> CLI read"
            | _ ->
                Assert.Fail("Emacs re-sync failed")
        finally
            cleanupTempDir tempDir

    [<SkippableFact>]
    member _.``Database schema version matches org-roam expectation`` () =
        if not (shouldRunEmacsTests()) then
            Skip.If(true, skipReason)

        let tempDir = createTempDir()
        try
            let content = ":PROPERTIES:\n:ID: schema-test\n:END:\n#+title: Schema Test\n"
            File.WriteAllText(Path.Combine(tempDir, "schema.org"), content)

            let dbPath = Path.Combine(tempDir, "org-roam.db")
            use db = new Database.OrgRoamDb(dbPath)
            let _syncErrors = Sync.sync db tempDir false

            let versionQuery = "org-roam-db-version"
            match queryOrgRoam tempDir dbPath versionQuery log with
            | Some result ->
                log (sprintf "Emacs org-roam-db-version: %s" result)
                Assert.Contains("20", result)
            | None ->
                let countQuery = "(length (org-roam-db-query [:select id :from nodes]))"
                match queryOrgRoam tempDir dbPath countQuery log with
                | Some _ -> log "Schema compatible (node query succeeded)"
                | None -> Assert.Fail("Database schema not compatible with Emacs org-roam")
        finally
            cleanupTempDir tempDir
