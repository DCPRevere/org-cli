module PerformanceTests

open System
open System.IO
open System.Diagnostics
open Xunit
open Xunit.Abstractions
open OrgCli.Roam
open OrgCli.Org

type PerformanceTests(output: ITestOutputHelper) =

    let createTempDir () =
        let path = Path.Combine(Path.GetTempPath(), "org-roam-perf-" + Guid.NewGuid().ToString())
        Directory.CreateDirectory(path) |> ignore
        path

    let cleanupTempDir (path: string) =
        if Directory.Exists(path) then
            Directory.Delete(path, true)

    let generateOrgFile (id: string) (title: string) (linkTargets: string list) =
        let links = linkTargets |> List.map (fun t -> sprintf "Link to [[id:%s][target]]." t) |> String.concat "\n"
        sprintf ":PROPERTIES:\n:ID: %s\n:END:\n#+title: %s\n\n%s\n" id title links

    let generateOrgFileWithHeadlines (id: string) (title: string) (headlineCount: int) =
        let headlines =
            [1..headlineCount]
            |> List.map (fun i -> sprintf "* Headline %d\n:PROPERTIES:\n:ID: %s-h%d\n:END:\nContent for headline %d.\n" i id i i)
            |> String.concat "\n"
        sprintf ":PROPERTIES:\n:ID: %s\n:END:\n#+title: %s\n\n%s" id title headlines

    [<Fact>]
    member _.``Sync 100 files completes in reasonable time`` () =
        let tempDir = createTempDir()
        try
            // Create 100 files with some cross-links
            for i in 1..100 do
                let linkTargets =
                    if i > 1 then [sprintf "node-%03d" (i - 1)]
                    else []
                let content = generateOrgFile (sprintf "node-%03d" i) (sprintf "Note %d" i) linkTargets
                File.WriteAllText(Path.Combine(tempDir, sprintf "note-%03d.org" i), content)

            let dbPath = Path.Combine(tempDir, ".org-roam.db")
            use db = new Database.OrgRoamDb(dbPath)

            let sw = Stopwatch.StartNew()
            Sync.sync db tempDir false |> List.iter (fun (f, e) -> output.WriteLine(sprintf "Error: %s: %s" f e))
            sw.Stop()

            output.WriteLine(sprintf "Sync 100 files: %d ms" sw.ElapsedMilliseconds)

            // Verify all nodes created
            let node = db.GetNode("node-050")
            Assert.True(node.IsSome)

            // Report but don't fail on time (varies by machine)
            // Just ensure it completes and isn't pathologically slow
            Assert.True(sw.ElapsedMilliseconds < 30000L, sprintf "Sync took too long: %d ms" sw.ElapsedMilliseconds)
        finally
            cleanupTempDir tempDir

    [<Fact>]
    member _.``Sync 500 files measures scaling`` () =
        let tempDir = createTempDir()
        try
            for i in 1..500 do
                let content = generateOrgFile (sprintf "node-%04d" i) (sprintf "Note %d" i) []
                File.WriteAllText(Path.Combine(tempDir, sprintf "note-%04d.org" i), content)

            let dbPath = Path.Combine(tempDir, ".org-roam.db")
            use db = new Database.OrgRoamDb(dbPath)

            let sw = Stopwatch.StartNew()
            Sync.sync db tempDir false |> List.iter (fun (f, e) -> output.WriteLine(sprintf "Error: %s: %s" f e))
            sw.Stop()

            output.WriteLine(sprintf "Sync 500 files: %d ms (%.2f ms/file)" sw.ElapsedMilliseconds (float sw.ElapsedMilliseconds / 500.0))

            let node = db.GetNode("node-0250")
            Assert.True(node.IsSome)
        finally
            cleanupTempDir tempDir

    [<Fact>]
    member _.``Parse file with 100 headlines`` () =
        let tempDir = createTempDir()
        try
            let content = generateOrgFileWithHeadlines "big-file" "Big File" 100
            let filePath = Path.Combine(tempDir, "big.org")
            File.WriteAllText(filePath, content)

            let sw = Stopwatch.StartNew()
            let doc = Document.parseFile filePath
            sw.Stop()

            output.WriteLine(sprintf "Parse 100 headlines: %d ms" sw.ElapsedMilliseconds)

            // 100 headlines + file-level = expecting 100 headline nodes
            Assert.Equal(100, doc.Headlines.Length)
        finally
            cleanupTempDir tempDir

    [<Fact>]
    member _.``Parse file with 500 headlines`` () =
        let tempDir = createTempDir()
        try
            let content = generateOrgFileWithHeadlines "huge-file" "Huge File" 500
            let filePath = Path.Combine(tempDir, "huge.org")
            File.WriteAllText(filePath, content)

            let sw = Stopwatch.StartNew()
            let doc = Document.parseFile filePath
            sw.Stop()

            output.WriteLine(sprintf "Parse 500 headlines: %d ms (%.2f ms/headline)" sw.ElapsedMilliseconds (float sw.ElapsedMilliseconds / 500.0))

            Assert.Equal(500, doc.Headlines.Length)
        finally
            cleanupTempDir tempDir

    [<Fact>]
    member _.``Query performance with 500 nodes`` () =
        let tempDir = createTempDir()
        try
            for i in 1..500 do
                let content = generateOrgFile (sprintf "qnode-%04d" i) (sprintf "Query Note %d" i) []
                File.WriteAllText(Path.Combine(tempDir, sprintf "qnote-%04d.org" i), content)

            let dbPath = Path.Combine(tempDir, ".org-roam.db")
            use db = new Database.OrgRoamDb(dbPath)
            Sync.sync db tempDir false |> List.iter (fun (f, e) -> output.WriteLine(sprintf "Error: %s: %s" f e))

            // Measure individual node lookups
            let sw = Stopwatch.StartNew()
            for i in 1..100 do
                let _ = db.GetNode(sprintf "qnode-%04d" (i * 5))
                ()
            sw.Stop()

            output.WriteLine(sprintf "100 node lookups: %d ms (%.3f ms/lookup)" sw.ElapsedMilliseconds (float sw.ElapsedMilliseconds / 100.0))

            // Measure list all nodes
            let sw2 = Stopwatch.StartNew()
            let allNodes = db.GetAllNodes()
            sw2.Stop()

            output.WriteLine(sprintf "List all 500 nodes: %d ms" sw2.ElapsedMilliseconds)
            Assert.Equal(500, allNodes.Length)
        finally
            cleanupTempDir tempDir

    [<Fact>]
    member _.``Link-heavy file performance`` () =
        let tempDir = createTempDir()
        try
            // Create 50 target files
            for i in 1..50 do
                let content = generateOrgFile (sprintf "target-%02d" i) (sprintf "Target %d" i) []
                File.WriteAllText(Path.Combine(tempDir, sprintf "target-%02d.org" i), content)

            // Create one file with 200 links
            let links = [1..200] |> List.map (fun i -> sprintf "[[id:target-%02d][Link %d]]" ((i % 50) + 1) i) |> String.concat " "
            let hubContent = sprintf ":PROPERTIES:\n:ID: hub-node\n:END:\n#+title: Hub\n\n%s\n" links
            File.WriteAllText(Path.Combine(tempDir, "hub.org"), hubContent)

            let dbPath = Path.Combine(tempDir, ".org-roam.db")
            use db = new Database.OrgRoamDb(dbPath)

            let sw = Stopwatch.StartNew()
            Sync.sync db tempDir false |> List.iter (fun (f, e) -> output.WriteLine(sprintf "Error: %s: %s" f e))
            sw.Stop()

            output.WriteLine(sprintf "Sync 51 files (200 links): %d ms" sw.ElapsedMilliseconds)

            let linksFrom = db.GetLinksFrom("hub-node")
            output.WriteLine(sprintf "Links from hub: %d" linksFrom.Length)
            Assert.True(linksFrom.Length >= 50) // At least 50 unique targets
        finally
            cleanupTempDir tempDir

    [<Fact>]
    member _.``Incremental sync is faster than full sync`` () =
        let tempDir = createTempDir()
        try
            for i in 1..100 do
                let content = generateOrgFile (sprintf "inc-%03d" i) (sprintf "Inc Note %d" i) []
                File.WriteAllText(Path.Combine(tempDir, sprintf "inc-%03d.org" i), content)

            let dbPath = Path.Combine(tempDir, ".org-roam.db")
            use db = new Database.OrgRoamDb(dbPath)

            // Initial sync
            let sw1 = Stopwatch.StartNew()
            Sync.sync db tempDir false |> List.iter (fun (f, e) -> output.WriteLine(sprintf "Error: %s: %s" f e))
            sw1.Stop()
            let initialTime = sw1.ElapsedMilliseconds

            // Modify one file
            let modContent = generateOrgFile "inc-050" "Modified Note 50" []
            File.WriteAllText(Path.Combine(tempDir, "inc-050.org"), modContent)

            // Incremental sync
            let sw2 = Stopwatch.StartNew()
            Sync.sync db tempDir false |> List.iter (fun (f, e) -> output.WriteLine(sprintf "Error: %s: %s" f e))
            sw2.Stop()
            let incrementalTime = sw2.ElapsedMilliseconds

            output.WriteLine(sprintf "Initial sync: %d ms" initialTime)
            output.WriteLine(sprintf "Incremental sync (1 file changed): %d ms" incrementalTime)

            // Incremental should be faster (or at least not much slower)
            // Allow some tolerance for timing variability
            Assert.True(incrementalTime <= initialTime + 100L,
                sprintf "Incremental sync (%d ms) should not be much slower than initial (%d ms)" incrementalTime initialTime)
        finally
            cleanupTempDir tempDir

    [<Fact>]
    member _.``Backlink query performance`` () =
        let tempDir = createTempDir()
        try
            // Create a star topology: 1 central node, 100 nodes linking to it
            let centerContent = generateOrgFile "center" "Center Node" []
            File.WriteAllText(Path.Combine(tempDir, "center.org"), centerContent)

            for i in 1..100 do
                let content = generateOrgFile (sprintf "spoke-%03d" i) (sprintf "Spoke %d" i) ["center"]
                File.WriteAllText(Path.Combine(tempDir, sprintf "spoke-%03d.org" i), content)

            let dbPath = Path.Combine(tempDir, ".org-roam.db")
            use db = new Database.OrgRoamDb(dbPath)
            Sync.sync db tempDir false |> List.iter (fun (f, e) -> output.WriteLine(sprintf "Error: %s: %s" f e))

            let sw = Stopwatch.StartNew()
            let backlinks = Sync.getBacklinks db "center"
            sw.Stop()

            output.WriteLine(sprintf "Get 100 backlinks: %d ms" sw.ElapsedMilliseconds)
            Assert.Equal(100, backlinks.Length)
        finally
            cleanupTempDir tempDir
