module OrgCli.Org.HeadlineEdit

open System
open System.Text.RegularExpressions

let private headlineRegex = Types.defaultHeadlineRegex
let internal planningLineRegex = Regex(@"^(SCHEDULED:|DEADLINE:|CLOSED:)\s")

let private planningPartsRegex =
    Regex(@"(SCHEDULED|DEADLINE|CLOSED):\s*((?:<[^>]+>|\[[^\]]+\])(?:--(?:<[^>]+>|\[[^\]]+\]))?)")

type HeadlineSection =
    { Before: string
      HeadlineLine: string
      PlanningLine: string option
      PropertyDrawer: string option
      LogbookDrawer: string option
      Body: string }

let split (content: string) (pos: int64) : HeadlineSection =
    let startIdx = int pos
    let before = if startIdx > 0 then content.Substring(0, startIdx) else ""
    let afterHeadlineStart = content.Substring(startIdx)
    let lines = afterHeadlineStart.Split([| '\n' |])
    let headlineLine = lines.[0]
    let mutable idx = 1

    let planningLine =
        if idx < lines.Length && planningLineRegex.IsMatch(lines.[idx].TrimStart()) then
            let pl = lines.[idx]
            idx <- idx + 1
            Some pl
        else
            None

    let propertyDrawer =
        if idx < lines.Length && lines.[idx].TrimStart() = ":PROPERTIES:" then
            let startLine = idx
            idx <- idx + 1

            while idx < lines.Length && lines.[idx].TrimStart() <> ":END:" do
                idx <- idx + 1

            if idx < lines.Length then
                let drawerLines = lines.[startLine..idx]
                idx <- idx + 1
                Some(String.Join("\n", drawerLines))
            else
                None
        else
            None

    let logbookDrawer =
        if idx < lines.Length && lines.[idx].TrimStart() = ":LOGBOOK:" then
            let startLine = idx
            idx <- idx + 1

            while idx < lines.Length && lines.[idx].TrimStart() <> ":END:" do
                idx <- idx + 1

            if idx < lines.Length then
                let drawerLines = lines.[startLine..idx]
                idx <- idx + 1
                Some(String.Join("\n", drawerLines))
            else
                None
        else
            None

    let body = String.Join("\n", lines.[idx..])

    { Before = before
      HeadlineLine = headlineLine
      PlanningLine = planningLine
      PropertyDrawer = propertyDrawer
      LogbookDrawer = logbookDrawer
      Body = body }

let reassemble (section: HeadlineSection) : string =
    let sb = System.Text.StringBuilder()
    sb.Append(section.Before) |> ignore
    sb.Append(section.HeadlineLine) |> ignore
    sb.Append('\n') |> ignore

    match section.PlanningLine with
    | Some pl ->
        sb.Append(pl) |> ignore
        sb.Append('\n') |> ignore
    | None -> ()

    match section.PropertyDrawer with
    | Some pd ->
        sb.Append(pd) |> ignore
        sb.Append('\n') |> ignore
    | None -> ()

    match section.LogbookDrawer with
    | Some lb ->
        sb.Append(lb) |> ignore
        sb.Append('\n') |> ignore
    | None -> ()

    sb.Append(section.Body) |> ignore
    sb.ToString()

// --- Headline line transforms ---

let private replaceKeywordUsing (regex: Regex) (headlineLine: string) (newState: string option) : string =
    let m = regex.Match(headlineLine)

    if not m.Success then
        headlineLine
    else
        let stars = m.Groups.[1].Value

        let priority =
            if m.Groups.[3].Success then
                m.Groups.[3].Value + " "
            else
                ""

        let title = m.Groups.[5].Value
        let tags = if m.Groups.[6].Success then m.Groups.[6].Value else ""

        match newState with
        | Some s -> sprintf "%s %s %s%s%s" stars s priority title tags
        | None -> sprintf "%s %s%s%s" stars priority title tags

let replaceKeyword (headlineLine: string) (newState: string option) : string =
    replaceKeywordUsing headlineRegex headlineLine newState

let replaceKeywordWith (keywords: string list) (headlineLine: string) (newState: string option) : string =
    replaceKeywordUsing (Types.buildHeadlineRegex keywords) headlineLine newState

