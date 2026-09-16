module OrgCli.RoamCommands

open System
open System.IO
open System.Text.Json.Nodes
open OrgCli.Org
open OrgCli.Index

/// The extension projects roam semantics from core document snapshots. It owns no database.
let defaultDbPath directory =
    Path.Combine(directory, ".org-index.db")

type private Node =
    { Id: string
      File: string
      Title: string
      Position: int64
      Level: int
      Tags: string list
      Aliases: string list }

let private nodes (docs: (string * OrgDocument) list) =
    docs
    |> List.collect (fun (file, doc) ->
        let root =
            if OrgCli.RoamProperties.isRoamExcluded doc.FileProperties then
                []
            else
                Types.tryGetId doc.FileProperties
                |> Option.map (fun id ->
                    { Id = id
                      File = file
                      Title =
                        Types.tryGetTitle doc.Keywords
                        |> Option.defaultValue (Path.GetFileNameWithoutExtension file)
                      Position = -1L
                      Level = 0
                      Tags = Types.getFileTags doc.Keywords
                      Aliases = OrgCli.RoamProperties.getRoamAliases doc.FileProperties })
                |> Option.toList

        root
        @ (doc.Headlines
           |> List.choose (fun h ->
               if OrgCli.RoamProperties.isRoamExcluded h.Properties then
                   None
               else
                   Types.tryGetId h.Properties
                   |> Option.map (fun id ->
                       { Id = id
                         File = file
                         Title = h.Title
                         Position = h.Position
                         Level = h.Level
                         Tags = Headlines.computeInheritedTags (Config.load ()) doc h
                         Aliases = OrgCli.RoamProperties.getRoamAliases h.Properties }))))

let private jsonNode (n: Node) : JsonNode =
    let o = JsonObject()
    o["id"] <- JsonValue.Create n.Id
    o["file"] <- JsonValue.Create n.File
    o["title"] <- JsonValue.Create n.Title
    o["level"] <- JsonValue.Create n.Level
    o["tags"] <- JsonOutput.jsonArray (n.Tags |> List.map (fun x -> JsonValue.Create(x) :> JsonNode))
    o["aliases"] <- JsonOutput.jsonArray (n.Aliases |> List.map (fun x -> JsonValue.Create(x) :> JsonNode))
    o

