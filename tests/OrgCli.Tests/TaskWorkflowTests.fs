[<Xunit.Collection("ConsoleCapture")>]
module OrgCli.Tests.TaskWorkflowTests

open System
open System.Text.Json.Nodes
open Xunit
open OrgCli.Org
open OrgCli.Index.Application
open OrgCli.Tests.VirtualWorkspaceTests

let arg (fields: (string * string) list) =
    let a = JsonObject()

    for key, value in fields do
        a[key] <- JsonValue.Create(value: string)

    a

let field (node: JsonNode) (key: string) = node[key].GetValue<string>()

let service h =
    WorkspaceService(h, "/work", "/work/.org-index.db", Types.defaultConfig)

let create (svc: WorkspaceService) title (review: bool) =
    let a =
        arg
            [ "request_id", Guid.NewGuid().ToString()
              "file", "tasks.org"
              "title", title
              "actor", "human"
              "acceptance", "Verified result" ]

    a["review_required"] <- JsonValue.Create review
    svc.Invoke("task_create", a)

let fetch (svc: WorkspaceService) entry =
    svc.Invoke("fetch", arg [ "ref", field entry "ref" ])

let action (svc: WorkspaceService) entry actor verb fields =
    let current = fetch svc entry

    let a =
        arg (
            [ "ref", field current "ref"
              "expected_revision", field current "revision"
              "actor", actor
              "action", verb ]
            @ fields
        )

    svc.Invoke("task_action", a)

let claim svc entry actor =
    action svc entry actor "claim" [ "claim_id", Guid.NewGuid().ToString() ]

let editArgs svc entry fields =
    let current = fetch svc entry

    arg (
        [ "ref", field current "ref"
          "expected_revision", field current "revision"
          "actor", "human" ]
        @ fields
    )

let configure (svc: WorkspaceService) entry fields =
    svc.Invoke("task_update", editArgs svc entry fields)

let fails (code: string) f =
    let ex = Assert.Throws<ServiceError>(Action(fun () -> f () |> ignore))

    match (ex :> exn) with
    | ServiceError(actual, _, _) -> Assert.Equal(code, actual)
    | _ -> failwith "Unexpected error"

let tasks (svc: WorkspaceService) status =
    (svc.Invoke("tasks", arg [ "status", status ])).["tasks"].AsArray()

let submit svc entry actor =
    action
        svc
        entry
        actor
        "submit"
        [ "claim_id", field entry "claim_id"
          "evidence", "Tests passed and result inspected" ]

[<Fact>]
let ``task lifecycle requires a separate reviewer and records evidence in Org`` () =
    use h = new VirtualHost()
    let svc = service h
    let entry = create svc "Deliver feature" true
    let claimed = claim svc entry "agent:one"
    Assert.Equal("working", field claimed "status")
    let submitted = submit svc claimed "agent:one"
    Assert.Equal("review", field submitted "status")
    fails "conflict" (fun () -> action svc submitted "agent:one" "approve" [ "evidence", "Self review" ])

    let approved =
        action svc submitted "human" "approve" [ "evidence", "Verified acceptance criteria" ]

    Assert.Equal("done", field approved "status")
    let content = h.Text "/work/tasks.org"
    Assert.Contains("Tests passed and result inspected", content)
    Assert.Contains("Verified acceptance criteria", content)
    Assert.Contains("TASK_REVIEWED_BY: human", content)
    Assert.Empty(tasks svc "open")

[<Fact>]
let ``persistent create retries neither duplicate tasks nor accept changed intent`` () =
    use h = new VirtualHost()

    let a =
        arg
            [ "request_id", Guid.NewGuid().ToString()
              "title", "Retry safe"
              "actor", "human" ]

    let first = (service h).Invoke("task_create", a)
    let repeated = (service h).Invoke("task_create", a)
    Assert.Equal(field first "ref", field repeated "ref")
    Assert.Single(tasks (service h) "all") |> ignore
    a["title"] <- JsonValue.Create "Different intent"
    fails "conflict" (fun () -> (service h).Invoke("task_create", a))

[<Fact>]
let ``dependency chain blocks execution until predecessor is approved`` () =
    use h = new VirtualHost()
    let svc = service h
    let predecessor = create svc "Build" true
    let successor = create svc "Ship" true
    let a = editArgs svc successor []
    a["depends_on"] <- JsonArray(JsonValue.Create(field predecessor "id"))
    let successor = svc.Invoke("task_update", a)
    Assert.Equal("blocked", field successor "status")
    fails "conflict" (fun () -> claim svc successor "agent:two")

    let submitted =
        claim svc predecessor "agent:one" |> fun task -> submit svc task "agent:one"

    fails "conflict" (fun () -> claim svc successor "agent:two")

    action svc submitted "human" "approve" [ "evidence", "Build verified" ]
    |> ignore

    Assert.Equal("working", field (claim svc successor "agent:two") "status")

