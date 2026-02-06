module OrgCli.Org.FileConfig

open System.Text.RegularExpressions

let private parseLogging (indicators: string) : LogAction * LogAction =
    // indicators is the content inside parens after stripping the optional fast-key letter
    // e.g. from "(w@/!)" we get "@/!" after stripping the leading single char fast key
    // Possible forms: "@", "!", "@/!", "@/", "/!", "/@" etc.
    // Before the / = enter action, after the / = leave action
    // @ = LogNote, ! = LogTime
    match indicators.Contains('/') with
    | true ->
        let parts = indicators.Split('/')
        let enterPart = parts.[0]
        let leavePart = if parts.Length > 1 then parts.[1] else ""
        let enter =
            if enterPart.Contains('@') then LogAction.LogNote
            elif enterPart.Contains('!') then LogAction.LogTime
            else LogAction.NoLog
        let leave =
            if leavePart.Contains('!') then LogAction.LogTime
            elif leavePart.Contains('@') then LogAction.LogNote
            else LogAction.NoLog
        (enter, leave)
    | false ->
        // No slash: everything is enter action
        let enter =
            if indicators.Contains('@') then LogAction.LogNote
            elif indicators.Contains('!') then LogAction.LogTime
            else LogAction.NoLog
        (enter, LogAction.NoLog)

let private parenRegex = Regex(@"^([A-Z_]+)\(([^)]*)\)$", RegexOptions.Compiled)

let private parseKeywordToken (token: string) : TodoKeywordDef =
    let m = parenRegex.Match(token)
    if m.Success then
        let name = m.Groups.[1].Value
        let inside = m.Groups.[2].Value
        // Strip optional leading single-char fast key
        let indicators =
            if inside.Length >= 1 && System.Char.IsLetter(inside.[0]) then
                inside.Substring(1)
            else
                inside
        let (enter, leave) = parseLogging indicators
        { Keyword = name; LogOnEnter = enter; LogOnLeave = leave }
    else
        { Keyword = token; LogOnEnter = LogAction.NoLog; LogOnLeave = LogAction.NoLog }

let parseTodoLine (value: string) : TodoKeywordConfig =
    let parts = value.Split('|')
    if parts.Length >= 2 then
        let activeTokens =
            parts.[0].Trim().Split([|' '|], System.StringSplitOptions.RemoveEmptyEntries)
            |> Array.toList
        let doneTokens =
            parts.[1].Trim().Split([|' '|], System.StringSplitOptions.RemoveEmptyEntries)
            |> Array.toList
        {
            ActiveStates = activeTokens |> List.map parseKeywordToken
            DoneStates = doneTokens |> List.map parseKeywordToken
        }
    else
        // No pipe: all are active except the last which is done
        let tokens =
            value.Trim().Split([|' '|], System.StringSplitOptions.RemoveEmptyEntries)
            |> Array.toList
        match tokens with
        | [] ->
            { ActiveStates = []; DoneStates = [] }
        | [single] ->
            { ActiveStates = []; DoneStates = [parseKeywordToken single] }
        | _ ->
            let activeTokens = tokens |> List.take (tokens.Length - 1)
            let doneToken = tokens |> List.last
            {
                ActiveStates = activeTokens |> List.map parseKeywordToken
                DoneStates = [parseKeywordToken doneToken]
            }

let parseStartupOptions (value: string) : {| LogDone: LogAction option; LogRepeat: LogAction option; LogReschedule: LogAction option; LogRedeadline: LogAction option; LogRefile: LogAction option |} =
    let words =
        value.Trim().Split([|' '|], System.StringSplitOptions.RemoveEmptyEntries)
        |> Array.toList
    let mutable logDone : LogAction option = None
    let mutable logRepeat : LogAction option = None
    let mutable logReschedule : LogAction option = None
    let mutable logRedeadline : LogAction option = None
    let mutable logRefile : LogAction option = None
    for w in words do
        match w.ToLowerInvariant() with
        | "logdone" -> logDone <- Some LogAction.LogTime
        | "lognotedone" -> logDone <- Some LogAction.LogNote
        | "nologdone" -> logDone <- Some LogAction.NoLog
        | "logrepeat" -> logRepeat <- Some LogAction.LogTime
        | "lognoterepeat" -> logRepeat <- Some LogAction.LogNote
        | "nologrepeat" -> logRepeat <- Some LogAction.NoLog
        | "logreschedule" -> logReschedule <- Some LogAction.LogTime
        | "lognotereschedule" -> logReschedule <- Some LogAction.LogNote
        | "nologreschedule" -> logReschedule <- Some LogAction.NoLog
        | "logredeadline" -> logRedeadline <- Some LogAction.LogTime
        | "lognoteredeadline" -> logRedeadline <- Some LogAction.LogNote
        | "nologredeadline" -> logRedeadline <- Some LogAction.NoLog
        | "logrefile" -> logRefile <- Some LogAction.LogTime
        | "lognoterefile" -> logRefile <- Some LogAction.LogNote
        | "nologrefile" -> logRefile <- Some LogAction.NoLog
        | _ -> ()
    {| LogDone = logDone; LogRepeat = logRepeat; LogReschedule = logReschedule; LogRedeadline = logRedeadline; LogRefile = logRefile |}

