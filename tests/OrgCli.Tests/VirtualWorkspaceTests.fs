[<Xunit.Collection("ConsoleCapture")>]
module OrgCli.Tests.VirtualWorkspaceTests

open System
open System.IO
open System.Text
open System.Collections.Generic
open Microsoft.Data.Sqlite
open Xunit
open OrgCli.Org
open OrgCli.Index

/// Entire filesystem and environment are private. SQLite is real, but lives in shared memory.
type VirtualHost() =
    let files = Dictionary<string, byte array>()
    let directories = HashSet<string>([ "/"; "/work"; "/home/test" ])
    let env = Dictionary<string, string>()
    let databases = Dictionary<string, SqliteConnection>()
    let token = Guid.NewGuid().ToString("N")
    let locks = HashSet<string>()
    let mutable moveCount = 0
    let mutable failMove = 0
    let mutable writes = 0
    let mutable reads = 0
    let mutable enumerations = 0
    let mutable clock = DateTime(2026, 9, 16, 12, 0, 0)
    member _.Files = files
    member _.Writes = writes
    member _.Reads = reads
    member _.Enumerations = enumerations
    member _.Environment = env

    member _.FailMove
        with set v =
            failMove <- v
            moveCount <- 0

    member _.Put(path, text) =
        files.[path] <- Encoding.UTF8.GetBytes(text: string)

    member _.Text path = Encoding.UTF8.GetString files.[path]

    member _.Clock
        with set v = clock <- v

    interface Runtime.IHost with
        member _.CurrentDirectory = "/work"
        member _.HomeDirectory = "/home/test"
        member _.Now = clock

        member _.GetEnvironmentVariable name =
            match env.TryGetValue name with
            | true, v -> v
            | _ -> null

        member _.FileExists p = files.ContainsKey p
        member _.DirectoryExists p = directories.Contains p

        member _.ReadBytes p =
            reads <- reads + 1
            Array.copy files.[p]

        member _.WriteBytes(p, b) =
            if not (directories.Contains(Path.GetDirectoryName p)) then
                raise (DirectoryNotFoundException p)

            writes <- writes + 1
            files.[p] <- Array.copy b

        member _.MoveFile(a, b, replace) =
            moveCount <- moveCount + 1

            if moveCount = failMove then
                raise (IOException "Injected rename failure")

            if directories.Contains b || (files.ContainsKey b && not replace) then
                raise (IOException "Destination exists")

            files.[b] <- files.[a]
            files.Remove a |> ignore

        member _.DeleteFile p = files.Remove p |> ignore
        member _.CreateDirectory p = directories.Add p |> ignore
        member _.DeleteDirectory p = directories.Remove p |> ignore

        member _.EnumerateFiles p =
            enumerations <- enumerations + 1

            files.Keys
            |> Seq.filter (fun f -> f.StartsWith(p.TrimEnd('/') + "/"))
            |> Seq.toList
        // Deliberately never advances: catches mtime-only invalidation bugs.
        member _.LastWriteTime _ =
            DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)

        member _.AcquireLock p =
            if not (locks.Add p) then
                raise (IOException "Locked")

            { new IDisposable with
                member _.Dispose() = locks.Remove p |> ignore }

        member _.DatabaseConnectionString p =
            match databases.TryGetValue p with
            | true, c -> c.ConnectionString
            | _ ->
                let cs =
                    sprintf "Data Source=%s-%d;Mode=Memory;Cache=Shared;Foreign Keys=True" token databases.Count

                let c = new SqliteConnection(cs)
                c.Open()
                databases.Add(p, c)
                cs

    interface IDisposable with
        member _.Dispose() =
            for c in databases.Values do
                c.Dispose()

let run (host: VirtualHost) args =
    use output = new StringWriter()
    use error = new StringWriter()
    let previousOut, previousError = Console.Out, Console.Error

    try
        Console.SetOut output
        Console.SetError error
        let code = Program.runWithHost host (List.toArray args)
        code, output.ToString(), error.ToString()
    finally
        Console.SetOut previousOut
        Console.SetError previousError

