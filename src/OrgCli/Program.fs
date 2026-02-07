open System
open System.IO
open System.Text.Json.Nodes
open OrgCli.Org

/// Format an agenda item for text output
let formatAgendaItemText (item: Agenda.AgendaItem) =
    let typeStr =
        match item.Type with
        | Agenda.Scheduled -> "Scheduled:"
        | Agenda.Deadline -> "Deadline: "
    let todo = item.Headline.TodoKeyword |> Option.defaultValue ""
    let priority =
        match item.Headline.Priority with
        | Some (Priority c) -> sprintf " [#%c]" c
        | None -> ""
    let tags =
        if List.isEmpty item.Headline.Tags then ""
        else sprintf " :%s:" (String.Join(":", item.Headline.Tags))
    sprintf "  %s %s%s %s%s" typeStr todo priority item.Headline.Title tags

/// Format an agenda item for JSON output
let formatAgendaItemJson (item: Agenda.AgendaItem) : JsonNode =
    let typeStr =
        match item.Type with
        | Agenda.Scheduled -> "scheduled"
        | Agenda.Deadline -> "deadline"
    let todo = item.Headline.TodoKeyword |> Option.defaultValue ""
    let priority =
        match item.Headline.Priority with
        | Some (Priority c) -> string c
        | None -> ""
    let obj = JsonObject()
    obj["date"] <- JsonValue.Create(item.Date.ToString("yyyy-MM-dd"))
    obj["type"] <- JsonValue.Create(typeStr)
    obj["todo"] <- JsonValue.Create(todo)
    obj["priority"] <- JsonValue.Create(priority)
    obj["title"] <- JsonValue.Create(item.Headline.Title)
    obj["tags"] <- JsonOutput.jsonArray (item.Headline.Tags |> List.map (fun t -> JsonValue.Create(t) :> JsonNode))
    obj["file"] <- JsonValue.Create(item.File)
    obj["level"] <- JsonValue.Create(item.Headline.Level)
    obj

/// Parse command line arguments (supports repeated flags)
let parseArgs (args: string array) : Map<string, string list> * string list =
    let addOpt (opts: Map<string, string list>) (key: string) (value: string) =
        let existing = Map.tryFind key opts |> Option.defaultValue []
        Map.add key (existing @ [value]) opts

    let rec parse (args: string list) (opts: Map<string, string list>) (positional: string list) =
        match args with
        | [] -> opts, List.rev positional
        | "--" :: rest -> opts, List.rev positional @ rest
        | opt :: value :: rest when opt.StartsWith("--") && not (value.StartsWith("-")) ->
            parse rest (addOpt opts (opt.TrimStart('-')) value) positional
        | opt :: rest when opt.StartsWith("--") ->
            parse rest (addOpt opts (opt.TrimStart('-')) "true") positional
        | opt :: value :: rest when opt.StartsWith("-") && opt.Length = 2 && not (value.StartsWith("-")) ->
            parse rest (addOpt opts (opt.TrimStart('-')) value) positional
        | opt :: rest when opt.StartsWith("-") && opt.Length = 2 ->
            parse rest (addOpt opts (opt.TrimStart('-')) "true") positional
        | arg :: rest ->
            parse rest opts (arg :: positional)
    parse (Array.toList args) Map.empty []

let getOpt (opts: Map<string, string list>) (key: string) (altKey: string option) (defaultVal: string) =
    match Map.tryFind key opts with
    | Some (v :: _) -> v
    | _ ->
        match altKey with
        | Some alt ->
            match Map.tryFind alt opts with
            | Some (v :: _) -> v
            | _ -> defaultVal
        | None -> defaultVal

let getOptAll (opts: Map<string, string list>) (key: string) (altKey: string option) : string list =
    let primary = Map.tryFind key opts |> Option.defaultValue []
    let alt = altKey |> Option.bind (fun k -> Map.tryFind k opts) |> Option.defaultValue []
    primary @ alt

let resolveFiles (opts: Map<string, string list>) : string list =
    match getOptAll opts "files" None with
    | _ :: _ as explicit -> explicit
    | [] ->
        let dir = getOpt opts "directory" (Some "d") (Directory.GetCurrentDirectory())
        Utils.listOrgFiles dir

/// Print a CliError in the appropriate format and return exit code 1.
let printError (isJson: bool) (e: CliError) : int =
    if isJson then
        printfn "%s" (JsonOutput.error e)
    else
        eprintfn "%s" e.Message
    1

/// Pure transform: resolve headline, apply mutation, return new content + position.
let applyMutation (content: string) (identifier: string) (transform: string -> int64 -> string)
    : Result<string * int64, CliError> =
    Headlines.resolveHeadlinePos content identifier
    |> Result.map (fun pos -> transform content pos, pos)

