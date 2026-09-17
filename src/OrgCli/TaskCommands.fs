module OrgCli.TaskCommands

open System
open System.Text.Json.Nodes
open OrgCli.Org
open OrgCli.Index.Application

let help () =
    printfn "org task list|ready|review [--project NAME] [--owner ACTOR] [--status STATUS]"
    printfn "org task create TITLE --actor ACTOR [--acceptance TEXT] [--depends-on ID] [--project NAME]"
    printfn "org task show REF"
    printfn "org task state REF --state KEYWORD --actor NAME"
    printfn "org task move REF --file PATH [--parent REF] --destination-revision HASH --actor NAME"

    printfn
        "Creation: --file PATH [--parent REF]; default inbox.org. Editing: --title --text --tags --priority --scheduled --deadline."

    printfn "org task edit REF --actor ACTOR [--owner NAME] [--review-required true|false] [--acceptance TEXT]"
    printfn "org task claim REF --actor ACTOR [--claim-id UUID] [--lease-minutes 30]"
    printfn "org task renew|release|submit REF --actor ACTOR --claim-id UUID [--evidence TEXT]"
    printfn "org task approve|reject|cancel|reopen REF --actor ACTOR [--evidence TEXT]"

    printfn
        "Use --expected-revision HASH to require an explicit revision, --input JSON for API arguments, -f json for machine output."

    printfn "Set ORG_ACTOR as a default actor. IDs are assigned on adoption; review is required by default."

