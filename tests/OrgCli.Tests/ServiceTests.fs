[<Xunit.Collection("ConsoleCapture")>]
module OrgCli.Tests.ServiceTests

open System
open System.IO
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
open OrgCli.Org
open OrgCli.Index.Application
open OrgCli.Tests.VirtualWorkspaceTests

let args (text: string) = JsonNode.Parse(text).AsObject()

let service (h: VirtualHost) readOnly =
    WorkspaceService(h, "/work", "/work/.org-index.db", Types.defaultConfig, readOnly = readOnly)

let field (node: JsonNode) (key: string) = node.[key].GetValue<string>()
let request = "5f19d4f1-9d06-4dc7-a5d2-500101b107f1"

let capture =
    """{"request_id":"5f19d4f1-9d06-4dc7-a5d2-500101b107f1","title":"A task","state":"TODO","text":"Useful body"}"""

let change entry (fields: (string * string) list) =
    let a = JsonObject()
    a.["ref"] <- JsonValue.Create(field entry "ref")
    a.["expected_revision"] <- JsonValue.Create(field entry "revision")

    for name, value in fields do
        a.[name] <- JsonValue.Create(value: string)

    a

let expectError (code: string) f =
    let error = Assert.Throws<ServiceError>(Action(fun () -> f () |> ignore))

    match (error :> exn) with
    | ServiceError(actual, _, _) -> Assert.Equal(code, actual)
    | _ -> failwith "Wrong exception"

[<Fact>]
let ``capture retries are persistent and distinct payload reuse fails`` () =
    use h = new VirtualHost()
    let first = (service h false).Invoke("capture", args capture)
    let again = (service h false).Invoke("capture", args capture)
    Assert.Equal(field first "ref", field again "ref")
    Assert.Equal(1, (h.Text "/work/inbox.org").Split(":ID:").Length - 1)

    expectError "conflict" (fun () ->
        (service h false).Invoke("capture", args (capture.Replace("A task", "Different"))))

[<Fact>]
let ``fetch revision protects updates and stale retries do not append twice`` () =
    use h = new VirtualHost()
    let svc = service h false
    let entry = svc.Invoke("capture", args capture)
    let updated = svc.Invoke("append_note", change entry [ "text", "Additional fact" ])
    expectError "conflict" (fun () -> svc.Invoke("append_note", change entry [ "text", "Additional fact" ]))
    Assert.Contains("Additional fact", field updated "text")

    let doneEntry =
        svc.Invoke("update_task", change updated [ "state", "DONE"; "scheduled", "2026-09-17" ])

    Assert.Contains("DONE", field doneEntry "text")
    Assert.Contains("2026-09-17", field doneEntry "text")

[<Fact>]
let ``queries refresh unchanged timestamps and locators reject changed files`` () =
    use h = new VirtualHost()
    h.Put("/work/plain.org", "* First\nUniquephrase\n")
    let svc = service h false
    let results = svc.Invoke("search", args """{"query":"Uniquephrase"}""")
    let reference = field results.["results"].[0] "ref"
    let fetchArgs = JsonObject()
    fetchArgs.["ref"] <- JsonValue.Create reference
    Assert.Contains("Uniquephrase", field (svc.Invoke("fetch", fetchArgs)) "text")
    h.Put("/work/plain.org", "* Second\nNewphrase\n")
    expectError "conflict" (fun () -> svc.Invoke("fetch", fetchArgs))
    let refreshed = svc.Invoke("search", args """{"query":"Newphrase"}""")
    Assert.Single(refreshed.["results"].AsArray()) |> ignore

[<Fact>]
let ``file roots append without org roam`` () =
    use h = new VirtualHost()
    h.Put("/work/root.org", ":PROPERTIES:\n:ID: root\n:END:\n#+title: Root\nBefore\n* Child\n")
    let svc = service h false
    let root = svc.Invoke("fetch", args """{"ref":"id:root"}""")
    svc.Invoke("append_note", change root [ "text", "After" ]) |> ignore
    Assert.True((h.Text "/work/root.org").IndexOf("After") < (h.Text "/work/root.org").IndexOf("* Child"))

