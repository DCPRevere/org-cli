module OrgCli.RoamCommands

open System
open System.IO
open System.Text.Json.Nodes
open OrgCli.Roam
open OrgCli.Org

/// Format a node for text output
let formatNodeText (node: RoamNode) =
    let tags =
        if List.isEmpty node.Tags then
            ""
        else
            sprintf " [%s]" (String.Join(", ", node.Tags))

    let aliases =
        if List.isEmpty node.Aliases then
            ""
        else
            sprintf " (aka: %s)" (String.Join(", ", node.Aliases))

    sprintf "%s: %s%s%s\n  File: %s\n  Level: %d" node.Id node.Title tags aliases node.File node.Level

/// Format a node for JSON output
let formatNodeJson (node: RoamNode) : JsonNode =
    let obj = JsonObject()
    obj["id"] <- JsonValue.Create(node.Id)
    obj["title"] <- JsonValue.Create(node.Title)
    obj["file"] <- JsonValue.Create(node.File)
    obj["level"] <- JsonValue.Create(node.Level)
    obj["tags"] <- JsonOutput.jsonArray (node.Tags |> List.map (fun t -> JsonValue.Create(t) :> JsonNode))
    obj["aliases"] <- JsonOutput.jsonArray (node.Aliases |> List.map (fun a -> JsonValue.Create(a) :> JsonNode))
    obj

let defaultDbPath () =
    let emacsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".emacs.d")

    Path.Combine(emacsDir, "org-roam.db")

let ensureDbDirectory (dbPath: string) =
    let dir = Path.GetDirectoryName(dbPath)

    if not (String.IsNullOrEmpty(dir)) && not (Directory.Exists(dir)) then
        Directory.CreateDirectory(dir) |> ignore

/// Open a database, initialize it, run f, then dispose.
let withDb dbPath (f: Database.OrgRoamDb -> int) : int =
    ensureDbDirectory dbPath
    use db = new Database.OrgRoamDb(dbPath)

    match db.Initialize() with
    | Error msg ->
        eprintfn "Database error: %s" msg
        1
    | Ok() -> f db

