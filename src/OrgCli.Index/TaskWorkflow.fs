module OrgCli.Index.TaskWorkflow

open System
open System.IO
open System.Text
open System.Text.Json.Nodes
open OrgCli.Org

exception TaskError of string * int * string

let private fail code status message =
    raise (TaskError(code, status, message))

let private text (s: string) : JsonNode = JsonValue.Create s
let private num (n: int) : JsonNode = JsonValue.Create n
let private flag (b: bool) : JsonNode = JsonValue.Create b

let private obj (fields: (string * JsonNode) list) : JsonNode =
    let node = JsonObject()

    for key, value in fields do
        node[key] <- value

    node

let private arr nodes : JsonNode = JsonArray(Seq.toArray nodes)

let private hash s =
    Runtime.hash (Encoding.UTF8.GetBytes(s: string))

let private prop key (h: Headline) =
    Types.tryGetProperty key h.Properties |> Option.defaultValue ""

let private id (h: Headline) =
    Types.tryGetId h.Properties |> Option.defaultValue ""

let private now () = (Runtime.now ()).ToUniversalTime()

let private single name (s: string) =
    if s.Length > 2048 || s |> Seq.exists Char.IsControl then
        fail "invalid_arguments" 400 (name + " must be one line, at most 2048 characters")

    s

let private optional (args: JsonObject) (key: string) fallback =
    match args[key] with
    | null when not (args.ContainsKey key) -> fallback
    | :? JsonValue as v ->
        let mutable s = ""

        if v.TryGetValue<string>(&s) then
            s
        else
            fail "invalid_arguments" 400 (key + " must be a string")
    | _ -> fail "invalid_arguments" 400 (key + " must be a string")

let private required args key =
    let value = optional args key ""

    if String.IsNullOrWhiteSpace value then
        fail "invalid_arguments" 400 (key + " is required")

    value

let private boolArg (args: JsonObject) key fallback =
    if not (args.ContainsKey key) then
        fallback
    else
        let mutable value = false

        match args[key] with
        | :? JsonValue as v when v.TryGetValue<bool>(&value) -> value
        | _ -> fail "invalid_arguments" 400 (key + " must be a boolean")

let private intArg (args: JsonObject) key fallback minimum maximum =
    if not (args.ContainsKey key) then
        fallback
    else
        let mutable value = 0

        match args[key] with
        | :? JsonValue as v when v.TryGetValue<int>(&value) && value >= minimum && value <= maximum -> value
        | _ -> fail "invalid_arguments" 400 (sprintf "%s must be between %d and %d" key minimum maximum)

let private uuid args key =
    match Guid.TryParse(required args key) with
    | true, value when value <> Guid.Empty -> value.ToString("D")
    | _ -> fail "invalid_arguments" 400 (key + " must be a nonzero UUID")

let private dependencyId (value: string) =
    let value =
        if value.StartsWith("id:", StringComparison.Ordinal) then
            value.Substring 3
        else
            value

    if String.IsNullOrWhiteSpace value || value |> Seq.exists Char.IsWhiteSpace then
        fail "invalid_arguments" 400 "Dependencies must be standard Org IDs"

    single "dependency" value

let private dependencies h =
    (prop "TASK_DEPENDS" h).Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
    |> Array.toList

let private dependsArg (args: JsonObject) =
    match args["depends_on"] with
    | :? JsonArray as values when values.Count <= 100 ->
        values
        |> Seq.map (fun v ->
            match v with
            | :? JsonValue as j ->
                let mutable s = ""

                if j.TryGetValue<string>(&s) then
                    dependencyId s
                else
                    fail "invalid_arguments" 400 "depends_on must contain ID strings"
            | _ -> fail "invalid_arguments" 400 "depends_on must contain ID strings")
        |> Seq.distinct
        |> Seq.toList
    | _ -> fail "invalid_arguments" 400 "depends_on must be an array of at most 100 IDs"

let private expiry h =
    match
        DateTime.TryParse(
            prop "TASK_CLAIM_UNTIL" h,
            Globalization.CultureInfo.InvariantCulture,
            Globalization.DateTimeStyles.RoundtripKind
        )
    with
    | true, value -> Some(value.ToUniversalTime())
    | _ -> None

