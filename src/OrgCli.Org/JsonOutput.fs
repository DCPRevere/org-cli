module OrgCli.Org.JsonOutput

open System.Text.Json
open System.Text.Json.Nodes
open System.Text.Encodings.Web

let private serializerOptions =
    JsonSerializerOptions(Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

/// Serialize a JsonNode to string with standard options.
let toJsonString (node: JsonNode) : string =
    node.ToJsonString(serializerOptions)

/// Build a JsonArray from a sequence of nodes.
let jsonArray (items: JsonNode seq) : JsonNode =
    let arr = JsonArray()
    for item in items do arr.Add(item)
    arr

let formatErrorType (t: CliErrorType) : string =
    match t with
    | CliErrorType.HeadlineNotFound -> "headline_not_found"
    | CliErrorType.FileNotFound -> "file_not_found"
    | CliErrorType.ParseError -> "parse_error"
    | CliErrorType.InvalidArgs -> "invalid_args"
    | CliErrorType.InternalError -> "internal_error"

/// Wrap data in an ok envelope and serialize to string.
let ok (data: JsonNode) : string =
    let obj = JsonObject()
    obj["ok"] <- JsonValue.Create(true)
    obj["data"] <- data
    toJsonString obj

/// Build an error envelope as a JsonNode.
let private errorNode (err: CliError) : JsonNode =
    let errObj = JsonObject()
    errObj["type"] <- JsonValue.Create(formatErrorType err.Type)
    errObj["message"] <- JsonValue.Create(err.Message)
    errObj["detail"] <-
        (match err.Detail with
         | Some d -> JsonValue.Create(d) :> JsonNode
         | None -> null)
    let obj = JsonObject()
    obj["ok"] <- JsonValue.Create(false)
    obj["error"] <- errObj
    obj

/// Format a CliError as a JSON error envelope string.
let error (err: CliError) : string =
    toJsonString (errorNode err)

/// A positional argument in a command definition.
type ArgDef = { Name: string; Description: string; Required: bool }

/// A command definition used as the source of truth for both JSON schema and CLI help.
type CommandDef = {
    Name: string
    Description: string
    Usage: string
    Args: ArgDef list
    Flags: (string * string) list
    HelpArgs: string list
}

/// All global flags.
let globalFlagDefs : (string * string) list = [
    ("--format", "Output format: text or json")
    ("--directory", "Base directory")
    ("--files", "Explicit file list (repeatable)")
    ("--dry-run", "Preview mutation without writing")
    ("--quiet", "Suppress informational output")
    ("--version", "Show version")
]

/// Shared command definitions.
let commandDefs : CommandDef list = [
    { Name = "agenda"; Description = "View scheduled items and deadlines"
      Usage = "agenda [today|week|todo] [--tag TAG] [--state STATE]"
      Args = [{ Name = "subcommand"; Description = "today, week, or todo"; Required = false }]
      Flags = [("--tag", "Filter by tag"); ("--state", "Filter TODO items by state")]
      HelpArgs = ["today       Today's scheduled items, deadlines, and overdue (default)"
                  "week        Next 7 days"; "todo        All TODO items"
                  "--tag TAG   Filter by tag"; "--state ST  Filter TODO items by state"] }
    { Name = "headlines"; Description = "List and filter headlines"
      Usage = "headlines [--todo STATE] [--tag TAG] [--level N] [--property K=V]"
      Args = []
      Flags = [("--todo", "Filter by TODO state"); ("--tag", "Filter by tag"); ("--level", "Filter by level"); ("--property", "Filter by property K=V")]
      HelpArgs = ["--todo STATE      Filter by TODO state"; "--tag TAG         Filter by tag"
                  "--level N         Filter by headline level"; "--property K=V    Filter by property"] }
    { Name = "add"; Description = "Add a new headline"
      Usage = "add <file> <title> [--todo STATE] [--priority P] [--tag TAG] [--scheduled DATE] [--deadline DATE] [--under <id-or-title>]"
      Args = [{ Name = "file"; Description = "Target file"; Required = true }; { Name = "title"; Description = "Headline title"; Required = true }]
      Flags = [("--todo", "TODO state"); ("--priority", "Priority A-Z"); ("--tag", "Tag (repeatable)"); ("--scheduled", "Scheduled date yyyy-MM-dd"); ("--deadline", "Deadline date yyyy-MM-dd"); ("--under", "Parent headline id or title")]
      HelpArgs = ["--todo STATE      Set TODO state"; "--priority P      Set priority (A-Z)"
                  "--tag TAG         Add tag (repeatable)"; "--scheduled DATE  Set SCHEDULED (yyyy-MM-dd)"
                  "--deadline DATE   Set DEADLINE (yyyy-MM-dd)"; "--under ID        Insert as child of headline"] }
    { Name = "todo"; Description = "Set TODO state"
      Usage = "todo <file> <headline> <state>"
      Args = [{ Name = "file"; Description = "Target file"; Required = true }; { Name = "identifier"; Description = "Position, org-id, or title"; Required = true }; { Name = "state"; Description = "New TODO state or empty to clear"; Required = true }]
      Flags = []
      HelpArgs = ["<headline>  Position number, org-id, or exact title"; "<state>     TODO state (TODO, DONE, NEXT, etc.) or \"\" to clear"] }
    { Name = "priority"; Description = "Set or clear priority"
      Usage = "priority <file> <headline> <A-Z|\"\">"
      Args = [{ Name = "file"; Description = "Target file"; Required = true }; { Name = "identifier"; Description = "Position, org-id, or title"; Required = true }; { Name = "priority"; Description = "Priority A-Z or empty to clear"; Required = true }]
      Flags = []
      HelpArgs = ["<headline>  Position number, org-id, or exact title"] }
    { Name = "tag"; Description = "Add or remove a tag"
      Usage = "tag add|remove <file> <headline> <tag>"
      Args = [{ Name = "subcommand"; Description = "add or remove"; Required = true }; { Name = "file"; Description = "Target file"; Required = true }; { Name = "identifier"; Description = "Position, org-id, or title"; Required = true }; { Name = "tag"; Description = "Tag name"; Required = true }]
      Flags = []
      HelpArgs = ["<headline>  Position number, org-id, or exact title"] }
    { Name = "property"; Description = "Set or remove a property"
      Usage = "property set|remove <file> <headline> <key> [<value>]"
      Args = [{ Name = "subcommand"; Description = "set or remove"; Required = true }; { Name = "file"; Description = "Target file"; Required = true }; { Name = "identifier"; Description = "Position, org-id, or title"; Required = true }; { Name = "key"; Description = "Property key"; Required = true }; { Name = "value"; Description = "Property value (set only)"; Required = false }]
      Flags = []
      HelpArgs = ["<headline>  Position number, org-id, or exact title"] }
    { Name = "schedule"; Description = "Set SCHEDULED timestamp"
      Usage = "schedule <file> <headline> <date>"
      Args = [{ Name = "file"; Description = "Target file"; Required = true }; { Name = "identifier"; Description = "Position, org-id, or title"; Required = true }; { Name = "date"; Description = "Date yyyy-MM-dd or empty to clear"; Required = true }]
      Flags = []
      HelpArgs = ["<headline>  Position number, org-id, or exact title"; "<date>      yyyy-MM-dd or \"\" to clear"] }
    { Name = "deadline"; Description = "Set DEADLINE timestamp"
      Usage = "deadline <file> <headline> <date>"
      Args = [{ Name = "file"; Description = "Target file"; Required = true }; { Name = "identifier"; Description = "Position, org-id, or title"; Required = true }; { Name = "date"; Description = "Date yyyy-MM-dd or empty to clear"; Required = true }]
      Flags = []
      HelpArgs = ["<headline>  Position number, org-id, or exact title"; "<date>      yyyy-MM-dd or \"\" to clear"] }
    { Name = "note"; Description = "Add a note to the logbook"
      Usage = "note <file> <headline> <text>"
      Args = [{ Name = "file"; Description = "Target file"; Required = true }; { Name = "identifier"; Description = "Position, org-id, or title"; Required = true }; { Name = "text"; Description = "Note text"; Required = true }]
      Flags = []
      HelpArgs = ["<headline>  Position number, org-id, or exact title"] }
    { Name = "clock"; Description = "Clock time tracking"
      Usage = "clock in|out <file> <headline> | clock [report]"
      Args = [{ Name = "subcommand"; Description = "in, out, or report"; Required = true }]
      Flags = []
      HelpArgs = ["in <file> <headline>    Start clock"; "out <file> <headline>   Stop clock"; "[report] [-d dir]       Show clock report"] }
    { Name = "refile"; Description = "Move a subtree to another location"
      Usage = "refile <src-file> <src-headline> <tgt-file> [<tgt-headline>]"
      Args = [{ Name = "src-file"; Description = "Source file"; Required = true }; { Name = "src-identifier"; Description = "Source headline"; Required = true }; { Name = "tgt-file"; Description = "Target file"; Required = true }; { Name = "tgt-identifier"; Description = "Target headline"; Required = false }]
      Flags = []
      HelpArgs = ["<headline>  Position number, org-id, or exact title"] }
    { Name = "archive"; Description = "Archive a subtree to .org_archive"
      Usage = "archive <file> <headline>"
      Args = [{ Name = "file"; Description = "Target file"; Required = true }; { Name = "identifier"; Description = "Position, org-id, or title"; Required = true }]
      Flags = []
      HelpArgs = ["<headline>  Position number, org-id, or exact title"] }
    { Name = "read"; Description = "Read subtree content"
      Usage = "read <file> <headline>"
      Args = [{ Name = "file"; Description = "Target file"; Required = true }; { Name = "identifier"; Description = "Position, org-id, or title"; Required = true }]
      Flags = []
      HelpArgs = ["<headline>  Position number, org-id, or exact title"] }
    { Name = "search"; Description = "Search org files for a regex pattern"
      Usage = "search <pattern> [-d dir]"
      Args = [{ Name = "pattern"; Description = "Regex pattern"; Required = true }]
      Flags = []
      HelpArgs = [] }
    { Name = "links"; Description = "List and resolve links in a file"
      Usage = "links <file> [-d dir]"
      Args = [{ Name = "file"; Description = "Target file"; Required = true }]
      Flags = []
      HelpArgs = [] }
    { Name = "export"; Description = "Export via pandoc"
      Usage = "export <file> --to <format>"
      Args = [{ Name = "file"; Description = "Source file"; Required = true }]
      Flags = [("--to", "Output format")]
      HelpArgs = ["--to FORMAT   Output format (markdown, html, etc.)"] }
    { Name = "roam"; Description = "Org-roam database commands"
      Usage = "roam <subcommand>"
      Args = [{ Name = "subcommand"; Description = "sync, node, backlinks, tag, link, alias, ref"; Required = true }]
      Flags = [("--db", "Database path")]
      HelpArgs = ["sync [--force]                    Sync database with files"
                  "node list|get|find|create|read    Node operations"
                  "backlinks <node-id>               Get backlinks"
                  "tag list|find                     Tag operations"
                  "link add                          Add link"
                  "alias add|remove                  Alias operations"
                  "ref add|remove                    Ref operations"
                  "--db <path>                       Database path"] }
    { Name = "batch"; Description = "Execute multiple commands from JSON stdin"
      Usage = "batch"
      Args = []; Flags = []; HelpArgs = [] }
    { Name = "schema"; Description = "Output command schema as JSON"
      Usage = "schema"
      Args = []; Flags = []; HelpArgs = [] }
]

/// Find a command definition by name.
let findCommandDef (name: string) : CommandDef option =
    commandDefs |> List.tryFind (fun c -> c.Name = name)

/// Generate the schema JSON describing all commands.
let schema () : string =
    let cmdToJson (def: CommandDef) : JsonNode =
        let argsArr = JsonArray()
        for a in def.Args do
            let obj = JsonObject()
            obj["name"] <- JsonValue.Create(a.Name)
            obj["description"] <- JsonValue.Create(a.Description)
            obj["required"] <- JsonValue.Create(a.Required)
            argsArr.Add(obj)
        let flagsArr = JsonArray()
        for (n, d) in def.Flags do
            let f = JsonObject()
            f["name"] <- JsonValue.Create(n)
            f["description"] <- JsonValue.Create(d)
            flagsArr.Add(f)
        let c = JsonObject()
        c["name"] <- JsonValue.Create(def.Name)
        c["description"] <- JsonValue.Create(def.Description)
        c["args"] <- argsArr
        c["flags"] <- flagsArr
        c

    let commands = JsonArray()
    for def in commandDefs do
        commands.Add(cmdToJson def)

    let globalFlags = JsonArray()
    for (n, d) in globalFlagDefs do
        let f = JsonObject()
        f["name"] <- JsonValue.Create(n)
        f["description"] <- JsonValue.Create(d)
        globalFlags.Add(f)

    let root = JsonObject()
    root["version"] <- JsonValue.Create("0.1.0")
    root["commands"] <- commands
    root["global_flags"] <- globalFlags
    toJsonString root

/// Convert an optional string to a JsonNode (null for None).
let jstr (s: string option) : JsonNode =
    match s with Some v -> JsonValue.Create(v) :> JsonNode | None -> null

/// Convert an optional char to a JsonNode as a single-character string (null for None).
let jchar (c: char option) : JsonNode =
    match c with Some v -> JsonValue.Create(string v) :> JsonNode | None -> null

/// Format a HeadlineState as a JsonNode.
let formatHeadlineState (state: HeadlineEdit.HeadlineState) : JsonNode =
    let obj = JsonObject()
    obj["pos"] <- JsonValue.Create(state.Pos)
    obj["id"] <- jstr state.Id
    obj["title"] <- JsonValue.Create(state.Title)
    obj["todo"] <- jstr state.Todo
    obj["priority"] <- jchar state.Priority
    obj["tags"] <- jsonArray (state.Tags |> List.map (fun t -> JsonValue.Create(t) :> JsonNode))
    obj["scheduled"] <- jstr state.Scheduled
    obj["deadline"] <- jstr state.Deadline
    obj["closed"] <- jstr state.Closed
    obj

/// Format a HeadlineState with dry_run:true appended.
let formatHeadlineStateDryRun (state: HeadlineEdit.HeadlineState) : JsonNode =
    let obj = formatHeadlineState state :?> JsonObject
    obj["dry_run"] <- JsonValue.Create(true)
    obj

/// Format a single batch result item as a JsonNode (each is an envelope).
let formatBatchResult (r: Result<HeadlineEdit.HeadlineState, CliError>) : JsonNode =
    match r with
    | Ok state ->
        let obj = JsonObject()
        obj["ok"] <- JsonValue.Create(true)
        obj["data"] <- formatHeadlineState state
        obj
    | Error e -> errorNode e

/// Format a list of batch results as a single JSON envelope wrapping an array.
let formatBatchResults (results: Result<HeadlineEdit.HeadlineState, CliError> list) : string =
    ok (jsonArray (results |> List.map formatBatchResult))