[<Fact>]
let ``cyclic and missing dependencies fail without source changes`` () =
    use h = new VirtualHost()
    let svc = service h
    let first = create svc "One" false
    let second = create svc "Two" false
    let a = editArgs svc second []
    a["depends_on"] <- JsonArray(JsonValue.Create(field first "id"))
    svc.Invoke("task_update", a) |> ignore
    let before = h.Text "/work/tasks.org"
    let cycle = editArgs svc first []
    cycle["depends_on"] <- JsonArray(JsonValue.Create(field second "id"))
    fails "invalid_arguments" (fun () -> svc.Invoke("task_update", cycle))
    cycle["depends_on"] <- JsonArray(JsonValue.Create "unknown")
    fails "not_found" (fun () -> svc.Invoke("task_update", cycle))
    Assert.Equal(before, h.Text "/work/tasks.org")

[<Fact>]
let ``claims exclude competing actors and retry without extending expiry`` () =
    use h = new VirtualHost()
    let svc = service h
    let entry = create svc "One owner" true

    let a =
        arg
            [ "ref", field entry "ref"
              "expected_revision", field entry "revision"
              "actor", "agent:one"
              "action", "claim"
              "claim_id", Guid.NewGuid().ToString() ]

    let first = svc.Invoke("task_action", a)
    let repeated = (service h).Invoke("task_action", a)
    Assert.Equal(field first "claim_until", field repeated "claim_until")
    Assert.Equal(field first "revision", field repeated "revision")
    fails "conflict" (fun () -> claim (service h) entry "agent:two")

    fails "conflict" (fun () ->
        action svc first "agent:two" "submit" [ "claim_id", field first "claim_id"; "evidence", "Wrong owner" ])

[<Fact>]
let ``expired claims can be reassigned and previous worker cannot submit`` () =
    use h = new VirtualHost()
    let svc = service h
    let claimed = create svc "Lease expiry" false |> fun e -> claim svc e "agent:one"
    h.Clock <- DateTime(2026, 9, 16, 13, 0, 0)
    Assert.Single(tasks svc "ready") |> ignore
    fails "conflict" (fun () -> submit svc claimed "agent:one")
    let fresh = claim svc claimed "agent:two"
    fails "conflict" (fun () -> submit svc claimed "agent:one")
    Assert.Equal("done", field (submit svc fresh "agent:two") "status")

[<Fact>]
let ``renew and release preserve ownership and allow subsequent handoff`` () =
    use h = new VirtualHost()
    let svc = service h
    let claimed = create svc "Handoff" false |> fun e -> claim svc e "agent:one"
    h.Clock <- DateTime(2026, 9, 16, 12, 10, 0)

    let renewed =
        action svc claimed "agent:one" "renew" [ "claim_id", field claimed "claim_id" ]

    Assert.NotEqual<string>(field claimed "claim_until", field renewed "claim_until")

    let released =
        action svc renewed "agent:one" "release" [ "claim_id", field renewed "claim_id"; "evidence", "Handing off" ]

    Assert.Equal("ready", field released "status")
    Assert.Equal("working", field (claim svc released "agent:two") "status")

[<Fact>]
let ``rejected work returns to ready and cancellation does not satisfy dependencies`` () =
    use h = new VirtualHost()
    let svc = service h
    let first = create svc "Review changes" true
    let second = create svc "Depends" false
    let a = editArgs svc second []
    a["depends_on"] <- JsonArray(JsonValue.Create(field first "id"))
    svc.Invoke("task_update", a) |> ignore
    let submitted = claim svc first "agent" |> fun e -> submit svc e "agent"

    Assert.Equal(
        "ready",
        field (action svc submitted "human" "reject" [ "evidence", "Add regression coverage" ]) "status"
    )

    let cancelled = action svc first "human" "cancel" [ "evidence", "No longer needed" ]
    Assert.Equal("cancelled", field cancelled "status")
    fails "conflict" (fun () -> claim svc second "agent")
    Assert.Equal("ready", field (action svc cancelled "human" "reopen" []) "status")

[<Fact>]
let ``adopts existing custom keyword TODO without changing its completion vocabulary`` () =
    use h = new VirtualHost()
    h.Put("/work/legacy.org", "#+TODO: OPEN | FINISHED\n* OPEN Existing work\n")
    let svc = service h
    let entry = (tasks svc "ready").[0]
    Assert.StartsWith("loc:", field entry "ref")
    let configured = configure svc entry [ "acceptance", "Check the result" ]
    Assert.StartsWith("id:", field configured "ref")
    let claimed = claim svc configured "agent"
    let submitted = submit svc claimed "agent"
    action svc submitted "human" "approve" [ "evidence", "Confirmed" ] |> ignore
    Assert.Contains("* FINISHED Existing work", h.Text "/work/legacy.org")

[<Fact>]
let ``managed task transitions cannot bypass review through generic update tool`` () =
    use h = new VirtualHost()
    let svc = service h
    let entry = create svc "Review required" true

    let a =
        arg
            [ "ref", field entry "ref"
              "expected_revision", field entry "revision"
              "state", "DONE" ]

    fails "conflict" (fun () -> svc.Invoke("update_task", a))
    Assert.Single(tasks svc "ready") |> ignore

