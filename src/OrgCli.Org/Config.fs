module OrgCli.Org.Config

open System
open System.IO
open System.Text.Json

let parseLogAction (s: string) : LogAction option =
    match s.ToLowerInvariant() with
    | "none" -> Some LogAction.NoLog
    | "time" -> Some LogAction.LogTime
    | "note" -> Some LogAction.LogNote
    | _ -> None

let private parseKeywordDef (el: JsonElement) : TodoKeywordDef =
    let keyword =
        match el.TryGetProperty("keyword") with
        | true, v -> v.GetString()
        | _ -> ""
    let logOnEnter =
        match el.TryGetProperty("logOnEnter") with
        | true, v -> parseLogAction (v.GetString()) |> Option.defaultValue LogAction.NoLog
        | _ -> LogAction.NoLog
    let logOnLeave =
        match el.TryGetProperty("logOnLeave") with
        | true, v -> parseLogAction (v.GetString()) |> Option.defaultValue LogAction.NoLog
        | _ -> LogAction.NoLog
    { Keyword = keyword; LogOnEnter = logOnEnter; LogOnLeave = logOnLeave }

let private parseKeywordDefList (el: JsonElement) : TodoKeywordDef list =
    if el.ValueKind = JsonValueKind.Array then
        [ for item in el.EnumerateArray() -> parseKeywordDef item ]
    else
        []