[<Fact>]
let ``fresh workspace creates persistent identities without an index or roam`` () =
    use h = new VirtualHost()

    let code, text, _ =
        run h [ "add"; "tasks.org"; "Rent"; "--todo"; "TODO"; "-f"; "json" ]

    Assert.Equal(0, code)
    let doc = Document.parse (h.Text "/work/tasks.org")
    Assert.True((Types.tryGetId doc.Headlines.Head.Properties).IsSome)
    Assert.DoesNotContain("CUSTOM_ID", h.Text "/work/tasks.org")
    Assert.DoesNotContain("org-roam", text)

[<Fact>]
let ``queries refresh changes with identical metadata and handle rename`` () =
    use h = new VirtualHost()
    h.Put("/work/a.org", "* Alpha\n:PROPERTIES:\n:ID: first\n:END:\n")
    let a, _, _ = run h [ "fts"; "Alpha"; "-f"; "json" ]
    Assert.Equal(0, a)
    h.Files.Remove "/work/a.org" |> ignore
    h.Put("/work/b.org", "* Bravo\n:PROPERTIES:\n:ID: first\n:END:\n")
    let b, text, _ = run h [ "fts"; "Bravo"; "-f"; "json" ]
    Assert.Equal(0, b)
    Assert.Contains("Bravo", text)
    let c, _, _ = run h [ "todo"; "id:first"; "DONE" ]
    Assert.Equal(0, c)
    Assert.Contains("* DONE Bravo", h.Text "/work/b.org")

[<Fact>]
let ``numeric custom identity never edits an offset`` () =
    use h = new VirtualHost()
    h.Put("/work/a.org", "* TODO Wrong\n* TODO Right\n:PROPERTIES:\n:CUSTOM_ID: 000\n:END:\n")
    let code, _, _ = run h [ "todo"; "000"; "DONE" ]
    Assert.Equal(0, code)
    Assert.StartsWith("* TODO Wrong\n* DONE Right", h.Text "/work/a.org")

[<Fact>]
let ``duplicate title creation preserves old identity`` () =
    use h = new VirtualHost()
    h.Put("/work/a.org", "* Same\n:PROPERTIES:\n:ID: original\n:END:\n")
    let code, _, _ = run h [ "add"; "a.org"; "Same" ]
    Assert.Equal(0, code)
    let doc = Document.parse (h.Text "/work/a.org")
    Assert.Equal(Some "original", Types.tryGetId doc.Headlines.Head.Properties)
    Assert.NotEqual(Some "original", Types.tryGetId doc.Headlines.[1].Properties)

[<Fact>]
let ``dry run before positional arguments writes nothing`` () =
    use h = new VirtualHost()
    let code, _, _ = run h [ "--dry-run"; "add"; "a.org"; "Preview" ]
    Assert.Equal(0, code)
    Assert.Equal(0, h.Writes)
    Assert.Empty h.Files

[<Fact>]
let ``environment selects workspace and clock without changing process globals`` () =
    use h = new VirtualHost()
    (h :> Runtime.IHost).CreateDirectory "/notes"
    h.Environment.["ORG_CLI_DIRECTORY"] <- "/notes"
    h.Put("/notes/a.org", "* TODO Due\nSCHEDULED: <2026-09-16 Wed>\n")
    let code, text, _ = run h [ "today"; "-f"; "json" ]
    Assert.Equal(0, code)
    Assert.Contains("Due", text)
    h.Clock <- DateTime(2026, 9, 15)
    let _, before, _ = run h [ "today"; "-f"; "json" ]
    Assert.DoesNotContain("Due", before)

[<Fact>]
let ``conflicting mutation is rejected before any writes`` () =
    use h = new VirtualHost()
    use scope = Runtime.useHost h
    h.Put("/work/a.org", "* Original\n")
    let plan = Runtime.edit "/work/a.org" "* Changed\n"
    h.Put("/work/a.org", "* External edit\n")
    Assert.Throws<IOException>(fun () -> Runtime.commit [ plan ]) |> ignore
    Assert.Equal("* External edit\n", h.Text "/work/a.org")
    Assert.Equal(0, h.Writes)