[<Fact>]
let ``stale task revisions and workspace contention do not change files`` () =
    use h = new VirtualHost()
    let svc = service h
    let entry = create svc "Concurrent" true

    let stale =
        arg
            [ "ref", field entry "ref"
              "expected_revision", field entry "revision"
              "actor", "human"
              "owner", "one" ]

    configure svc entry [ "project", "Changed" ] |> ignore
    let before = h.Text "/work/tasks.org"
    fails "conflict" (fun () -> svc.Invoke("task_update", stale))
    use lock = (h :> Runtime.IHost).AcquireLock "/work/.org-tasks.lock"
    fails "conflict" (fun () -> configure svc entry [ "owner", "two" ])
    Assert.Equal(before, h.Text "/work/tasks.org")

[<Fact>]
let ``claim respects assignment but future scheduled date is not a blocker`` () =
    use h = new VirtualHost()
    let svc = service h
    let entry = create svc "Assigned" false

    let assigned =
        configure svc entry [ "owner", "agent:one"; "scheduled", "2026-09-17" ]

    Assert.Equal("ready", field assigned "status")
    fails "conflict" (fun () -> claim svc assigned "agent:two")
    let claimed = claim svc assigned "agent:one"
    fails "conflict" (fun () -> configure svc claimed [ "acceptance", "Changed contract" ])

    let a =
        arg
            [ "ref", field claimed "ref"
              "expected_revision", field claimed "revision"
              "actor", "agent:one"
              "action", "submit"
              "claim_id", field claimed "claim_id" ]

    fails "invalid_arguments" (fun () -> svc.Invoke("task_action", a))

[<Fact>]
let ``read only service exposes task inspection and rejects workflow mutations`` () =
    use h = new VirtualHost()
    let svc = service h
    let entry = create svc "Read only" true

    let readOnly =
        WorkspaceService(h, "/work", "/work/.org-index.db", Types.defaultConfig, readOnly = true)

    Assert.Single(tasks readOnly "ready") |> ignore
    fails "read_only" (fun () -> claim readOnly entry "agent")
    Assert.Contains(OrgCli.Server.tools true, fun tool -> tool.Name = "tasks")
    Assert.DoesNotContain(OrgCli.Server.tools true, fun tool -> tool.Name = "task_action")

[<Fact>]
let ``evidence cannot inject a heading or terminate a logbook`` () =
    use h = new VirtualHost()
    let svc = service h
    let claimed = create svc "Safe evidence" false |> fun e -> claim svc e "agent"

    action
        svc
        claimed
        "agent"
        "submit"
        [ "claim_id", field claimed "claim_id"
          "evidence", "Result\n:END:\n* TODO Injected" ]
    |> ignore

    let doc = Document.parse (h.Text "/work/tasks.org")
    Assert.Single(doc.Headlines) |> ignore

[<Fact>]
let ``human CLI uses virtual actor environment and the shared workflow`` () =
    use h = new VirtualHost()
    h.Environment["ORG_ACTOR"] <- "human"

    let code, output, _ =
        run h [ "task"; "create"; "Human task"; "--acceptance"; "Verified"; "-f"; "json" ]

    Assert.Equal(0, code)
    let entry = JsonNode.Parse(output).["data"]
    let code, claimed, _ = run h [ "task"; "claim"; field entry "ref"; "-f"; "json" ]
    Assert.Equal(0, code)
    Assert.Equal("working", field (JsonNode.Parse(claimed).["data"]) "status")
    let code, board, _ = run h [ "task"; "list" ]
    Assert.Equal(0, code)
    Assert.Contains("Human task", board)
    Assert.Contains("working", board)

[<Fact>]
let ``CLI and API agenda agree on overdue scheduled work and file local done states`` () =
    use h = new VirtualHost()

    h.Put(
        "/work/tasks.org",
        "#+TODO: OPEN | FINISHED\n* OPEN Overdue\nSCHEDULED: <2026-09-15 Tue>\n* FINISHED Complete\nSCHEDULED: <2026-09-16 Wed>\n* OPEN Tomorrow\nSCHEDULED: <2026-09-17 Thu>\n"
    )

    let code, output, _ = run h [ "agenda"; "week"; "-f"; "json" ]
    Assert.Equal(0, code)

    let cli =
        (JsonNode.Parse(output)).["data"].AsArray()
        |> Seq.map (fun n -> field n "title")
        |> Set.ofSeq

    let api =
        ((service h).Invoke("agenda", JsonObject())).["items"].AsArray()
        |> Seq.map (fun n -> field n "title")
        |> Set.ofSeq

    Assert.Equal<Set<string>>(set [ "Overdue"; "Tomorrow" ], cli)
    Assert.Equal<Set<string>>(cli, api)

[<Fact>]
let ``empty Org properties do not consume the following identity or keyword`` () =
    let content =
        ":PROPERTIES:\n:EMPTY: \n:ID: root-id\n:END:\n#+EMPTY:\n#+TODO: OPEN | FINISHED\n* OPEN Task\n:PROPERTIES:\n:OWNER: \n:ID: heading-id\n:END:\n"

    let doc = Document.parse content
    Assert.Equal(Some "root-id", Types.tryGetId doc.FileProperties)
    Assert.Equal(Some "heading-id", Types.tryGetId doc.Headlines.Head.Properties)
    Assert.Equal(Some "OPEN", doc.Headlines.Head.TodoKeyword)

    match Parsers.runParser Parsers.pPropertyDrawer ":PROPERTIES:\n:EMPTY: \n:ID: kept\n:END:\n" with
    | Ok properties -> Assert.Equal(Some "kept", Types.tryGetId (Some properties))
    | Error error -> failwith error