let private active h =
    expiry h |> Option.exists (fun until -> until > now ())

let private reviewRequired h =
    prop "TASK_REVIEW_REQUIRED" h <> "false"

/// Transport-free workflow. Org text is authoritative; the caller supplies the
/// same scoped filesystem, cached documents and checked writes used by the CLI.
type Context =
    { Root: string
      Config: OrgConfig
      Documents: unit -> (string * OrgDocument) list
      Resolve: string -> string * int64 * string * OrgDocument
      Reference: string -> OrgDocument -> int64 -> string
      Save: string -> string -> string -> string option
      Reconcile: unit -> unit }

type private Entry =
    { File: string
      Doc: OrgDocument
      Heading: Headline
      Config: OrgConfig }

let private entries (ctx: Context) docs =
    docs
    |> List.collect (fun (file, doc) ->
        let cfg = FileConfig.mergeFileConfig ctx.Config doc.Keywords

        doc.Headlines
        |> List.filter (fun h -> h.TodoKeyword.IsSome)
        |> List.map (fun h ->
            { File = file
              Doc = doc
              Heading = h
              Config = cfg }))

// Fingerprints cover the task's own text, not its location or coordination history.
// Editors remain free to change files; stale work is detected without rewriting them.
let private contractText (content: string) e =
    let start = int e.Heading.Position

    let finish =
        e.Doc.Headlines
        |> List.tryFind (fun h -> h.Position > e.Heading.Position)
        |> Option.map (fun h -> int h.Position)
        |> Option.defaultValue content.Length

    let section = content.Substring(start, finish - start).Replace("\r\n", "\n")

    let withoutHistory =
        System.Text.RegularExpressions.Regex.Replace(
            section,
            @"(?im)^[ \t]*:LOGBOOK:[ \t]*\n.*?^[ \t]*:END:[ \t]*(?:\n|$)",
            "",
            System.Text.RegularExpressions.RegexOptions.Singleline
        )

    let stable =
        withoutHistory.Split('\n')
        |> Array.filter (fun line ->
            not (
                System.Text.RegularExpressions.Regex.IsMatch(
                    line,
                    @"(?i)^[ \t]*:(?:ID|TASK_MANAGED|TASK_CREATE_HASH|TASK_CLAIM_[A-Z_]+|TASK_SUBMISSION_CONTRACT|TASK_PHASE|TASK_SUBMITTED_BY|TASK_REVIEWED_BY):"
                )
            ))
        |> Array.map (fun line -> line.TrimEnd())
        |> String.concat "\n"
    // Refiling under a different parent changes stars, not requirements.
    System.Text.RegularExpressions.Regex.Replace(stable.Trim(), @"^\*+ ", "* ")
    |> hash

let private contract e =
    contractText (Runtime.readText e.File) e

let private stale property e =
    let saved = prop property e.Heading
    saved = "" || saved <> contract e

let private claimStale e =
    prop "TASK_CLAIM_ID" e.Heading <> "" && stale "TASK_CLAIM_CONTRACT" e

let private submissionStale e =
    prop "TASK_PHASE" e.Heading = "REVIEW" && stale "TASK_SUBMISSION_CONTRACT" e

let private isCancelled e =
    prop "TASK_PHASE" e.Heading = "CANCELLED"
    || List.contains (e.Heading.TodoKeyword |> Option.defaultValue "") [ "CANCELLED"; "CANCELED" ]

let private isDone e =
    Agenda.isDoneState e.Config e.Heading.TodoKeyword && not (isCancelled e)

let private graph all =
    all
    |> List.filter (fun e -> id e.Heading <> "")
    |> List.groupBy (fun e -> id e.Heading)
    |> Map.ofList

let private reaches (graph: Map<string, Entry list>) target start =
    let seen = Collections.Generic.HashSet<string>()
    let pending = Collections.Generic.Stack<string>()
    pending.Push start
    let mutable found = false

    while pending.Count > 0 && not found do
        let current = pending.Pop()

        if current = target then
            found <- true
        elif seen.Add current then
            match graph.TryFind current with
            | Some [ entry ] ->
                for dependency in dependencies entry.Heading do
                    pending.Push dependency
            | _ -> ()

    found

