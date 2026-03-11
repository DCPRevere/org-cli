open System
open System.IO
open System.Reflection
open System.Text.Json.Nodes
open OrgCli.Org
open OrgCli.Index

/// Extract CUSTOM_ID from a parsed headline's properties.
let headlineCustomId (h: Headline) : string option =
    Types.tryGetProperty "CUSTOM_ID" h.Properties

/// Format CUSTOM_ID as a text prefix, e.g. " (k4t)".
let formatCustomIdText (h: Headline) : string =
    match headlineCustomId h with
    | Some id -> sprintf " (%s)" id
    | None -> ""

/// Set custom_id on a JSON object from a parsed headline.
let setCustomIdJson (obj: JsonObject) (h: Headline) =
    obj["custom_id"] <- JsonOutput.jstr (headlineCustomId h)

/// Print rows as an aligned table with uppercase headers.
/// columns: list of (header, extract) pairs.
/// rows: list of data items.
let printTable (columns: (string * ('a -> string)) list) (rows: 'a list) =
    if List.isEmpty rows then
        ()
    else
        let headers = columns |> List.map fst
        let extractors = columns |> List.map snd
        let data = rows |> List.map (fun r -> extractors |> List.map (fun f -> f r))

        let widths =
            List.init columns.Length (fun i ->
                let headerW = headers.[i].Length

                let maxDataW = data |> List.map (fun row -> row.[i].Length) |> List.max

                max headerW maxDataW)

        let gap = 3

        let formatRow (cells: string list) =
            cells
            |> List.mapi (fun i cell ->
                if i = cells.Length - 1 then
                    cell
                else
                    cell.PadRight(widths.[i] + gap))
            |> String.concat ""

        printfn "%s" (formatRow headers)

        for row in data do
            printfn "%s" (formatRow row)

/// Make an absolute path relative to a base directory.
let relativePath (baseDir: string) (filePath: string) =
    Path.GetRelativePath(baseDir, filePath)

/// Format an agenda item for text output
let formatAgendaItemText (item: Agenda.AgendaItem) =
    let typeStr =
        match item.Type with
        | Agenda.Scheduled -> "Scheduled:"
        | Agenda.Deadline -> "Deadline: "

    let todo = item.Headline.TodoKeyword |> Option.defaultValue ""

    let priority =
        match item.Headline.Priority with
        | Some(Priority c) -> sprintf " [#%c]" c
        | None -> ""

    let tags =
        if List.isEmpty item.Headline.Tags then
            ""
        else
            sprintf " :%s:" (String.Join(":", item.Headline.Tags))

    let cid = formatCustomIdText item.Headline
    sprintf "  %s %s%s %s%s%s" typeStr todo priority item.Headline.Title tags cid

/// Format an agenda item for JSON output
let formatAgendaItemJson (baseDir: string) (item: Agenda.AgendaItem) : JsonNode =
    let typeStr =
        match item.Type with
        | Agenda.Scheduled -> "scheduled"
        | Agenda.Deadline -> "deadline"

    let todo = item.Headline.TodoKeyword |> Option.defaultValue ""

    let priority =
        match item.Headline.Priority with
        | Some(Priority c) -> string c
        | None -> ""

    let obj = JsonObject()

    let dateFmt =
        if item.HasTime then
            item.Date.ToString("yyyy-MM-dd HH:mm")
        else
            item.Date.ToString("yyyy-MM-dd")

    obj["date"] <- JsonValue.Create(dateFmt)
    obj["type"] <- JsonValue.Create(typeStr)
    obj["todo"] <- JsonValue.Create(todo)
    obj["priority"] <- JsonValue.Create(priority)
    obj["title"] <- JsonValue.Create(item.Headline.Title)
    obj["tags"] <- JsonOutput.jsonArray (item.Headline.Tags |> List.map (fun t -> JsonValue.Create(t) :> JsonNode))
    obj["file"] <- JsonValue.Create(relativePath baseDir item.File)
    obj["level"] <- JsonValue.Create(item.Headline.Level)
    setCustomIdJson obj item.Headline
    obj

/// Parse command line arguments (supports repeated flags)
let parseArgs (args: string array) : Map<string, string list> * string list =
    let addOpt (opts: Map<string, string list>) (key: string) (value: string) =
        let existing = Map.tryFind key opts |> Option.defaultValue []
        Map.add key (existing @ [ value ]) opts

    let rec parse (args: string list) (opts: Map<string, string list>) (positional: string list) =
        match args with
        | [] -> opts, List.rev positional
        | "--" :: rest -> opts, List.rev positional @ rest
        | opt :: value :: rest when opt.StartsWith("--") && not (value.StartsWith("-")) ->
            parse rest (addOpt opts (opt.TrimStart('-')) value) positional
        | opt :: rest when opt.StartsWith("--") -> parse rest (addOpt opts (opt.TrimStart('-')) "true") positional
        | opt :: value :: rest when opt.StartsWith("-") && opt.Length = 2 && not (value.StartsWith("-")) ->
            parse rest (addOpt opts (opt.TrimStart('-')) value) positional
        | opt :: rest when opt.StartsWith("-") && opt.Length = 2 ->
            parse rest (addOpt opts (opt.TrimStart('-')) "true") positional
        | arg :: rest -> parse rest opts (arg :: positional)

    parse (Array.toList args) Map.empty []

let getOpt (opts: Map<string, string list>) (key: string) (altKey: string option) (defaultVal: string) =
    match Map.tryFind key opts with
    | Some(v :: _) -> v
    | _ ->
        match altKey with
        | Some alt ->
            match Map.tryFind alt opts with
            | Some(v :: _) -> v
            | _ -> defaultVal
        | None -> defaultVal

let getOptAll (opts: Map<string, string list>) (key: string) (altKey: string option) : string list =
    let primary = Map.tryFind key opts |> Option.defaultValue []

    let alt =
        altKey |> Option.bind (fun k -> Map.tryFind k opts) |> Option.defaultValue []

    primary @ alt

/// Read ORG_CLI_DIRECTORY env var, split by platform path separator, expand ~.
let loadDirectoriesFromEnv () : string list =
    match Environment.GetEnvironmentVariable("ORG_CLI_DIRECTORY") with
    | null
    | "" -> []
    | v ->
        v.Split(Path.PathSeparator)
        |> Array.map Utils.expandHome
        |> Array.toList

/// Read "directories" array from the org-cli config JSON, expand ~.
let loadDirectoriesFromConfig () : string list =
    let configPath = Utils.orgCliConfigFile ()

    if not (File.Exists(configPath)) then
        []
    else
        try
            let json = File.ReadAllText(configPath)
            use doc = System.Text.Json.JsonDocument.Parse(json)

            match doc.RootElement.TryGetProperty("directories") with
            | true, arr when arr.ValueKind = System.Text.Json.JsonValueKind.Array ->
                [ for item in arr.EnumerateArray() do
                      match item.GetString() with
                      | null -> ()
                      | s -> yield Utils.expandHome s ]
            | _ -> []
        with _ ->
            []

/// Resolve a single directory from CLI flags, env var, or config.json.
let resolveDirectory (opts: Map<string, string list>) : string =
    match Map.tryFind "directory" opts, Map.tryFind "d" opts with
    | Some(v :: _), _
    | _, Some(v :: _) -> v
    | _ ->
        match loadDirectoriesFromEnv () with
        | dir :: _ -> dir
        | [] ->
            match loadDirectoriesFromConfig () with
            | dir :: _ -> dir
            | [] -> Directory.GetCurrentDirectory()

let resolveFiles (opts: Map<string, string list>) : string list =
    match getOptAll opts "files" None with
    | _ :: _ as explicit -> explicit
    | [] ->
        match Map.tryFind "directory" opts, Map.tryFind "d" opts with
        | Some(_ :: _), _
        | _, Some(_ :: _) ->
            let dir = getOpt opts "directory" (Some "d") (Directory.GetCurrentDirectory())
            Utils.listOrgFiles dir
        | _ ->
            let dirs =
                match loadDirectoriesFromEnv () with
                | _ :: _ as envDirs -> envDirs
                | [] ->
                    match loadDirectoriesFromConfig () with
                    | _ :: _ as cfgDirs -> cfgDirs
                    | [] -> [ Directory.GetCurrentDirectory() ]

            dirs
            |> List.collect Utils.listOrgFiles
            |> List.map Path.GetFullPath
            |> List.distinct

/// Print a CliError in the appropriate format and return exit code 1.
let printError (isJson: bool) (e: CliError) : int =
    if isJson then
        printfn "%s" (JsonOutput.error e)
    else
        eprintfn "%s" e.Message

    1

/// Pure transform: resolve headline, apply mutation, return new content + position.
let applyMutation
    (content: string)
    (identifier: string)
    (transform: string -> int64 -> string)
    : Result<string * int64, CliError> =
    Headlines.resolveHeadlinePos content identifier
    |> Result.map (fun pos -> transform content pos, pos)

let resolveIndexDbPath (opts: Map<string, string list>) : string =
    let dir = resolveDirectory opts
    getOpt opts "db" None (Path.Combine(dir, ".org-index.db"))

let tryAutoSyncIndex (opts: Map<string, string list>) (filePaths: string list) =
    let dbPath = resolveIndexDbPath opts

    if File.Exists(dbPath) then
        try
            use db = new IndexDatabase.OrgIndexDb(dbPath)
            db.Initialize()

            for f in filePaths do
                IndexSync.syncFile db f
        with _ ->
            ()

let resolveRoamDbPath (opts: Map<string, string list>) =
    match Map.tryFind "db" opts with
    | Some(p :: _) -> p
    | _ -> OrgCli.RoamCommands.defaultDbPath ()

let tryAutoSyncRoam (opts: Map<string, string list>) (filePaths: string list) =
    let dbPath = resolveRoamDbPath opts

    if File.Exists(dbPath) then
        try
            let dir = filePaths |> List.head |> Path.GetDirectoryName
            use db = new OrgCli.Roam.Database.OrgRoamDb(dbPath)

            match db.Initialize() with
            | Error _ -> ()
            | Ok() ->
                for f in filePaths do
                    OrgCli.Roam.Sync.updateFile db dir f
        with _ ->
            ()

/// Determine if an argument looks like a file path (as opposed to a bare identifier).
let looksLikeFile (arg: string) : bool =
    arg.EndsWith(".org", StringComparison.OrdinalIgnoreCase)
    || arg.Contains('/')
    || arg.Contains('\\')
    || File.Exists(arg)

/// Look up file path for a CUSTOM_ID or other identifier via the index database.
let resolveFileFromIndex (opts: Map<string, string list>) (identifier: string) : Result<string, CliError> =
    let dbPath = resolveIndexDbPath opts

    if not (File.Exists(dbPath)) then
        Error
            { Type = CliErrorType.InvalidArgs
              Message = "No index database found. Use <file> <identifier> or run 'org index' first."
              Detail = None }
    else
        try
            use db = new IndexDatabase.OrgIndexDb(dbPath)
            db.Initialize()
            let dir = resolveDirectory opts
            IndexSync.syncDirectory db dir

            match db.FindByCustomId(identifier) with
            | Some(file, _) -> Ok file
            | None ->
                Error
                    { Type = CliErrorType.HeadlineNotFound
                      Message = sprintf "No headline found for identifier: %s" identifier
                      Detail = None }
        with ex ->
            Error
                { Type = CliErrorType.InternalError
                  Message = sprintf "Index lookup failed: %s" ex.Message
                  Detail = None }

/// Parse a date with optional --repeater and --delay flags from opts.
let parseTimestamp (opts: Map<string, string list>) (date: string) : Result<Timestamp option, CliError> =
    if date = "" then
        Ok None
    else
        let repeater = Map.tryFind "repeater" opts |> Option.bind List.tryHead
        let delay = Map.tryFind "delay" opts |> Option.bind List.tryHead

        match repeater, delay with
        | None, None -> Ok(Some(Utils.parseDate date))
        | _ ->
            match Utils.parseDateWithRepeat date repeater delay with
            | Ok ts -> Ok(Some ts)
            | Error msg ->
                Error
                    { Type = CliErrorType.InvalidArgs
                      Message = msg
                      Detail = None }

/// Read file, resolve headline, apply transform, optionally write, print message.
let executeMutation
    (opts: Map<string, string list>)
    (file: string)
    (identifier: string)
    (isJson: bool)
    (isDryRun: bool)
    (isQuiet: bool)
    (msg: string)
    (transform: string -> int64 -> string)
    : int =
    if not (File.Exists file) then
        printError
            isJson
            { Type = CliErrorType.FileNotFound
              Message = sprintf "File not found: %s" file
              Detail = None }
    else
        let content = File.ReadAllText(file)

        match applyMutation content identifier transform with
        | Ok(newContent, pos) ->
            if not isDryRun then
                File.WriteAllText(file, newContent)
                tryAutoSyncIndex opts [ file ]
                tryAutoSyncRoam opts [ file ]

            if isJson then
                let state = HeadlineEdit.extractState newContent pos

                let data =
                    if isDryRun then
                        JsonOutput.formatHeadlineStateDryRun state
                    else
                        JsonOutput.formatHeadlineState state

                printfn "%s" (JsonOutput.ok data)
            else if not isQuiet then
                if isDryRun then
                    printfn "%s (dry run)" msg
                else
                    printfn "%s" msg

            0
        | Error e -> printError isJson e

let hasHelpFlag (opts: Map<string, string list>) (args: string list) =
    List.contains "--help" args
    || List.contains "-h" args
    || Map.containsKey "help" opts
    || Map.containsKey "h" opts

let printCommandHelp (name: string) =
    match JsonOutput.findCommandDef name with
    | None -> eprintfn "Unknown command: %s" name
    | Some def ->
        printfn "org %s - %s" def.Name def.Description
        printfn ""
        printfn "Usage: org %s" def.Usage

        if not (List.isEmpty def.HelpArgs) then
            printfn ""

            for a in def.HelpArgs do
                printfn "  %s" a

/// Load config: --config path → file config → env vars → CLI overrides.
let loadConfig (opts: Map<string, string list>) : OrgConfig =
    let baseConfig =
        match Map.tryFind "config" opts with
        | Some(path :: _) ->
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

    printfn
        "  --db <path>             Database path (default: ~/.emacs.d/org-roam.db for roam, <dir>/.org-index.db for index)"

    printfn ""
    printfn "Org Commands:"
    printfn "  headlines [-d dir] [--todo STATE] [--tag TAG] [--level N] [--property K=V]"
    printfn "                                         List headlines with optional filters"
    printfn "  todo list [-d dir] [--state STATE] [--tag TAG] [--priority P] [--sort FIELD]"
    printfn "                                         List and filter TODO items"
    printfn "  todo set [<file>] <headline> <state>   Set TODO state (use \"\" to clear)"
    printfn "  add <file> <title> [options]           Add a new headline"
    printfn "    --todo STATE  --priority P  --tag TAG  --scheduled DATE  --deadline DATE"
    printfn "    --under <title-or-pos>               Insert as child of headline"
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
    printfn "  append <file> <title-or-pos> <text>    Append text to headline body"
    printfn "    --stdin                              Read text from stdin instead"
    printfn "  refile <src-file> <src-title-or-pos> <tgt-file> [<tgt-title-or-pos>]"
    printfn "                                         Refile subtree"
    printfn "  read <file> <title-or-pos>             Read subtree content"
    printfn "  search <pattern> [-d dir]              Search org files for pattern"
    printfn "  archive <file> <title-or-pos>          Archive subtree to .org_archive"
    printfn "  links <file> [-d dir]                  List links with resolution"
    printfn "  export <file> --to <format>            Export via pandoc"
    printfn ""
    printfn "Index Commands:"
    printfn "  index [-d dir] [--force]                Build or update the headline index"
    printfn "  fts <query> [-d dir]                    Full-text search via index"
    printfn "  custom-id assign [-d dir] [--dry-run]   Assign CUSTOM_ID to all headlines"
    printfn ""
    printfn "Agenda Commands:"
    printfn "  today                                 All TODOs due today or overdue"
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

let agendaTableColumns (baseDir: string) : (string * (Agenda.AgendaItem -> string)) list =
    [ "ID", (fun item -> headlineCustomId item.Headline |> Option.defaultValue "")
      "STATE", (fun item -> item.Headline.TodoKeyword |> Option.defaultValue "")
      "TYPE",
      (fun item ->
          match item.Type with
          | Agenda.Scheduled -> "Sched"
          | Agenda.Deadline -> "Dead")
      "DATE",
      (fun item ->
          if item.HasTime then
              item.Date.ToString("yyyy-MM-dd HH:mm")
          else
              item.Date.ToString("yyyy-MM-dd"))
      "TITLE", (fun item -> item.Headline.Title)
      "TAGS",
      (fun item ->
          if List.isEmpty item.Headline.Tags then
              ""
          else
              sprintf ":%s:" (String.Join(":", item.Headline.Tags)))
      "FILE", (fun item -> relativePath baseDir item.File) ]


let todayTableColumns (baseDir: string) : (string * (Agenda.AgendaItem -> string)) list =
    [ "ID", (fun item -> headlineCustomId item.Headline |> Option.defaultValue "")
      "STATE", (fun item -> item.Headline.TodoKeyword |> Option.defaultValue "")
      "DATE",
      (fun item ->
          if item.HasTime then
              item.Date.ToString("yyyy-MM-dd HH:mm")
          else
              item.Date.ToString("yyyy-MM-dd"))
      "TITLE", (fun item -> item.Headline.Title)
      "TAGS",
      (fun item ->
          if List.isEmpty item.Headline.Tags then
              ""
          else
              sprintf ":%s:" (String.Join(":", item.Headline.Tags)))
      "FILE", (fun item -> relativePath baseDir item.File) ]

let handleToday (config: OrgConfig) (opts: Map<string, string list>) (isJson: bool) =
    let baseDir = resolveDirectory opts
    let files = resolveFiles opts
    let tagFilter = Map.tryFind "tag" opts |> Option.bind List.tryHead
    let today = DateTime.Today
    let tomorrow = today.AddDays(1.0)

    let items = Agenda.collectDatedItems config files

    let due =
        items
        |> List.filter (fun i ->
            i.Date < tomorrow
            && not (Agenda.isDoneState config i.Headline.TodoKeyword)
            && i.Headline.TodoKeyword.IsSome)
        |> List.distinctBy (fun i -> i.Headline.Position, i.File)
        |> (fun items ->
            match tagFilter with
            | Some t -> Agenda.filterByTag t items
            | None -> items)
        |> List.sortBy (fun i -> i.Date.Date, (if i.HasTime then 0 else 1), i.Date.TimeOfDay)

    if isJson then
        printfn "%s" (JsonOutput.ok (JsonOutput.jsonArray (due |> List.map (formatAgendaItemJson baseDir))))
    else if List.isEmpty due then
        printfn "Nothing due today."
    else
        let overdue = due |> List.filter (fun i -> i.Date.Date < today)
        let todayItems = due |> List.filter (fun i -> i.Date.Date >= today)

        if not (List.isEmpty overdue) then
            printfn "Overdue:"
            printTable (todayTableColumns baseDir) overdue
            printfn ""

        if not (List.isEmpty todayItems) then
            printfn "Today:"
            printTable (todayTableColumns baseDir) todayItems

    0

let handleTodos (config: OrgConfig) (opts: Map<string, string list>) (isJson: bool) =
    let baseDir = resolveDirectory opts
    let files = resolveFiles opts
    let matches = Headlines.collectHeadlines files

    // Only headlines with a TODO keyword
    let todos = matches |> List.filter (fun m -> m.Headline.TodoKeyword.IsSome)

    // Apply filters
    let filtered =
        todos
        |> fun items ->
            match Map.tryFind "state" opts |> Option.bind List.tryHead with
            | Some s -> items |> List.filter (fun m -> m.Headline.TodoKeyword = Some s)
            | None -> items
        |> fun items ->
            let tags = Map.tryFind "tag" opts |> Option.defaultValue []

            match tags with
            | [] -> items
            | _ ->
                items
                |> List.filter (fun m -> tags |> List.exists (fun t -> List.contains t m.Headline.Tags))
        |> fun items ->
            match Map.tryFind "priority" opts |> Option.bind List.tryHead with
            | Some p ->
                let pChar = p.ToUpper().[0]

                items
                |> List.filter (fun m ->
                    match m.Headline.Priority with
                    | Some(Priority c) -> c = pChar
                    | None -> false)
            | None -> items
        |> fun items ->
            if Map.containsKey "scheduled" opts then
                items
                |> List.filter (fun m -> m.Headline.Planning |> Option.bind (fun p -> p.Scheduled) |> Option.isSome)
            else
                items
        |> fun items ->
            if Map.containsKey "unscheduled" opts then
                items
                |> List.filter (fun m -> m.Headline.Planning |> Option.bind (fun p -> p.Scheduled) |> Option.isNone)
            else
                items
        |> fun items ->
            if Map.containsKey "overdue" opts then
                let today = DateTime.Today

                items
                |> List.filter (fun m ->
                    let sched =
                        m.Headline.Planning
                        |> Option.bind (fun p -> p.Scheduled)
                        |> Option.map (fun ts -> ts.Date)

                    let dead =
                        m.Headline.Planning
                        |> Option.bind (fun p -> p.Deadline)
                        |> Option.map (fun ts -> ts.Date)

                    let earliest = [ sched; dead ] |> List.choose id |> List.sort |> List.tryHead

                    match earliest with
                    | Some d -> d < today
                    | None -> false)
            else
                items
        |> fun items ->
            match Map.tryFind "due-before" opts |> Option.bind List.tryHead with
            | Some dateStr ->
                let cutoff = (Utils.parseDate dateStr).Date

                items
                |> List.filter (fun m ->
                    let sched =
                        m.Headline.Planning
                        |> Option.bind (fun p -> p.Scheduled)
                        |> Option.map (fun ts -> ts.Date)

                    let dead =
                        m.Headline.Planning
                        |> Option.bind (fun p -> p.Deadline)
                        |> Option.map (fun ts -> ts.Date)

                    let earliest = [ sched; dead ] |> List.choose id |> List.sort |> List.tryHead

                    match earliest with
                    | Some d -> d <= cutoff
                    | None -> false)
            | None -> items
        |> fun items ->
            match Map.tryFind "due-after" opts |> Option.bind List.tryHead with
            | Some dateStr ->
                let cutoff = (Utils.parseDate dateStr).Date

                items
                |> List.filter (fun m ->
                    let sched =
                        m.Headline.Planning
                        |> Option.bind (fun p -> p.Scheduled)
                        |> Option.map (fun ts -> ts.Date)

                    let dead =
                        m.Headline.Planning
                        |> Option.bind (fun p -> p.Deadline)
                        |> Option.map (fun ts -> ts.Date)

                    let latest =
                        [ sched; dead ] |> List.choose id |> List.sortDescending |> List.tryHead

                    match latest with
                    | Some d -> d >= cutoff
                    | None -> false)
            | None -> items
        |> fun items ->
            match Map.tryFind "file" opts |> Option.bind List.tryHead with
            | Some pat ->
                items
                |> List.filter (fun m -> Path.GetFileName(m.File).Contains(pat, StringComparison.OrdinalIgnoreCase))
            | None -> items
        |> fun items ->
            match Map.tryFind "search" opts |> Option.bind List.tryHead with
            | Some text ->
                items
                |> List.filter (fun m -> m.Headline.Title.Contains(text, StringComparison.OrdinalIgnoreCase))
            | None -> items

    // Sort
    let sortField =
        Map.tryFind "sort" opts
        |> Option.bind List.tryHead
        |> Option.defaultValue "scheduled"

    let reverseSort = Map.containsKey "reverse" opts

    let getScheduledDate (m: Headlines.HeadlineMatch) =
        m.Headline.Planning
        |> Option.bind (fun p -> p.Scheduled)
        |> Option.map (fun ts -> ts.Date)

    let getDeadlineDate (m: Headlines.HeadlineMatch) =
        m.Headline.Planning
        |> Option.bind (fun p -> p.Deadline)
        |> Option.map (fun ts -> ts.Date)

    let sorted =
        match sortField with
        | "deadline" ->
            filtered
            |> List.sortBy (fun m ->
                let d = getDeadlineDate m |> Option.defaultValue DateTime.MaxValue
                (d, m.Headline.Title))
        | "priority" ->
            filtered
            |> List.sortBy (fun m ->
                let p =
                    match m.Headline.Priority with
                    | Some(Priority c) -> int c
                    | None -> int 'Z' + 1

                (p, m.Headline.Title))
        | "title" -> filtered |> List.sortBy (fun m -> m.Headline.Title)
        | "file" -> filtered |> List.sortBy (fun m -> Path.GetFileName(m.File), m.Headline.Title)
        | _ -> // "scheduled" (default)
            filtered
            |> List.sortBy (fun m ->
                let d = getScheduledDate m |> Option.defaultValue DateTime.MaxValue
                (d, m.Headline.Title))

    let sorted = if reverseSort then List.rev sorted else sorted

    if isJson then
        let json =
            sorted
            |> List.map (fun m ->
                let obj = JsonObject()
                obj["title"] <- JsonValue.Create(m.Headline.Title)
                obj["todo"] <- JsonValue.Create(m.Headline.TodoKeyword |> Option.defaultValue "")

                obj["priority"] <-
                    (match m.Headline.Priority with
                     | Some(Priority c) -> JsonValue.Create(string c) :> JsonNode
                     | None -> JsonValue.Create("") :> JsonNode)

                obj["tags"] <-
                    JsonOutput.jsonArray (m.Headline.Tags |> List.map (fun t -> JsonValue.Create(t) :> JsonNode))

                obj["file"] <- JsonValue.Create(relativePath baseDir m.File)
                obj["pos"] <- JsonValue.Create(m.Headline.Position)

                obj["scheduled"] <-
                    (getScheduledDate m
                     |> Option.map (fun d -> JsonValue.Create(d.ToString("yyyy-MM-dd")) :> JsonNode)
                     |> Option.defaultValue null)

                obj["deadline"] <-
                    (getDeadlineDate m
                     |> Option.map (fun d -> JsonValue.Create(d.ToString("yyyy-MM-dd")) :> JsonNode)
                     |> Option.defaultValue null)

                obj["level"] <- JsonValue.Create(m.Headline.Level)

                obj["path"] <-
                    JsonOutput.jsonArray (m.OutlinePath |> List.map (fun p -> JsonValue.Create(p) :> JsonNode))

                setCustomIdJson obj m.Headline
                obj :> JsonNode)

        printfn "%s" (JsonOutput.ok (JsonOutput.jsonArray json))
    else if List.isEmpty sorted then
        printfn "No TODO items found."
    else
        let todosTableColumns: (string * (Headlines.HeadlineMatch -> string)) list =
            [ "ID", (fun m -> headlineCustomId m.Headline |> Option.defaultValue "")
              "STATE", (fun m -> m.Headline.TodoKeyword |> Option.defaultValue "")
              "PRI",
              (fun m ->
                  match m.Headline.Priority with
                  | Some(Priority c) -> string c
                  | None -> "")
              "TITLE", (fun m -> m.Headline.Title)
              "SCHEDULED",
              (fun m ->
                  getScheduledDate m
                  |> Option.map (fun d -> d.ToString("yyyy-MM-dd"))
                  |> Option.defaultValue "")
              "DEADLINE",
              (fun m ->
                  getDeadlineDate m
                  |> Option.map (fun d -> d.ToString("yyyy-MM-dd"))
                  |> Option.defaultValue "")
              "TAGS",
              (fun m ->
                  if List.isEmpty m.Headline.Tags then
                      ""
                  else
                      sprintf ":%s:" (String.Join(":", m.Headline.Tags)))
              "FILE", (fun m -> relativePath baseDir m.File) ]

        printTable todosTableColumns sorted

    0

let handleAgenda (config: OrgConfig) (opts: Map<string, string list>) (isJson: bool) (rest: string list) =
    let baseDir = resolveDirectory opts
    let files = resolveFiles opts
    let tagFilter = Map.tryFind "tag" opts |> Option.bind List.tryHead

    let applyTagFilter items =
        match tagFilter with
        | Some t -> Agenda.filterByTag t items
        | None -> items

    match rest with
    | []
    | "today" :: _ ->
        let items = Agenda.collectDatedItems config files
        let today = DateTime.Today
        let tomorrow = today.AddDays(1.0)
        let todayItems = Agenda.filterByDateRange today tomorrow items
        let overdue = Agenda.filterOverdue config today items

        let combined =
            (overdue @ todayItems)
            |> List.distinctBy (fun i -> i.Headline.Position, i.File)
            |> applyTagFilter
            |> List.sortBy (fun i -> i.Date.Date, (if i.HasTime then 0 else 1), i.Date.TimeOfDay)

        if isJson then
            printfn "%s" (JsonOutput.ok (JsonOutput.jsonArray (combined |> List.map (formatAgendaItemJson baseDir))))
        else if List.isEmpty combined then
            printfn "No agenda items for today."
        else
            let overdueOnly = combined |> List.filter (fun i -> i.Date < today)

            let todayOnly =
                combined |> List.filter (fun i -> i.Date >= today && i.Date < tomorrow)

            if not (List.isEmpty overdueOnly) then
                printfn "Overdue:"
                printTable (agendaTableColumns baseDir) overdueOnly
                printfn ""

            if not (List.isEmpty todayOnly) then
                printfn "%s %s" (today.ToString("yyyy-MM-dd")) (today.ToString("ddd"))
                printTable (agendaTableColumns baseDir) todayOnly

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
            |> List.sortBy (fun i -> i.Date.Date, (if i.HasTime then 0 else 1), i.Date.TimeOfDay)

        if isJson then
            printfn "%s" (JsonOutput.ok (JsonOutput.jsonArray (combined |> List.map (formatAgendaItemJson baseDir))))
        else if List.isEmpty combined then
            printfn "No agenda items for this week."
        else
            let overdueOnly = combined |> List.filter (fun i -> i.Date < today)
            let weekOnly = combined |> List.filter (fun i -> i.Date >= today)

            if not (List.isEmpty overdueOnly) then
                printfn "Overdue:"
                printTable (agendaTableColumns baseDir) overdueOnly
                printfn ""

            let dates = weekOnly |> List.map (fun i -> i.Date) |> List.distinct |> List.sort

            for date in dates do
                let dayItems = weekOnly |> List.filter (fun i -> i.Date = date.Date)

                if not (List.isEmpty dayItems) then
                    printfn "%s %s" (date.ToString("yyyy-MM-dd")) (date.ToString("ddd"))
                    printTable (agendaTableColumns baseDir) dayItems
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
            printfn "%s" (JsonOutput.ok (JsonOutput.jsonArray (filtered |> List.map (formatAgendaItemJson baseDir))))
        else if List.isEmpty filtered then
            printfn "No TODO items found."
        else
            let todoTableColumns: (string * (Agenda.AgendaItem -> string)) list =
                [ "ID", (fun item -> headlineCustomId item.Headline |> Option.defaultValue "")
                  "STATE", (fun item -> item.Headline.TodoKeyword |> Option.defaultValue "")
                  "PRI",
                  (fun item ->
                      match item.Headline.Priority with
                      | Some(Priority c) -> string c
                      | None -> "")
                  "TITLE", (fun item -> item.Headline.Title)
                  "SCHEDULED",
                  (fun item ->
                      item.Headline.Planning
                      |> Option.bind (fun p -> p.Scheduled)
                      |> Option.map (fun ts -> ts.Date.ToString("yyyy-MM-dd"))
                      |> Option.defaultValue "")
                  "DEADLINE",
                  (fun item ->
                      item.Headline.Planning
                      |> Option.bind (fun p -> p.Deadline)
                      |> Option.map (fun ts -> ts.Date.ToString("yyyy-MM-dd"))
                      |> Option.defaultValue "")
                  "TAGS",
                  (fun item ->
                      if List.isEmpty item.Headline.Tags then
                          ""
                      else
                          sprintf ":%s:" (String.Join(":", item.Headline.Tags)))
                  "FILE", (fun item -> relativePath baseDir item.File) ]

            printTable todoTableColumns filtered

        0

    | sub :: _ ->
        eprintfn "Unknown agenda subcommand: %s" sub
        1

let handleHeadlines (config: OrgConfig) (opts: Map<string, string list>) (isJson: bool) =
    let baseDir = resolveDirectory opts
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

                obj["tags"] <-
                    JsonOutput.jsonArray (m.Headline.Tags |> List.map (fun t -> JsonValue.Create(t) :> JsonNode))

                obj["file"] <- JsonValue.Create(relativePath baseDir m.File)
                obj["pos"] <- JsonValue.Create(m.Headline.Position)

                obj["path"] <-
                    JsonOutput.jsonArray (m.OutlinePath |> List.map (fun p -> JsonValue.Create(p) :> JsonNode))

                setCustomIdJson obj m.Headline
                obj :> JsonNode)

        printfn "%s" (JsonOutput.ok (JsonOutput.jsonArray json))
    else
        let headlineTableColumns: (string * (Headlines.HeadlineMatch -> string)) list =
            [ "ID", (fun m -> headlineCustomId m.Headline |> Option.defaultValue (string m.Headline.Position))
              "LVL", (fun m -> string m.Headline.Level)
              "STATE", (fun m -> m.Headline.TodoKeyword |> Option.defaultValue "")
              "TITLE", (fun m -> m.Headline.Title)
              "TAGS",
              (fun m ->
                  if List.isEmpty m.Headline.Tags then
                      ""
                  else
                      sprintf ":%s:" (String.Join(":", m.Headline.Tags)))
              "PATH",
              (fun m ->
                  if List.isEmpty m.OutlinePath then
                      ""
                  else
                      String.Join(" > ", m.OutlinePath))
              "FILE", (fun m -> relativePath baseDir m.File) ]

        printTable headlineTableColumns filtered

    0

let handleCustomIdAssign (opts: Map<string, string list>) (isJson: bool) (isDryRun: bool) (isQuiet: bool) : int =
    let dir = resolveDirectory opts
    let dbPath = resolveIndexDbPath opts
    use db = new IndexDatabase.OrgIndexDb(dbPath)
    db.Initialize()
    IndexSync.syncDirectory db dir

    let files = Utils.listOrgFiles dir
    let mutable totalAssigned = 0
    let mutable totalSkipped = 0
    let sessionIds = System.Collections.Generic.HashSet<string>()

    let generateUniqueWithSession () =
        let count = db.CountCustomIds() + sessionIds.Count
        let mutable length = CustomId.recommendedLength count 0.01
        let mutable result = None

        while result.IsNone do
            let mutable tries = 0

            while tries < 10 && result.IsNone do
                let candidate = CustomId.generate length

                if not (db.CustomIdExists(candidate)) && not (sessionIds.Contains(candidate)) then
                    result <- Some candidate

                tries <- tries + 1

            if result.IsNone then
                length <- length + 1

        let id = result.Value
        sessionIds.Add(id) |> ignore
        id

    for file in files do
        let content = File.ReadAllText(file)
        let doc = Document.parse content

        let needsId =
            doc.Headlines
            |> List.filter (fun h -> Types.tryGetProperty "CUSTOM_ID" h.Properties |> Option.isNone)
            |> List.sortByDescending (fun h -> h.Position)

        totalSkipped <- totalSkipped + (doc.Headlines.Length - needsId.Length)

        if not (List.isEmpty needsId) then
            let mutable current = content

            for h in needsId do
                let customId = generateUniqueWithSession ()
                current <- Mutations.setProperty current h.Position "CUSTOM_ID" customId
                totalAssigned <- totalAssigned + 1

            if not isDryRun then
                File.WriteAllText(file, current)
                IndexSync.syncFile db file

    if isJson then
        let obj = System.Text.Json.Nodes.JsonObject()
        obj["assigned"] <- System.Text.Json.Nodes.JsonValue.Create(totalAssigned)
        obj["skipped"] <- System.Text.Json.Nodes.JsonValue.Create(totalSkipped)
        obj["files"] <- System.Text.Json.Nodes.JsonValue.Create(files.Length)
        printfn "%s" (JsonOutput.ok obj)
    else if not isQuiet then
        if isDryRun then
            eprintfn
                "Would assign %d CUSTOM_IDs across %d files (%d already have IDs) (dry run)"
                totalAssigned
                files.Length
                totalSkipped
        else
            eprintfn
                "Assigned %d CUSTOM_IDs across %d files (%d already had IDs)"
                totalAssigned
                files.Length
                totalSkipped

    0

let handleIndex (opts: Map<string, string list>) (isJson: bool) (isQuiet: bool) : int =
    let dir = resolveDirectory opts
    let dbPath = resolveIndexDbPath opts
    let force = Map.containsKey "force" opts
    use db = new IndexDatabase.OrgIndexDb(dbPath)
    db.Initialize()

    if not isQuiet then
        if force then
            eprintfn "Rebuilding index (force)..."
        else
            eprintfn "Updating index..."

    if force then
        IndexSync.syncDirectoryForce db dir
    else
        IndexSync.syncDirectory db dir

    let files = db.GetAllFiles()

    if isJson then
        let obj = JsonObject()
        obj["files"] <- JsonValue.Create(files.Length)
        obj["db"] <- JsonValue.Create(dbPath)
        printfn "%s" (JsonOutput.ok obj)
    else if not isQuiet then
        eprintfn "Indexed %d files -> %s" files.Length dbPath

    0

let handleFts (opts: Map<string, string list>) (isJson: bool) (query: string) : int =
    let dir = resolveDirectory opts
    let dbPath = resolveIndexDbPath opts

    if not (File.Exists(dbPath)) then
        printError
            isJson
            { Type = CliErrorType.InvalidArgs
              Message = sprintf "No index found at %s. Run 'org index' first." dbPath
              Detail = None }
    else
        let noSync = Map.containsKey "no-sync" opts
        use db = new IndexDatabase.OrgIndexDb(dbPath)
        db.Initialize()

        if not noSync then
            IndexSync.syncDirectory db dir

        let results =
            try
                Ok(db.SearchFts(query))
            with ex ->
                Error(sprintf "Invalid FTS query: %s" ex.Message)

        match results with
        | Error msg ->
            printError
                isJson
                { Type = CliErrorType.InvalidArgs
                  Message = msg
                  Detail = None }
        | Ok results ->
            if isJson then
                let json =
                    results
                    |> List.map (fun r ->
                        let obj = JsonObject()
                        obj["file"] <- JsonValue.Create(relativePath dir r.File)
                        obj["char_pos"] <- JsonValue.Create(r.CharPos)
                        obj["title"] <- JsonValue.Create(r.Title)

                        obj["outline_path"] <-
                            (match r.OutlinePath with
                             | Some p -> JsonValue.Create(p) :> JsonNode
                             | None -> null)

                        obj["context"] <-
                            (match r.Context with
                             | Some c -> JsonValue.Create(c) :> JsonNode
                             | None -> null)

                        obj["rank"] <- JsonValue.Create(r.Rank)
                        obj["custom_id"] <- JsonOutput.jstr r.CustomId
                        obj :> JsonNode)

                printfn "%s" (JsonOutput.ok (JsonOutput.jsonArray json))
            else if List.isEmpty results then
                printfn "No results."
            else
                let ftsTableColumns: (string * (FtsResult -> string)) list =
                    [ "ID", (fun (r: FtsResult) -> r.CustomId |> Option.defaultValue "")
                      "TITLE", (fun (r: FtsResult) -> r.Title)
                      "CONTEXT",
                      (fun (r: FtsResult) ->
                          match r.Context with
                          | Some c -> c.Replace("\n", " ").Trim()
                          | None -> "")
                      "FILE", (fun (r: FtsResult) -> relativePath dir r.File) ]

                printTable ftsTableColumns results

            0

let handleRoam (opts: Map<string, string list>) (isJson: bool) (roamRest: string list) =
    OrgCli.RoamCommands.handleRoam printError opts isJson roamRest printUsage getOpt getOptAll resolveDirectory

[<EntryPoint>]
let main args =
    let opts, positional = parseArgs args

    let format = getOpt opts "format" (Some "f") "text"
    let isJson = format = "json"
    let isDryRun = Map.containsKey "dry-run" opts
    let isQuiet = Map.containsKey "quiet" opts || Map.containsKey "q" opts
    let config = loadConfig opts

    if Map.containsKey "version" opts || List.contains "--version" positional then
        let ver =
            Assembly.GetEntryAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            |> Option.ofObj
            |> Option.map (fun a ->
                match a.InformationalVersion.IndexOf('+') with
                | -1 -> a.InformationalVersion
                | i -> a.InformationalVersion.Substring(0, i))
            |> Option.defaultValue (Assembly.GetEntryAssembly().GetName().Version.ToString())

        printfn "org %s" ver
        0
    elif
        List.isEmpty positional
        || positional.[0] = "help"
        || positional.[0] = "--help"
        || positional.[0] = "-h"
    then
        printUsage ()
        0
    else
        try
            match positional with
            | "today" :: rest when hasHelpFlag opts rest ->
                printCommandHelp "today"
                0
            | "today" :: _ -> handleToday config opts isJson

            | "agenda" :: rest when hasHelpFlag opts rest ->
                printCommandHelp "agenda"
                0
            | "agenda" :: rest -> handleAgenda config opts isJson rest

            | "headlines" :: rest when hasHelpFlag opts rest ->
                printCommandHelp "headlines"
                0
            | "headlines" :: _ -> handleHeadlines config opts isJson

            | "todos" :: rest when hasHelpFlag opts rest ->
                printCommandHelp "todo"
                0
            | "todos" :: _ -> handleTodos config opts isJson

            | "add" :: rest when hasHelpFlag opts rest ->
                printCommandHelp "add"
                0
            | "add" :: file :: title :: _ ->
                let todoState = Map.tryFind "todo" opts |> Option.bind List.tryHead

                let priority =
                    Map.tryFind "priority" opts
                    |> Option.bind List.tryHead
                    |> Option.map (fun s -> s.[0])

                let tags = Map.tryFind "tag" opts |> Option.defaultValue []

                let scheduled =
                    Map.tryFind "scheduled" opts
                    |> Option.bind List.tryHead
                    |> Option.map Utils.parseDate

                let deadline =
                    Map.tryFind "deadline" opts
                    |> Option.bind List.tryHead
                    |> Option.map Utils.parseDate

                let under = Map.tryFind "under" opts |> Option.bind List.tryHead
                let content = if File.Exists(file) then File.ReadAllText(file) else ""

                let stampCustomId (result: string) =
                    let dbPath = resolveIndexDbPath opts

                    if File.Exists(dbPath) then
                        try
                            use db = new IndexDatabase.OrgIndexDb(dbPath)
                            db.Initialize()
                            let customId = CustomIdService.generateUnique db

                            match Headlines.resolveHeadlinePos result title with
                            | Ok pos -> Mutations.setProperty result pos "CUSTOM_ID" customId
                            | Error _ -> result
                        with _ ->
                            result
                    else
                        result

                let printAdded (result: string) =
                    let result = stampCustomId result
                    File.WriteAllText(file, result)
                    tryAutoSyncIndex opts [ file ]
                    tryAutoSyncRoam opts [ file ]

                    if isJson then
                        match Headlines.resolveHeadlinePos result title with
                        | Ok pos ->
                            let state = HeadlineEdit.extractState result pos
                            printfn "%s" (JsonOutput.ok (JsonOutput.formatHeadlineState state))
                        | Error _ -> printfn "%s" (JsonOutput.ok (JsonValue.Create("Headline added")))
                    else if not isQuiet then
                        printfn "Headline added"

                    0

                match under with
                | Some parentId ->
                    match Headlines.resolveHeadlinePos content parentId with
                    | Ok pos ->
                        let result =
                            Mutations.addHeadlineUnder content pos title todoState priority tags scheduled deadline

                        printAdded result
                    | Error e -> printError isJson e
                | None ->
                    let result =
                        Mutations.addHeadline content title 1 todoState priority tags scheduled deadline

                    printAdded result
            | "add" :: _ ->
                eprintfn "Error: 'add' requires <file> and <title> arguments."
                printCommandHelp "add"
                1

            | "todo" :: rest when hasHelpFlag opts rest ->
                printCommandHelp "todo"
                0
            | "todo" :: "list" :: _ -> handleTodos config opts isJson
            | "todo" :: "set" :: file :: identifier :: state :: _ when looksLikeFile file ->
                let newState = if state = "" then None else Some state

                executeMutation opts file identifier isJson isDryRun isQuiet "TODO state updated" (fun c p ->
                    let fileCfg = mergeFileConfig config c
                    Mutations.setTodoState fileCfg c p newState DateTime.Now)
            | "todo" :: "set" :: identifier :: state :: _ ->
                let newState = if state = "" then None else Some state

                match resolveFileFromIndex opts identifier with
                | Ok file ->
                    executeMutation opts file identifier isJson isDryRun isQuiet "TODO state updated" (fun c p ->
                        let fileCfg = mergeFileConfig config c
                        Mutations.setTodoState fileCfg c p newState DateTime.Now)
                | Error e -> printError isJson e
            | "todo" :: "set" :: _ ->
                eprintfn "Error: 'todo set' requires <headline> and <state> arguments."
                printCommandHelp "todo"
                1
            | "todo" :: file :: identifier :: state :: _ when looksLikeFile file ->
                let newState = if state = "" then None else Some state

                executeMutation opts file identifier isJson isDryRun isQuiet "TODO state updated" (fun c p ->
                    let fileCfg = mergeFileConfig config c
                    Mutations.setTodoState fileCfg c p newState DateTime.Now)
            | "todo" :: identifier :: state :: _ ->
                let newState = if state = "" then None else Some state

                match resolveFileFromIndex opts identifier with
                | Ok file ->
                    executeMutation opts file identifier isJson isDryRun isQuiet "TODO state updated" (fun c p ->
                        let fileCfg = mergeFileConfig config c
                        Mutations.setTodoState fileCfg c p newState DateTime.Now)
                | Error e -> printError isJson e
            | "todo" :: _ ->
                eprintfn "Error: 'todo' requires a subcommand (list, set) or arguments (<headline> <state>)"
                printCommandHelp "todo"
                1

            | "priority" :: rest when hasHelpFlag opts rest ->
                printCommandHelp "priority"
                0
            | "priority" :: file :: identifier :: pri :: _ when looksLikeFile file ->
                let priority = if pri = "" then None else Some pri.[0]

                executeMutation opts file identifier isJson isDryRun isQuiet "Priority updated" (fun c p ->
                    Mutations.setPriority c p priority)
            | "priority" :: identifier :: pri :: _ ->
                let priority = if pri = "" then None else Some pri.[0]

                match resolveFileFromIndex opts identifier with
                | Ok file ->
                    executeMutation opts file identifier isJson isDryRun isQuiet "Priority updated" (fun c p ->
                        Mutations.setPriority c p priority)
                | Error e -> printError isJson e
            | "priority" :: _ ->
                eprintfn "Error: 'priority' requires <file>, <headline>, and <priority> arguments."
                printCommandHelp "priority"
                1

            | "tag" :: rest when hasHelpFlag opts rest ->
                printCommandHelp "tag"
                0
            | "tag" :: "add" :: file :: identifier :: tag :: _ when looksLikeFile file ->
                executeMutation opts file identifier isJson isDryRun isQuiet "Tag added" (fun c p ->
                    Mutations.addTag c p tag)
            | "tag" :: "add" :: identifier :: tag :: _ ->
                match resolveFileFromIndex opts identifier with
                | Ok file ->
                    executeMutation opts file identifier isJson isDryRun isQuiet "Tag added" (fun c p ->
                        Mutations.addTag c p tag)
                | Error e -> printError isJson e

            | "tag" :: "remove" :: file :: identifier :: tag :: _ when looksLikeFile file ->
                executeMutation opts file identifier isJson isDryRun isQuiet "Tag removed" (fun c p ->
                    Mutations.removeTag c p tag)
            | "tag" :: "remove" :: identifier :: tag :: _ ->
                match resolveFileFromIndex opts identifier with
                | Ok file ->
                    executeMutation opts file identifier isJson isDryRun isQuiet "Tag removed" (fun c p ->
                        Mutations.removeTag c p tag)
                | Error e -> printError isJson e
            | "tag" :: _ ->
                eprintfn "Error: 'tag' requires add|remove <file> <headline> <tag>."
                printCommandHelp "tag"
                1

            | "property" :: rest when hasHelpFlag opts rest ->
                printCommandHelp "property"
                0
            | "property" :: "set" :: file :: identifier :: key :: value :: _ when looksLikeFile file ->
                executeMutation opts file identifier isJson isDryRun isQuiet "Property set" (fun c p ->
                    Mutations.setProperty c p key value)
            | "property" :: "set" :: identifier :: key :: value :: _ ->
                match resolveFileFromIndex opts identifier with
                | Ok file ->
                    executeMutation opts file identifier isJson isDryRun isQuiet "Property set" (fun c p ->
                        Mutations.setProperty c p key value)
                | Error e -> printError isJson e

            | "property" :: "remove" :: file :: identifier :: key :: _ when looksLikeFile file ->
                executeMutation opts file identifier isJson isDryRun isQuiet "Property removed" (fun c p ->
                    Mutations.removeProperty c p key)
            | "property" :: "remove" :: identifier :: key :: _ ->
                match resolveFileFromIndex opts identifier with
                | Ok file ->
                    executeMutation opts file identifier isJson isDryRun isQuiet "Property removed" (fun c p ->
                        Mutations.removeProperty c p key)
                | Error e -> printError isJson e
            | "property" :: _ ->
                eprintfn "Error: 'property' requires set|remove <file> <headline> <key> [<value>]."
                printCommandHelp "property"
                1

            | "schedule" :: rest when hasHelpFlag opts rest ->
                printCommandHelp "schedule"
                0
            | "schedule" :: file :: identifier :: date :: _ when looksLikeFile file ->
                match parseTimestamp opts date with
                | Ok ts ->
                    executeMutation opts file identifier isJson isDryRun isQuiet "Schedule updated" (fun c p ->
                        let fileCfg = mergeFileConfig config c
                        Mutations.setScheduled fileCfg c p ts DateTime.Now)
                | Error e -> printError isJson e
            | "schedule" :: identifier :: date :: _ ->
                match parseTimestamp opts date with
                | Ok ts ->
                    match resolveFileFromIndex opts identifier with
                    | Ok file ->
                        executeMutation opts file identifier isJson isDryRun isQuiet "Schedule updated" (fun c p ->
                            let fileCfg = mergeFileConfig config c
                            Mutations.setScheduled fileCfg c p ts DateTime.Now)
                    | Error e -> printError isJson e
                | Error e -> printError isJson e
            | "schedule" :: _ ->
                eprintfn "Error: 'schedule' requires <file>, <headline>, and <date> arguments."
                printCommandHelp "schedule"
                1

            | "deadline" :: rest when hasHelpFlag opts rest ->
                printCommandHelp "deadline"
                0
            | "deadline" :: file :: identifier :: date :: _ when looksLikeFile file ->
                match parseTimestamp opts date with
                | Ok ts ->
                    executeMutation opts file identifier isJson isDryRun isQuiet "Deadline updated" (fun c p ->
                        let fileCfg = mergeFileConfig config c
                        Mutations.setDeadline fileCfg c p ts DateTime.Now)
                | Error e -> printError isJson e
            | "deadline" :: identifier :: date :: _ ->
                match parseTimestamp opts date with
                | Ok ts ->
                    match resolveFileFromIndex opts identifier with
                    | Ok file ->
                        executeMutation opts file identifier isJson isDryRun isQuiet "Deadline updated" (fun c p ->
                            let fileCfg = mergeFileConfig config c
                            Mutations.setDeadline fileCfg c p ts DateTime.Now)
                    | Error e -> printError isJson e
                | Error e -> printError isJson e
            | "deadline" :: _ ->
                eprintfn "Error: 'deadline' requires <file>, <headline>, and <date> arguments."
                printCommandHelp "deadline"
                1

            | "note" :: rest when hasHelpFlag opts rest ->
                printCommandHelp "note"
                0
            | "note" :: file :: identifier :: text :: _ when looksLikeFile file ->
                executeMutation opts file identifier isJson isDryRun isQuiet "Note added" (fun c p ->
                    Mutations.addNote c p text DateTime.Now)
            | "note" :: identifier :: text :: _ ->
                match resolveFileFromIndex opts identifier with
                | Ok file ->
                    executeMutation opts file identifier isJson isDryRun isQuiet "Note added" (fun c p ->
                        Mutations.addNote c p text DateTime.Now)
                | Error e -> printError isJson e
            | "note" :: _ ->
                eprintfn "Error: 'note' requires <file>, <headline>, and <text> arguments."
                printCommandHelp "note"
                1

            | "append" :: rest when hasHelpFlag opts rest ->
                printCommandHelp "append"
                0
            | "append" :: file :: identifier :: text :: _ when looksLikeFile file ->
                let actualText =
                    if Map.containsKey "stdin" opts then
                        Console.In.ReadToEnd()
                    else
                        text

                executeMutation opts file identifier isJson isDryRun isQuiet "Content appended" (fun c p ->
                    Mutations.appendBody c p actualText)
            | "append" :: file :: identifier :: _ when looksLikeFile file && Map.containsKey "stdin" opts ->
                let text = Console.In.ReadToEnd()

                executeMutation opts file identifier isJson isDryRun isQuiet "Content appended" (fun c p ->
                    Mutations.appendBody c p text)
            | "append" :: identifier :: text :: _ ->
                let actualText =
                    if Map.containsKey "stdin" opts then
                        Console.In.ReadToEnd()
                    else
                        text

                match resolveFileFromIndex opts identifier with
                | Ok file ->
                    executeMutation opts file identifier isJson isDryRun isQuiet "Content appended" (fun c p ->
                        Mutations.appendBody c p actualText)
                | Error e -> printError isJson e
            | "append" :: identifier :: _ when Map.containsKey "stdin" opts ->
                let text = Console.In.ReadToEnd()

                match resolveFileFromIndex opts identifier with
                | Ok file ->
                    executeMutation opts file identifier isJson isDryRun isQuiet "Content appended" (fun c p ->
                        Mutations.appendBody c p text)
                | Error e -> printError isJson e
            | "append" :: _ ->
                eprintfn "Error: 'append' requires <file>, <headline>, and <text> arguments."
                printCommandHelp "append"
                1

            | "refile" :: rest when hasHelpFlag opts rest ->
                printCommandHelp "refile"
                0
            | "refile" :: srcFile :: srcId :: tgtFile :: rest ->
                if not (File.Exists srcFile) then
                    printError
                        isJson
                        { Type = CliErrorType.FileNotFound
                          Message = sprintf "File not found: %s" srcFile
                          Detail = None }
                elif not (File.Exists tgtFile) then
                    printError
                        isJson
                        { Type = CliErrorType.FileNotFound
                          Message = sprintf "File not found: %s" tgtFile
                          Detail = None }
                else
                    let srcContent = File.ReadAllText(srcFile)
                    let tgtContent = File.ReadAllText(tgtFile)

                    match Headlines.resolveHeadlinePos srcContent srcId with
                    | Error e -> printError isJson e
                    | Ok srcPos ->
                        let tgtPosResult =
                            match rest with
                            | tgtId :: _ -> Some(Headlines.resolveHeadlinePos tgtContent tgtId)
                            | [] -> None

                        match tgtPosResult with
                        | Some(Error e) -> printError isJson e
                        | _ ->
                            let sameFile = Path.GetFullPath(srcFile) = Path.GetFullPath(tgtFile)
                            let fileCfg = mergeFileConfig config srcContent

                            match tgtPosResult with
                            | Some(Ok tgtPos) ->
                                if sameFile then
                                    let (result, _) =
                                        Mutations.refile fileCfg srcContent srcPos srcContent tgtPos true DateTime.Now

                                    File.WriteAllText(srcFile, result)
                                    tryAutoSyncIndex opts [ srcFile ]
                                    tryAutoSyncRoam opts [ srcFile ]
                                else
                                    let (newSrc, newTgt) =
                                        Mutations.refile fileCfg srcContent srcPos tgtContent tgtPos false DateTime.Now

                                    File.WriteAllText(srcFile, newSrc)
                                    File.WriteAllText(tgtFile, newTgt)
                                    tryAutoSyncIndex opts [ srcFile; tgtFile ]
                                    tryAutoSyncRoam opts [ srcFile; tgtFile ]
                            | _ ->
                                let subtree = Subtree.extractSubtree srcContent srcPos
                                let newSrc = Subtree.removeSubtree srcContent srcPos
                                let newTgt = Subtree.appendSubtree tgtContent (subtree + "\n")
                                File.WriteAllText(srcFile, newSrc)

                                if not sameFile then
                                    File.WriteAllText(tgtFile, newTgt)
                                else
                                    File.WriteAllText(srcFile, newTgt)

                                tryAutoSyncIndex opts (if sameFile then [ srcFile ] else [ srcFile; tgtFile ])
                                tryAutoSyncRoam opts (if sameFile then [ srcFile ] else [ srcFile; tgtFile ])

                            if isJson then
                                let obj = JsonObject()
                                obj["source_file"] <- JsonValue.Create(srcFile)
                                obj["target_file"] <- JsonValue.Create(tgtFile)
                                printfn "%s" (JsonOutput.ok obj)
                            else if not isQuiet then
                                printfn "Refile complete"

                            0
            | "refile" :: _ ->
                eprintfn "Error: 'refile' requires <src-file>, <src-headline>, and <tgt-file> arguments."
                printCommandHelp "refile"
                1

            | "archive" :: rest when hasHelpFlag opts rest ->
                printCommandHelp "archive"
                0
            | "archive" :: file :: identifier :: _ when looksLikeFile file ->
                if not (File.Exists file) then
                    printError
                        isJson
                        { Type = CliErrorType.FileNotFound
                          Message = sprintf "File not found: %s" file
                          Detail = None }
                else
                    let content = File.ReadAllText(file)

                    match Headlines.resolveHeadlinePos content identifier with
                    | Error e -> printError isJson e
                    | Ok pos ->
                        let doc = Document.parse content
                        let matches = Headlines.collectHeadlinesFromDocs [ (file, doc) ]

                        let outlinePath =
                            matches
                            |> List.tryFind (fun m -> m.Headline.Position = pos)
                            |> Option.map (fun m -> m.OutlinePath)
                            |> Option.defaultValue []

                        let archiveFile = file + "_archive"

                        let archiveContent =
                            if File.Exists(archiveFile) then
                                File.ReadAllText(archiveFile)
                            else
                                ""

                        let (newSrc, newArchive) =
                            Mutations.archive content pos archiveContent file outlinePath DateTime.Now

                        File.WriteAllText(file, newSrc)
                        File.WriteAllText(archiveFile, newArchive)
                        tryAutoSyncIndex opts [ file ]
                        tryAutoSyncRoam opts [ file ]

                        if isJson then
                            let obj = JsonObject()
                            obj["archive_file"] <- JsonValue.Create(archiveFile)
                            obj["source_file"] <- JsonValue.Create(file)
                            printfn "%s" (JsonOutput.ok obj)
                        else if not isQuiet then
                            printfn "Archived to %s" archiveFile

                        0
            | "archive" :: identifier :: _ ->
                match resolveFileFromIndex opts identifier with
                | Ok file ->
                    let content = File.ReadAllText(file)

                    match Headlines.resolveHeadlinePos content identifier with
                    | Error e -> printError isJson e
                    | Ok pos ->
                        let doc = Document.parse content
                        let matches = Headlines.collectHeadlinesFromDocs [ (file, doc) ]

                        let outlinePath =
                            matches
                            |> List.tryFind (fun m -> m.Headline.Position = pos)
                            |> Option.map (fun m -> m.OutlinePath)
                            |> Option.defaultValue []

                        let archiveFile = file + "_archive"

                        let archiveContent =
                            if File.Exists(archiveFile) then
                                File.ReadAllText(archiveFile)
                            else
                                ""

                        let (newSrc, newArchive) =
                            Mutations.archive content pos archiveContent file outlinePath DateTime.Now

                        File.WriteAllText(file, newSrc)
                        File.WriteAllText(archiveFile, newArchive)
                        tryAutoSyncIndex opts [ file ]
                        tryAutoSyncRoam opts [ file ]

                        if isJson then
                            let obj = JsonObject()
                            obj["archive_file"] <- JsonValue.Create(archiveFile)
                            obj["source_file"] <- JsonValue.Create(file)
                            printfn "%s" (JsonOutput.ok obj)
                        else if not isQuiet then
                            printfn "Archived to %s" archiveFile

                        0
                | Error e -> printError isJson e
            | "archive" :: _ ->
                eprintfn "Error: 'archive' requires <file> and <headline> arguments."
                printCommandHelp "archive"
                1

            | "read" :: rest when hasHelpFlag opts rest ->
                printCommandHelp "read"
                0
            | "read" :: file :: identifier :: _ when looksLikeFile file ->
                if not (File.Exists file) then
                    printError
                        isJson
                        { Type = CliErrorType.FileNotFound
                          Message = sprintf "File not found: %s" file
                          Detail = None }
                else
                    let content = File.ReadAllText(file)

                    match Headlines.resolveHeadlinePos content identifier with
                    | Ok pos ->
                        let subtree = Subtree.extractSubtree content pos
                        printfn "%s" subtree
                        0
                    | Error e -> printError isJson e
            | "read" :: identifier :: _ ->
                match resolveFileFromIndex opts identifier with
                | Ok file ->
                    let content = File.ReadAllText(file)

                    match Headlines.resolveHeadlinePos content identifier with
                    | Ok pos ->
                        let subtree = Subtree.extractSubtree content pos
                        printfn "%s" subtree
                        0
                    | Error e -> printError isJson e
                | Error e -> printError isJson e
            | "read" :: _ ->
                eprintfn "Error: 'read' requires <file> and <headline> arguments."
                printCommandHelp "read"
                1

            | "search" :: rest when hasHelpFlag opts rest ->
                printCommandHelp "search"
                0
            | "search" :: pattern :: _ ->
                let searchBaseDir = resolveDirectory opts
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
                                obj["file"] <- JsonValue.Create(relativePath searchBaseDir r.File)
                                obj["line"] <- JsonValue.Create(r.LineNumber)

                                obj["headline"] <-
                                    (match r.Headline with
                                     | Some h -> JsonValue.Create(h.Title) :> JsonNode
                                     | None -> null)

                                obj["custom_id"] <- JsonOutput.jstr (r.Headline |> Option.bind headlineCustomId)

                                obj["match"] <- JsonValue.Create(r.MatchLine)
                                obj :> JsonNode)

                        printfn "%s" (JsonOutput.ok (JsonOutput.jsonArray json))
                    else
                        let searchTableColumns: (string * (Search.SearchResult -> string)) list =
                            [ "ID", (fun r -> r.Headline |> Option.bind headlineCustomId |> Option.defaultValue "")
                              "LINE", (fun r -> string r.LineNumber)
                              "HEADLINE",
                              (fun r ->
                                  match r.Headline with
                                  | Some h -> h.Title
                                  | None -> "(file level)")
                              "MATCH", (fun r -> r.MatchLine.Trim())
                              "FILE", (fun r -> relativePath searchBaseDir r.File) ]

                        printTable searchTableColumns results

                    0
            | "search" :: _ ->
                eprintfn "Error: 'search' requires a <pattern> argument."
                printCommandHelp "search"
                1

            | "clock" :: rest when hasHelpFlag opts rest ->
                printCommandHelp "clock"
                0
            | "clock" :: "in" :: file :: identifier :: _ when looksLikeFile file ->
                executeMutation opts file identifier isJson isDryRun isQuiet "Clock started" (fun c p ->
                    Mutations.clockIn c p DateTime.Now)
            | "clock" :: "in" :: identifier :: _ ->
                match resolveFileFromIndex opts identifier with
                | Ok file ->
                    executeMutation opts file identifier isJson isDryRun isQuiet "Clock started" (fun c p ->
                        Mutations.clockIn c p DateTime.Now)
                | Error e -> printError isJson e

            | "clock" :: "out" :: file :: identifier :: _ when looksLikeFile file ->
                executeMutation opts file identifier isJson isDryRun isQuiet "Clock stopped" (fun c p ->
                    Mutations.clockOut c p DateTime.Now)
            | "clock" :: "out" :: identifier :: _ ->
                match resolveFileFromIndex opts identifier with
                | Ok file ->
                    executeMutation opts file identifier isJson isDryRun isQuiet "Clock stopped" (fun c p ->
                        Mutations.clockOut c p DateTime.Now)
                | Error e -> printError isJson e

            | "clock" :: _ ->
                let clockBaseDir = resolveDirectory opts
                let files = resolveFiles opts
                let results = Clock.collectClockEntries files

                if isJson then
                    let json =
                        results
                        |> List.map (fun (h, f, entries) ->
                            let dur = Clock.totalDuration entries
                            let obj = JsonObject()
                            obj["headline"] <- JsonValue.Create(h.Title)
                            obj["file"] <- JsonValue.Create(relativePath clockBaseDir f)
                            obj["entries"] <- JsonValue.Create(entries.Length)
                            obj["total"] <- JsonValue.Create(sprintf "%d:%02d" (int dur.TotalHours) dur.Minutes)
                            setCustomIdJson obj h
                            obj :> JsonNode)

                    printfn "%s" (JsonOutput.ok (JsonOutput.jsonArray json))
                else
                    let mutable grandTotal = TimeSpan.Zero

                    for (_, _, entries) in results do
                        grandTotal <- grandTotal.Add(Clock.totalDuration entries)

                    let clockTableColumns: (string * (Headline * string * ClockEntry list -> string)) list =
                        [ "ID", (fun (h, _, _) -> headlineCustomId h |> Option.defaultValue "")
                          "TIME",
                          (fun (_, _, entries) ->
                              let dur = Clock.totalDuration entries
                              sprintf "%d:%02d" (int dur.TotalHours) dur.Minutes)
                          "TITLE", (fun (h, _, _) -> h.Title)
                          "FILE", (fun (_, f, _) -> relativePath clockBaseDir f) ]

                    printTable clockTableColumns results
                    printfn ""
                    printfn "Total: %d:%02d" (int grandTotal.TotalHours) grandTotal.Minutes

                0

            | "links" :: rest when hasHelpFlag opts rest ->
                printCommandHelp "links"
                0
            | "links" :: file :: _ ->
                let files = resolveFiles opts
                let docs = files |> List.map (fun f -> (f, Document.parseFile f))
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
                            | Some f, Some h ->
                                sprintf "%s:%s \"%s\"" f (r.TargetPos |> Option.map string |> Option.defaultValue "0") h
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

            | "index" :: rest when hasHelpFlag opts rest ->
                printCommandHelp "index"
                0
            | "index" :: _ -> handleIndex opts isJson isQuiet

            | "fts" :: rest when hasHelpFlag opts rest ->
                printCommandHelp "fts"
                0
            | "fts" :: query :: _ -> handleFts opts isJson query
            | "fts" :: _ ->
                eprintfn "Error: 'fts' requires a <query> argument."
                printCommandHelp "fts"
                1

            | "custom-id" :: rest when hasHelpFlag opts rest ->
                printCommandHelp "custom-id"
                0
            | "custom-id" :: "assign" :: _ -> handleCustomIdAssign opts isJson isDryRun isQuiet
            | "custom-id" :: _ ->
                eprintfn "Error: 'custom-id' requires a subcommand. Usage: custom-id assign"
                printCommandHelp "custom-id"
                1

            | "batch" :: _ ->
                let input = System.Console.In.ReadToEnd()
                let files = resolveFiles opts
                let fileContents = files |> List.map (fun f -> f, File.ReadAllText(f)) |> Map.ofList

                let (results, newFiles) =
                    BatchMode.executeBatch config input fileContents DateTime.Now

                if not isDryRun then
                    let mutable dirtyFiles = []

                    for kv in newFiles do
                        if Map.tryFind kv.Key fileContents <> Some kv.Value then
                            File.WriteAllText(kv.Key, kv.Value)
                            dirtyFiles <- kv.Key :: dirtyFiles

                    if not (List.isEmpty dirtyFiles) then
                        tryAutoSyncIndex opts dirtyFiles
                        tryAutoSyncRoam opts dirtyFiles

                printfn "%s" (JsonOutput.formatBatchResults results)
                0

            | "schema" :: _ ->
                printfn "%s" (JsonOutput.schema ())
                0

            | "completions" :: "bash" :: _ ->
                printfn
                    """_org_completions() {
    local commands="today agenda headlines add todo priority tag property schedule deadline note clock refile archive read search links export index fts custom-id roam batch schema completions"
    local flags="--format --directory --files --config --log-done --deadline-warning-days --dry-run --quiet --version --help"
    if [ "${#COMP_WORDS[@]}" -eq 2 ]; then
        COMPREPLY=($(compgen -W "$commands $flags" -- "${COMP_WORDS[1]}"))
    fi
}
complete -F _org_completions org"""

                0

            | "completions" :: "zsh" :: _ ->
                printfn
                    """#compdef org
_org() {
    local commands=(today agenda headlines add todo priority tag property schedule deadline note clock refile archive read search links export index fts roam batch schema completions)
    local flags=(--format --directory --files --config --log-done --deadline-warning-days --dry-run --quiet --version --help)
    _arguments '1:command:($commands)' '*:flags:($flags)'
}
compdef _org org"""

                0

            | "completions" :: "fish" :: _ ->
                printfn
                    """set -l commands today agenda headlines add todo priority tag property schedule deadline note clock refile archive read search links export index fts roam batch schema completions
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
            | "roam" :: roamRest -> handleRoam opts isJson roamRest

            | cmd :: _ ->
                eprintfn "Unknown command: %s" cmd
                printUsage ()
                1

            | [] ->
                printUsage ()
                0
        with
        | :? FileNotFoundException as ex ->
            printError
                isJson
                { Type = CliErrorType.FileNotFound
                  Message = sprintf "File not found: %s" ex.FileName
                  Detail = None }
        | :? DirectoryNotFoundException ->
            printError
                isJson
                { Type = CliErrorType.FileNotFound
                  Message = "Directory not found. Check --directory path."
                  Detail = None }
        | :? FormatException as ex ->
            printError
                isJson
                { Type = CliErrorType.InvalidArgs
                  Message = sprintf "Invalid format: %s" ex.Message
                  Detail = None }
        | ex ->
            if isJson then
                printfn
                    "%s"
                    (JsonOutput.error
                        { Type = CliErrorType.InternalError
                          Message = ex.Message
                          Detail = None })
            else
                eprintfn "Error: %s" ex.Message

            1