[<Fact>]
let ``reopened dependencies prevent a running successor from submitting`` () =
    use h = new VirtualHost()
    let svc = service h
    let first = create svc "Prerequisite" false
    let second = create svc "Successor" false
    let a = editArgs svc second []
    a["depends_on"] <- JsonArray(JsonValue.Create(field first "id"))
    svc.Invoke("task_update", a) |> ignore
    let completed = claim svc first "one" |> fun e -> submit svc e "one"
    let successor = claim svc second "two"
    action svc completed "human" "reopen" [] |> ignore
    fails "conflict" (fun () -> submit svc successor "two")

[<Fact>]
let ``invalid workflow options never silently change the task contract`` () =
    use h = new VirtualHost()
    let svc = service h
    let entry = create svc "Strict contract" true
    let before = h.Text "/work/tasks.org"
    let a = editArgs svc entry []
    fails "invalid_arguments" (fun () -> svc.Invoke("task_update", a))

    let code, _, _ =
        run h [ "task"; "edit"; field entry "ref"; "--actor"; "human"; "--owenr"; "typo" ]

    Assert.Equal(1, code)
    Assert.Equal(before, h.Text "/work/tasks.org")

[<Fact>]
let ``completed contracts require reopening and malformed CLI JSON remains structured`` () =
    use h = new VirtualHost()
    let svc = service h

    let completed =
        create svc "Delivered" false
        |> fun e -> claim svc e "agent" |> fun e -> submit svc e "agent"

    fails "conflict" (fun () -> configure svc completed [ "acceptance", "New requirements" ])
    let code, output, _ = run h [ "task"; "create"; "--input"; "{"; "-f"; "json" ]
    Assert.Equal(1, code)
    Assert.Equal("invalid_arguments", field (JsonNode.Parse(output).["error"]) "code")

[<Fact>]
let ``direct requirement edits invalidate claims and submissions even with fresh revisions`` () =
    use h = new VirtualHost()
    let svc = service h
    svc.EnableWatching()
    let entry = create svc "Original requirements" true
    let claimed = claim svc entry "agent:one"

    let change (before: string) (after: string) =
        h.Put("/work/tasks.org", (h.Text "/work/tasks.org").Replace(before, after))
        svc.InvalidatePath("/work/tasks.org")

    change "Original requirements" "Changed requirements"
    let current = (tasks svc "working")[0]
    Assert.True(current["claim_stale"].GetValue<bool>())
    fails "conflict" (fun () -> submit svc claimed "agent:one")
    fails "conflict" (fun () -> action svc claimed "agent:one" "renew" [ "claim_id", field claimed "claim_id" ])

    let released =
        action svc claimed "agent:one" "release" [ "claim_id", field claimed "claim_id" ]

    let fresh = claim svc released "agent:one"
    Assert.False(fresh["claim_stale"].GetValue<bool>())
    let submitted = submit svc fresh "agent:one"
    Assert.False(submitted["submission_stale"].GetValue<bool>())
    change "Verified result" "Additional acceptance requirement"
    let pending = (tasks svc "review")[0]
    Assert.True(pending["submission_stale"].GetValue<bool>())
    fails "conflict" (fun () -> action svc submitted "human" "approve" [ "evidence", "Old evidence" ])

    let rejected =
        action svc submitted "human" "reject" [ "evidence", "Please meet the revised requirements" ]

    let fresh = claim svc rejected "agent:one"
    let submitted = submit svc fresh "agent:one"

    let approved =
        action svc submitted "human" "approve" [ "evidence", "New requirements verified" ]

    Assert.Equal("done", field approved "status")

[<Fact>]
let ``direct moves preserve claims while completion and deletion are authoritative`` () =
    use h = new VirtualHost()
    let svc = service h
    svc.EnableWatching()
    let claimed = create svc "Move me" true |> fun task -> claim svc task "agent:one"
    let content = (h.Text "/work/tasks.org")
    h.Put("/work/moved.org", content.Replace("* TODO Move me", "** TODO Move me"))
    h.Put("/work/tasks.org", "* TODO Unrelated new task\n")
    svc.InvalidateAll()
    let moved = (tasks svc "working")[0]
    Assert.Equal("moved.org", field moved "file")
    Assert.False(moved["claim_stale"].GetValue<bool>())
    let submitted = submit svc claimed "agent:one"
    h.Put("/work/moved.org", (h.Text "/work/moved.org").Replace("** TODO Move me", "** DONE Move me"))
    svc.InvalidatePath("/work/moved.org")
    Assert.Single(tasks svc "done") |> ignore
    fails "conflict" (fun () -> action svc submitted "human" "approve" [ "evidence", "Already completed in editor" ])
    h.Put("/work/moved.org", "")
    svc.InvalidatePath("/work/moved.org")
    Assert.Empty(tasks svc "done")
    fails "not_found" (fun () -> fetch svc submitted)