let parsePriorities (value: string) : PriorityConfig option =
    let tokens =
        value.Trim().Split([|' '|], System.StringSplitOptions.RemoveEmptyEntries)
    if tokens.Length = 3 && tokens |> Array.forall (fun t -> t.Length = 1) then
        Some {
            Highest = tokens.[0].[0]
            Lowest = tokens.[1].[0]
            Default = tokens.[2].[0]
        }
    else
        None

let mergeFileConfig (baseConfig: OrgConfig) (keywords: Keyword list) : OrgConfig =
    let todoLines =
        keywords
        |> List.filter (fun k ->
            let key = k.Key.ToUpperInvariant()
            key = "TODO" || key = "SEQ_TODO")
        |> List.map (fun k -> parseTodoLine k.Value)

    let todoKeywords =
        if todoLines.IsEmpty then
            baseConfig.TodoKeywords
        else
            let active = todoLines |> List.collect (fun c -> c.ActiveStates)
            let done' = todoLines |> List.collect (fun c -> c.DoneStates)
            { ActiveStates = active; DoneStates = done' }

    let startupOpts =
        keywords
        |> List.filter (fun k -> k.Key.ToUpperInvariant() = "STARTUP")
        |> List.map (fun k -> parseStartupOptions k.Value)

    let logDone =
        startupOpts
        |> List.tryPick (fun o -> o.LogDone)
        |> Option.defaultValue baseConfig.LogDone

    let logRepeat =
        startupOpts
        |> List.tryPick (fun o -> o.LogRepeat)
        |> Option.defaultValue baseConfig.LogRepeat

    let logReschedule =
        startupOpts
        |> List.tryPick (fun o -> o.LogReschedule)
        |> Option.defaultValue baseConfig.LogReschedule

    let logRedeadline =
        startupOpts
        |> List.tryPick (fun o -> o.LogRedeadline)
        |> Option.defaultValue baseConfig.LogRedeadline

    let logRefile =
        startupOpts
        |> List.tryPick (fun o -> o.LogRefile)
        |> Option.defaultValue baseConfig.LogRefile

    let priorities =
        keywords
        |> List.tryFind (fun k -> k.Key.ToUpperInvariant() = "PRIORITIES")
        |> Option.bind (fun k -> parsePriorities k.Value)
        |> Option.defaultValue baseConfig.Priorities

    let archiveLocation =
        keywords
        |> List.tryFind (fun k -> k.Key.ToUpperInvariant() = "ARCHIVE")
        |> Option.map (fun k -> k.Value)
        |> Option.orElse baseConfig.ArchiveLocation

    { baseConfig with
        TodoKeywords = todoKeywords
        Priorities = priorities
        LogDone = logDone
        LogRepeat = logRepeat
        LogReschedule = logReschedule
        LogRedeadline = logRedeadline
        LogRefile = logRefile
        ArchiveLocation = archiveLocation }

let private tagTokenRegex = Regex(@"^([^\(]+)(?:\((.)\))?$", RegexOptions.Compiled)

let private parseTagToken (token: string) : TagDef =
    let m = tagTokenRegex.Match(token)
    if m.Success then
        let name = m.Groups.[1].Value
        let fastKey = if m.Groups.[2].Success then Some m.Groups.[2].Value.[0] else None
        { Name = name; FastKey = fastKey }
    else
        { Name = token; FastKey = None }

let parseTagsLine (value: string) : TagGroup list =
    let tokens =
        value.Trim().Split([|' '; '\t'|], System.StringSplitOptions.RemoveEmptyEntries)
        |> Array.toList
    let rec parse (remaining: string list) (currentRegular: TagDef list) (acc: TagGroup list) : TagGroup list =
        match remaining with
        | [] ->
            if currentRegular.IsEmpty then List.rev acc
            else List.rev (TagGroup.Regular (List.rev currentRegular) :: acc)
        | "{" :: rest ->
            let acc =
                if currentRegular.IsEmpty then acc
                else TagGroup.Regular (List.rev currentRegular) :: acc
            let groupTokens = rest |> List.takeWhile (fun t -> t <> "}")
            let afterGroup = rest |> List.skipWhile (fun t -> t <> "}") |> List.skip 1
            let defs = groupTokens |> List.map parseTagToken
            parse afterGroup [] (TagGroup.MutuallyExclusive defs :: acc)
        | token :: rest ->
            parse rest (parseTagToken token :: currentRegular) acc
    parse tokens [] []
