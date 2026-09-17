module OrgCli.Index.Application

open System
open System.IO
open System.Text
open System.Text.Json.Nodes
open OrgCli.Org

exception ServiceError of code: string * status: int * message: string

let fail code status message =
    raise (ServiceError(code, status, message))

let str (value: string) : JsonNode = JsonValue.Create value
let number (value: int) : JsonNode = JsonValue.Create value
let boolean (value: bool) : JsonNode = JsonValue.Create value

let obj (fields: (string * JsonNode) list) : JsonNode =
    let result = JsonObject()

    for key, value in fields do
        result[key] <- value

    result

let array (values: JsonNode seq) : JsonNode = JsonArray(values |> Seq.toArray)

let revision (text: string) =
    Runtime.hash (Encoding.UTF8.GetBytes text)

/// Shared application boundary: no console, HTTP, MCP, or org-roam dependency.
/// Every call gets an explicit host and configuration, including when hosted concurrently.
type WorkspaceService(host: Runtime.IHost, directory: string, dbPath: string, config: OrgConfig, ?readOnly: bool) =
    let gate = System.Object()
    let root = Path.GetFullPath(directory, host.CurrentDirectory)
    let database = Path.GetFullPath(dbPath, host.CurrentDirectory)
    let readOnly = defaultArg readOnly false
    let inbox = Path.Combine(root, "inbox.org")

    let pendingGate = System.Object()
    let pending = Collections.Generic.HashSet<string>()
    let mutable watching = false
    let mutable reconcile = false
    let mutable cached: Map<string, OrgDocument> = Map.empty

    let invalidateAll () =
        lock pendingGate (fun () ->
            reconcile <- true
            pending.Clear())

    let invalidatePath path =
        let path = Path.GetFullPath(path, root)
        let relative = Path.GetRelativePath(root, path)

        if
            not (Path.IsPathRooted relative)
            && relative <> ".."
            && not (relative.StartsWith(".." + string Path.DirectorySeparatorChar))
        then
            lock pendingGate (fun () ->
                if not reconcile then
                    pending.Add path |> ignore

                    if pending.Count > 4096 then
                        reconcile <- true
                        pending.Clear())

    let files () =
        if not (Runtime.directoryExists root) then
            fail "not_found" 404 "Workspace directory does not exist"

        Utils.listOrgFiles root

    // Call only under gate with the workspace host/configuration installed. Events use a
    // separate short lock so notifications arriving during a refresh remain pending.
    let refresh () =
        if watching then
            let full, paths =
                lock pendingGate (fun () ->
                    let batch = reconcile, List.ofSeq pending
                    reconcile <- false
                    pending.Clear()
                    batch)

            if full || not paths.IsEmpty then
                try
                    let selected = files () |> Set.ofList
                    use db = new IndexDatabase.OrgIndexDb(database)
                    db.Initialize()

                    if full then
                        IndexSync.syncFiles db (Set.toList selected) false

                        cached <-
                            db.GetDocuments()
                            |> List.filter (fun (file, _) -> selected.Contains file)
                            |> Map.ofList
                    else
                        let mutable next = cached

                        for path in paths do
                            if selected.Contains path then
                                IndexSync.syncFileIncremental db path

                                match db.GetDocument path with
                                | Some doc -> next <- next.Add(path, doc)
                                | None -> next <- next.Remove path
                            else
                                db.ExecuteInTransaction(fun () ->
                                    db.DeleteFtsForFile path
                                    db.DeleteFile path)

                                next <- next.Remove path

                        cached <- next
                with _ ->
                    invalidateAll ()
                    reraise ()

    let documents () =
        if watching then
            refresh ()
            Map.toList cached
        else
            Workspace.documents database (files ())

    let relative path = Path.GetRelativePath(root, path)
    let read path = Runtime.readText path

    let required (args: JsonObject) (key: string) : string =
        match args[key] with
        | :? JsonValue as v ->
            let mutable text = ""

            if v.TryGetValue<string>(&text) && not (String.IsNullOrWhiteSpace text) then
                text
            else
                fail "invalid_arguments" 400 (key + " must be a nonempty string")
        | _ -> fail "invalid_arguments" 400 (key + " is required")

    let optional (args: JsonObject) (key: string) (fallback: string) =
        if not (args.ContainsKey key) then
            fallback
        else
            match args[key] with
            | :? JsonValue as v ->
                let mutable text = ""

                if v.TryGetValue<string>(&text) then
                    text
                else
                    fail "invalid_arguments" 400 (key + " must be a string")
            | _ -> fail "invalid_arguments" 400 (key + " must be a string")

    let integer (args: JsonObject) (key: string) fallback minimum maximum =
        if not (args.ContainsKey key) then
            fallback
        else
            let mutable value = 0

            match args[key] with
            | :? JsonValue as v when v.TryGetValue<int>(&value) && value >= minimum && value <= maximum -> value
            | _ -> fail "invalid_arguments" 400 (sprintf "%s must be between %d and %d" key minimum maximum)

    let singleLine key (text: string) =
        if text.Contains('\n') || text.Contains('\r') then
            fail "invalid_arguments" 400 (key + " must be one line")

        text

    let locator file pos content =
        let data =
            obj
                [ "file", str (relative file)
                  "position", JsonValue.Create(pos: int64)
                  "revision", str (revision content) ]

        "loc:"
        + Convert
            .ToBase64String(Encoding.UTF8.GetBytes(data.ToJsonString()))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_')

    let reference file (doc: OrgDocument) pos =
        let id =
            if pos = -1L then
                Types.tryGetId doc.FileProperties
            else
                doc.Headlines
                |> List.tryFind (fun h -> h.Position = pos)
                |> Option.bind (fun h -> Types.tryGetId h.Properties)

        id
        |> Option.map (fun value -> "id:" + value)
        |> Option.defaultWith (fun () ->
            let content = read file
            let current = Document.parseWithConfig config content

            if
                current.Headlines <> doc.Headlines
                || current.FileProperties <> doc.FileProperties
            then
                fail "conflict" 409 "Workspace changed during search; retry"

            locator file pos content)

    let resolve (identifier: string) =
        let selected =
            if watching then
                refresh ()
                cached |> Map.keys |> Seq.toList
            else
                files ()

        if identifier.StartsWith("loc:", StringComparison.Ordinal) then
            let data =
                try
                    let encoded = identifier.Substring(4).Replace('-', '+').Replace('_', '/')
                    let padded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=')
                    JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String padded))
                with _ ->
                    fail "invalid_arguments" 400 "Invalid entry reference"

            try
                let path = Path.GetFullPath(data["file"].GetValue<string>(), root)

                if not (List.contains path selected) then
                    fail "not_found" 404 "Entry is outside the selected workspace or missing"

                let content = read path

                if revision content <> data["revision"].GetValue<string>() then
                    fail "conflict" 409 "Entry reference is stale; search again"

                let pos = data["position"].GetValue<int64>()
                let doc = Document.parseWithConfig config content

                if pos <> -1L && not (doc.Headlines |> List.exists (fun h -> h.Position = pos)) then
                    fail "not_found" 404 "Entry not found"

                path, pos, content, doc
            with
            | ServiceError _ -> reraise ()
            | _ -> fail "invalid_arguments" 400 "Invalid entry reference"
        else
            let lookup =
                if watching then
                    Workspace.resolveIndexed
                else
                    Workspace.resolve

            match lookup database selected identifier with
            | Error e ->
                fail
                    (if e.Type = CliErrorType.HeadlineNotFound then
                         "not_found"
                     else
                         "ambiguous")
                    (if e.Type = CliErrorType.HeadlineNotFound then 404 else 409)
                    e.Message
            | Ok(path, _) ->
                let content = read path
                let doc = Document.parseWithConfig config content

                if Workspace.isFileRoot doc identifier then
                    path, -1L, content, doc
                else
                    match Headlines.resolveHeadlinePos content identifier with
                    | Ok pos -> path, pos, content, doc
                    | Error _ -> fail "conflict" 409 "Entry changed during lookup; retry the read"

    let describe (file: string) pos (content: string) (doc: OrgDocument) offset limit =
        let title, text, outline =
            if pos = -1L then
                Types.tryGetTitle doc.Keywords |> Option.defaultValue (Path.GetFileName file), content, []
            else
                let headline = doc.Headlines |> List.find (fun h -> h.Position = pos)
                headline.Title, Subtree.extractSubtree content pos, Document.computeOutlinePath doc.Headlines headline

        let start = min offset text.Length
        let length = min limit (text.Length - start)

        obj
            [ "ref", str (reference file doc pos)
              "file", str (relative file)
              "title", str title
              "revision", str (revision content)
              "text", str (text.Substring(start, length))
              "outline", array (outline |> Seq.map str)
              "offset", number start
              "next_offset",
              (if start + length < text.Length then
                   number (start + length)
               else
                   null) ]

    let requireRevision args content =
        if required args "expected_revision" <> revision content then
            fail "conflict" 409 "The file has changed; fetch the entry again before editing"

    let save path before after =
        try
            Runtime.commit [ Runtime.editExpected path before after ]
        with :? IOException as ex ->
            fail "conflict" 409 ex.Message
        // Files are authoritative. A failed cache refresh must not turn a successful write into a retry.
        try
            use db = new IndexDatabase.OrgIndexDb(database)
            db.Initialize()
            IndexSync.syncFileIncremental db path

            if watching then
                match db.GetDocument path with
                | Some doc -> cached <- cached.Add(path, doc)
                | None -> invalidatePath path

            None
        with ex ->
            invalidatePath path
            Some("File saved; index refresh failed: " + ex.Message)

    let saved path pos before after =
        let warning = save path before after
        let result = describe path pos after (Document.parseWithConfig config after) 0 65536
        warning |> Option.iter (fun w -> result["warning"] <- str w)
        result

    let date text =
        match
            DateTime.TryParseExact(
                text,
                "yyyy-MM-dd",
                Globalization.CultureInfo.InvariantCulture,
                Globalization.DateTimeStyles.None
            )
        with
        | true, value -> value
        | _ -> fail "invalid_arguments" 400 "Dates must use yyyy-MM-dd"

    let invoke operation (args: JsonObject) =
        let allowed =
            match operation with
            | "entry_details"
            | "entry_checkbox"
            | "tasks"
            | "task_create"
            | "task_update"
            | "task_action"
            | "task_move" -> TaskWorkflow.allowed operation
            | "workspace" -> []
            | "board_settings" -> [ "expected_revision"; "settings" ]
            | "task_undo" -> [ "token"; "actor" ]
            | "search" -> [ "query"; "limit"; "offset" ]
            | "fetch" -> [ "ref"; "limit"; "offset" ]
            | "agenda" -> [ "from"; "through"; "limit"; "offset" ]
            | "capture" -> [ "request_id"; "title"; "text"; "state" ]
            | "append_note" -> [ "ref"; "text"; "expected_revision" ]
            | "update_task" -> [ "ref"; "expected_revision"; "state"; "scheduled"; "deadline"; "priority" ]
            | "related" -> [ "ref"; "limit"; "offset" ]
            | _ -> fail "unknown_operation" 404 "Unknown operation"

        for pair in args do
            if not (List.contains pair.Key allowed) then
                fail "invalid_arguments" 400 ("Unknown argument: " + pair.Key)

        if
            readOnly
            && (List.contains operation [ "capture"; "append_note"; "update_task"; "board_settings"; "task_undo" ]
                || TaskWorkflow.isMutation operation)
        then
            fail "read_only" 403 "This server is read-only"

        match operation with
        | "workspace" ->
            let result = TaskStorage.settings root

            result["default_states"] <-
                (config.TodoKeywords.ActiveStates @ config.TodoKeywords.DoneStates)
                |> Seq.map (fun k -> str k.Keyword)
                |> array

            result["default_done_states"] <- config.TodoKeywords.DoneStates |> Seq.map (fun k -> str k.Keyword) |> array
            result["undo"] <- TaskStorage.undoInfo root

            result["files"] <-
                documents ()
                |> Seq.map (fun (file, doc) ->
                    let cfg = FileConfig.mergeFileConfig config doc.Keywords

                    obj
                        [ "file", str (relative file)
                          "revision", str (revision (read file))
                          "states",
                          (cfg.TodoKeywords.ActiveStates @ cfg.TodoKeywords.DoneStates)
                          |> Seq.map (fun k -> str k.Keyword)
                          |> array
                          "done_states", cfg.TodoKeywords.DoneStates |> Seq.map (fun k -> str k.Keyword) |> array
                          "parents",
                          doc.Headlines
                          |> Seq.map (fun h ->
                              obj
                                  [ "ref", str (reference file doc h.Position)
                                    "title", str h.Title
                                    "level", number h.Level ])
                          |> array ])
                |> array

            result
        | "board_settings" ->
            TaskStorage.prepare root
            use settingsLock = host.AcquireLock(TaskStorage.path root "board-lock")
            let before = TaskStorage.read root "board"

            if required args "expected_revision" <> revision before then
                fail "conflict" 409 "Board settings changed; reload before saving"

            let settings = args["settings"]

            if
                isNull settings
                || not (settings :? JsonObject)
                || settings.ToJsonString().Length > 65536
            then
                fail "invalid_arguments" 400 "settings must be an object of at most 64 KiB"

            for pair in settings.AsObject() do
                if pair.Key <> "column_order" then
                    fail "invalid_arguments" 400 "Only column_order is a shared board setting"

                match pair.Value with
                | :? JsonArray as values when values.Count <= 100 ->
                    for v in values do
                        if isNull v || not (v :? JsonValue) then
                            fail "invalid_arguments" 400 "Column names must be strings"

                        let mutable name = ""

                        if
                            not (v.AsValue().TryGetValue<string>(&name))
                            || name.Length > 100
                            || String.IsNullOrWhiteSpace name
                        then
                            fail "invalid_arguments" 400 "Invalid column name"
                | _ -> fail "invalid_arguments" 400 "column_order must be an array"

            let p = TaskStorage.path root "board"

            if Runtime.fileExists p then
                Runtime.commit [ Runtime.editExpected p before (settings.ToJsonString()) ]
            else
                Runtime.writeText (p, settings.ToJsonString())

            TaskStorage.settings root
        | "task_undo" ->
            use taskLock = host.AcquireLock(Path.Combine(root, ".org-tasks.lock"))

            try
                TaskStorage.undo root (required args "token") (required args "actor")
            with :? InvalidOperationException as ex ->
                fail "conflict" 409 ex.Message

            invalidateAll ()
            refresh ()
            obj [ "undone", boolean true ]
        | "entry_details"
        | "entry_checkbox"
        | "tasks"
        | "task_create"
        | "task_update"
        | "task_action"
        | "task_move" ->
            let mutable saved = false

            let saveTasks changes =
                try
                    TaskStorage.commit root (required args "actor") changes
                with :? IOException as ex ->
                    fail "conflict" 409 ex.Message

                saved <- true

                try
                    for file, _, _ in changes do
                        invalidatePath file

                    refresh ()
                    None
                with ex ->
                    Some("Saved; index refresh failed: " + ex.Message)

            try
                let result =
                    TaskWorkflow.invoke
                        { Root = root
                          Config = config
                          Documents = documents
                          Resolve = resolve
                          Reference = reference
                          Save = fun file before after -> saveTasks [ file, before, after ]
                          SaveMany = saveTasks
                          Reconcile =
                            fun () ->
                                invalidateAll ()
                                refresh () }
                        operation
                        args

                if saved then
                    result["undo"] <- TaskStorage.undoInfo root

                result
            with
            | TaskWorkflow.TaskError(code, status, message) -> fail code status message
            | :? ArgumentException as ex -> fail "invalid_arguments" 400 ex.Message
        | "search" ->
            let query = required args "query"
            let limit = integer args "limit" 20 1 100
            let offset = integer args "offset" 0 0 1000000
            let docs = documents () |> Map.ofList
            use db = new IndexDatabase.OrgIndexDb(database)
            db.Initialize()

            let hits =
                try
                    db.SearchFts query
                with :? Microsoft.Data.Sqlite.SqliteException as ex ->
                    fail "invalid_query" 400 ex.Message

            let hits = hits |> List.filter (fun h -> docs.ContainsKey h.File)

            let rows =
                hits
                |> List.skip (min offset hits.Length)
                |> List.truncate limit
                |> List.map (fun h ->
                    obj
                        [ "ref", str (reference h.File docs[h.File] h.CharPos)
                          "file", str (relative h.File)
                          "title", str h.Title
                          "excerpt", str (h.Context |> Option.defaultValue "")
                          "outline", str (h.OutlinePath |> Option.defaultValue "")
                          "rank", JsonValue.Create h.Rank ])

            obj
                [ "results", array rows
                  "next_offset",
                  (if offset + rows.Length < hits.Length then
                       number (offset + rows.Length)
                   else
                       null) ]
        | "fetch" ->
            let path, pos, content, doc = resolve (required args "ref")

            describe
                path
                pos
                content
                doc
                (integer args "offset" 0 0 Int32.MaxValue)
                (integer args "limit" 16000 1 65536)
        | "agenda" ->
            let fromDate =
                optional args "from" ((Runtime.now ()).ToString("yyyy-MM-dd")) |> date

            let throughDate =
                optional args "through" (fromDate.AddDays(6).ToString("yyyy-MM-dd")) |> date

            if throughDate < fromDate || (throughDate - fromDate).TotalDays > 366 then
                fail "invalid_arguments" 400 "Agenda range must be between 0 and 366 days"

            let docs = documents ()
            let byFile = docs |> Map.ofList
            let limit = integer args "limit" 50 1 100
            let offset = integer args "offset" 0 0 1000000

            let items = Agenda.openThrough config throughDate docs

            let rows =
                items
                |> List.skip (min offset items.Length)
                |> List.truncate limit
                |> List.map (fun item ->
                    obj
                        [ "ref", str (reference item.File byFile[item.File] item.Headline.Position)
                          "title", str item.Headline.Title
                          "file", str (relative item.File)
                          "date", str (item.Date.ToString("yyyy-MM-dd"))
                          "kind",
                          str (
                              if item.Type = Agenda.Scheduled then
                                  "scheduled"
                              else
                                  "deadline"
                          )
                          "overdue", boolean (item.Date.Date < fromDate)
                          "state", JsonOutput.jstr item.Headline.TodoKeyword ])

            obj
                [ "items", array rows
                  "next_offset",
                  (if offset + rows.Length < items.Length then
                       number (offset + rows.Length)
                   else
                       null) ]
        | "capture" ->
            let request = required args "request_id"
            let mutable guid = Guid.Empty

            if not (Guid.TryParse(request, &guid)) then
                fail "invalid_arguments" 400 "request_id must be a UUID; reuse it when retrying the same capture"

            let id = guid.ToString("D")
            let title = required args "title" |> singleLine "title"
            let body = optional args "text" ""
            let state = optional args "state" "" |> singleLine "state"

            let fingerprint =
                obj [ "title", str title; "text", str body; "state", str state ]
                |> fun n -> revision (n.ToJsonString())

            let docs = documents ()

            if
                docs
                |> List.exists (fun (_, doc) -> Types.tryGetId doc.FileProperties = Some id)
            then
                fail "conflict" 409 "request_id already belongs to a file-level entry"

            let existing =
                docs
                |> List.collect (fun (file, doc) ->
                    doc.Headlines
                    |> List.choose (fun h ->
                        if Types.tryGetId h.Properties = Some id then
                            Some(file, doc, h)
                        else
                            None))

            match existing with
            | [ (file, doc, h) ] when Types.tryGetProperty "ORG_CLI_CAPTURE_HASH" h.Properties = Some fingerprint ->
                describe file h.Position (read file) doc 0 65536
            | _ :: _ -> fail "conflict" 409 "request_id already belongs to another capture"
            | [] ->
                // Automatic discovery excludes symlinks. Never write an excluded existing inbox.
                if Runtime.fileExists inbox && not (files () |> List.contains inbox) then
                    fail "invalid_arguments" 400 "Inbox is not a regular workspace Org file"

                let before = if Runtime.fileExists inbox then read inbox else ""
                Document.ensureEditable before
                let cfg = FileConfig.mergeFileConfig config (Document.parse before).Keywords

                if state <> "" && not (Types.allKeywords cfg.TodoKeywords |> List.contains state) then
                    fail "invalid_arguments" 400 "Unknown TODO state"

                let headline =
                    Mutations.formatNewHeadline title 1 (if state = "" then None else Some state) None [] None None

                let headline =
                    Mutations.setProperty headline 0L "ID" id
                    |> fun t -> Mutations.setProperty t 0L "ORG_CLI_CAPTURE_HASH" fingerprint

                let headline =
                    if body = "" then
                        headline
                    else
                        Mutations.appendBody headline 0L body

                let after = Subtree.appendSubtree before headline
                Document.ensureEditable after

                let pos =
                    Headlines.resolveHeadlinePos after ("id:" + id)
                    |> Result.defaultWith (fun e -> fail "invalid_arguments" 400 e.Message)

                saved inbox pos before after
        | "append_note" ->
            let file, pos, content, _ = resolve (required args "ref")
            requireRevision args content
            let text = required args "text"
            Document.ensureEditable content

            let after =
                if pos = -1L then
                    Workspace.appendRoot content text
                else
                    Mutations.appendBody content pos text

            Document.ensureEditable after
            saved file pos content after
        | "update_task" ->
            let file, pos, content, doc = resolve (required args "ref")
            requireRevision args content

            if pos = -1L then
                fail "invalid_arguments" 400 "File-level notes are not tasks"

            if not ([ "state"; "scheduled"; "deadline"; "priority" ] |> List.exists args.ContainsKey) then
                fail "invalid_arguments" 400 "No task changes supplied"

            let cfg = FileConfig.mergeFileConfig config doc.Keywords
            let mutable after = content

            if
                Types.tryGetProperty "TASK_MANAGED" (doc.Headlines |> List.find (fun h -> h.Position = pos)).Properties = Some
                    "true"
            then
                fail "conflict" 409 "Use task_update or task_action for managed tasks"

            if args.ContainsKey "state" then
                let state = optional args "state" ""

                if state <> "" && not (Types.allKeywords cfg.TodoKeywords |> List.contains state) then
                    fail "invalid_arguments" 400 "Unknown TODO state"

                after <- Mutations.setTodoState cfg after pos (if state = "" then None else Some state) (Runtime.now ())

            for name, setter in [ "scheduled", Mutations.setScheduled; "deadline", Mutations.setDeadline ] do
                if args.ContainsKey name then
                    let text = optional args name ""

                    let timestamp =
                        if text = "" then
                            None
                        else
                            date text |> ignore
                            Some(Utils.parseDate text)

                    after <- setter cfg after pos timestamp (Runtime.now ())

            if args.ContainsKey "priority" then
                let text = optional args "priority" ""

                if text <> "" && (text.Length <> 1 || text[0] < 'A' || text[0] > 'Z') then
                    fail "invalid_arguments" 400 "priority must be A-Z or an empty string"

                after <- Mutations.setPriority after pos (if text = "" then None else Some text[0])

            saved file pos content after
        | "related" ->
            let _, pos, _, doc = resolve (required args "ref")

            let id =
                if pos = -1L then
                    Types.tryGetId doc.FileProperties
                else
                    doc.Headlines
                    |> List.find (fun h -> h.Position = pos)
                    |> fun h -> Types.tryGetId h.Properties

            let id =
                id
                |> Option.defaultWith (fun () -> fail "invalid_arguments" 400 "Backlinks require a standard Org ID")

            let limit = integer args "limit" 20 1 100
            let offset = integer args "offset" 0 0 1000000

            let links =
                documents ()
                |> List.collect (fun (file, doc) ->
                    doc.Links
                    |> List.choose (fun (link, owner) ->
                        if link.LinkType = "id" && link.Path = id then
                            let position =
                                doc.Headlines
                                |> List.filter (fun h -> h.Position <= int64 link.Position)
                                |> List.tryLast
                                |> Option.map (fun h -> h.Position)
                                |> Option.defaultValue -1L

                            Some(
                                obj
                                    [ "ref", str (reference file doc position)
                                      "file", str (relative file)
                                      "source_id", JsonOutput.jstr owner ]
                            )
                        else
                            None))

            let rows = links |> List.skip (min offset links.Length) |> List.truncate limit

            obj
                [ "backlinks", array rows
                  "next_offset",
                  (if offset + rows.Length < links.Length then
                       number (offset + rows.Length)
                   else
                       null) ]
        | _ -> fail "unknown_operation" 404 "Unknown operation"

    /// Native watching is only attached to the physical host. Virtual hosts inject
    /// notifications through the same methods without observing the user's disk.
    member _.WatchDirectory =
        match host with
        | :? Runtime.PhysicalHost -> Some root
        | _ -> None

    member _.EnableWatching() =
        lock gate (fun () ->
            watching <- true
            invalidateAll ())

    member _.DisableWatching() =
        lock gate (fun () ->
            watching <- false
            cached <- Map.empty
            invalidateAll ())

    member _.InvalidatePath(path) = invalidatePath path
    member _.InvalidateAll() = invalidateAll ()

    member _.Refresh() =
        lock gate (fun () ->
            use hostScope = Runtime.useHost host
            use configScope = Config.useConfig config
            refresh ())

    member _.ReadOnly = readOnly

    member _.Invoke(operation: string, args: JsonObject) =
        lock gate (fun () ->
            use hostScope = Runtime.useHost host
            use configScope = Config.useConfig config
            invoke operation args)