[<Fact>]
let ``manually reopening cancellation respects the TODO keyword without property cleanup`` () =
    use h = new VirtualHost()
    let svc = service h
    let task = create svc "Cancelled then reconsidered" true
    let cancelled = action svc task "human" "cancel" [ "evidence", "Postponed" ]
    h.Put("/work/tasks.org", (h.Text "/work/tasks.org").Replace("* CANCELLED ", "* TODO "))
    let ready = (tasks svc "ready")[0]
    Assert.Equal(field cancelled "id", field ready "id")
    let claimed = claim svc ready "worker"
    Assert.Equal("working", field claimed "status")
    Assert.DoesNotContain(":TASK_PHASE: CANCELLED", h.Text "/work/tasks.org")
    Assert.Equal("review", field (submit svc claimed "worker") "status")

[<Fact>]
let ``stale workflow recovery clears metadata with evidence and preserves editor state`` () =
    use h = new VirtualHost()
    let svc = service h
    let claimed = create svc "Original" true |> fun t -> claim svc t "worker"
    fails "conflict" (fun () -> action svc claimed "human" "reopen" [ "evidence", "Cannot steal valid work" ])
    h.Put("/work/tasks.org", (h.Text "/work/tasks.org").Replace("Original", "Revised"))
    fails "invalid_arguments" (fun () -> action svc claimed "human" "reopen" [])

    let recovered =
        action svc claimed "human" "reopen" [ "evidence", "Requirements changed in editor" ]

    Assert.Equal("ready", field recovered "status")
    Assert.DoesNotContain(":TASK_CLAIM_ID:", h.Text "/work/tasks.org")
    let submitted = claim svc recovered "worker" |> fun c -> submit svc c "worker"
    h.Put("/work/tasks.org", (h.Text "/work/tasks.org").Replace("* TODO Revised", "* WAITING Revised"))

    let recovered =
        action svc submitted "human" "reopen" [ "evidence", "Awaiting new requirements" ]

    Assert.Equal("WAITING", field recovered "state")
    Assert.Equal("blocked", field recovered "status")
    Assert.DoesNotContain(":TASK_PHASE:", h.Text "/work/tasks.org")
    Assert.DoesNotContain(":TASK_SUBMISSION_CONTRACT:", h.Text "/work/tasks.org")
    Assert.Contains("Requirements changed in editor", h.Text "/work/tasks.org")

[<Fact>]
let ``task cards expose planning and source context without changing Org files`` () =
    use h = new VirtualHost()

    let content =
        "* Project\n** Earlier sibling\n*** Old child\n** TODO [#A] Plan launch :release:team:\nSCHEDULED: <2026-09-17 Thu 09:30 +1w> DEADLINE: <2026-09-20 Sun>\n:PROPERTIES:\n:TASK_OWNER: daniel\n:END:\n** TODO Undated\n"

    h.Put("/work/project.org", content)
    let svc = service h
    let entries = tasks svc "all"
    let entry = entries |> Seq.find (fun row -> field row "title" = "Plan launch")
    Assert.Equal("project.org", field entry "file")
    Assert.Equal("daniel", field entry "owner")
    Assert.Equal("A", field entry "priority")
    Assert.Equal("Project", (entry["outline"][0]).GetValue<string>())
    Assert.Single(entry["outline"].AsArray()) |> ignore
    Assert.Equal("release", (entry["tags"][0]).GetValue<string>())
    Assert.Equal("2026-09-17", field (entry["scheduled"]) "date")
    Assert.Equal("09:30", field (entry["scheduled"]) "time")
    Assert.Equal("+1w", field (entry["scheduled"]) "repeater")
    Assert.Equal("2026-09-20", field (entry["deadline"]) "date")
    Assert.Null(entry["deadline"]["time"])
    let undated = entries |> Seq.find (fun row -> field row "title" = "Undated")
    Assert.Null(undated["scheduled"])
    Assert.Null(undated["deadline"])
    Assert.Equal(content, h.Text "/work/project.org")

[<Fact>]
let ``cancellation without a reason revokes claims and preserves history`` () =
    use h = new VirtualHost()
    let svc = service h
    let task = create svc "No longer needed" true
    let claimed = claim svc task "worker"
    let cancelled = action svc claimed "human" "cancel" []
    Assert.Equal("cancelled", field cancelled "status")
    Assert.Equal("", field cancelled "claimed_by")
    Assert.DoesNotContain(":TASK_CLAIM_ID:", h.Text "/work/tasks.org")
    Assert.Contains("Task cancel by human", h.Text "/work/tasks.org")
    Assert.Contains("Task claim by worker", h.Text "/work/tasks.org")

[<Fact>]
let ``planning dates do not block and exact file states distinguish KILL from DONE`` () =
    use h = new VirtualHost()

    h.Put(
        "/work/custom.org",
        "#+TODO: WAIT TODO PROG | DONE KILL\n* TODO Future\nSCHEDULED: <2030-01-01 Tue> DEADLINE: <2030-02-01 Fri>\n* WAIT Waiting\n* KILL Abandoned\n"
    )

    let svc = service h
    let rows = tasks svc "all"
    let future = rows |> Seq.find (fun row -> field row "title" = "Future")
    Assert.Equal("ready", field future "status")
    Assert.Empty(future["blockers"].AsArray())
    let waiting = rows |> Seq.find (fun row -> field row "title" = "Waiting")
    Assert.Equal("blocked", field waiting "status")
    let killed = rows |> Seq.find (fun row -> field row "title" = "Abandoned")
    Assert.Equal("KILL", field killed "state")
    Assert.Equal("cancelled", field killed "status")
    let claimed = claim svc future "worker"
    Assert.Equal("PROG", field claimed "state")
    let cancelled = action svc claimed "worker" "cancel" []
    Assert.Equal("KILL", field cancelled "state")
    Assert.Contains("* KILL Future", h.Text "/work/custom.org")