[<Fact>]
let ``failed second replacement leaves destination and recovery record`` () =
    use h = new VirtualHost()
    use scope = Runtime.useHost h
    h.Put("/work/source.org", "* Valuable\n")
    h.FailMove <- 2

    Assert.Throws<IOException>(fun () ->
        Runtime.commit
            [ Runtime.edit "/work/destination.org" "* Valuable\n"
              Runtime.edit "/work/source.org" "" ])
    |> ignore

    Assert.Equal("* Valuable\n", h.Text "/work/source.org")
    Assert.Equal("* Valuable\n", h.Text "/work/destination.org")
    Assert.True(h.Files.Keys |> Seq.exists (fun p -> p.EndsWith("manifest.json")))

[<Fact>]
let ``file root roam note can be appended and read without a roam database`` () =
    use h = new VirtualHost()
    let code, json, _ = run h [ "roam"; "node"; "create"; "Sarah"; "-f"; "json" ]
    Assert.Equal(0, code)
    let parsed = System.Text.Json.Nodes.JsonNode.Parse json
    let id = parsed.["data"].["id"].GetValue<string>()
    let append, _, _ = run h [ "append"; "id:" + id; "Prefers mornings" ]
    Assert.Equal(0, append)
    let read, text, _ = run h [ "read"; "id:" + id; "-f"; "json" ]
    Assert.Equal(0, read)
    Assert.Contains("Prefers mornings", text)
    Assert.False(h.Files.Keys |> Seq.exists (fun p -> p.EndsWith(".org.db")))

[<Fact>]
let ``duplicate IDs are retained and reported as ambiguous`` () =
    use h = new VirtualHost()
    h.Put("/work/a.org", "* First\n:PROPERTIES:\n:ID: same\n:END:\n")
    h.Put("/work/b.org", "* Second\n:PROPERTIES:\n:ID: same\n:END:\n")
    let code, _, error = run h [ "todo"; "id:same"; "DONE" ]
    Assert.Equal(1, code)
    Assert.Contains("Ambiguous", error)
    Assert.DoesNotContain("DONE", h.Text "/work/a.org")

[<Fact>]
let ``recovery completes a partial move and refuses external edits`` () =
    use h = new VirtualHost()
    use scope = Runtime.useHost h
    h.Put("/work/source.org", "* Original\n")
    h.FailMove <- 2

    Assert.Throws<IOException>(fun () ->
        Runtime.commit
            [ Runtime.edit "/work/dest.org" "* Original\n"
              Runtime.edit "/work/source.org" "" ])
    |> ignore

    let manifest = h.Files.Keys |> Seq.find (fun p -> p.EndsWith("manifest.json"))
    h.Put("/work/source.org", "* External\n")
    Assert.Throws<IOException>(fun () -> Runtime.recover manifest) |> ignore
    Assert.Equal("* External\n", h.Text "/work/source.org")
    h.Put("/work/source.org", "* Original\n")
    h.FailMove <- 0
    Runtime.recover manifest
    Assert.Equal("", h.Text "/work/source.org")
    Assert.Equal("* Original\n", h.Text "/work/dest.org")

    Assert.False(
        h.Files.Keys
        |> Seq.exists (fun p -> p.EndsWith(".org-pending") || p.EndsWith("manifest.json"))
    )

[<Fact>]
let ``file roots are searchable without a headline`` () =
    use h = new VirtualHost()
    h.Put("/work/note.org", ":PROPERTIES:\n:ID: note\n:END:\n#+title: Sarah\nPrefers mornings\n")
    let code, text, _ = run h [ "fts"; "mornings"; "-f"; "json" ]
    Assert.Equal(0, code)
    Assert.Contains("Sarah", text)
    Assert.Contains("note", text)

[<Fact>]
let ``core backlinks include ordinary headings and omit source block examples`` () =
    use h = new VirtualHost()

    h.Put(
        "/work/a.org",
        ":PROPERTIES:\n:ID: root\n:END:\n* Ordinary\n[[id:target]]\n#+begin_src org\n[[id:example]]\n#+end_src\n"
    )

    let code, text, _ = run h [ "backlinks"; "id:target"; "-f"; "json" ]
    Assert.Equal(0, code)
    Assert.Contains("root", text)
    let _, example, _ = run h [ "backlinks"; "id:example"; "-f"; "json" ]
    Assert.DoesNotContain("source_id", example)