/// Read file, resolve headline, apply transform, optionally write, print message.
let executeMutation (file: string) (identifier: string) (isJson: bool) (isDryRun: bool) (isQuiet: bool)
    (msg: string) (transform: string -> int64 -> string) : int =
    if not (File.Exists file) then
        printError isJson { Type = CliErrorType.FileNotFound; Message = sprintf "File not found: %s" file; Detail = None }
    else
    let content = File.ReadAllText(file)
    match applyMutation content identifier transform with
    | Ok (newContent, pos) ->
        if not isDryRun then
            File.WriteAllText(file, newContent)
        if isJson then
            let state = HeadlineEdit.extractState newContent pos
            let data =
                if isDryRun then JsonOutput.formatHeadlineStateDryRun state
                else JsonOutput.formatHeadlineState state
            printfn "%s" (JsonOutput.ok data)
        else
            if not isQuiet then
                if isDryRun then
                    printfn "%s (dry run)" msg
                else
                    printfn "%s" msg
        0
    | Error e -> printError isJson e

let hasHelpFlag (opts: Map<string, string list>) (args: string list) =
    List.contains "--help" args || List.contains "-h" args
    || Map.containsKey "help" opts || Map.containsKey "h" opts

let printCommandHelp (name: string) =
    match JsonOutput.findCommandDef name with
    | None -> eprintfn "Unknown command: %s" name
    | Some def ->
        printfn "org %s - %s" def.Name def.Description
        printfn ""
        printfn "Usage: org %s" def.Usage
        if not (List.isEmpty def.HelpArgs) then
            printfn ""
            for a in def.HelpArgs do printfn "  %s" a

/// Load config: --config path → file config → env vars → CLI overrides.
let loadConfig (opts: Map<string, string list>) : OrgConfig =
    let baseConfig =
        match Map.tryFind "config" opts with
        | Some (path :: _) ->
            match Config.loadFromFile path with
            | Ok cfg -> cfg
            | Error msg ->
                eprintfn "Warning: %s" msg
                Config.load ()
        | _ -> Config.load ()

    let mutable cfg = baseConfig

    match Map.tryFind "log-done" opts |> Option.bind List.tryHead with
    | Some v ->
        match Config.parseLogAction v with
        | Some a -> cfg <- { cfg with LogDone = a }
        | None -> eprintfn "Warning: invalid --log-done value: %s" v
    | None -> ()

    match Map.tryFind "deadline-warning-days" opts |> Option.bind List.tryHead with
    | Some v ->
        match System.Int32.TryParse(v) with
        | true, n -> cfg <- { cfg with DeadlineWarningDays = n }
        | _ -> eprintfn "Warning: invalid --deadline-warning-days value: %s" v
    | None -> ()

    cfg

/// Merge base config with file-level in-buffer settings.
let mergeFileConfig (baseConfig: OrgConfig) (content: string) : OrgConfig =
    let doc = Document.parse content
    FileConfig.mergeFileConfig baseConfig doc.Keywords

let printUsage () =
    printfn "org - Org file querying and roam database management"
    printfn ""
    printfn "Usage: org [options] <command> [arguments]"
    printfn ""
    printfn "Global Options:"
    printfn "  -d, --directory <path>  Base directory (default: current directory)"
    printfn "  --files <file>          Explicit file list (can be repeated)"
    printfn "  -f, --format <format>   Output format: text or json (default: text)"
    printfn "  --config <path>         Config file path (default: $XDG_CONFIG_HOME/org-cli/config.json)"
    printfn "  --log-done <action>     Override log-done: none, time, or note"
    printfn "  --deadline-warning-days <n>  Override deadline warning days"
    printfn "  --dry-run               Preview mutation without writing to file"
    printfn "  -q, --quiet             Suppress informational text output"
    printfn ""
    printfn "Org Commands:"
    printfn "  headlines [-d dir] [--todo STATE] [--tag TAG] [--level N] [--property K=V]"
    printfn "                                         List headlines with optional filters"
    printfn "  add <file> <title> [options]           Add a new headline"
    printfn "    --todo STATE  --priority P  --tag TAG  --scheduled DATE  --deadline DATE"
    printfn "    --under <title-or-pos>               Insert as child of headline"
    printfn "  todo <file> <title-or-pos> <state>     Set TODO state (use \"\" to clear)"
    printfn "  priority <file> <title-or-pos> <A-Z|\"\">"
    printfn "                                         Set or clear priority"
    printfn "  tag add <file> <title-or-pos> <tag>    Add tag to headline"
    printfn "  tag remove <file> <title-or-pos> <tag> Remove tag from headline"
    printfn "  property set <file> <title-or-pos> <key> <value>"
    printfn "                                         Set a property"
    printfn "  property remove <file> <title-or-pos> <key>"
    printfn "                                         Remove a property"
    printfn "  schedule <file> <title-or-pos> <date>  Set SCHEDULED (use \"\" to clear)"
    printfn "  deadline <file> <title-or-pos> <date>  Set DEADLINE (use \"\" to clear)"
    printfn "  clock in <file> <title-or-pos>         Start clock"
    printfn "  clock out <file> <title-or-pos>        Stop clock"
    printfn "  clock [report] [-d dir]                Show clock report"
    printfn "  note <file> <title-or-pos> <text>      Add note to logbook"
    printfn "  refile <src-file> <src-title-or-pos> <tgt-file> [<tgt-title-or-pos>]"
    printfn "                                         Refile subtree"
    printfn "  read <file> <title-or-pos>             Read subtree content"
    printfn "  search <pattern> [-d dir]              Search org files for pattern"
    printfn "  archive <file> <title-or-pos>          Archive subtree to .org_archive"
    printfn "  links <file> [-d dir]                  List links with resolution"
    printfn "  export <file> --to <format>            Export via pandoc"
    printfn ""
    printfn "Agenda Commands:"
    printfn "  agenda [today]                        Scheduled + deadlines + overdue for today"
    printfn "  agenda week                           Next 7 days"
    printfn "  agenda todo [--state STATE]            All TODO items, optionally filtered"
    printfn "  agenda --tag TAG                       Filter any view by tag"
    printfn ""
    printfn "Roam Commands:"
    printfn "  roam sync [--force]                    Sync database with files"
    printfn "  roam node list                         List all nodes"
    printfn "  roam node get <node-id>                Get a node by ID"
    printfn "  roam node find <title-or-alias>        Find a node by title or alias"
    printfn "  roam node create <title> [options]     Create a new node"
    printfn "    -t, --tags <tag>                     Add tag (can be repeated)"
    printfn "    -a, --aliases <alias>                Add alias (can be repeated)"
    printfn "    -r, --refs <ref>                     Add ref (can be repeated)"
    printfn "    --parent <file>                      Parent file for headline node"
    printfn "  roam node read <node-id>               Read node file content"
    printfn "  roam backlinks <node-id>               Get backlinks to a node"
    printfn "  roam tag list                          List all tags"
    printfn "  roam tag find <tag>                    Find nodes by tag"
    printfn "  roam link add <src-file> <src-id> <tgt-id> [--description <desc>]"
    printfn "                                         Add a link between nodes"
    printfn "  roam alias add <file> <node-id> <alias>"
    printfn "                                         Add an alias"
    printfn "  roam alias remove <file> <node-id> <alias>"
    printfn "                                         Remove an alias"
    printfn "  roam ref add <file> <node-id> <ref>    Add a reference"
    printfn "  roam ref remove <file> <node-id> <ref> Remove a reference"
    printfn ""
    printfn "Roam Options:"
    printfn "  --db <path>             Database path (default: <directory>/.org-roam.db)"