let private getStateUsing (regex: Regex) (headlineLine: string) : string option =
    let m = regex.Match(headlineLine)

    if m.Success && m.Groups.[2].Success then
        Some m.Groups.[2].Value
    else
        None

let getState (headlineLine: string) : string option =
    getStateUsing headlineRegex headlineLine

let getStateWith (keywords: string list) (headlineLine: string) : string option =
    getStateUsing (Types.buildHeadlineRegex keywords) headlineLine

let private parseTagsUsing (regex: Regex) (headlineLine: string) : string list =
    let m = regex.Match(headlineLine)

    if m.Success && m.Groups.[6].Success then
        m.Groups.[6].Value.Trim().Split([| ':' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.toList
    else
        []

let parseTags (headlineLine: string) : string list =
    parseTagsUsing headlineRegex headlineLine

let parseTagsWith (keywords: string list) (headlineLine: string) : string list =
    parseTagsUsing (Types.buildHeadlineRegex keywords) headlineLine

let private replaceTagsUsing (regex: Regex) (headlineLine: string) (tags: string list) : string =
    let m = regex.Match(headlineLine)

    if not m.Success then
        headlineLine
    else
        let stars = m.Groups.[1].Value

        let keyword =
            if m.Groups.[2].Success then
                m.Groups.[2].Value + " "
            else
                ""

        let priority =
            if m.Groups.[3].Success then
                m.Groups.[3].Value + " "
            else
                ""

        let title = m.Groups.[5].Value

        let tagStr =
            if List.isEmpty tags then
                ""
            else
                sprintf " :%s:" (String.Join(":", tags))

        sprintf "%s %s%s%s%s" stars keyword priority title tagStr

let replaceTags (headlineLine: string) (tags: string list) : string =
    replaceTagsUsing headlineRegex headlineLine tags

let replaceTagsWith (keywords: string list) (headlineLine: string) (tags: string list) : string =
    replaceTagsUsing (Types.buildHeadlineRegex keywords) headlineLine tags

let private replacePriorityUsing (regex: Regex) (headlineLine: string) (priority: char option) : string =
    let m = regex.Match(headlineLine)

    if not m.Success then
        headlineLine
    else
        let stars = m.Groups.[1].Value

        let keyword =
            if m.Groups.[2].Success then
                m.Groups.[2].Value + " "
            else
                ""

        let priorityStr =
            match priority with
            | Some c -> sprintf "[#%c] " c
            | None -> ""

        let title = m.Groups.[5].Value
        let tags = if m.Groups.[6].Success then m.Groups.[6].Value else ""
        sprintf "%s %s%s%s%s" stars keyword priorityStr title tags

let replacePriority (headlineLine: string) (priority: char option) : string =
    replacePriorityUsing headlineRegex headlineLine priority

let replacePriorityWith (keywords: string list) (headlineLine: string) (priority: char option) : string =
    replacePriorityUsing (Types.buildHeadlineRegex keywords) headlineLine priority

// --- Planning operations ---

let parsePlanningParts (line: string) : Map<string, string> =
    planningPartsRegex.Matches(line)
    |> Seq.cast<Match>
    |> Seq.map (fun m -> m.Groups.[1].Value, m.Groups.[2].Value)
    |> Map.ofSeq

let buildPlanningLine (parts: Map<string, string>) : string option =
    if Map.isEmpty parts then
        None
    else
        let items =
            [ "SCHEDULED"; "DEADLINE"; "CLOSED" ]
            |> List.choose (fun k -> Map.tryFind k parts |> Option.map (fun v -> sprintf "%s: %s" k v))

        Some(String.Join(" ", items))

let modifyPlanning (content: string) (pos: int64) (modify: Map<string, string> -> Map<string, string>) : string =
    let section = split content pos

    let existingParts =
        section.PlanningLine
        |> Option.map parsePlanningParts
        |> Option.defaultValue Map.empty

    let newParts = modify existingParts
    let newPlanningLine = buildPlanningLine newParts

    reassemble
        { section with
            PlanningLine = newPlanningLine }

// --- Property drawer operations ---

let getProperty (drawer: string) (key: string) : string option =
    let pattern = sprintf @"^:%s:\s+(.+)" (Regex.Escape(key))
    let m = Regex.Match(drawer, pattern, RegexOptions.Multiline)
    if m.Success then Some(m.Groups.[1].Value.Trim()) else None

let setProperty (drawer: string) (key: string) (value: string) : string =
    let propLine = sprintf ":%s: %s" key value
    let pattern = sprintf @"^:%s:\s+.+" (Regex.Escape(key))

    if Regex.IsMatch(drawer, pattern, RegexOptions.Multiline) then
        Regex.Replace(drawer, pattern, propLine, RegexOptions.Multiline)
    else
        let endIdx = drawer.LastIndexOf(":END:")

        if endIdx >= 0 then
            drawer.Substring(0, endIdx) + propLine + "\n" + drawer.Substring(endIdx)
        else
            drawer

let removeProperty (drawer: string) (key: string) : string option =
    let lines = drawer.Split([| '\n' |])
    let pattern = sprintf @"^:%s:" (Regex.Escape(key))

    let filtered =
        lines
        |> Array.filter (fun line -> not (Regex.IsMatch(line.TrimStart(), pattern)))

    if filtered.Length <= 2 then
        None
    else
        Some(String.Join("\n", filtered))

let ensureDrawer (section: HeadlineSection) : HeadlineSection =
    match section.PropertyDrawer with
    | Some _ -> section
    | None ->
        { section with
            PropertyDrawer = Some ":PROPERTIES:\n:END:" }

// --- Logbook operations ---

let formatStateChange (newState: string) (oldState: string) (now: DateTime) : string =
    let ts =
        Writer.formatTimestamp
            { Type = TimestampType.Inactive
              Date = now
              HasTime = true
              Repeater = None
              Delay = None
              RangeEnd = None }

    sprintf "- State %-12s from %-12s %s" (sprintf "\"%s\"" newState) (sprintf "\"%s\"" oldState) ts

let insertEntry (section: HeadlineSection) (entry: string) : HeadlineSection =
    match section.LogbookDrawer with
    | Some lb ->
        let lines = lb.Split([| '\n' |])

        let newDrawer =
            Array.concat [ [| lines.[0] |]; [| entry |]; lines.[1..] ] |> String.concat "\n"

        { section with
            LogbookDrawer = Some newDrawer }
    | None ->
        let newDrawer = sprintf ":LOGBOOK:\n%s\n:END:" entry

        { section with
            LogbookDrawer = Some newDrawer }

// --- Utility ---

let formatInactiveTimestamp (now: DateTime) : string =
    Writer.formatTimestamp
        { Type = TimestampType.Inactive
          Date = now
          HasTime = true
          Repeater = None
          Delay = None
          RangeEnd = None }

// --- HeadlineState extraction ---

type HeadlineState =
    { Pos: int64
      Id: string option
      Title: string
      Todo: string option
      Priority: char option
      Tags: string list
      Scheduled: string option
      Deadline: string option
      Closed: string option }

let private extractStateUsing (regex: Regex) (content: string) (pos: int64) : HeadlineState =
    let section = split content pos
    let m = regex.Match(section.HeadlineLine)

    let todo =
        if m.Success && m.Groups.[2].Success then
            Some m.Groups.[2].Value
        else
            None

    let priority =
        if m.Success && m.Groups.[4].Success then
            Some m.Groups.[4].Value.[0]
        else
            None

    let title =
        if m.Success then
            m.Groups.[5].Value
        else
            section.HeadlineLine

    let tags =
        if m.Success && m.Groups.[6].Success then
            m.Groups.[6].Value.Trim().Split([| ':' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.toList
        else
            []

    let id = section.PropertyDrawer |> Option.bind (fun pd -> getProperty pd "ID")

    let planningParts =
        section.PlanningLine
        |> Option.map parsePlanningParts
        |> Option.defaultValue Map.empty

    { Pos = pos
      Id = id
      Title = title
      Todo = todo
      Priority = priority
      Tags = tags
      Scheduled = Map.tryFind "SCHEDULED" planningParts
      Deadline = Map.tryFind "DEADLINE" planningParts
      Closed = Map.tryFind "CLOSED" planningParts }

/// Extract headline state from content at a given position.
let extractState (content: string) (pos: int64) : HeadlineState =
    extractStateUsing headlineRegex content pos

let extractStateWith (keywords: string list) (content: string) (pos: int64) : HeadlineState =
    extractStateUsing (Types.buildHeadlineRegex keywords) content pos