let handleRoam printError (opts: Map<string, string list>) isJson args printUsage getOpt getOptAll resolveDirectory =
    let root = resolveDirectory opts |> Runtime.fullPath
    let dbPath = getOpt opts "db" None (defaultDbPath root)
    let dry = Map.containsKey "dry-run" opts

    let docs () =
        Workspace.documents dbPath (Utils.listOrgFiles root)

    let all () = docs () |> nodes

    let output data =
        printfn "%s" (if isJson then JsonOutput.ok data else data.ToJsonString())
        0

    let failure (text: string) =
        printError
            isJson
            { Type =
                (if text.StartsWith("Ambiguous") then
                     CliErrorType.InvalidArgs
                 else
                     CliErrorType.HeadlineNotFound)
              Message = text
              Detail = None }

    let find (value: string) =
        let id =
            if value.StartsWith("id:", StringComparison.Ordinal) then
                value.Substring 3
            else
                value

        match
            all ()
            |> List.filter (fun n -> n.Id = id || n.Title = value || List.contains value n.Aliases)
        with
        | [ n ] -> Ok n
        | [] -> Error("Node not found: " + value)
        | _ -> Error("Ambiguous node: " + value)

    let mutate path transform =
        let plan = Runtime.edit path (Runtime.readText path)
        let content = plan.Before |> Option.map Runtime.decode |> Option.defaultValue ""
        let updated = transform content

        if not dry then
            Runtime.commit
                [ { plan with
                      After = (Runtime.edit path updated).After } ]

            docs () |> ignore

        output (JsonValue.Create(if dry then "Preview: " + updated else "Updated") :> JsonNode)

    match args with
    | "sync" :: _ ->
        docs () |> ignore
        output (JsonValue.Create "Index refreshed")
    | "node" :: "list" :: _ -> output (JsonOutput.jsonArray (all () |> List.map jsonNode))
    | "node" :: ("get" | "find") :: value :: _ ->
        match find value with
        | Ok n -> output (jsonNode n)
        | Error e -> failure e
    | "node" :: "read" :: value :: _ ->
        match find value with
        | Error e -> failure e
        | Ok n ->
            let content = Runtime.readText n.File

            let text =
                if n.Level = 0 then
                    content
                else
                    match Headlines.resolveHeadlinePos content ("id:" + n.Id) with
                    | Ok p -> Subtree.extractSubtree content p
                    | Error e -> failwith e.Message

            if isJson then
                output (JsonValue.Create text)
            else
                printfn "%s" text
                0
    | "node" :: "create" :: title :: _ ->
        let id = Utils.generateId ()
        let tags = getOptAll opts "tags" (Some "t")
        let aliases = getOptAll opts "aliases" (Some "a")
        let refs = getOptAll opts "refs" (Some "r")
        let parent = Map.tryFind "parent" opts |> Option.bind List.tryHead

        let file =
            parent
            |> Option.defaultWith (fun () -> Path.Combine(root, Utils.slugify title + "-" + id + ".org"))

        let text =
            match parent with
            | None -> Writer.createFileNode id title tags aliases refs
            | Some _ ->
                Runtime.readText file
                + "\n"
                + Writer.createHeadlineNode 1 id title tags None None aliases refs None None

        if not dry then
            if parent.IsNone then
                Runtime.createDirectory root

            if parent.IsNone && Runtime.fileExists file then
                failwith "Node filename collision"

            Runtime.writeText (file, text)
            docs () |> ignore

        output (
            jsonNode
                { Id = id
                  File = file
                  Title = title
                  Position = -1L
                  Level = (if parent.IsSome then 1 else 0)
                  Tags = tags
                  Aliases = aliases }
        )
    | "backlinks" :: value :: _ ->
        match find value with
        | Error e -> failure e
        | Ok n ->
            let links =
                docs ()
                |> List.collect (fun (file, doc) ->
                    doc.Links
                    |> List.choose (fun (l, owner) ->
                        if l.LinkType <> "id" || l.Path <> n.Id then
                            None
                        else
                            let o = JsonObject()
                            o["source_id"] <- JsonOutput.jstr owner
                            o["source_file"] <- JsonValue.Create file
                            o["target_id"] <- JsonValue.Create n.Id
                            Some(o :> JsonNode)))

            output (JsonOutput.jsonArray links)
    | "tag" :: "list" :: _ ->
        output (
            JsonOutput.jsonArray (
                all ()
                |> List.collect (fun n -> n.Tags)
                |> List.distinct
                |> List.map (fun s -> JsonValue.Create(s) :> JsonNode)
            )
        )
    | "tag" :: "find" :: tag :: _ ->
        output (JsonOutput.jsonArray (all () |> List.filter (fun n -> List.contains tag n.Tags) |> List.map jsonNode))
    | "link" :: "add" :: file :: source :: target :: _ ->
        mutate file (fun content ->
            let doc = Document.parse content

            let link =
                Writer.formatLink
                    { LinkType = "id"
                      Path = target
                      Description = Map.tryFind "description" opts |> Option.bind List.tryHead
                      SearchOption = None
                      Position = 0 }

            if Workspace.isFileRoot doc source then
                Workspace.appendRoot content link
            else
                match Headlines.resolveHeadlinePos content ("id:" + source) with
                | Ok pos -> Mutations.appendBody content pos link
                | Error e -> failwith e.Message)
    | ("alias" | "ref" as kind) :: ("add" | "remove" as action) :: file :: id :: value :: _ ->
        mutate file (fun content ->
            let doc = Document.parse content

            let pos =
                if Workspace.isFileRoot doc id then
                    0
                else
                    match Headlines.resolveHeadlinePos content ("id:" + id) with
                    | Ok p -> int p
                    | Error e -> failwith e.Message

            let key = if kind = "alias" then "ROAM_ALIASES" else "ROAM_REFS"

            if action = "add" then
                Writer.addToMultiValueProperty content pos key value
            else
                Writer.removeFromMultiValueProperty content pos key value)
    | [] ->
        printUsage ()
        0
    | _ -> failure "Unknown or incomplete roam command"