[<Fact>]
let ``creation targets relevant file and parent while fallback uses inbox`` () =
    use h = new VirtualHost()
    h.Put("/work/project.org", "#+TODO: TODO PROG | DONE KILL\n* Project\nBody\n")
    let svc = service h
    let metadata = svc.Invoke("workspace", JsonObject())
    let file = metadata["files"][0]

    let created =
        svc.Invoke(
            "task_create",
            arg
                [ "request_id", Guid.NewGuid().ToString()
                  "actor", "human"
                  "title", "Child"
                  "file", "project.org"
                  "parent", field (file["parents"][0]) "ref"
                  "destination_revision", field file "revision" ]
        )

    Assert.Equal("project.org", field created "file")
    Assert.Contains("** TODO Child", h.Text "/work/project.org")

    let fallback =
        svc.Invoke("task_create", arg [ "request_id", Guid.NewGuid().ToString(); "actor", "human"; "title", "Inbox" ])

    Assert.Equal("inbox.org", field fallback "file")
    Assert.False(h.Files.ContainsKey "/work/tasks.org")

    fails "invalid_arguments" (fun () ->
        svc.Invoke(
            "task_create",
            arg
                [ "request_id", Guid.NewGuid().ToString()
                  "actor", "human"
                  "title", "Escape"
                  "file", "../escape.org" ]
        ))

[<Fact>]
let ``fresh workspace uses recommended states without altering existing workflow`` () =
    use h = new VirtualHost()

    let entry =
        (service h)
            .Invoke("task_create", arg [ "request_id", Guid.NewGuid().ToString(); "actor", "human"; "title", "First" ])

    Assert.Equal("TODO", field entry "state")

    Assert.Equal<string list>(
        [ "WAIT"; "TODO"; "PROG"; "DONE"; "KILL" ],
        entry["states"].AsArray()
        |> Seq.map (fun v -> v.GetValue<string>())
        |> Seq.toList
    )

[<Fact>]
let ``plain field editing preserves children and history and supports date times`` () =
    use h = new VirtualHost()

    h.Put(
        "/work/project.org",
        "* TODO Parent :old:\n:LOGBOOK:\n- Existing history\n:END:\nOriginal body\n** TODO Child\nChild body\n"
    )

    let svc = service h
    let entry = tasks svc "all" |> Seq.find (fun row -> field row "title" = "Parent")

    let updated =
        configure
            svc
            entry
            [ "title", "Renamed"
              "text", "New body"
              "tags", "one two"
              "priority", "A"
              "scheduled", "2030-01-01T09:30" ]

    Assert.Equal("Renamed", field updated "title")
    Assert.Equal("New body", field updated "description")
    Assert.Equal("09:30", field (updated["scheduled"]) "time")
    Assert.Contains("** TODO Child\nChild body", h.Text "/work/project.org")
    Assert.Contains("Existing history", h.Text "/work/project.org")
    Assert.DoesNotContain("Original body", h.Text "/work/project.org")

[<Fact>]
let ``moving is atomic revision checked compatible with workflow and undoable`` () =
    use h = new VirtualHost()
    let source = "#+TODO: TODO PROG | DONE KILL\n* TODO Move me\n** TODO Child\n"
    let target = "#+TODO: TODO PROG | DONE KILL\n* Destination\n"
    h.Put("/work/source.org", source)
    h.Put("/work/destination.org", target)
    let svc = service h
    let entry = tasks svc "all" |> Seq.find (fun row -> field row "title" = "Move me")
    let meta = svc.Invoke("workspace", JsonObject())

    let dest =
        meta["files"].AsArray()
        |> Seq.find (fun f -> field f "file" = "destination.org")

    let args =
        editArgs
            svc
            entry
            [ "file", "destination.org"
              "parent", field (dest["parents"][0]) "ref"
              "destination_revision", field dest "revision" ]

    let moved = svc.Invoke("task_move", args)
    Assert.Equal("destination.org", field moved "file")
    Assert.Contains("** TODO Move me", h.Text "/work/destination.org")
    Assert.Contains("*** TODO Child", h.Text "/work/destination.org")
    Assert.DoesNotContain("Move me", h.Text "/work/source.org")

    svc.Invoke("task_undo", arg [ "actor", "human"; "token", field (moved["undo"]) "token" ])
    |> ignore

    Assert.Equal(source, h.Text "/work/source.org")
    Assert.Equal(target, h.Text "/work/destination.org")