let run (service: WorkspaceService) (opts: Map<string, string list>) positional =
    let option name =
        opts.TryFind name |> Option.bind List.tryHead

    let jsonOutput = (option "format" |> Option.orElse (option "f")) = Some "json"

    let emitError code message =
        if jsonOutput then
            printfn
                "%s"
                ((obj
                    [ "ok", boolean false
                      "error", obj [ "code", str code; "message", str message ] ])
                    .ToJsonString())
        else
            eprintfn "Error: %s" message

    let action, rest =
        match positional with
        | [] -> "list", []
        | action :: rest -> action, rest

    if action = "help" || opts.ContainsKey "help" || opts.ContainsKey "h" then
        help ()
        0
    else
        try
            let known =
                set
                    [ "d"
                      "directory"
                      "db"
                      "config"
                      "f"
                      "format"
                      "h"
                      "help"
                      "q"
                      "quiet"
                      "v"
                      "verbose"
                      "dry-run"
                      "input"
                      "actor"
                      "file"
                      "parent"
                      "destination-revision"
                      "state"
                      "tags"
                      "title"
                      "text"
                      "acceptance"
                      "project"
                      "owner"
                      "priority"
                      "scheduled"
                      "deadline"
                      "status"
                      "evidence"
                      "ref"
                      "request-id"
                      "claim-id"
                      "expected-revision"
                      "limit"
                      "offset"
                      "lease-minutes"
                      "review-required"
                      "depends-on" ]

            for KeyValue(key, _) in opts do
                if not (known.Contains key) then
                    fail "invalid_arguments" 400 ("Unknown task option: --" + key)

            if opts.ContainsKey "dry-run" then
                fail "invalid_arguments" 400 "Task workflow does not support --dry-run; no changes made"

            let args =
                match option "input" with
                | Some value ->
                    match JsonNode.Parse value with
                    | :? JsonObject as o -> o
                    | _ -> fail "invalid_arguments" 400 "--input must be a JSON object"
                | None -> JsonObject()

            let set (key: string) (value: string) = args[key] <- JsonValue.Create value

            let operation =
                match action with
                | "list"
                | "ready"
                | "review" ->
                    if action <> "list" then
                        set "status" action

                    "tasks"
                | "show" -> "fetch"
                | "create" -> "task_create"
                | "edit" -> "task_update"
                | "move" -> "task_move"
                | "state"
                | "claim"
                | "renew"
                | "release"
                | "submit"
                | "approve"
                | "reject"
                | "cancel"
                | "reopen" ->
                    set "action" action
                    "task_action"
                | _ -> fail "invalid_arguments" 400 "Unknown task command; use org task --help"

            match rest with
            | [ value ] when action = "create" -> set "title" value
            | [ value ] when operation <> "tasks" -> set "ref" value
            | [] -> ()
            | _ -> fail "invalid_arguments" 400 "Unexpected task arguments"

            for key in
                [ "actor"
                  "file"
                  "parent"
                  "state"
                  "tags"
                  "title"
                  "text"
                  "acceptance"
                  "project"
                  "owner"
                  "priority"
                  "scheduled"
                  "deadline"
                  "status"
                  "evidence"
                  "ref" ] do
                option key |> Option.iter (set key)

            for key in [ "request-id"; "claim-id"; "expected-revision"; "destination-revision" ] do
                option key |> Option.iter (set (key.Replace('-', '_')))

            for key in [ "limit"; "offset"; "lease-minutes" ] do
                option key
                |> Option.iter (fun value ->
                    match Int32.TryParse value with
                    | true, number -> args[key.Replace('-', '_')] <- JsonValue.Create number
                    | _ -> fail "invalid_arguments" 400 ("--" + key + " must be an integer"))

            option "review-required"
            |> Option.iter (fun value ->
                match Boolean.TryParse value with
                | true, enabled -> args["review_required"] <- JsonValue.Create enabled
                | _ -> fail "invalid_arguments" 400 "--review-required must be true or false")

            opts.TryFind "depends-on"
            |> Option.iter (fun values ->
                args["depends_on"] <-
                    JsonArray(
                        values
                        |> List.filter ((<>) "")
                        |> List.map (fun v -> JsonValue.Create(v) :> JsonNode)
                        |> List.toArray
                    ))

            if OrgCli.Index.TaskWorkflow.isMutation operation then
                if not (args.ContainsKey "actor") then
                    Runtime.environment "ORG_ACTOR" |> Option.ofObj |> Option.iter (set "actor")

                if not (args.ContainsKey "actor") then
                    fail "invalid_arguments" 400 "Use --actor or set ORG_ACTOR"

                if operation = "task_create" && not (args.ContainsKey "request_id") then
                    set "request_id" (Guid.NewGuid().ToString("D"))

                if action = "claim" && not (args.ContainsKey "claim_id") then
                    set "claim_id" (Guid.NewGuid().ToString("D"))

                if operation <> "task_create" && not (args.ContainsKey "expected_revision") then
                    if not (args.ContainsKey "ref") then
                        fail "invalid_arguments" 400 "A task reference is required"

                    let fetch = JsonObject()
                    fetch["ref"] <- args["ref"].DeepClone()
                    let entry = service.Invoke("fetch", fetch)
                    args["expected_revision"] <- entry["revision"].DeepClone()

            let status, result = OrgCli.Server.invoke service operation args

            if jsonOutput then
                printfn "%s" (result.ToJsonString())
            elif status >= 400 then
                eprintfn "Error: %s" (result["error"].["message"].GetValue<string>())
            else
                let data = result["data"]

                if operation = "tasks" then
                    let tasks = data["tasks"].AsArray()

                    for item in tasks do
                        printfn
                            "%-10s %-3s %s"
                            (item["status"].GetValue<string>())
                            (item["priority"].GetValue<string>())
                            (item["title"].GetValue<string>())

                        printfn "  %s" (item["ref"].GetValue<string>())

                        for blocker in item["blockers"].AsArray() do
                            printfn "  ! %s" (blocker.GetValue<string>())

                    printfn "%d of %d tasks" tasks.Count (data["total"].GetValue<int>())

                    if not (isNull data["next_offset"]) then
                        printfn "Next page: --offset %d" (data["next_offset"].GetValue<int>())
                elif operation = "fetch" then
                    printfn "%s" (data["text"].GetValue<string>())
                else
                    printfn "%s: %s" (data["status"].GetValue<string>()) (data["title"].GetValue<string>())
                    printfn "Ref: %s" (data["ref"].GetValue<string>())
                    printfn "Revision: %s" (data["revision"].GetValue<string>())

                    if not (isNull data["claim_id"]) then
                        printfn
                            "Claim: %s (until %s)"
                            (data["claim_id"].GetValue<string>())
                            (data["claim_until"].GetValue<string>())

                    if not (isNull data["warning"]) then
                        eprintfn "%s" (data["warning"].GetValue<string>())

            if status >= 400 then 1 else 0
        with
        | ServiceError(code, _, message) ->
            emitError code message
            1
        | :? System.Text.Json.JsonException ->
            emitError "invalid_arguments" "Invalid JSON in --input"
            1