let private blockers graph e =
    [ if id e.Heading <> "" then
          match Map.tryFind (id e.Heading) graph with
          | Some(_ :: _ :: _) -> yield "Duplicate task ID"
          | _ -> ()
      for dependency in dependencies e.Heading do
          match Map.tryFind dependency graph with
          | None -> yield "Missing dependency: " + dependency
          | Some [ target ] when reaches graph (id e.Heading) dependency -> yield "Dependency cycle: " + dependency
          | Some [ target ] when not (isDone target) -> yield "Unfinished dependency: " + dependency
          | Some [ _ ] -> ()
          | _ -> yield "Ambiguous dependency: " + dependency
      if
          List.contains (e.Heading.TodoKeyword |> Option.defaultValue "") [ "WAITING"; "HOLD"; "SOMEDAY"; "PROJECT" ]
      then
          yield "Task state is not actionable"
      match e.Heading.Planning |> Option.bind (fun p -> p.Scheduled) with
      | Some scheduled when scheduled.Date.Date > (Runtime.today ()) -> yield "Scheduled for a future date"
      | _ -> ()
      if prop "TASK_CLAIM_ID" e.Heading <> "" && expiry e.Heading = None then
          yield "Invalid claim expiry" ]

let private status graph e =
    if isCancelled e then "cancelled"
    elif isDone e then "done"
    elif prop "TASK_PHASE" e.Heading = "REVIEW" then "review"
    elif active e.Heading then "working"
    elif not (List.isEmpty (blockers graph e)) then "blocked"
    else "ready"

let private row ctx graph e =
    let h = e.Heading

    obj
        [ "ref", text (ctx.Reference e.File e.Doc h.Position)
          "id", (if id h = "" then null else text (id h))
          "title", text h.Title
          "file", text (Path.GetRelativePath(ctx.Root, e.File))
          "revision", text (hash (Runtime.readText e.File))
          "state", text (h.TodoKeyword |> Option.defaultValue "")
          "status", text (status graph e)
          "project", text (prop "TASK_PROJECT" h)
          "owner", text (prop "TASK_OWNER" h)
          "acceptance", text (prop "TASK_ACCEPTANCE" h)
          "priority",
          text (
              h.Priority
              |> Option.map (fun (Priority c) -> string c)
              |> Option.defaultValue ""
          )
          "depends_on", dependencies h |> Seq.map text |> arr
          "blockers", blockers graph e |> Seq.map text |> arr
          "review_required", flag (reviewRequired h)
          "claimed_by", text (if active h then prop "TASK_CLAIM_OWNER" h else "")
          "claim_until", text (prop "TASK_CLAIM_UNTIL" h)
          "claim_stale", flag (claimStale e)
          "submission_stale", flag (submissionStale e)
          "claim_expired", flag (prop "TASK_CLAIM_ID" h <> "" && not (active h))
          "submitted_by", text (prop "TASK_SUBMITTED_BY" h) ]

let allowed operation =
    match operation with
    | "tasks" -> [ "status"; "project"; "owner"; "limit"; "offset" ]
    | "task_create" ->
        [ "request_id"
          "title"
          "text"
          "actor"
          "acceptance"
          "project"
          "owner"
          "depends_on"
          "review_required"
          "priority"
          "scheduled"
          "deadline" ]
    | "task_update" ->
        [ "ref"
          "expected_revision"
          "actor"
          "acceptance"
          "project"
          "owner"
          "depends_on"
          "review_required"
          "priority"
          "scheduled"
          "deadline" ]
    | "task_action" ->
        [ "ref"
          "expected_revision"
          "actor"
          "action"
          "claim_id"
          "lease_minutes"
          "evidence" ]
    | _ -> []

let isMutation operation =
    List.contains operation [ "task_create"; "task_update"; "task_action" ]

let private requireRevision args content =
    if required args "expected_revision" <> hash content then
        fail "conflict" 409 "File changed; fetch the task and retry with its current revision"