[<Fact>]
let ``undo cannot overwrite later external edits or another actors work`` () =
    use h = new VirtualHost()
    let svc = service h
    let entry = create svc "Keep edits" false
    let cancelled = action svc entry "human" "cancel" []
    let undo = arg [ "token", field (cancelled["undo"]) "token"; "actor", "other" ]
    fails "conflict" (fun () -> svc.Invoke("task_undo", undo))
    undo["actor"] <- JsonValue.Create "human"
    h.Put("/work/tasks.org", h.Text "/work/tasks.org" + "\nExternal note\n")
    fails "conflict" (fun () -> svc.Invoke("task_undo", undo))
    Assert.Contains("External note", h.Text "/work/tasks.org")

[<Fact>]
let ``column ordering is persistent outside notes and protected from stale overwrites`` () =
    use h = new VirtualHost()
    h.Environment["XDG_CONFIG_HOME"] <- "/home/test/settings"
    let svc = service h
    let initial = svc.Invoke("workspace", JsonObject())
    let args = arg [ "expected_revision", field initial "revision" ]
    args["settings"] <- JsonNode.Parse("{\"column_order\":[\"WAIT\",\"TODO\",\"PROG\",\"DONE\",\"KILL\"]}")
    let result = svc.Invoke("board_settings", args)
    Assert.StartsWith("/home/test/settings/org-cli/workspaces/", field result "path")
    Assert.Equal("WAIT", (result["settings"].["column_order"].[0]).GetValue<string>())
    let restored = (service h).Invoke("workspace", JsonObject())
    Assert.Equal(result["settings"].ToJsonString(), restored["settings"].ToJsonString())
    fails "conflict" (fun () -> svc.Invoke("board_settings", args))

    Assert.False(
        h.Files.Keys
        |> Seq.exists (fun p -> p.StartsWith("/work/") && p.EndsWith(".json"))
    )

[<Fact>]
let ``state changes respect file workflows review claims and cancellation dependencies`` () =
    use h = new VirtualHost()
    h.Put("/work/states.org", "#+TODO: WAIT TODO PROG | DONE KILL\n* TODO Plain\n")
    let svc = service h
    let plain = tasks svc "all" |> Seq.exactlyOne
    fails "invalid_arguments" (fun () -> action svc plain "human" "state" [ "state", "DOING" ])
    let doneTask = action svc plain "human" "state" [ "state", "DONE" ]
    Assert.Equal("DONE", field doneTask "state")
    let reopened = action svc doneTask "human" "state" [ "state", "TODO" ]
    let configured = configure svc reopened [ "acceptance", "Review required" ]
    fails "conflict" (fun () -> action svc configured "human" "state" [ "state", "DONE" ])
    let claimed = claim svc configured "worker"
    fails "conflict" (fun () -> action svc claimed "other" "state" [ "state", "WAIT" ])
    let killed = action svc claimed "worker" "state" [ "state", "KILL" ]
    let dependent = create svc "Depends on killed work" false
    let args = editArgs svc dependent []
    args["depends_on"] <- JsonArray(JsonValue.Create(field killed "id"))
    let blocked = svc.Invoke("task_update", args)
    Assert.Equal("blocked", field blocked "status")

[<Fact>]
let ``moves reject stale destinations changed state meanings and descendants`` () =
    use h = new VirtualHost()
    let source = "#+TODO: TODO PROG | DONE KILL\n* TODO Parent\n** TODO Child\n"
    h.Put("/work/source.org", source)
    h.Put("/work/target.org", "#+TODO: PROG | TODO DONE\n")
    let svc = service h
    let parent = tasks svc "all" |> Seq.find (fun row -> field row "title" = "Parent")

    let args =
        editArgs
            svc
            parent
            [ "file", "target.org"
              "destination_revision", OrgCli.Index.Application.revision (h.Text "/work/target.org") ]

    fails "conflict" (fun () -> svc.Invoke("task_move", args))
    h.Put("/work/target.org", "#+TODO: TODO PROG | DONE KILL\n")
    fails "conflict" (fun () -> svc.Invoke("task_move", args))
    let child = tasks svc "all" |> Seq.find (fun row -> field row "title" = "Child")

    let intoChild =
        editArgs
            svc
            parent
            [ "file", "source.org"
              "parent", field child "ref"
              "destination_revision", OrgCli.Index.Application.revision source ]

    fails "invalid_arguments" (fun () -> svc.Invoke("task_move", intoChild))
    Assert.Equal(source, h.Text "/work/source.org")

[<Fact>]
let ``read only workspaces cannot reorder undo or move`` () =
    use h = new VirtualHost()

    let readOnly =
        WorkspaceService(h, "/work", "/work/.org-index.db", Types.defaultConfig, readOnly = true)

    for operation in [ "board_settings"; "task_undo"; "task_move" ] do
        fails "read_only" (fun () -> readOnly.Invoke(operation, JsonObject()))