let private parseTodoKeywords (el: JsonElement) : TodoKeywordConfig =
    let active =
        match el.TryGetProperty("activeStates") with
        | true, v -> parseKeywordDefList v
        | _ -> Types.defaultConfig.TodoKeywords.ActiveStates
    let done' =
        match el.TryGetProperty("doneStates") with
        | true, v -> parseKeywordDefList v
        | _ -> Types.defaultConfig.TodoKeywords.DoneStates
    { ActiveStates = active; DoneStates = done' }

let private parsePriorities (el: JsonElement) : PriorityConfig =
    let getChar (prop: string) def =
        match el.TryGetProperty(prop) with
        | true, v ->
            let s = v.GetString()
            if String.IsNullOrEmpty(s) then def else s.[0]
        | _ -> def
    { Highest = getChar "highest" Types.defaultConfig.Priorities.Highest
      Lowest = getChar "lowest" Types.defaultConfig.Priorities.Lowest
      Default = getChar "default" Types.defaultConfig.Priorities.Default }

let private getLogAction (el: JsonElement) (prop: string) (def: LogAction) : LogAction =
    match el.TryGetProperty(prop) with
    | true, v -> parseLogAction (v.GetString()) |> Option.defaultValue def
    | _ -> def

let loadFromJson (json: string) : Result<OrgConfig, string> =
    try
        use doc = JsonDocument.Parse(json)
        let root = doc.RootElement

        let todoKeywords =
            match root.TryGetProperty("todoKeywords") with
            | true, v -> parseTodoKeywords v
            | _ -> Types.defaultConfig.TodoKeywords

        let priorities =
            match root.TryGetProperty("priorities") with
            | true, v -> parsePriorities v
            | _ -> Types.defaultConfig.Priorities

        let logDone = getLogAction root "logDone" Types.defaultConfig.LogDone
        let logRepeat = getLogAction root "logRepeat" Types.defaultConfig.LogRepeat
        let logReschedule = getLogAction root "logReschedule" Types.defaultConfig.LogReschedule
        let logRedeadline = getLogAction root "logRedeadline" Types.defaultConfig.LogRedeadline
        let logRefile = getLogAction root "logRefile" Types.defaultConfig.LogRefile

        let logIntoDrawer =
            match root.TryGetProperty("logIntoDrawer") with
            | true, v when v.ValueKind = JsonValueKind.Null -> None
            | true, v when v.ValueKind = JsonValueKind.String -> Some (v.GetString())
            | _ -> Types.defaultConfig.LogIntoDrawer

        let tagInheritance =
            match root.TryGetProperty("tagInheritance") with
            | true, v -> v.GetBoolean()
            | _ -> Types.defaultConfig.TagInheritance

        let deadlineWarningDays =
            match root.TryGetProperty("deadlineWarningDays") with
            | true, v -> max 0 (v.GetInt32())
            | _ -> Types.defaultConfig.DeadlineWarningDays

        let archiveLocation =
            match root.TryGetProperty("archiveLocation") with
            | true, v when v.ValueKind = JsonValueKind.Null -> None
            | true, v when v.ValueKind = JsonValueKind.String -> Some (v.GetString())
            | _ -> Types.defaultConfig.ArchiveLocation

        Ok {
            TodoKeywords = todoKeywords
            Priorities = priorities
            LogDone = logDone
            LogRepeat = logRepeat
            LogReschedule = logReschedule
            LogRedeadline = logRedeadline
            LogRefile = logRefile
            LogIntoDrawer = logIntoDrawer
            TagInheritance = tagInheritance
            InheritTags = Types.defaultConfig.InheritTags
            TagsExcludeFromInheritance = Types.defaultConfig.TagsExcludeFromInheritance
            PropertyInheritance = Types.defaultConfig.PropertyInheritance
            InheritProperties = Types.defaultConfig.InheritProperties
            DeadlineWarningDays = deadlineWarningDays
            ArchiveLocation = archiveLocation
        }
    with ex ->
        Error $"Failed to parse config JSON: {ex.Message}"

let loadFromFile (path: string) : Result<OrgConfig, string> =
    if not (File.Exists(path)) then
        Ok Types.defaultConfig
    else
        try
            let json = File.ReadAllText(path)
            loadFromJson json
        with ex ->
            Error $"Failed to read config file '{path}': {ex.Message}"

type private EnvOverrides = {
    LogDone: LogAction option
    LogIntoDrawer: string option option  // Some None = explicitly empty, None = not set
    DeadlineWarningDays: int option
    TagInheritance: bool option
}

let private readEnvOverrides () : EnvOverrides =
    let logDone =
        match Environment.GetEnvironmentVariable("ORG_CLI_LOG_DONE") with
        | null | "" -> None
        | v -> parseLogAction v

    let logIntoDrawer =
        match Environment.GetEnvironmentVariable("ORG_CLI_LOG_INTO_DRAWER") with
        | null -> None
        | "" -> Some None
        | v -> Some (Some v)

    let deadlineWarningDays =
        match Environment.GetEnvironmentVariable("ORG_CLI_DEADLINE_WARNING_DAYS") with
        | null | "" -> None
        | v ->
            match Int32.TryParse(v) with
            | true, n -> Some n
            | _ -> None

    let tagInheritance =
        match Environment.GetEnvironmentVariable("ORG_CLI_TAG_INHERITANCE") with
        | null | "" -> None
        | v ->
            match v.ToLowerInvariant() with
            | "true" | "1" | "yes" -> Some true
            | "false" | "0" | "no" -> Some false
            | _ -> None

    { LogDone = logDone; LogIntoDrawer = logIntoDrawer; DeadlineWarningDays = deadlineWarningDays; TagInheritance = tagInheritance }

let private applyEnvOverrides (baseCfg: OrgConfig) (env: EnvOverrides) : OrgConfig =
    { baseCfg with
        LogDone = env.LogDone |> Option.defaultValue baseCfg.LogDone
        LogIntoDrawer = env.LogIntoDrawer |> Option.defaultValue baseCfg.LogIntoDrawer
        DeadlineWarningDays = env.DeadlineWarningDays |> Option.defaultValue baseCfg.DeadlineWarningDays
        TagInheritance = env.TagInheritance |> Option.defaultValue baseCfg.TagInheritance }

/// Apply env var overrides onto a base config.
/// Only env vars that are actually set override the base; unset vars are left alone.
let overlayEnv (baseCfg: OrgConfig) : OrgConfig =
    applyEnvOverrides baseCfg (readEnvOverrides ())

/// Backward-compatible: read env vars, apply over defaultConfig.
let loadFromEnv () : OrgConfig =
    overlayEnv Types.defaultConfig

let load () : OrgConfig =
    let fileCfg =
        match loadFromFile (Utils.orgCliConfigFile ()) with
        | Ok cfg -> cfg
        | Error _ -> Types.defaultConfig
    overlayEnv fileCfg