let private applyFields graph taskId cfg (args: JsonObject) content pos =
    let mutable result = content

    for key, property in
        [ "acceptance", "TASK_ACCEPTANCE"
          "project", "TASK_PROJECT"
          "owner", "TASK_OWNER" ] do
        if args.ContainsKey key then
            result <- Mutations.setProperty result pos property (optional args key "" |> single key)

    if args.ContainsKey "review_required" then
        result <-
            Mutations.setProperty
                result
                pos
                "TASK_REVIEW_REQUIRED"
                (if boolArg args "review_required" true then
                     "true"
                 else
                     "false")

    if args.ContainsKey "depends_on" then
        let deps = dependsArg args

        for dependency in deps do
            if dependency = taskId || reaches graph taskId dependency then
                fail "invalid_arguments" 400 "Dependencies would introduce a cycle"

            match Map.tryFind dependency graph with
            | Some [ _ ] -> ()
            | Some _ -> fail "ambiguous" 409 ("Duplicate dependency ID: " + dependency)
            | None -> fail "not_found" 404 ("Dependency task not found: " + dependency)

        result <- Mutations.setProperty result pos "TASK_DEPENDS" (String.concat " " deps)

    if args.ContainsKey "priority" then
        let value = optional args "priority" ""

        if value <> "" && (value.Length <> 1 || value[0] < 'A' || value[0] > 'Z') then
            fail "invalid_arguments" 400 "priority must be A-Z or empty"

        result <- Mutations.setPriority result pos (if value = "" then None else Some value[0])

    for key, setter in [ "scheduled", Mutations.setScheduled; "deadline", Mutations.setDeadline ] do
        if args.ContainsKey key then
            let value = optional args key ""

            if value <> "" then
                match
                    DateTime.TryParseExact(
                        value,
                        "yyyy-MM-dd",
                        Globalization.CultureInfo.InvariantCulture,
                        Globalization.DateTimeStyles.None
                    )
                with
                | true, _ -> ()
                | _ -> fail "invalid_arguments" 400 (key + " must be yyyy-MM-dd or empty")

            result <- setter cfg result pos (if value = "" then None else Some(Utils.parseDate value)) (Runtime.now ())

    result

let private clearClaim content pos =
    [ "TASK_CLAIM_ID"
      "TASK_CLAIM_OWNER"
      "TASK_CLAIM_UNTIL"
      "TASK_CLAIM_MINUTES"
      "TASK_CLAIM_CONTRACT" ]
    |> List.fold (fun text key -> Mutations.removeProperty text pos key) content

let private note actor action (evidence: string) content pos =
    // Prefix each line so evidence cannot terminate the drawer or create headlines.
    let safe = evidence.Replace("\r", "").Split('\n') |> String.concat "\n  "

    Mutations.addNote
        content
        pos
        (sprintf "Task %s by %s%s" action actor (if safe = "" then "" else ": " + safe))
        (Runtime.now ())

let private state (cfg: OrgConfig) doneState content pos =
    let choices =
        if doneState then
            cfg.TodoKeywords.DoneStates
        else
            cfg.TodoKeywords.ActiveStates

    match
        choices
        |> List.tryFind (fun s -> not (List.contains s.Keyword [ "CANCELLED"; "CANCELED" ]))
    with
    | Some choice -> Mutations.setTodoState cfg content pos (Some choice.Keyword) (Runtime.now ())
    | None -> fail "invalid_arguments" 400 "The file needs active and completed TODO keywords"