let handleRoam
    (printError: bool -> CliError -> int)
    (opts: Map<string, string list>)
    (isJson: bool)
    (roamRest: string list)
    (printUsage: unit -> unit)
    (getOpt: Map<string, string list> -> string -> string option -> string -> string)
    (getOptAll: Map<string, string list> -> string -> string option -> string list)
    =
    let roamDir = getOpt opts "directory" (Some "d") (Directory.GetCurrentDirectory())

    let dbPath =
        match Map.tryFind "db" opts with
        | Some(p :: _) -> p
        | _ -> defaultDbPath ()

    match roamRest with
    | "sync" :: rest ->
        let force =
            List.contains "--force" rest
            || Map.containsKey "force" opts
            || List.contains "-f" rest

        ensureDbDirectory dbPath
        use db = new Database.OrgRoamDb(dbPath)
        let errors = Sync.sync db roamDir force

        if isJson then
            let obj = JsonObject()

            obj["errors"] <-
                JsonOutput.jsonArray (
                    errors
                    |> List.map (fun (file, msg) ->
                        let e = JsonObject()
                        e["file"] <- JsonValue.Create(file)
                        e["message"] <- JsonValue.Create(msg)
                        e :> JsonNode)
                )

            printfn "%s" (JsonOutput.ok obj)
        else
            for (file, msg) in errors do
                eprintfn "Error processing %s: %s" file msg

            if List.isEmpty errors then
                printfn "Sync complete"
            else
                eprintfn "Sync completed with %d error(s)" errors.Length

        if List.isEmpty errors then 0 else 1

    | "node" :: "list" :: _ ->
        withDb dbPath (fun db ->
            let nodes = Sync.getAllNodes db

            if isJson then
                printfn "%s" (JsonOutput.ok (JsonOutput.jsonArray (nodes |> List.map formatNodeJson)))
            else
                for node in nodes do
                    printfn "%s\n" (formatNodeText node)

            0)

    | "node" :: "get" :: nodeId :: _ ->
        withDb dbPath (fun db ->
            match Sync.getNode db nodeId with
            | None ->
                printError
                    isJson
                    { Type = CliErrorType.HeadlineNotFound
                      Message = sprintf "Node not found: %s" nodeId
                      Detail = None }
            | Some node ->
                printfn
                    "%s"
                    (if isJson then
                         JsonOutput.ok (formatNodeJson node)
                     else
                         formatNodeText node)

                0)

    | "node" :: "find" :: searchTerm :: _ ->
        withDb dbPath (fun db ->
            match Sync.findNodeByTitleOrAlias db searchTerm with
            | None ->
                printError
                    isJson
                    { Type = CliErrorType.HeadlineNotFound
                      Message = sprintf "Node not found: %s" searchTerm
                      Detail = None }
            | Some node ->
                printfn
                    "%s"
                    (if isJson then
                         JsonOutput.ok (formatNodeJson node)
                     else
                         formatNodeText node)

                0)

    | "node" :: "create" :: title :: _ ->
        let tags = getOptAll opts "tags" (Some "t")
        let aliases = getOptAll opts "aliases" (Some "a")
        let refs = getOptAll opts "refs" (Some "r")

        let parentFile =
            match Map.tryFind "parent" opts with
            | Some(v :: _) -> Some v
            | _ -> None

        let options =
            { NodeOperations.defaultCreateOptions title with
                Tags = tags
                Aliases = aliases
                Refs = refs }

        let nodeId =
            match parentFile with
            | Some file -> NodeOperations.createHeadlineNode file options
            | None ->
                let filePath = NodeOperations.createFileNode roamDir options
                let doc = OrgCli.Org.Document.parseFile filePath

                match Types.tryGetId doc.FileProperties with
                | Some id -> id
                | None -> failwith "Bug: createFileNode did not write an ID property"

        withDb dbPath (fun db ->
            let errors = Sync.sync db roamDir false

            for (file, msg) in errors do
                eprintfn "Error syncing %s: %s" file msg

            match Sync.getNode db nodeId with
            | Some node ->
                if isJson then
                    printfn "%s" (JsonOutput.ok (formatNodeJson node))
                else
                    printfn "%s" (formatNodeText node)
            | None -> printfn "Created node: %s" nodeId

            0)

    | "node" :: "read" :: nodeId :: _ ->
        withDb dbPath (fun db ->
            match db.GetNode(nodeId) with
            | None ->
                printError
                    isJson
                    { Type = CliErrorType.HeadlineNotFound
                      Message = sprintf "Node not found: %s" nodeId
                      Detail = None }
            | Some node ->
                if File.Exists(node.File) then
                    printfn "%s" (File.ReadAllText(node.File))
                    0
                else
                    printError
                        isJson
                        { Type = CliErrorType.FileNotFound
                          Message = sprintf "File not found: %s" node.File
                          Detail = None })

    | "backlinks" :: nodeId :: _ ->
        withDb dbPath (fun db ->
            let backlinks = Sync.getBacklinks db nodeId

            if isJson then
                let json =
                    backlinks
                    |> List.map (fun bl ->
                        let obj = JsonObject()
                        obj["source_id"] <- JsonValue.Create(bl.SourceNode.Id)
                        obj["source_title"] <- JsonValue.Create(bl.SourceNode.Title)
                        obj["target_id"] <- JsonValue.Create(bl.TargetNodeId)
                        obj :> JsonNode)

                printfn "%s" (JsonOutput.ok (JsonOutput.jsonArray json))
            else if List.isEmpty backlinks then
                printfn "No backlinks found"
            else
                for bl in backlinks do
                    printfn "%s: %s -> %s" bl.SourceNode.Id bl.SourceNode.Title bl.TargetNodeId

            0)

    | "tag" :: "list" :: _ ->
        withDb dbPath (fun db ->
            let tags = db.GetAllTags()

            if isJson then
                printfn
                    "%s"
                    (JsonOutput.ok (JsonOutput.jsonArray (tags |> List.map (fun t -> JsonValue.Create(t) :> JsonNode))))
            else
                for tag in tags do
                    printfn "%s" tag

            0)

    | "tag" :: "find" :: tag :: _ ->
        withDb dbPath (fun db ->
            let nodes = Sync.findNodesByTag db tag

            if isJson then
                printfn "%s" (JsonOutput.ok (JsonOutput.jsonArray (nodes |> List.map formatNodeJson)))
            else
                for node in nodes do
                    printfn "%s\n" (formatNodeText node)

            0)

    | "link" :: "add" :: srcFile :: srcNode :: tgtNode :: _ ->
        let desc =
            match Map.tryFind "description" opts with
            | Some(v :: _) -> Some v
            | _ -> None

        withDb dbPath (fun db ->
            match NodeOperations.addLink db srcFile srcNode tgtNode desc with
            | Error msg ->
                printError
                    isJson
                    { Type = CliErrorType.HeadlineNotFound
                      Message = msg
                      Detail = None }
            | Ok() ->
                let errors = Sync.sync db roamDir false

                for (file, msg) in errors do
                    eprintfn "Error syncing %s: %s" file msg

                if isJson then
                    match Sync.getNode db srcNode with
                    | Some node -> printfn "%s" (JsonOutput.ok (formatNodeJson node))
                    | None -> printfn "%s" (JsonOutput.ok (JsonValue.Create("Link added")))
                else
                    printfn "Link added"

                0)

    | "alias" :: "add" :: filePath :: nodeId :: alias :: _ ->
        match NodeOperations.addAlias filePath nodeId alias with
        | Error msg ->
            printError
                isJson
                { Type = CliErrorType.HeadlineNotFound
                  Message = msg
                  Detail = None }
        | Ok() ->
            withDb dbPath (fun db ->
                let errors = Sync.sync db roamDir false

                for (file, msg) in errors do
                    eprintfn "Error syncing %s: %s" file msg

                if isJson then
                    match Sync.getNode db nodeId with
                    | Some node -> printfn "%s" (JsonOutput.ok (formatNodeJson node))
                    | None -> printfn "%s" (JsonOutput.ok (JsonValue.Create("Alias added")))
                else
                    printfn "Alias added"

                0)

    | "alias" :: "remove" :: filePath :: nodeId :: alias :: _ ->
        match NodeOperations.removeAlias filePath nodeId alias with
        | Error msg ->
            printError
                isJson
                { Type = CliErrorType.HeadlineNotFound
                  Message = msg
                  Detail = None }
        | Ok() ->
            withDb dbPath (fun db ->
                let errors = Sync.sync db roamDir false

                for (file, msg) in errors do
                    eprintfn "Error syncing %s: %s" file msg

                if isJson then
                    match Sync.getNode db nodeId with
                    | Some node -> printfn "%s" (JsonOutput.ok (formatNodeJson node))
                    | None -> printfn "%s" (JsonOutput.ok (JsonValue.Create("Alias removed")))
                else
                    printfn "Alias removed"

                0)

    | "ref" :: "add" :: filePath :: nodeId :: ref :: _ ->
        match NodeOperations.addRef filePath nodeId ref with
        | Error msg ->
            printError
                isJson
                { Type = CliErrorType.HeadlineNotFound
                  Message = msg
                  Detail = None }
        | Ok() ->
            withDb dbPath (fun db ->
                let errors = Sync.sync db roamDir false

                for (file, msg) in errors do
                    eprintfn "Error syncing %s: %s" file msg

                if isJson then
                    match Sync.getNode db nodeId with
                    | Some node -> printfn "%s" (JsonOutput.ok (formatNodeJson node))
                    | None -> printfn "%s" (JsonOutput.ok (JsonValue.Create("Ref added")))
                else
                    printfn "Ref added"

                0)

    | "ref" :: "remove" :: filePath :: nodeId :: ref :: _ ->
        match NodeOperations.removeRef filePath nodeId ref with
        | Error msg ->
            printError
                isJson
                { Type = CliErrorType.HeadlineNotFound
                  Message = msg
                  Detail = None }
        | Ok() ->
            withDb dbPath (fun db ->
                let errors = Sync.sync db roamDir false

                for (file, msg) in errors do
                    eprintfn "Error syncing %s: %s" file msg

                if isJson then
                    match Sync.getNode db nodeId with
                    | Some node -> printfn "%s" (JsonOutput.ok (formatNodeJson node))
                    | None -> printfn "%s" (JsonOutput.ok (JsonValue.Create("Ref removed")))
                else
                    printfn "Ref removed"

                0)

    | "node" :: "get" :: _ ->
        eprintfn "Error: 'roam node get' requires a <node-id> argument."
        1
    | "node" :: "find" :: _ ->
        eprintfn "Error: 'roam node find' requires a <title-or-alias> argument."
        1
    | "node" :: "create" :: _ ->
        eprintfn "Error: 'roam node create' requires a <title> argument."
        1
    | "node" :: "read" :: _ ->
        eprintfn "Error: 'roam node read' requires a <node-id> argument."
        1
    | "node" :: _ ->
        eprintfn "Usage: roam node list|get|find|create|read"
        1

    | "backlinks" :: _ ->
        eprintfn "Error: 'roam backlinks' requires a <node-id> argument."
        1

    | "tag" :: "find" :: _ ->
        eprintfn "Error: 'roam tag find' requires a <tag> argument."
        1
    | "tag" :: _ ->
        eprintfn "Usage: roam tag list|find <tag>"
        1

    | "link" :: "add" :: _ ->
        eprintfn "Error: 'roam link add' requires <src-file>, <src-id>, and <tgt-id> arguments."
        1
    | "link" :: _ ->
        eprintfn "Usage: roam link add <src-file> <src-id> <tgt-id>"
        1

    | "alias" :: "add" :: _ ->
        eprintfn "Error: 'roam alias add' requires <file>, <node-id>, and <alias> arguments."
        1
    | "alias" :: "remove" :: _ ->
        eprintfn "Error: 'roam alias remove' requires <file>, <node-id>, and <alias> arguments."
        1
    | "alias" :: _ ->
        eprintfn "Usage: roam alias add|remove <file> <node-id> <alias>"
        1

    | "ref" :: "add" :: _ ->
        eprintfn "Error: 'roam ref add' requires <file>, <node-id>, and <ref> arguments."
        1
    | "ref" :: "remove" :: _ ->
        eprintfn "Error: 'roam ref remove' requires <file>, <node-id>, and <ref> arguments."
        1
    | "ref" :: _ ->
        eprintfn "Usage: roam ref add|remove <file> <node-id> <ref>"
        1

    | sub :: _ ->
        eprintfn "Unknown roam subcommand: %s" sub
        1

    | [] ->
        printUsage ()
        0