let printAgendaDayText (date: DateTime) (items: Agenda.AgendaItem list) =
    let dayItems = items |> List.filter (fun i -> i.Date = date.Date)
    if not (List.isEmpty dayItems) then
        printfn "%s %s" (date.ToString("yyyy-MM-dd")) (date.ToString("ddd"))
        for item in dayItems do
            printfn "%s  %s" (formatAgendaItemText item) (Path.GetFileName(item.File))

let handleAgenda (config: OrgConfig) (opts: Map<string, string list>) (isJson: bool) (rest: string list) =
    let files = resolveFiles opts
    let tagFilter = Map.tryFind "tag" opts |> Option.bind List.tryHead
    let applyTagFilter items =
        match tagFilter with
        | Some t -> Agenda.filterByTag t items
        | None -> items

    match rest with
    | [] | "today" :: _ ->
        let items = Agenda.collectDatedItems config files
        let today = DateTime.Today
        let tomorrow = today.AddDays(1.0)
        let todayItems = Agenda.filterByDateRange today tomorrow items
        let overdue = Agenda.filterOverdue config today items
        let combined =
            (overdue @ todayItems)
            |> List.distinctBy (fun i -> i.Headline.Position, i.File)
            |> applyTagFilter
            |> List.sortBy (fun i -> i.Date)
        if isJson then
            printfn "%s" (JsonOutput.ok (JsonOutput.jsonArray (combined |> List.map formatAgendaItemJson)))
        else
            if List.isEmpty combined then
                printfn "No agenda items for today."
            else
                let overdueOnly = combined |> List.filter (fun i -> i.Date < today)
                let todayOnly = combined |> List.filter (fun i -> i.Date >= today && i.Date < tomorrow)
                if not (List.isEmpty overdueOnly) then
                    printfn "Overdue:"
                    for item in overdueOnly do
                        printfn "%s  %s" (formatAgendaItemText item) (Path.GetFileName(item.File))
                    printfn ""
                printAgendaDayText today todayOnly
        0

    | "week" :: _ ->
        let items = Agenda.collectDatedItems config files
        let today = DateTime.Today
        let weekEnd = today.AddDays(7.0)
        let weekItems = Agenda.filterByDateRange today weekEnd items
        let overdue = Agenda.filterOverdue config today items
        let combined =
            (overdue @ weekItems)
            |> List.distinctBy (fun i -> i.Headline.Position, i.File)
            |> applyTagFilter
            |> List.sortBy (fun i -> i.Date)
        if isJson then
            printfn "%s" (JsonOutput.ok (JsonOutput.jsonArray (combined |> List.map formatAgendaItemJson)))
        else
            if List.isEmpty combined then
                printfn "No agenda items for this week."
            else
                let overdueOnly = combined |> List.filter (fun i -> i.Date < today)
                let weekOnly = combined |> List.filter (fun i -> i.Date >= today)
                if not (List.isEmpty overdueOnly) then
                    printfn "Overdue:"
                    for item in overdueOnly do
                        printfn "%s  %s" (formatAgendaItemText item) (Path.GetFileName(item.File))
                    printfn ""
                let dates =
                    weekOnly
                    |> List.map (fun i -> i.Date)
                    |> List.distinct
                    |> List.sort
                for date in dates do
                    printAgendaDayText date weekOnly
                    printfn ""
        0

    | "todo" :: _ ->
        let todoItems = Agenda.collectTodoItems config files
        let stateFilter = Map.tryFind "state" opts |> Option.bind List.tryHead
        let filtered =
            match stateFilter with
            | Some s -> todoItems |> List.filter (fun i -> i.Headline.TodoKeyword = Some s)
            | None -> todoItems
        let filtered = applyTagFilter filtered
        if isJson then
            printfn "%s" (JsonOutput.ok (JsonOutput.jsonArray (filtered |> List.map formatAgendaItemJson)))
        else
            if List.isEmpty filtered then
                printfn "No TODO items found."
            else
                for item in filtered do
                    let todo = item.Headline.TodoKeyword |> Option.defaultValue ""
                    let priority =
                        match item.Headline.Priority with
                        | Some (Priority c) -> sprintf " [#%c]" c
                        | None -> ""
                    let tags =
                        if List.isEmpty item.Headline.Tags then ""
                        else sprintf " :%s:" (String.Join(":", item.Headline.Tags))
                    printfn "  %s%s %s%s  %s" todo priority item.Headline.Title tags (Path.GetFileName(item.File))
        0

    | sub :: _ ->
        eprintfn "Unknown agenda subcommand: %s" sub
        1

