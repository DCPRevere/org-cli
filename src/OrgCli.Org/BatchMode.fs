module OrgCli.Org.BatchMode

open System
open System.Text.Json

type BatchCommand =
    { Command: string
      File: string
      Identifier: string
      Args: Map<string, string> }

let private parseCommands (json: string) : BatchCommand list =
    let doc = JsonDocument.Parse(json)
    let commands = doc.RootElement.GetProperty("commands")

    [ for i in 0 .. commands.GetArrayLength() - 1 ->
          let cmd = commands.[i]

          let args =
              if cmd.TryGetProperty("args") |> fst then
                  let argsEl = cmd.GetProperty("args")

                  [ for prop in argsEl.EnumerateObject() -> prop.Name, prop.Value.GetString() ]
                  |> Map.ofList
              else
                  Map.empty

          { Command = cmd.GetProperty("command").GetString()
            File = cmd.GetProperty("file").GetString()
            Identifier =
              if cmd.TryGetProperty("identifier") |> fst then
                  cmd.GetProperty("identifier").GetString()
              else
                  ""
            Args = args } ]

let private applyCommand
    (config: OrgConfig)
    (content: string)
    (cmd: BatchCommand)
    (now: DateTime)
    : Result<string * int64, CliError> =
    let resolve () =
        Headlines.resolveHeadlinePos content cmd.Identifier

    let getArg key =
        Map.tryFind key cmd.Args |> Option.defaultValue ""

    match cmd.Command with
    | "todo" ->
        resolve ()
        |> Result.map (fun pos ->
            let state = let s = getArg "state" in if s = "" then None else Some s
            let fileCfg = FileConfig.mergeFileConfig config (Document.parse content).Keywords
            Mutations.setTodoState fileCfg content pos state now, pos)

    | "tag-add" ->
        resolve ()
        |> Result.map (fun pos -> Mutations.addTag content pos (getArg "tag"), pos)

    | "tag-remove" ->
        resolve ()
        |> Result.map (fun pos -> Mutations.removeTag content pos (getArg "tag"), pos)

    | "priority" ->
        resolve ()
        |> Result.map (fun pos ->
            let pri = let s = getArg "priority" in if s = "" then None else Some s.[0]
            Mutations.setPriority content pos pri, pos)

    | "schedule" ->
        resolve ()
        |> Result.map (fun pos ->
            let ts = let s = getArg "date" in if s = "" then None else Some(Utils.parseDate s)
            Mutations.setScheduled config content pos ts now, pos)

    | "deadline" ->
        resolve ()
        |> Result.map (fun pos ->
            let ts = let s = getArg "date" in if s = "" then None else Some(Utils.parseDate s)
            Mutations.setDeadline config content pos ts now, pos)

    | "property-set" ->
        resolve ()
        |> Result.map (fun pos -> Mutations.setProperty content pos (getArg "key") (getArg "value"), pos)

    | "property-remove" ->
        resolve ()
        |> Result.map (fun pos -> Mutations.removeProperty content pos (getArg "key"), pos)

    | "note" ->
        resolve ()
        |> Result.map (fun pos -> Mutations.addNote content pos (getArg "text") now, pos)

    | "clock-in" -> resolve () |> Result.map (fun pos -> Mutations.clockIn content pos now, pos)

    | "clock-out" -> resolve () |> Result.map (fun pos -> Mutations.clockOut content pos now, pos)

    | unknown ->
        Error
            { Type = CliErrorType.InvalidArgs
              Message = sprintf "Unknown batch command: %s" unknown
              Detail = None }

/// Execute a batch of commands against in-memory file contents.
/// Returns (per-command results, final file contents).
let executeBatch
    (config: OrgConfig)
    (json: string)
    (files: Map<string, string>)
    (now: DateTime)
    : Result<HeadlineEdit.HeadlineState, CliError> list * Map<string, string> =
    let commands = parseCommands json
    let mutable currentFiles = files

    let results =
        commands
        |> List.map (fun cmd ->
            let content = Map.tryFind cmd.File currentFiles |> Option.defaultValue ""

            match applyCommand config content cmd now with
            | Ok(newContent, pos) ->
                currentFiles <- Map.add cmd.File newContent currentFiles
                let state = HeadlineEdit.extractState newContent pos
                Ok state
            | Error e -> Error e)

    results, currentFiles