[<Fact>]
let ``read only transport hides and refuses mutations`` () =
    use h = new VirtualHost()
    let svc = service h true
    Assert.DoesNotContain(OrgCli.Server.tools true, fun t -> t.Name = "capture")
    let status, result = OrgCli.Server.invoke svc "capture" (args capture)
    Assert.Equal(403, status)
    Assert.Equal("read_only", field result.["error"] "code")
    Assert.Empty(h.Files)

[<Fact>]
let ``agenda uses virtual clock and file local completion states`` () =
    use h = new VirtualHost()

    h.Put(
        "/work/tasks.org",
        "#+TODO: OPEN | FINISHED\n* OPEN Overdue\nSCHEDULED: <2026-09-15 Tue>\n* FINISHED Complete\nDEADLINE: <2026-09-16 Wed>\n* OPEN Future\nSCHEDULED: <2026-10-01 Thu>\n"
    )

    let results = (service h false).Invoke("agenda", JsonObject())
    Assert.Single(results.["items"].AsArray()) |> ignore
    Assert.Equal("Overdue", field results.["items"].[0] "title")
    Assert.True((results.["items"].[0].["overdue"]).GetValue<bool>())

[<Fact>]
let ``invalid requests cannot mutate or choose arbitrary files`` () =
    use h = new VirtualHost()
    let svc = service h false
    let entry = svc.Invoke("capture", args capture)
    let before = h.Text "/work/inbox.org"
    expectError "invalid_arguments" (fun () -> svc.Invoke("update_task", change entry [ "state", "UNKNOWN" ]))
    expectError "invalid_arguments" (fun () -> svc.Invoke("update_task", change entry [ "deadline", "not-a-date" ]))

    expectError "invalid_arguments" (fun () ->
        svc.Invoke("append_note", args """{"ref":"id:x","text":"x","file":"/outside.org"}"""))

    expectError "invalid_arguments" (fun () -> svc.Invoke("search", args """{"query":123}"""))
    Assert.Equal(before, h.Text "/work/inbox.org")