let invoke (ctx: Context) operation (args: JsonObject) =
    for pair in args do
        if not (List.contains pair.Key (allowed operation)) then
            fail "invalid_arguments" 400 ("Unknown argument: " + pair.Key)
    // Cross-process task operations serialize graph decisions before checked file commits.
    use workspaceLock =
        if isMutation operation then
            try
                (Runtime.host ()).AcquireLock(Path.Combine(ctx.Root, ".org-tasks.lock"))
            with :? IOException ->
                fail "conflict" 409 "Another task operation is running; retry"
        else
            { new IDisposable with
                member _.Dispose() = () }

    if isMutation operation then
        ctx.Reconcile()

    let docs = ctx.Documents()
    let all = entries ctx docs
    let graph = graph all

    let finish file pos before after =
        Document.ensureEditable after
        let warning = ctx.Save file before after
        let doc = Document.parseWithConfig ctx.Config after
        let heading = doc.Headlines |> List.find (fun h -> h.Position = pos)

        let entry =
            { File = file
              Doc = doc
              Heading = heading
              Config = FileConfig.mergeFileConfig ctx.Config doc.Keywords }

        let graph =
            if id heading = "" then
                graph
            else
                graph.Add(id heading, [ entry ])

        let result = row ctx graph entry
        warning |> Option.iter (fun w -> result["warning"] <- text w)
        result

    match operation with
    | "tasks" ->
        let selectedStatus = optional args "status" "open"

        if
            not (
                List.contains
                    selectedStatus
                    [ "open"; "all"; "ready"; "blocked"; "working"; "review"; "done"; "cancelled" ]
            )
        then
            fail "invalid_arguments" 400 "Unknown task status"

        let project = optional args "project" ""
        let owner = optional args "owner" ""
        let limit = intArg args "limit" 50 1 100
        let offset = intArg args "offset" 0 0 1000000

        let filtered =
            all
            |> List.filter (fun e ->
                let phase = status graph e

                (selectedStatus = "all"
                 || (selectedStatus = "open" && phase <> "done" && phase <> "cancelled")
                 || selectedStatus = phase)
                && (project = "" || prop "TASK_PROJECT" e.Heading = project)
                && (owner = "" || prop "TASK_OWNER" e.Heading = owner))

        let sorted =
            filtered
            |> List.sortBy (fun e ->
                let due =
                    e.Heading.Planning
                    |> Option.bind (fun p -> p.Deadline)
                    |> Option.map (fun t -> t.Date)
                    |> Option.defaultValue DateTime.MaxValue

                (if due.Date < Runtime.today () then 0 else 1),
                (e.Heading.Priority
                 |> Option.map (fun (Priority c) -> c)
                 |> Option.defaultValue 'B'),
                due,
                e.File,
                e.Heading.Position)

        let page = sorted |> List.skip (min offset sorted.Length) |> List.truncate limit

        obj
            [ "tasks", page |> Seq.map (row ctx graph) |> arr
              "total", num sorted.Length
              "next_offset",
              (if offset + page.Length < sorted.Length then
                   num (offset + page.Length)
               else
                   null) ]
    | "task_create" ->
        let requestId = uuid args "request_id"
        let actor = required args "actor" |> single "actor"
        let title = required args "title" |> single "title"

        let fingerprint =
            args
            |> Seq.sortBy (fun pair -> pair.Key)
            |> Seq.map (fun pair -> pair.Key, (if isNull pair.Value then null else pair.Value.DeepClone()))
            |> Seq.toList
            |> obj
            |> fun n -> hash (n.ToJsonString())

        let collisions =
            docs
            |> List.collect (fun (file, doc) ->
                [ if Types.tryGetId doc.FileProperties = Some requestId then
                      yield file, doc, None
                  for h in doc.Headlines do
                      if id h = requestId then
                          yield file, doc, Some h ])

        match collisions with
        | [ file, doc, Some h ] when prop "TASK_CREATE_HASH" h = fingerprint ->
            row
                ctx
                graph
                { File = file
                  Doc = doc
                  Heading = h
                  Config = FileConfig.mergeFileConfig ctx.Config doc.Keywords }
        | _ :: _ -> fail "conflict" 409 "request_id already exists with a different task payload"
        | [] ->
            let file = Path.Combine(ctx.Root, "tasks.org")

            if
                Runtime.fileExists file
                && not (docs |> List.exists (fun (path, _) -> path = file))
            then
                fail "invalid_arguments" 400 "tasks.org is not a regular workspace Org file"

            let before =
                if Runtime.fileExists file then
                    Runtime.readText file
                else
                    ""

            Document.ensureEditable before

            let cfg =
                FileConfig.mergeFileConfig ctx.Config (Document.parseWithConfig ctx.Config before).Keywords

            let initial =
                cfg.TodoKeywords.ActiveStates
                |> List.tryHead
                |> Option.map (fun k -> k.Keyword)
                |> Option.defaultWith (fun () -> fail "invalid_arguments" 400 "No active TODO keyword configured")

            let prefix =
                if before = "" || before.EndsWith("\n") then
                    before
                else
                    before + "\n"

            let pos = int64 prefix.Length

            let mutable after =
                prefix + Mutations.formatNewHeadline title 1 (Some initial) None [] None None

            after <- Mutations.setProperty after pos "ID" requestId
            after <- Mutations.setProperty after pos "TASK_MANAGED" "true"
            after <- Mutations.setProperty after pos "TASK_CREATE_HASH" fingerprint
            after <- applyFields graph requestId cfg args after pos
            let body = optional args "text" ""

            if body <> "" then
                after <- Mutations.appendBody after pos body

            after <- note actor "created" "" after pos
            finish file pos before after
    | "task_update"
    | "task_action" ->
        let actor = required args "actor" |> single "actor"
        let file, pos, before, doc = ctx.Resolve(required args "ref")

        if pos < 0L then
            fail "invalid_arguments" 400 "A task must be a TODO heading"

        Document.ensureEditable before
        let h = doc.Headlines |> List.find (fun h -> h.Position = pos)

        if h.TodoKeyword.IsNone then
            fail "invalid_arguments" 400 "A task must be a TODO heading"

        let cfg = FileConfig.mergeFileConfig ctx.Config doc.Keywords

        let e =
            { File = file
              Doc = doc
              Heading = h
              Config = cfg }

        let mutable after = before
        let taskId = if id h = "" then Guid.NewGuid().ToString("D") else id h

        let action =
            if operation = "task_update" then
                "configured"
            else
                required args "action"

        if operation = "task_action" then
            let specific =
                match action with
                | "claim"
                | "renew" -> [ "claim_id"; "lease_minutes" ]
                | "release"
                | "submit" -> [ "claim_id"; "evidence" ]
                | "approve"
                | "reject"
                | "cancel"
                | "reopen" -> [ "evidence" ]
                | _ -> fail "invalid_arguments" 400 "Unknown task action"

            for pair in args do
                if not (List.contains pair.Key ([ "ref"; "expected_revision"; "actor"; "action" ] @ specific)) then
                    fail "invalid_arguments" 400 ("Argument does not apply to this action: " + pair.Key)
        elif
            not (
                [ "acceptance"
                  "project"
                  "owner"
                  "depends_on"
                  "review_required"
                  "priority"
                  "scheduled"
                  "deadline" ]
                |> List.exists args.ContainsKey
            )
        then
            fail "invalid_arguments" 400 "No task changes supplied"

        let claimId =
            if List.contains action [ "claim"; "renew"; "release"; "submit" ] then
                uuid args "claim_id"
            else
                ""

        let lease = intArg args "lease_minutes" 30 1 240
        let evidence = optional args "evidence" ""

        if evidence.Length > 65536 then
            fail "invalid_arguments" 400 "Evidence must be at most 65536 characters"
        // A claim retry never extends the lease. Tokens are generated by the caller.
        if
            action = "claim"
            && prop "TASK_CLAIM_ID" h = claimId
            && prop "TASK_CLAIM_OWNER" h = actor
            && active h
            && not (claimStale e)
            && not (isDone e || isCancelled e)
            && prop "TASK_CLAIM_MINUTES" h = string lease
        then
            let result = row ctx graph e
            result["claim_id"] <- text claimId
            result
        else
            requireRevision args before

            if action <> "claim" && List.contains action [ "renew"; "release"; "submit" ] then
                if prop "TASK_CLAIM_ID" h <> claimId || prop "TASK_CLAIM_OWNER" h <> actor then
                    fail "conflict" 409 "This actor does not hold the claim"

                if action <> "release" && not (active h) then
                    fail "conflict" 409 "Claim expired; obtain a new claim before continuing"

            if List.contains action [ "renew"; "submit" ] && claimStale e then
                fail
                    "conflict"
                    409
                    "Task changed outside this claim; release it, inspect the current requirements and claim again"

            if action = "approve" && submissionStale e then
                fail
                    "conflict"
                    409
                    "Task changed after submission; reject the stale submission and request fresh evidence"

            if action = "configured" then
                if isDone e || isCancelled e then
                    fail "conflict" 409 "Reopen completed or cancelled work before changing its task contract"

                if active h || prop "TASK_PHASE" h = "REVIEW" then
                    fail "conflict" 409 "Release or reject active work before changing its task contract"

                after <- applyFields graph taskId cfg args after pos
            elif action = "claim" then
                if status graph e <> "ready" then
                    fail "conflict" 409 "Task is not ready: inspect its status and blockers"

                if prop "TASK_OWNER" h <> "" && prop "TASK_OWNER" h <> actor then
                    fail "conflict" 409 "Task is assigned to a different actor"

                if prop "TASK_CLAIM_ID" h = claimId then
                    fail "conflict" 409 "Use a new claim_id after a lease expires"

                after <- Mutations.setProperty after pos "TASK_CLAIM_ID" claimId
                after <- Mutations.setProperty after pos "TASK_CLAIM_OWNER" actor
            elif action = "renew" then
                if isDone e || isCancelled e || prop "TASK_PHASE" h = "REVIEW" then
                    fail "conflict" 409 "Task is no longer working"
            elif action = "release" then
                after <- clearClaim after pos
            elif action = "submit" then
                if String.IsNullOrWhiteSpace evidence then
                    fail "invalid_arguments" 400 "Completion evidence is required"

                if isDone e || isCancelled e || prop "TASK_PHASE" h = "REVIEW" then
                    fail "conflict" 409 "Task is no longer working"

                if not (List.isEmpty (blockers graph e)) then
                    fail "conflict" 409 "Task has unfinished or invalid dependencies"

                after <- clearClaim after pos
                after <- Mutations.setProperty after pos "TASK_SUBMITTED_BY" actor

                if reviewRequired h then
                    after <- Mutations.setProperty after pos "TASK_PHASE" "REVIEW"
                else
                    after <- state cfg true after pos
            elif action = "approve" || action = "reject" then
                if prop "TASK_PHASE" h <> "REVIEW" || isDone e || isCancelled e then
                    fail "conflict" 409 "Task is not awaiting review"

                if prop "TASK_SUBMITTED_BY" h = actor then
                    fail "conflict" 409 "A different actor must review submitted work"

                if String.IsNullOrWhiteSpace evidence then
                    fail "invalid_arguments" 400 "A review decision needs evidence or feedback"

                if action = "approve" && not (List.isEmpty (blockers graph e)) then
                    fail "conflict" 409 "Dependencies are no longer complete"

                after <- Mutations.removeProperty after pos "TASK_PHASE"

                if action = "approve" then
                    after <- state cfg true after pos

                after <- Mutations.setProperty after pos "TASK_REVIEWED_BY" actor
            elif action = "cancel" then
                if String.IsNullOrWhiteSpace evidence then
                    fail "invalid_arguments" 400 "Cancellation needs a reason"

                after <- clearClaim after pos
                after <- state cfg true after pos
                after <- Mutations.setProperty after pos "TASK_PHASE" "CANCELLED"
            elif action = "reopen" then
                if not (isDone e || isCancelled e) then
                    fail "conflict" 409 "Only completed or cancelled tasks can be reopened"

                after <- clearClaim after pos
                after <- Mutations.removeProperty after pos "TASK_PHASE"
                after <- state cfg false after pos
            else
                fail "invalid_arguments" 400 "Unknown task action"

            if action = "claim" || action = "renew" then
                after <-
                    Mutations.setProperty after pos "TASK_CLAIM_UNTIL" ((now ()).AddMinutes(float lease).ToString("O"))

                after <- Mutations.setProperty after pos "TASK_CLAIM_MINUTES" (string lease)

            after <- Mutations.setProperty after pos "ID" taskId
            after <- Mutations.setProperty after pos "TASK_MANAGED" "true"
            after <- note actor action evidence after pos

            if action = "claim" || action = "submit" then
                // Hash the final text (including an ID assigned when adopting a task).
                let updatedDoc = Document.parseWithConfig ctx.Config after
                let updatedHeading = updatedDoc.Headlines |> List.find (fun h -> h.Position = pos)

                let fingerprint =
                    contractText
                        after
                        { e with
                            Doc = updatedDoc
                            Heading = updatedHeading }

                let key =
                    if action = "claim" then
                        "TASK_CLAIM_CONTRACT"
                    else
                        "TASK_SUBMISSION_CONTRACT"

                after <- Mutations.setProperty after pos key fingerprint

            let result = finish file pos before after

            if action = "claim" || action = "renew" then
                result["claim_id"] <- text claimId

            result
    | _ -> fail "unknown_operation" 404 "Unknown task operation"