let handleHeadlines (config: OrgConfig) (opts: Map<string, string list>) (isJson: bool) =
    let files = resolveFiles opts
    let matches = Headlines.collectHeadlines files
    let filtered =
        matches
        |> fun m ->
            match Map.tryFind "todo" opts |> Option.bind List.tryHead with
            | Some s -> Headlines.filterByTodo s m
            | None -> m
        |> fun m ->
            match Map.tryFind "tag" opts |> Option.bind List.tryHead with
            | Some t when config.TagInheritance ->
                let docs = files |> List.map (fun f -> (f, Document.parseFile f))
                Headlines.filterByTagWithInheritance config docs t m
            | Some t -> Headlines.filterByTag t m
            | None -> m
        |> fun m ->
            match Map.tryFind "level" opts |> Option.bind List.tryHead with
            | Some l -> Headlines.filterByLevel (int l) m
            | None -> m
        |> fun m ->
            match Map.tryFind "property" opts |> Option.bind List.tryHead with
            | Some kv ->
                match kv.IndexOf('=') with
                | -1 -> m
                | i -> Headlines.filterByProperty (kv.Substring(0, i)) (kv.Substring(i + 1)) m
            | None -> m
    if isJson then
        let json =
            filtered
            |> List.map (fun m ->
                let obj = JsonObject()
                obj["title"] <- JsonValue.Create(m.Headline.Title)
                obj["todo"] <- JsonValue.Create(m.Headline.TodoKeyword |> Option.defaultValue "")
                obj["level"] <- JsonValue.Create(m.Headline.Level)
                obj["tags"] <- JsonOutput.jsonArray (m.Headline.Tags |> List.map (fun t -> JsonValue.Create(t) :> JsonNode))
                obj["file"] <- JsonValue.Create(m.File)
                obj["pos"] <- JsonValue.Create(m.Headline.Position)
                obj["path"] <- JsonOutput.jsonArray (m.OutlinePath |> List.map (fun p -> JsonValue.Create(p) :> JsonNode))
                obj :> JsonNode)
        printfn "%s" (JsonOutput.ok (JsonOutput.jsonArray json))
    else
        for m in filtered do
            let todo = m.Headline.TodoKeyword |> Option.map (fun t -> t + " ") |> Option.defaultValue ""
            let tags =
                if List.isEmpty m.Headline.Tags then ""
                else sprintf " :%s:" (String.Join(":", m.Headline.Tags))
            let pathStr =
                if List.isEmpty m.OutlinePath then ""
                else sprintf " [%s]" (String.Join(" > ", m.OutlinePath))
            printfn "%d  %s%s%s%s  %s" m.Headline.Position (String.replicate m.Headline.Level "*") (sprintf " %s%s" todo m.Headline.Title) tags pathStr (Path.GetFileName m.File)
    0

let handleRoam (opts: Map<string, string list>) (isJson: bool) (roamRest: string list) =
    OrgCli.RoamCommands.handleRoam printError opts isJson roamRest printUsage getOpt getOptAll

