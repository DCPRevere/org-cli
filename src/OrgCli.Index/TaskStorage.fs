module OrgCli.Index.TaskStorage

open System
open System.IO
open System.Text
open System.Text.Json.Nodes
open OrgCli.Org

let private text (s: string) : JsonNode = JsonValue.Create s

let private rootPath (root: string) =
    let configured = Runtime.environment "XDG_CONFIG_HOME"

    let config =
        if String.IsNullOrWhiteSpace configured || not (Path.IsPathRooted configured) then
            Path.Combine(Runtime.home (), ".config")
        else
            configured

    Path.Combine(config, "org-cli", "workspaces", Runtime.hash (Encoding.UTF8.GetBytes root))

let path root name =
    Path.Combine(rootPath root, name + ".json")

let prepare root =
    Runtime.createDirectory (rootPath root)

    match Runtime.host () with
    | :? Runtime.PhysicalHost when not (OperatingSystem.IsWindows()) ->
        File.SetUnixFileMode(
            rootPath root,
            UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
        )
    | _ -> ()

let read root name =
    let p = path root name
    if Runtime.fileExists p then Runtime.readText p else "{}"

let settings (root: string) =
    let contents = read root "board"
    let result = JsonObject()
    result["settings"] <- JsonNode.Parse contents
    result["revision"] <- text (Runtime.hash (Encoding.UTF8.GetBytes contents))
    result["workspace"] <- text (Runtime.hash (Encoding.UTF8.GetBytes root))
    result["path"] <- text (path root "board")
    result :> JsonNode

/// A durable, single-step undo receipt. Never restore over an intervening file edit.
let commit root actor (changes: (string * string * string) list) =
    prepare root
    let receipt = JsonObject()
    receipt["token"] <- text (Guid.NewGuid().ToString("D"))
    receipt["actor"] <- text actor
    let entries = JsonArray()

    for file, before, after in changes do
        let entry = JsonObject()
        entry["file"] <- text (Path.GetRelativePath(root, file))
        entry["before"] <- text before
        entry["after"] <- text (Runtime.hash (Encoding.UTF8.GetBytes after))
        entries.Add entry

    receipt["changes"] <- entries

    let edits =
        changes
        |> List.map (fun (file, before, after) -> Runtime.editExpected file before after)

    Runtime.commit (edits @ [ Runtime.edit (path root "undo") (receipt.ToJsonString()) ])

let undoInfo root =
    let receipt = JsonNode.Parse(read root "undo")
    let result = JsonObject()

    if not (isNull receipt["token"]) then
        result["token"] <- receipt["token"].DeepClone()
        result["actor"] <- receipt["actor"].DeepClone()

    result :> JsonNode

let undo root token actor =
    let content = read root "undo"
    let receipt = JsonNode.Parse content

    if isNull receipt["token"] || receipt["token"].GetValue<string>() <> token then
        invalidOp "This undo is no longer the latest change"

    if receipt["actor"].GetValue<string>() <> actor then
        invalidOp "Only the actor who made the change can undo it"

    let edits =
        receipt["changes"].AsArray()
        |> Seq.map (fun entry ->
            let file = Path.GetFullPath(entry["file"].GetValue<string>(), root)
            let relative = Path.GetRelativePath(root, file)

            if
                Path.IsPathRooted relative
                || relative = ".."
                || relative.StartsWith(".." + string Path.DirectorySeparatorChar)
            then
                invalidOp "Invalid undo destination"

            match Runtime.host () with
            | :? Runtime.PhysicalHost ->
                let mutable candidate = file

                while candidate <> root do
                    if
                        (File.Exists candidate || Directory.Exists candidate)
                        && File.GetAttributes(candidate).HasFlag(FileAttributes.ReparsePoint)
                    then
                        invalidOp "Undo cannot follow a symbolic link"

                    candidate <- Path.GetDirectoryName candidate
            | _ -> ()

            if not (Runtime.fileExists file) then
                invalidOp "An affected file was deleted; undo cannot overwrite intervening edits"

            let current = Runtime.readText file

            if
                Runtime.hash (Encoding.UTF8.GetBytes current)
                <> entry["after"].GetValue<string>()
            then
                invalidOp "File changed after this operation; undo would overwrite intervening edits"

            Runtime.editExpected file current (entry["before"].GetValue<string>()))
        |> Seq.toList

    Runtime.commit (edits @ [ Runtime.editExpected (path root "undo") content "{}" ])