[<Fact>]
let ``archive validates destination before removing source`` () =
    use h = new VirtualHost()
    h.Put("/work/a.org", "* Keep\nImportant\n")
    (h :> Runtime.IHost).CreateDirectory "/work/a.org_archive"
    let code, _, _ = run h [ "archive"; "a.org"; "Keep" ]
    Assert.Equal(1, code)
    Assert.Equal("* Keep\nImportant\n", h.Text "/work/a.org")

[<Fact>]
let ``refile preserves source blocks and moves within the same file exactly once`` () =
    use h = new VirtualHost()
    let block = "#+begin_src org\n* Example\n#+end_src\n"
    h.Put("/work/a.org", "* One\n" + block + "* Two\n")
    let code, _, _ = run h [ "refile"; "a.org"; "One"; "a.org" ]
    Assert.Equal(0, code)
    Assert.Equal("* Two\n* One\n" + block, h.Text "/work/a.org")

[<Fact>]
let ``file-specific completion keywords exclude finished work from today`` () =
    use h = new VirtualHost()
    h.Put("/work/a.org", "#+TODO: NEXT | FINISHED\n* FINISHED Closed\nSCHEDULED: <2020-01-01 Wed>\n")
    let code, text, _ = run h [ "today"; "-f"; "json" ]
    Assert.Equal(0, code)
    Assert.DoesNotContain("Closed", text)

[<Fact>]
let ``cache reuses unchanged projections and invalidates configuration changes`` () =
    use h = new VirtualHost()
    use scope = Runtime.useHost h
    h.Put("/work/a.org", "* TODO Task\n")
    use db = new IndexDatabase.OrgIndexDb("/work/.org-index.db")
    db.Initialize()
    IndexSync.syncDirectory db "/work"
    let first = db.GetFileHash "/work/a.org"
    IndexSync.syncDirectory db "/work"
    Assert.Equal(first, db.GetFileHash "/work/a.org")
    h.Environment.["ORG_CLI_TAG_INHERITANCE"] <- "false"
    IndexSync.syncDirectory db "/work"
    Assert.NotEqual(first, db.GetFileHash "/work/a.org")

[<Fact>]
let ``virtual environments and caches do not leak between invocations`` () =
    use first = new VirtualHost()
    use second = new VirtualHost()
    first.Put("/work/a.org", "* Private\n")
    second.Put("/work/a.org", "* Separate\n")
    let _, a, _ = run first [ "fts"; "Private"; "-f"; "json" ]
    let _, b, _ = run second [ "fts"; "Private"; "-f"; "json" ]
    Assert.Contains("Private", a)
    Assert.DoesNotContain("Private", b)

[<Fact>]
let ``refused overlapping edit preserves another operation pending markers`` () =
    use h = new VirtualHost()
    use scope = Runtime.useHost h
    h.Put("/work/a.org", "* Before\n")
    h.Put("/work/a.org.org-pending", "/work/existing/manifest.json")

    Assert.Throws<IOException>(fun () -> Runtime.commit [ Runtime.edit "/work/a.org" "* After\n" ])
    |> ignore

    Assert.Equal("/work/existing/manifest.json", h.Text "/work/a.org.org-pending")
    Assert.Equal("* Before\n", h.Text "/work/a.org")

[<Fact>]
let ``failed recovery keeps every target pending until completion`` () =
    use h = new VirtualHost()
    use scope = Runtime.useHost h
    h.Put("/work/a.org", "* A\n")
    h.Put("/work/b.org", "* B\n")
    h.FailMove <- 1

    Assert.Throws<IOException>(fun () ->
        Runtime.commit
            [ Runtime.edit "/work/a.org" "* New A\n"
              Runtime.edit "/work/b.org" "* New B\n" ])
    |> ignore

    let manifest = h.Files.Keys |> Seq.find (fun p -> p.EndsWith("manifest.json"))
    h.FailMove <- 2
    Assert.Throws<IOException>(fun () -> Runtime.recover manifest) |> ignore
    Assert.True(h.Files.ContainsKey "/work/a.org.org-pending")
    Assert.True(h.Files.ContainsKey "/work/b.org.org-pending")
    h.FailMove <- 0
    Runtime.recover manifest
    Assert.Equal("* New A\n", h.Text "/work/a.org")
    Assert.Equal("* New B\n", h.Text "/work/b.org")