[<Fact>]
let ``recurrence cancellation keeps dates and terminal state while completion advances a range`` () =
    for terminal in [ "DONE KILL"; "DONE" ] do
        use h = new VirtualHost()

        h.Put(
            "/work/repeat.org",
            "#+TODO: TODO | "
            + terminal
            + "\n* TODO Repeat\nSCHEDULED: <2030-01-01 Tue +1w>--<2030-01-02 Wed>\n"
        )

        let svc = service h
        let entry = tasks svc "all" |> Seq.head
        let cancelled = action svc entry "human" "cancel" []
        Assert.Equal("cancelled", field cancelled "status")
        Assert.Contains("2030-01-01", h.Text "/work/repeat.org")
        Assert.DoesNotContain("LAST_REPEAT", h.Text "/work/repeat.org")

    use h = new VirtualHost()

    h.Put(
        "/work/repeat.org",
        "#+TODO: TODO | DONE KILL\n* TODO Repeat\nSCHEDULED: <2030-01-01 Tue +1w>--<2030-01-02 Wed>\n"
    )

    let svc = service h
    let entry = tasks svc "all" |> Seq.head
    let completed = action svc entry "human" "state" [ "state", "DONE" ]
    Assert.Equal("TODO", field completed "state")
    Assert.Equal("2030-01-08", field completed["scheduled"] "date")
    Assert.Equal("2030-01-09", field completed["scheduled"].["end"] "date")
    let killed = action svc completed "human" "state" [ "state", "KILL" ]
    Assert.Equal("KILL", field killed "state")
    Assert.Equal("2030-01-08", field killed["scheduled"] "date")

[<Fact>]
let ``calendar entries include active appointments but exclude examples history and inactive timestamps`` () =
    use h = new VirtualHost()

    h.Put(
        "/work/events.org",
        "* Meeting <2030-01-01 Tue 09:00-10:00>\n<2030-01-02 Wed>--<2030-01-03 Thu>\n[2030-01-04 Fri]\n:LOGBOOK:\n<2030-01-05 Sat>\n:END:\n#+BEGIN_SRC org\n<2030-01-06 Sun>\n#+END_SRC\n** TODO Child\nSCHEDULED: <2030-01-07 Mon>\n<2030-01-08 Tue>\n"
    )

    let svc = service h
    Assert.Single(tasks svc "all") |> ignore
    let args = arg [ "status", "all" ]
    args["include_events"] <- JsonValue.Create true
    let rows = (svc.Invoke("tasks", args)).["tasks"].AsArray()
    Assert.Equal(2, rows.Count)
    let meeting = rows |> Seq.find (fun row -> not (row["is_task"].GetValue<bool>()))
    Assert.Equal(2, meeting["appointments"].AsArray().Count)
    Assert.Equal("10:00", field meeting["appointments"].[0].["end"] "time")
    let child = rows |> Seq.find (fun row -> row["is_task"].GetValue<bool>())
    Assert.Single(child["appointments"].AsArray()) |> ignore

[<Fact>]
let ``task rows distinguish local and inherited tags and expose body search`` () =
    use h = new VirtualHost()

    h.Put(
        "/work/tags.org",
        "#+FILETAGS: :office:\n* Parent :team:\n** TODO Child :local:\nNeedle only in description\n"
    )

    let svc = service h
    let entry = tasks svc "all" |> Seq.head
    Assert.Equal("local", entry["tags"].[0].GetValue<string>())
    Assert.Equal(2, entry["inherited_tags"].AsArray().Count)
    Assert.Contains("Needle only", field entry "search_text")
    let updated = svc.Invoke("task_update", editArgs svc entry [ "tags", "new" ])
    Assert.Equal(2, updated["inherited_tags"].AsArray().Count)
    Assert.Contains("** TODO Child :new:", h.Text "/work/tags.org")
    Assert.DoesNotContain(":new:office:", h.Text "/work/tags.org")

[<Fact>]
let ``full planning timestamp editor handles repeat warning delay and range without losing endpoints`` () =
    use h = new VirtualHost()
    h.Put("/work/edit.org", "* TODO Edit\n")
    let svc = service h
    let entry = tasks svc "all" |> Seq.head

    let updated =
        svc.Invoke(
            "task_update",
            editArgs
                svc
                entry
                [ "scheduled", "<2030-01-01 09:00 +1w --2d>--<2030-01-02 10:00>"
                  "deadline", "<2030-01-03 17:00 -1d>" ]
        )

    Assert.Equal("--2d", field updated["scheduled"] "delay")
    Assert.Equal("+1w", field updated["scheduled"] "repeater")
    Assert.Equal("2030-01-02", field updated["scheduled"].["end"] "date")

    let cleared =
        svc.Invoke("task_update", editArgs svc updated [ "scheduled", "<2030-02-01 11:00>" ])

    Assert.Null(cleared["scheduled"].["repeater"])
    Assert.Null(cleared["scheduled"].["end"])

    for invalid in [ "<2030-01-01>junk"; "<2030-01-03>--<2030-01-01>"; "<2030-01-01 +0w>" ] do
        fails "invalid_arguments" (fun () -> svc.Invoke("task_update", editArgs svc cleared [ "scheduled", invalid ]))

[<Fact>]
let ``inherited tag selection and exclusion never remove explicit local tags`` () =
    let doc =
        Document.parse "#+FILETAGS: :office:\n* Parent :team:secret:\n** TODO Child :secret:\n"

    let child = doc.Headlines |> List.last

    let config =
        { Types.defaultConfig with
            InheritTags = Some [ "team" ]
            TagsExcludeFromInheritance = [ "secret" ] }

    Assert.Equal<string list>([ "team"; "secret" ], Headlines.computeInheritedTags config doc child)

    Assert.Equal<string list>(
        [ "secret" ],
        Headlines.computeInheritedTags { config with TagInheritance = false } doc child
    )