[<Fact>]
let ``HTTP and MCP use the virtual workspace and enforce transport boundaries`` () : Task =
    task {
        use h = new VirtualHost()
        let app = OrgCli.Server.buildHttp (service h false) 0 true (Some "test-secret")
        do! app.StartAsync()

        try
            use client = new HttpClient(BaseAddress = Uri(Seq.head app.Urls))
            let! unauthenticated = client.GetAsync("/health")
            Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode)
            client.DefaultRequestHeaders.Authorization <- Headers.AuthenticationHeaderValue("Bearer", "test-secret")
            let! tools = client.GetAsync("/api/v1/tools")
            Assert.Equal(HttpStatusCode.OK, tools.StatusCode)

            let! created =
                client.PostAsync("/api/v1/capture", new StringContent(capture, Encoding.UTF8, "application/json"))

            Assert.Equal(HttpStatusCode.OK, created.StatusCode)
            Assert.Contains("Useful body", h.Text "/work/inbox.org")
            let! invalid = client.PostAsync("/api/v1/fetch", new StringContent("{", Encoding.UTF8, "application/json"))
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode)
            use evil = new HttpRequestMessage(HttpMethod.Get, "/health")
            evil.Headers.Add("Origin", "https://unrelated.example")
            let! forbidden = client.SendAsync evil
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode)
            use rebinding = new HttpRequestMessage(HttpMethod.Get, "/health")
            rebinding.Headers.Host <- "unrelated.example"
            let! blocked = client.SendAsync rebinding
            Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode)
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream")

            let initialize =
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}"""

            let! initialized =
                client.PostAsync("/mcp", new StringContent(initialize, Encoding.UTF8, "application/json"))

            let! initializedText = initialized.Content.ReadAsStringAsync()
            Assert.Equal(HttpStatusCode.OK, initialized.StatusCode)
            Assert.Contains("org-cli", initializedText)
            client.DefaultRequestHeaders.Add("MCP-Protocol-Version", "2025-11-25")

            let query =
                """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"search","arguments":{"query":"Useful"}}}"""

            let! queried = client.PostAsync("/mcp", new StringContent(query, Encoding.UTF8, "application/json"))
            let! queriedText = queried.Content.ReadAsStringAsync()
            Assert.Equal(HttpStatusCode.OK, queried.StatusCode)
            Assert.Contains("A task", queriedText)
        finally
            app.StopAsync().GetAwaiter().GetResult()
            app.DisposeAsync().AsTask().GetAwaiter().GetResult()
    }

[<Fact>]
let ``plain API does not expose MCP`` () : Task =
    task {
        use h = new VirtualHost()
        let app = OrgCli.Server.buildHttp (service h true) 0 false None
        do! app.StartAsync()

        try
            use client = new HttpClient(BaseAddress = Uri(Seq.head app.Urls))
            let! response = client.PostAsync("/mcp", new StringContent("{}", Encoding.UTF8, "application/json"))
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode)
        finally
            app.StopAsync().GetAwaiter().GetResult()
            app.DisposeAsync().AsTask().GetAwaiter().GetResult()
    }

[<Fact>]
let ``entry locators cannot escape the configured workspace`` () =
    use h = new VirtualHost()
    h.Put("/outside.org", "* Private\n")

    let data =
        obj
            [ "file", str "../outside.org"
              "position", number 0
              "revision", str (revision "* Private\n") ]

    let reference =
        "loc:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(data.ToJsonString()))

    let a = JsonObject()
    a["ref"] <- JsonValue.Create reference
    expectError "not_found" (fun () -> (service h false).Invoke("fetch", a))

[<Fact>]
let ``captures cannot reuse file root identities`` () =
    use h = new VirtualHost()
    h.Put("/work/root.org", ":PROPERTIES:\n:ID: " + request + "\n:END:\n#+title: Root\n")
    expectError "conflict" (fun () -> (service h false).Invoke("capture", args capture))
    Assert.False(h.Files.ContainsKey "/work/inbox.org")

[<Fact>]
let ``invalid later field prevents every task change`` () =
    use h = new VirtualHost()
    let svc = service h false
    let entry = svc.Invoke("capture", args capture)
    let before = h.Text "/work/inbox.org"

    expectError "invalid_arguments" (fun () ->
        svc.Invoke("update_task", change entry [ "state", "DONE"; "deadline", "bad-date" ]))

    Assert.Equal(before, h.Text "/work/inbox.org")

[<Fact>]
let ``fetch pagination and explicit clearing keep their documented semantics`` () =
    use h = new VirtualHost()
    let svc = service h false
    let entry = svc.Invoke("capture", args capture)

    let scheduled =
        svc.Invoke("update_task", change entry [ "scheduled", "2026-09-17"; "priority", "A" ])

    let cleared =
        svc.Invoke("update_task", change scheduled [ "scheduled", ""; "priority", "" ])

    Assert.DoesNotContain("SCHEDULED:", field cleared "text")
    Assert.DoesNotContain("[#A]", field cleared "text")
    let page = args """{"limit":5,"offset":0}"""
    page["ref"] <- JsonValue.Create(field entry "ref")
    let first = svc.Invoke("fetch", page)
    Assert.Equal(5, (field first "text").Length)
    Assert.Equal(5, first["next_offset"].GetValue<int>())

[<Fact>]
let ``external changes after fetch are preserved`` () =
    use h = new VirtualHost()
    let svc = service h false
    let entry = svc.Invoke("capture", args capture)
    h.Put("/work/inbox.org", h.Text "/work/inbox.org" + "\n* External task\n")
    expectError "conflict" (fun () -> svc.Invoke("update_task", change entry [ "state", "DONE" ]))
    Assert.Contains("External task", h.Text "/work/inbox.org")
    Assert.DoesNotContain("DONE", h.Text "/work/inbox.org")

let watchedNote id word =
    $"* {word}\n:PROPERTIES:\n:ID: {id}\n:END:\n"

let watchedSearch (svc: WorkspaceService) word =
    let a = JsonObject()
    a["query"] <- JsonValue.Create(word: string)
    svc.Invoke("search", a).["results"].AsArray()

[<Fact>]
let ``watched queries avoid corpus reads and refresh only notified files`` () =
    use h = new VirtualHost()

    for i in 1..20 do
        h.Put($"/work/{i}.org", watchedNote (string i) "oldword")

    let svc = service h false
    svc.EnableWatching()
    svc.Refresh()
    let reads = h.Reads
    let enumerations = h.Enumerations
    Assert.Equal(20, (watchedSearch svc "oldword").Count)
    svc.Invoke("agenda", args "{}") |> ignore
    Assert.Equal(reads, h.Reads)
    Assert.Equal(enumerations, h.Enumerations)
    // VirtualHost never advances mtimes, and these replacements have equal size.
    h.Put("/work/1.org", watchedNote "1" "newword")
    svc.InvalidatePath "/work/1.org"
    svc.InvalidatePath "/work/1.org"
    Assert.Single(watchedSearch svc "newword") |> ignore
    Assert.Equal(reads + 1, h.Reads)
    Assert.Equal(19, (watchedSearch svc "oldword").Count)

[<Fact>]
let ``watched refresh handles rename deletion and new files`` () =
    use h = new VirtualHost()
    h.Put("/work/old.org", watchedNote "one" "renameword")
    let svc = service h false
    svc.EnableWatching()
    svc.Refresh()
    (h :> Runtime.IHost).MoveFile("/work/old.org", "/work/new.org", false)
    svc.InvalidatePath "/work/old.org"
    svc.InvalidatePath "/work/new.org"
    let hits = watchedSearch svc "renameword"
    Assert.Single(hits) |> ignore
    Assert.Equal("new.org", field hits[0] "file")
    (h :> Runtime.IHost).DeleteFile "/work/new.org"
    svc.InvalidatePath "/work/new.org"
    Assert.Empty(watchedSearch svc "renameword")
    h.Put("/work/created.org", watchedNote "two" "createdword")
    svc.InvalidatePath "/work/created.org"
    Assert.Single(watchedSearch svc "createdword") |> ignore

[<Fact>]
let ``watcher reconciliation repairs missed events and retries failed refresh`` () =
    use h = new VirtualHost()
    h.Put("/work/note.org", watchedNote "one" "beforeword")
    let svc = service h false
    svc.EnableWatching()
    svc.Refresh()
    h.Put("/work/note.org", watchedNote "one" "afterword")
    // Same path used for startup, overflow, directory events and periodic repair.
    svc.InvalidateAll()
    Assert.Single(watchedSearch svc "afterword") |> ignore
    h.Files["/work/note.org"] <- [| 0xffuy |]
    svc.InvalidatePath "/work/note.org"
    Assert.ThrowsAny<Exception>(Action(fun () -> svc.Refresh())) |> ignore
    h.Put("/work/note.org", watchedNote "one" "repairedword")
    // A failed batch remains pending even without another event.
    Assert.Single(watchedSearch svc "repairedword") |> ignore
    Assert.Empty(watchedSearch svc "afterword")

[<Fact>]
let ``server writes update watched projections without relying on watcher events`` () =
    use h = new VirtualHost()
    let svc = service h false
    svc.EnableWatching()
    svc.Refresh()
    let entry = svc.Invoke("capture", args capture)
    Assert.Single(watchedSearch svc "Useful") |> ignore
    let updated = svc.Invoke("append_note", change entry [ "text", "immediateword" ])
    Assert.Single(watchedSearch svc "immediateword") |> ignore
    let doneEntry = svc.Invoke("update_task", change updated [ "state", "DONE" ])
    Assert.Contains("DONE", field doneEntry "text")
    let again = svc.Invoke("capture", args capture)
    Assert.Equal(field entry "ref", field again "ref")

[<Fact>]
let ``stopping monitoring restores scan based freshness and excludes outside paths`` () =
    use h = new VirtualHost()
    h.Put("/work/note.org", watchedNote "one" "beforeword")
    h.Put("/home/test/outside.org", watchedNote "outside" "outsideword")
    let svc = service h false
    svc.EnableWatching()
    svc.InvalidatePath "/home/test/outside.org"
    Assert.Empty(watchedSearch svc "outsideword")
    svc.DisableWatching()
    h.Put("/work/note.org", watchedNote "one" "afterword")
    Assert.Single(watchedSearch svc "afterword") |> ignore

[<Fact>]
let ``watcher event queue overflow reconciles changes without an individual event`` () =
    use h = new VirtualHost()
    h.Put("/work/note.org", watchedNote "one" "beforeword")
    let svc = service h false
    svc.EnableWatching()
    svc.Refresh()
    h.Put("/work/note.org", watchedNote "one" "afterword")

    for i in 0..4096 do
        svc.InvalidatePath $"/work/burst-{i}.org"

    Assert.Single(watchedSearch svc "afterword") |> ignore
    Assert.Empty(watchedSearch svc "beforeword")