[<EntryPoint>]
let main args =
    let opts, positional = parseArgs args

    let format = getOpt opts "format" (Some "f") "text"
    let isJson = format = "json"
    let isDryRun = Map.containsKey "dry-run" opts
    let isQuiet = Map.containsKey "quiet" opts || Map.containsKey "q" opts
    let config = loadConfig opts

    if Map.containsKey "version" opts || List.contains "--version" positional then
        printfn "org 0.1.0"
        0
    elif List.isEmpty positional || positional.[0] = "help" || positional.[0] = "--help" || positional.[0] = "-h" then
        printUsage()
        0
    else
        try
            match positional with
            | "agenda" :: rest when hasHelpFlag opts rest ->
                printCommandHelp "agenda"
                0
            | "agenda" :: rest ->
                handleAgenda config opts isJson rest

            | "headlines" :: rest when hasHelpFlag opts rest ->
                printCommandHelp "headlines"
                0
            | "headlines" :: _ ->
                handleHeadlines config opts isJson

            | "add" :: rest when hasHelpFlag opts rest ->
                printCommandHelp "add"
                0
            | "add" :: file :: title :: _ ->
                let todoState = Map.tryFind "todo" opts |> Option.bind List.tryHead
                let priority = Map.tryFind "priority" opts |> Option.bind List.tryHead |> Option.map (fun s -> s.[0])
                let tags = Map.tryFind "tag" opts |> Option.defaultValue []
                let scheduled = Map.tryFind "scheduled" opts |> Option.bind List.tryHead |> Option.map Utils.parseDate
                let deadline = Map.tryFind "deadline" opts |> Option.bind List.tryHead |> Option.map Utils.parseDate
                let under = Map.tryFind "under" opts |> Option.bind List.tryHead
                let content = if File.Exists(file) then File.ReadAllText(file) else ""
                let printAdded (result: string) =
                    File.WriteAllText(file, result)
                    if isJson then
                        match Headlines.resolveHeadlinePos result title with
                        | Ok pos ->
                            let state = HeadlineEdit.extractState result pos
                            printfn "%s" (JsonOutput.ok (JsonOutput.formatHeadlineState state))
                        | Error _ ->
                            printfn "%s" (JsonOutput.ok (JsonValue.Create("Headline added")))
                    else
                        if not isQuiet then printfn "Headline added"
                    0
                match under with
                | Some parentId ->
                    match Headlines.resolveHeadlinePos content parentId with
                    | Ok pos ->
                        let result = Mutations.addHeadlineUnder content pos title todoState priority tags scheduled deadline
                        printAdded result
                    | Error e -> printError isJson e
                | None ->
                    let result = Mutations.addHeadline content title 1 todoState priority tags scheduled deadline
                    printAdded result
            | "add" :: _ ->
                eprintfn "Error: 'add' requires <file> and <title> arguments."
                printCommandHelp "add"
                1

            | "todo" :: rest when hasHelpFlag opts rest ->
                printCommandHelp "todo"
                0
            | "todo" :: file :: identifier :: state :: _ ->
                let newState = if state = "" then None else Some state
                executeMutation file identifier isJson isDryRun isQuiet "TODO state updated" (fun c p ->
                    let fileCfg = mergeFileConfig config c
                    Mutations.setTodoState fileCfg c p newState DateTime.Now)
            | "todo" :: _ ->
                eprintfn "Error: 'todo' requires <file>, <headline>, and <state> arguments."
                printCommandHelp "todo"
                1

            | "priority" :: rest when hasHelpFlag opts rest ->
                printCommandHelp "priority"
                0
            | "priority" :: file :: identifier :: pri :: _ ->
                let priority = if pri = "" then None else Some pri.[0]
                executeMutation file identifier isJson isDryRun isQuiet "Priority updated" (fun c p ->
                    Mutations.setPriority c p priority)
            | "priority" :: _ ->
                eprintfn "Error: 'priority' requires <file>, <headline>, and <priority> arguments."
                printCommandHelp "priority"
                1

            | "tag" :: rest when hasHelpFlag opts rest ->
                printCommandHelp "tag"
                0
            | "tag" :: "add" :: file :: identifier :: tag :: _ ->
                executeMutation file identifier isJson isDryRun isQuiet "Tag added" (fun c p ->
                    Mutations.addTag c p tag)

            | "tag" :: "remove" :: file :: identifier :: tag :: _ ->
                executeMutation file identifier isJson isDryRun isQuiet "Tag removed" (fun c p ->
                    Mutations.removeTag c p tag)
            | "tag" :: _ ->
                eprintfn "Error: 'tag' requires add|remove <file> <headline> <tag>."
                printCommandHelp "tag"
                1

            | "property" :: rest when hasHelpFlag opts rest ->
                printCommandHelp "property"
                0
            | "property" :: "set" :: file :: identifier :: key :: value :: _ ->
                executeMutation file identifier isJson isDryRun isQuiet "Property set" (fun c p ->
                    Mutations.setProperty c p key value)

            | "property" :: "remove" :: file :: identifier :: key :: _ ->
                executeMutation file identifier isJson isDryRun isQuiet "Property removed" (fun c p ->
                    Mutations.removeProperty c p key)
            | "property" :: _ ->
                eprintfn "Error: 'property' requires set|remove <file> <headline> <key> [<value>]."
                printCommandHelp "property"
                1

            | "schedule" :: rest when hasHelpFlag opts rest ->
                printCommandHelp "schedule"
                0
            | "schedule" :: file :: identifier :: date :: _ ->
                let ts = if date = "" then None else Some (Utils.parseDate date)
                executeMutation file identifier isJson isDryRun isQuiet "Schedule updated" (fun c p ->
                    let fileCfg = mergeFileConfig config c
                    Mutations.setScheduled fileCfg c p ts DateTime.Now)
            | "schedule" :: _ ->
                eprintfn "Error: 'schedule' requires <file>, <headline>, and <date> arguments."
                printCommandHelp "schedule"
                1

            | "deadline" :: rest when hasHelpFlag opts rest ->
                printCommandHelp "deadline"
                0
            | "deadline" :: file :: identifier :: date :: _ ->
                let ts = if date = "" then None else Some (Utils.parseDate date)
                executeMutation file identifier isJson isDryRun isQuiet "Deadline updated" (fun c p ->
                    let fileCfg = mergeFileConfig config c
                    Mutations.setDeadline fileCfg c p ts DateTime.Now)
            | "deadline" :: _ ->
                eprintfn "Error: 'deadline' requires <file>, <headline>, and <date> arguments."
                printCommandHelp "deadline"
                1

            | "note" :: rest when hasHelpFlag opts rest ->
                printCommandHelp "note"
                0
            | "note" :: file :: identifier :: text :: _ ->
                executeMutation file identifier isJson isDryRun isQuiet "Note added" (fun c p ->
                    Mutations.addNote c p text DateTime.Now)
            | "note" :: _ ->
                eprintfn "Error: 'note' requires <file>, <headline>, and <text> arguments."
                printCommandHelp "note"
                1

            | "refile" :: rest when hasHelpFlag opts rest ->
                printCommandHelp "refile"
                0
            | "refile" :: srcFile :: srcId :: tgtFile :: rest ->
                if not (File.Exists srcFile) then
                    printError isJson { Type = CliErrorType.FileNotFound; Message = sprintf "File not found: %s" srcFile; Detail = None }
                elif not (File.Exists tgtFile) then
                    printError isJson { Type = CliErrorType.FileNotFound; Message = sprintf "File not found: %s" tgtFile; Detail = None }
                else
                let srcContent = File.ReadAllText(srcFile)
                let tgtContent = File.ReadAllText(tgtFile)
                match Headlines.resolveHeadlinePos srcContent srcId with
                | Error e -> printError isJson e
                | Ok srcPos ->
                    let tgtPosResult =
                        match rest with
                        | tgtId :: _ -> Headlines.resolveHeadlinePos tgtContent tgtId
                        | [] ->
                            let doc = Document.parse tgtContent
                            match doc.Headlines |> List.tryLast with
                            | Some h -> Ok h.Position
                            | None -> Ok 0L
                    match tgtPosResult with
                    | Error e -> printError isJson e
                    | Ok tgtPos ->
                        let sameFile = Path.GetFullPath(srcFile) = Path.GetFullPath(tgtFile)
                        let fileCfg = mergeFileConfig config srcContent
                        if sameFile then
                            let (result, _) = Mutations.refile fileCfg srcContent srcPos srcContent tgtPos true DateTime.Now
                            File.WriteAllText(srcFile, result)
                        else
                            let (newSrc, newTgt) = Mutations.refile fileCfg srcContent srcPos tgtContent tgtPos false DateTime.Now
                            File.WriteAllText(srcFile, newSrc)
                            File.WriteAllText(tgtFile, newTgt)
                        if isJson then
                            let obj = JsonObject()
                            obj["source_file"] <- JsonValue.Create(srcFile)
                            obj["target_file"] <- JsonValue.Create(tgtFile)
                            printfn "%s" (JsonOutput.ok obj)
                        else
                            if not isQuiet then printfn "Refile complete"
                        0
            | "refile" :: _ ->
                eprintfn "Error: 'refile' requires <src-file>, <src-headline>, and <tgt-file> arguments."
                printCommandHelp "refile"
                1

            | "archive" :: rest when hasHelpFlag opts rest ->
                printCommandHelp "archive"
                0
            | "archive" :: file :: identifier :: _ ->
                if not (File.Exists file) then
                    printError isJson { Type = CliErrorType.FileNotFound; Message = sprintf "File not found: %s" file; Detail = None }
                else
                let content = File.ReadAllText(file)
                match Headlines.resolveHeadlinePos content identifier with
                | Error e -> printError isJson e
                | Ok pos ->
                    let doc = Document.parse content
                    let matches = Headlines.collectHeadlinesFromDocs [(file, doc)]
                    let outlinePath =
                        matches
                        |> List.tryFind (fun m -> m.Headline.Position = pos)
                        |> Option.map (fun m -> m.OutlinePath)
                        |> Option.defaultValue []
                    let archiveFile = file + "_archive"
                    let archiveContent =
                        if File.Exists(archiveFile) then File.ReadAllText(archiveFile)
                        else ""
                    let (newSrc, newArchive) = Mutations.archive content pos archiveContent file outlinePath DateTime.Now
                    File.WriteAllText(file, newSrc)
                    File.WriteAllText(archiveFile, newArchive)
                    if isJson then
                        let obj = JsonObject()
                        obj["archive_file"] <- JsonValue.Create(archiveFile)
                        obj["source_file"] <- JsonValue.Create(file)
                        printfn "%s" (JsonOutput.ok obj)
                    else
                        if not isQuiet then printfn "Archived to %s" archiveFile
                    0
            | "archive" :: _ ->
                eprintfn "Error: 'archive' requires <file> and <headline> arguments."
                printCommandHelp "archive"
                1

            | "read" :: rest when hasHelpFlag opts rest ->
                printCommandHelp "read"
                0
            | "read" :: file :: identifier :: _ ->
                if not (File.Exists file) then
                    printError isJson { Type = CliErrorType.FileNotFound; Message = sprintf "File not found: %s" file; Detail = None }
                else
                let content = File.ReadAllText(file)
                match Headlines.resolveHeadlinePos content identifier with
                | Ok pos ->
                    let subtree = Subtree.extractSubtree content pos
                    printfn "%s" subtree
                    0
                | Error e -> printError isJson e
            | "read" :: _ ->
                eprintfn "Error: 'read' requires <file> and <headline> arguments."
                printCommandHelp "read"
                1

            | "search" :: rest when hasHelpFlag opts rest ->
                printCommandHelp "search"
                0
            | "search" :: pattern :: _ ->
                let files = resolveFiles opts
                match Search.search pattern files with
                | Result.Error msg ->
                    eprintfn "%s" msg
                    1
                | Result.Ok results ->
                    if isJson then
                        let json =
                            results
                            |> List.map (fun r ->
                                let obj = JsonObject()
                                obj["file"] <- JsonValue.Create(r.File)
                                obj["line"] <- JsonValue.Create(r.LineNumber)
                                obj["headline"] <-
                                    (match r.Headline with
                                     | Some h -> JsonValue.Create(h.Title) :> JsonNode
                                     | None -> null)
                                obj["match"] <- JsonValue.Create(r.MatchLine)
                                obj :> JsonNode)
                        printfn "%s" (JsonOutput.ok (JsonOutput.jsonArray json))
                    else
                        for r in results do
                            let headline =
                                match r.Headline with
                                | Some h -> h.Title
                                | None -> "(file level)"
                            printfn "%s:%d  [%s]  %s" (Path.GetFileName r.File) r.LineNumber headline (r.MatchLine.Trim())
                    0
            | "search" :: _ ->
                eprintfn "Error: 'search' requires a <pattern> argument."
                printCommandHelp "search"
                1

            | "clock" :: rest when hasHelpFlag opts rest ->
                printCommandHelp "clock"
                0
            | "clock" :: "in" :: file :: identifier :: _ ->
                executeMutation file identifier isJson isDryRun isQuiet "Clock started" (fun c p ->
                    Mutations.clockIn c p DateTime.Now)

            | "clock" :: "out" :: file :: identifier :: _ ->
                executeMutation file identifier isJson isDryRun isQuiet "Clock stopped" (fun c p ->
                    Mutations.clockOut c p DateTime.Now)

            | "clock" :: _ ->
                let files = resolveFiles opts
                let results = Clock.collectClockEntries files
                if isJson then
                    let json =
                        results
                        |> List.map (fun (h, f, entries) ->
                            let dur = Clock.totalDuration entries
                            let obj = JsonObject()
                            obj["headline"] <- JsonValue.Create(h.Title)
                            obj["file"] <- JsonValue.Create(f)
                            obj["entries"] <- JsonValue.Create(entries.Length)
                            obj["total"] <- JsonValue.Create(sprintf "%d:%02d" (int dur.TotalHours) dur.Minutes)
                            obj :> JsonNode)
                    printfn "%s" (JsonOutput.ok (JsonOutput.jsonArray json))
                else
                    let mutable grandTotal = TimeSpan.Zero
                    for (h, f, entries) in results do
                        let dur = Clock.totalDuration entries
                        grandTotal <- grandTotal.Add(dur)
                        printfn "%d:%02d  %s  %s" (int dur.TotalHours) dur.Minutes h.Title (Path.GetFileName f)
                    printfn ""
                    printfn "Total: %d:%02d" (int grandTotal.TotalHours) grandTotal.Minutes
                0

            | "links" :: rest when hasHelpFlag opts rest ->
                printCommandHelp "links"
                0
            | "links" :: file :: _ ->
                let files = resolveFiles opts
                let docs =
                    files
                    |> List.map (fun f -> (f, Document.parseFile f))
                let resolved = Links.resolveLinksInFile file docs
                if isJson then
                    let json =
                        resolved
                        |> List.map (fun r ->
                            let obj = JsonObject()
                            obj["source_file"] <- JsonValue.Create(file)
                            obj["source_pos"] <- JsonValue.Create(r.Link.Position)
                            obj["link_type"] <- JsonValue.Create(r.Link.LinkType)
                            obj["target"] <- JsonValue.Create(r.Link.Path)
                            obj["resolved_file"] <-
                                (match r.TargetFile with
                                 | Some f -> JsonValue.Create(f) :> JsonNode
                                 | None -> null)
                            obj["resolved_pos"] <-
                                (match r.TargetPos with
                                 | Some p -> JsonValue.Create(p) :> JsonNode
                                 | None -> null)
                            obj["resolved_title"] <-
                                (match r.TargetHeadline with
                                 | Some t -> JsonValue.Create(t) :> JsonNode
                                 | None -> null)
                            obj :> JsonNode)
                    printfn "%s" (JsonOutput.ok (JsonOutput.jsonArray json))
                else
                    for r in resolved do
                        let target =
                            match r.TargetFile, r.TargetHeadline with
                            | Some f, Some h -> sprintf "%s:%s \"%s\"" f (r.TargetPos |> Option.map string |> Option.defaultValue "0") h
                            | Some f, None -> f
                            | None, _ -> "(unresolved)"
                        printfn "%s:%d  [[%s:%s]] -> %s" file r.Link.Position r.Link.LinkType r.Link.Path target
                0
            | "links" :: _ ->
                eprintfn "Error: 'links' requires a <file> argument."
                printCommandHelp "links"
                1

            | "export" :: rest when hasHelpFlag opts rest ->
                printCommandHelp "export"
                0
            | "export" :: file :: _ ->
                let toFormat = getOpt opts "to" None "markdown"
                use proc = new System.Diagnostics.Process()
                proc.StartInfo.FileName <- "pandoc"
                proc.StartInfo.ArgumentList.Add("-f")
                proc.StartInfo.ArgumentList.Add("org")
                proc.StartInfo.ArgumentList.Add("-t")
                proc.StartInfo.ArgumentList.Add(toFormat)
                proc.StartInfo.ArgumentList.Add(file)
                proc.StartInfo.RedirectStandardOutput <- true
                proc.StartInfo.RedirectStandardError <- true
                proc.StartInfo.UseShellExecute <- false
                if proc.Start() then
                    let output = proc.StandardOutput.ReadToEnd()
                    let error = proc.StandardError.ReadToEnd()
                    proc.WaitForExit()
                    if proc.ExitCode = 0 then
                        printf "%s" output
                        0
                    else
                        eprintfn "pandoc error: %s" error
                        1
                else
                    eprintfn "Failed to start pandoc. Is it installed?"
                    1
            | "export" :: _ ->
                eprintfn "Error: 'export' requires a <file> argument."
                printCommandHelp "export"
                1

            | "batch" :: _ ->
                let input = System.Console.In.ReadToEnd()
                let files = resolveFiles opts
                let fileContents =
                    files
                    |> List.map (fun f -> f, File.ReadAllText(f))
                    |> Map.ofList
                let (results, newFiles) = BatchMode.executeBatch config input fileContents DateTime.Now
                if not isDryRun then
                    for kv in newFiles do
                        if Map.tryFind kv.Key fileContents <> Some kv.Value then
                            File.WriteAllText(kv.Key, kv.Value)
                printfn "%s" (JsonOutput.formatBatchResults results)
                0

            | "schema" :: _ ->
                printfn "%s" (JsonOutput.schema ())
                0

            | "completions" :: "bash" :: _ ->
                printfn """_org_completions() {
    local commands="agenda headlines add todo priority tag property schedule deadline note clock refile archive read search links export roam batch schema completions"
    local flags="--format --directory --files --config --log-done --deadline-warning-days --dry-run --quiet --version --help"
    if [ "${#COMP_WORDS[@]}" -eq 2 ]; then
        COMPREPLY=($(compgen -W "$commands $flags" -- "${COMP_WORDS[1]}"))
    fi
}
complete -F _org_completions org"""
                0

            | "completions" :: "zsh" :: _ ->
                printfn """#compdef org
_org() {
    local commands=(agenda headlines add todo priority tag property schedule deadline note clock refile archive read search links export roam batch schema completions)
    local flags=(--format --directory --files --config --log-done --deadline-warning-days --dry-run --quiet --version --help)
    _arguments '1:command:($commands)' '*:flags:($flags)'
}
compdef _org org"""
                0

            | "completions" :: "fish" :: _ ->
                printfn """set -l commands agenda headlines add todo priority tag property schedule deadline note clock refile archive read search links export roam batch schema completions
complete -c org -f -n '__fish_use_subcommand' -a "$commands"
complete -c org -l format -d 'Output format: text or json'
complete -c org -l directory -s d -d 'Base directory'
complete -c org -l files -d 'Explicit file list'
complete -c org -l config -d 'Config file path'
complete -c org -l log-done -d 'Log on done: none, time, note'
complete -c org -l deadline-warning-days -d 'Deadline warning days'
complete -c org -l dry-run -d 'Preview without writing'
complete -c org -l quiet -s q -d 'Suppress output'
complete -c org -l version -d 'Show version'
complete -c org -l help -d 'Show help'"""
                0

            | "completions" :: _ ->
                printfn "Usage: org completions bash|zsh|fish"
                0

            | "roam" :: rest when hasHelpFlag opts rest ->
                printCommandHelp "roam"
                0
            | "roam" :: roamRest ->
                handleRoam opts isJson roamRest

            | cmd :: _ ->
                eprintfn "Unknown command: %s" cmd
                printUsage()
                1

            | [] ->
                printUsage()
                0
        with
        | :? FileNotFoundException as ex ->
            printError isJson { Type = CliErrorType.FileNotFound; Message = sprintf "File not found: %s" ex.FileName; Detail = None }
        | :? DirectoryNotFoundException ->
            printError isJson { Type = CliErrorType.FileNotFound; Message = "Directory not found. Check --directory path."; Detail = None }
        | :? FormatException as ex ->
            printError isJson { Type = CliErrorType.InvalidArgs; Message = sprintf "Invalid format: %s" ex.Message; Detail = None }
        | ex ->
            if isJson then
                printfn "%s" (JsonOutput.error {
                    Type = CliErrorType.InternalError
                    Message = ex.Message
                    Detail = None
                })
            else
                eprintfn "Error: %s" ex.Message
            1
