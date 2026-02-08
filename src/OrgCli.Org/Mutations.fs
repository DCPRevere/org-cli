module OrgCli.Org.Mutations

open System
open System.Text.RegularExpressions

let private headlineRegex = Types.defaultHeadlineRegex
let private repeaterRegex = Regex(@"^(\.\+|\+\+|\+)(\d+)([hdwmy])$")

// --- Repeater parsing and shifting ---

let parseRepeater (s: string) : (RepeaterType * int * char) option =
    let m = repeaterRegex.Match(s)

    if not m.Success then
        None
    else
        let rtype =
            match m.Groups.[1].Value with
            | ".+" -> RepeaterType.FromToday
            | "++" -> RepeaterType.NextFuture
            | _ -> RepeaterType.Standard

        let n = int m.Groups.[2].Value
        let unit = m.Groups.[3].Value.[0]
        Some(rtype, n, unit)

let private addUnits (date: DateTime) (n: int) (unit: char) : DateTime =
    match unit with
    | 'h' -> date.AddHours(float n)
    | 'd' -> date.AddDays(float n)
    | 'w' -> date.AddDays(float (n * 7))
    | 'm' -> date.AddMonths(n)
    | 'y' -> date.AddYears(n)
    | _ -> date

let shiftTimestamp (ts: Timestamp) (now: DateTime) : Timestamp =
    match ts.Repeater |> Option.bind parseRepeater with
    | None -> ts
    | Some(rtype, n, unit) ->
        let newDate =
            match rtype with
            | RepeaterType.Standard -> addUnits ts.Date n unit
            | RepeaterType.FromToday ->
                let baseDate = if ts.HasTime then now else now.Date
                addUnits baseDate n unit
            | RepeaterType.NextFuture ->
                let today = if ts.HasTime then now else now.Date
                let mutable d = ts.Date

                while d <= today do
                    d <- addUnits d n unit

                d

        let delta = newDate - ts.Date

        let newRangeEnd =
            ts.RangeEnd
            |> Option.map (fun endTs -> { endTs with Date = endTs.Date + delta })

        { ts with
            Date = newDate
            RangeEnd = newRangeEnd }

// --- Planning timestamp parsing ---

let private parseTimestampFromRaw (raw: string) : Timestamp option =
    match Parsers.runParser Parsers.pTimestamp raw with
    | Result.Ok ts -> Some ts
    | Result.Error _ -> None

let private hasRepeaterInPlanning (planningParts: Map<string, string>) : (string * Timestamp) option =
    [ "SCHEDULED"; "DEADLINE" ]
    |> List.tryPick (fun key ->
        Map.tryFind key planningParts
        |> Option.bind parseTimestampFromRaw
        |> Option.bind (fun ts -> ts.Repeater |> Option.bind parseRepeater |> Option.map (fun _ -> (key, ts))))

// --- Public mutation functions ---

let private findKeywordDef (config: OrgConfig) (kw: string) : TodoKeywordDef option =
    let all = config.TodoKeywords.ActiveStates @ config.TodoKeywords.DoneStates
    all |> List.tryFind (fun d -> d.Keyword = kw)

let private getEffectiveLogAction
    (config: OrgConfig)
    (loggingProp: string option)
    (oldState: string option)
    (newState: string option)
    (isDone: bool)
    : LogAction =
    match loggingProp with
    | Some "nil" -> LogAction.NoLog
    | _ ->
        // Per-keyword LogOnEnter for target state
        let enterAction =
            newState
            |> Option.bind (findKeywordDef config)
            |> Option.map (fun d -> d.LogOnEnter)
            |> Option.filter (fun a -> a <> LogAction.NoLog)
        // Per-keyword LogOnLeave for source state
        let leaveAction =
            oldState
            |> Option.bind (findKeywordDef config)
            |> Option.map (fun d -> d.LogOnLeave)
            |> Option.filter (fun a -> a <> LogAction.NoLog)
        // LogDone applies when entering a done state
        let doneAction =
            if isDone && config.LogDone <> LogAction.NoLog then
                Some config.LogDone
            else
                None

        enterAction
        |> Option.orElse leaveAction
        |> Option.orElse doneAction
        |> Option.defaultValue LogAction.NoLog

let private keywordsFromConfig (config: OrgConfig) : string list = Types.allKeywords config.TodoKeywords

let private keywordsFromContent (content: string) : string list =
    let kws, _, _ = Document.parseFileSection content
    let effectiveConfig = FileConfig.mergeFileConfig Types.defaultConfig kws
    Types.allKeywords effectiveConfig.TodoKeywords

let setTodoState (config: OrgConfig) (content: string) (pos: int64) (newState: string option) (now: DateTime) : string =
    let kws = keywordsFromConfig config
    let section = HeadlineEdit.split content pos
    let oldState = HeadlineEdit.getStateWith kws section.HeadlineLine

    let isDoneTransition =
        newState
        |> Option.map (fun s -> Types.isDoneState config.TodoKeywords s)
        |> Option.defaultValue false

    let doc = Document.parse content

    let loggingProp =
        doc.Headlines
        |> List.tryFind (fun h -> h.Position = pos)
        |> Option.bind (fun h -> Headlines.resolveProperty config doc h "LOGGING")

    let logAction =
        getEffectiveLogAction config loggingProp oldState newState isDoneTransition

    let existingParts =
        section.PlanningLine
        |> Option.map HeadlineEdit.parsePlanningParts
        |> Option.defaultValue Map.empty

    let isRepeat =
        isDoneTransition && hasRepeaterInPlanning existingParts |> Option.isSome

    if isRepeat then
        let repeatToState =
            section.PropertyDrawer
            |> Option.bind (fun pd -> HeadlineEdit.getProperty pd "REPEAT_TO_STATE")
            |> Option.defaultWith (fun () -> oldState |> Option.defaultValue "TODO")

        let newHeadlineLine =
            HeadlineEdit.replaceKeywordWith kws section.HeadlineLine (Some repeatToState)

        let newParts =
            existingParts
            |> Map.map (fun key raw ->
                if key = "CLOSED" then
                    raw
                else
                    match parseTimestampFromRaw raw with
                    | Some ts when ts.Repeater |> Option.bind parseRepeater |> Option.isSome ->
                        Writer.formatTimestamp (shiftTimestamp ts now)
                    | _ -> raw)
            |> Map.remove "CLOSED"

        let newPlanningLine = HeadlineEdit.buildPlanningLine newParts

        let lastRepeatTs = HeadlineEdit.formatInactiveTimestamp now
        let sectionWithProps = HeadlineEdit.ensureDrawer section

        let updatedDrawer =
            sectionWithProps.PropertyDrawer
            |> Option.map (fun pd -> HeadlineEdit.setProperty pd "LAST_REPEAT" lastRepeatTs)

        let loggingSuppressed =
            loggingProp
            |> Option.map (fun v -> v.Trim().ToLowerInvariant() = "nil")
            |> Option.defaultValue false

        let shouldLogRepeat = config.LogRepeat <> LogAction.NoLog && not loggingSuppressed

        let loggedSection =
            if shouldLogRepeat then
                let doneStr = newState |> Option.defaultValue "DONE"
                let logEntry = HeadlineEdit.formatStateChange repeatToState doneStr now

                HeadlineEdit.insertEntry
                    { sectionWithProps with
                        HeadlineLine = newHeadlineLine
                        PlanningLine = newPlanningLine
                        PropertyDrawer = updatedDrawer }
                    logEntry
            else
                { sectionWithProps with
                    HeadlineLine = newHeadlineLine
                    PlanningLine = newPlanningLine
                    PropertyDrawer = updatedDrawer }

        HeadlineEdit.reassemble loggedSection
    else
        let newHeadlineLine =
            HeadlineEdit.replaceKeywordWith kws section.HeadlineLine newState

        let newParts =
            if isDoneTransition && logAction <> LogAction.NoLog then
                let closedTs = HeadlineEdit.formatInactiveTimestamp now
                existingParts |> Map.add "CLOSED" closedTs
            else
                existingParts |> Map.remove "CLOSED"

        let newPlanningLine = HeadlineEdit.buildPlanningLine newParts

        let shouldLog = logAction <> LogAction.NoLog

        let loggedSection =
            if shouldLog then
                let newStateStr = newState |> Option.defaultValue ""
                let oldStateStr = oldState |> Option.defaultValue ""
                let logEntry = HeadlineEdit.formatStateChange newStateStr oldStateStr now

                HeadlineEdit.insertEntry
                    { section with
                        HeadlineLine = newHeadlineLine
                        PlanningLine = newPlanningLine }
                    logEntry
            else
                { section with
                    HeadlineLine = newHeadlineLine
                    PlanningLine = newPlanningLine }

        HeadlineEdit.reassemble loggedSection

let private formatRescheduleEntry (keyword: string) (oldRaw: string) (now: DateTime) : string =
    let ts = HeadlineEdit.formatInactiveTimestamp now

    let label =
        if keyword = "SCHEDULED" then
            "Rescheduled"
        else
            "New deadline"

    sprintf "- %s from \"%s: %s\" on %s" label keyword oldRaw ts

let setScheduled (config: OrgConfig) (content: string) (pos: int64) (ts: Timestamp option) (now: DateTime) : string =
    let section = HeadlineEdit.split content pos

    let existingParts =
        section.PlanningLine
        |> Option.map HeadlineEdit.parsePlanningParts
        |> Option.defaultValue Map.empty

    let oldScheduled = Map.tryFind "SCHEDULED" existingParts

    let newParts =
        match ts with
        | Some t -> existingParts |> Map.add "SCHEDULED" (Writer.formatTimestamp t)
        | None -> existingParts |> Map.remove "SCHEDULED"

    let newPlanningLine = HeadlineEdit.buildPlanningLine newParts

    let shouldLog =
        config.LogReschedule <> LogAction.NoLog && oldScheduled.IsSome && ts.IsSome

    let result =
        if shouldLog then
            let entry = formatRescheduleEntry "SCHEDULED" oldScheduled.Value now

            HeadlineEdit.insertEntry
                { section with
                    PlanningLine = newPlanningLine }
                entry
        else
            { section with
                PlanningLine = newPlanningLine }

    HeadlineEdit.reassemble result

let setDeadline (config: OrgConfig) (content: string) (pos: int64) (ts: Timestamp option) (now: DateTime) : string =
    let section = HeadlineEdit.split content pos

    let existingParts =
        section.PlanningLine
        |> Option.map HeadlineEdit.parsePlanningParts
        |> Option.defaultValue Map.empty

    let oldDeadline = Map.tryFind "DEADLINE" existingParts

    let newParts =
        match ts with
        | Some t -> existingParts |> Map.add "DEADLINE" (Writer.formatTimestamp t)
        | None -> existingParts |> Map.remove "DEADLINE"

    let newPlanningLine = HeadlineEdit.buildPlanningLine newParts

    let shouldLog =
        config.LogRedeadline <> LogAction.NoLog && oldDeadline.IsSome && ts.IsSome

    let result =
        if shouldLog then
            let entry = formatRescheduleEntry "DEADLINE" oldDeadline.Value now

            HeadlineEdit.insertEntry
                { section with
                    PlanningLine = newPlanningLine }
                entry
        else
            { section with
                PlanningLine = newPlanningLine }

    HeadlineEdit.reassemble result

let private addRefileLogEntry (config: OrgConfig) (content: string) (pos: int64) (now: DateTime) : string =
    if config.LogRefile = LogAction.NoLog then
        content
    else
        let section = HeadlineEdit.split content pos
        let ts = HeadlineEdit.formatInactiveTimestamp now
        let entry = sprintf "- Refiled on %s" ts
        let logged = HeadlineEdit.insertEntry section entry
        HeadlineEdit.reassemble logged

let refile
    (config: OrgConfig)
    (srcContent: string)
    (srcPos: int64)
    (tgtContent: string)
    (tgtPos: int64)
    (sameFile: bool)
    (now: DateTime)
    : string * string =
    let subtree = Subtree.extractSubtree srcContent srcPos

    if sameFile then
        let removedSrc = Subtree.removeSubtree srcContent srcPos
        let (srcStart, srcEnd) = Subtree.getSubtreeRange srcContent srcPos
        let adjustment = if srcStart < int tgtPos then -(srcEnd - srcStart) else 0
        let adjustedTgtPos = int64 (int tgtPos + adjustment)
        let result = Subtree.insertSubtreeAsChild removedSrc adjustedTgtPos (subtree + "\n")
        let result = addRefileLogEntry config result adjustedTgtPos now
        result, result
    else
        let newSrc = Subtree.removeSubtree srcContent srcPos
        let newTgt = Subtree.insertSubtreeAsChild tgtContent tgtPos (subtree + "\n")
        let newTgt = addRefileLogEntry config newTgt tgtPos now
        newSrc, newTgt

// --- Set / remove property ---

let setProperty (content: string) (pos: int64) (key: string) (value: string) : string =
    let section = HeadlineEdit.split content pos
    let s = HeadlineEdit.ensureDrawer section

    let newDrawer =
        s.PropertyDrawer |> Option.map (fun pd -> HeadlineEdit.setProperty pd key value)

    HeadlineEdit.reassemble { s with PropertyDrawer = newDrawer }

let removeProperty (content: string) (pos: int64) (key: string) : string =
    let section = HeadlineEdit.split content pos

    match section.PropertyDrawer with
    | None -> content
    | Some pd ->
        let newDrawer = HeadlineEdit.removeProperty pd key

        HeadlineEdit.reassemble
            { section with
                PropertyDrawer = newDrawer }

// --- Add / remove tag ---

let addTag (content: string) (pos: int64) (tag: string) : string =
    let kws = keywordsFromContent content
    let section = HeadlineEdit.split content pos
    let existing = HeadlineEdit.parseTagsWith kws section.HeadlineLine

    if List.contains tag existing then
        content
    else
        let newTags = existing @ [ tag ]
        let newHeadline = HeadlineEdit.replaceTagsWith kws section.HeadlineLine newTags

        HeadlineEdit.reassemble
            { section with
                HeadlineLine = newHeadline }

let addTagWithExclusion (content: string) (pos: int64) (tag: string) (tagDefs: TagGroup list) : string =
    let kws = keywordsFromContent content
    let section = HeadlineEdit.split content pos
    let existing = HeadlineEdit.parseTagsWith kws section.HeadlineLine

    if List.contains tag existing then
        content
    else
        let exclusionPeers =
            tagDefs
            |> List.tryPick (fun g ->
                match g with
                | TagGroup.MutuallyExclusive defs ->
                    if defs |> List.exists (fun d -> d.Name = tag) then
                        Some(defs |> List.map (fun d -> d.Name) |> List.filter (fun n -> n <> tag))
                    else
                        None
                | _ -> None)
            |> Option.defaultValue []

        let filtered =
            existing |> List.filter (fun t -> not (List.contains t exclusionPeers))

        let newTags = filtered @ [ tag ]
        let newHeadline = HeadlineEdit.replaceTagsWith kws section.HeadlineLine newTags

        HeadlineEdit.reassemble
            { section with
                HeadlineLine = newHeadline }

let removeTag (content: string) (pos: int64) (tag: string) : string =
    let kws = keywordsFromContent content
    let section = HeadlineEdit.split content pos
    let existing = HeadlineEdit.parseTagsWith kws section.HeadlineLine

    if not (List.contains tag existing) then
        content
    else
        let newTags = existing |> List.filter (fun t -> t <> tag)
        let newHeadline = HeadlineEdit.replaceTagsWith kws section.HeadlineLine newTags

        HeadlineEdit.reassemble
            { section with
                HeadlineLine = newHeadline }

// --- Set priority ---

let setPriority (content: string) (pos: int64) (priority: char option) : string =
    let kws = keywordsFromContent content
    let section = HeadlineEdit.split content pos
    let newHeadline = HeadlineEdit.replacePriorityWith kws section.HeadlineLine priority

    HeadlineEdit.reassemble
        { section with
            HeadlineLine = newHeadline }

// --- Clock in / out ---

let private openClockRegex = Regex(@"^CLOCK:\s+(\[[^\]]+\])\s*$")

let clockIn (content: string) (pos: int64) (now: DateTime) : string =
    let section = HeadlineEdit.split content pos
    let clockTs = HeadlineEdit.formatInactiveTimestamp now
    let entry = sprintf "CLOCK: %s" clockTs
    let sectionWithLog = HeadlineEdit.insertEntry section entry
    HeadlineEdit.reassemble sectionWithLog

let clockOut (content: string) (pos: int64) (now: DateTime) : string =
    let section = HeadlineEdit.split content pos

    match section.LogbookDrawer with
    | None -> content
    | Some lb ->
        let lines = lb.Split([| '\n' |])
        let mutable found = false

        let newLines =
            lines
            |> Array.map (fun line ->
                if found then
                    line
                else
                    let m = openClockRegex.Match(line.TrimStart())

                    if m.Success then
                        let startRaw = m.Groups.[1].Value
                        let endTs = HeadlineEdit.formatInactiveTimestamp now

                        match parseTimestampFromRaw startRaw with
                        | Some startTs ->
                            let dur = now - startTs.Date

                            if dur.TotalSeconds < 0.0 then
                                line // Don't close with negative duration
                            else
                                found <- true
                                let hours = int dur.TotalHours
                                let mins = dur.Minutes
                                sprintf "CLOCK: %s--%s => %2d:%02d" startRaw endTs hours mins
                        | None -> line
                    else
                        line)

        if not found then
            content
        else
            let newDrawer = String.Join("\n", newLines)

            HeadlineEdit.reassemble
                { section with
                    LogbookDrawer = Some newDrawer }

// --- Add note ---

let addNote (content: string) (pos: int64) (note: string) (now: DateTime) : string =
    let section = HeadlineEdit.split content pos
    let ts = HeadlineEdit.formatInactiveTimestamp now
    let entry = sprintf "- Note taken on %s \\\\\n  %s" ts note
    let sectionWithLog = HeadlineEdit.insertEntry section entry
    HeadlineEdit.reassemble sectionWithLog

// --- Add headline ---

let formatNewHeadline
    (title: string)
    (level: int)
    (todoState: string option)
    (priority: char option)
    (tags: string list)
    (scheduled: Timestamp option)
    (deadline: Timestamp option)
    : string =
    let sb = System.Text.StringBuilder()
    sb.Append(String.replicate level "*") |> ignore
    sb.Append(' ') |> ignore

    match todoState with
    | Some s -> sb.Append(s).Append(' ') |> ignore
    | None -> ()

    match priority with
    | Some c -> sb.Append(sprintf "[#%c] " c) |> ignore
    | None -> ()

    sb.Append(title) |> ignore

    if not (List.isEmpty tags) then
        sb.Append(sprintf " :%s:" (String.Join(":", tags))) |> ignore

    let planningParts =
        [ scheduled |> Option.map (fun ts -> "SCHEDULED: " + Writer.formatTimestamp ts)
          deadline |> Option.map (fun ts -> "DEADLINE: " + Writer.formatTimestamp ts) ]
        |> List.choose id

    if not (List.isEmpty planningParts) then
        sb.Append('\n') |> ignore
        sb.Append(String.Join(" ", planningParts)) |> ignore

    sb.Append('\n') |> ignore
    sb.ToString()

let addHeadline
    (content: string)
    (title: string)
    (level: int)
    (todoState: string option)
    (priority: char option)
    (tags: string list)
    (scheduled: Timestamp option)
    (deadline: Timestamp option)
    : string =
    let headline =
        formatNewHeadline title level todoState priority tags scheduled deadline

    let separator =
        if content.Length > 0 && not (content.EndsWith("\n")) then
            "\n"
        else
            ""

    content + separator + headline

let addHeadlineUnder
    (content: string)
    (parentPos: int64)
    (title: string)
    (todoState: string option)
    (priority: char option)
    (tags: string list)
    (scheduled: Timestamp option)
    (deadline: Timestamp option)
    : string =
    let headline = formatNewHeadline title 1 todoState priority tags scheduled deadline
    Subtree.insertSubtreeAsChild content parentPos headline

let private addPropertiesToSubtree (subtreeContent: string) (newProps: (string * string) list) : string =
    let lines = subtreeContent.Split([| '\n' |])
    let afterHeadline = 1

    let afterPlanning =
        if
            afterHeadline < lines.Length
            && HeadlineEdit.planningLineRegex.IsMatch(lines.[afterHeadline].TrimStart())
        then
            afterHeadline + 1
        else
            afterHeadline

    let newPropLines =
        newProps |> List.map (fun (k, v) -> sprintf ":%s: %s" k v) |> Array.ofList

    let hasDrawer =
        afterPlanning < lines.Length
        && lines.[afterPlanning].TrimStart() = ":PROPERTIES:"

    if hasDrawer then
        let endLineIdx =
            seq { afterPlanning .. lines.Length - 1 }
            |> Seq.tryFind (fun i -> lines.[i].TrimStart() = ":END:")

        match endLineIdx with
        | Some ei ->
            let before = lines.[0 .. ei - 1]
            let after = lines.[ei..]
            Array.concat [ before; newPropLines; after ] |> String.concat "\n"
        | None -> subtreeContent
    else
        let drawerLines = Array.concat [ [| ":PROPERTIES:" |]; newPropLines; [| ":END:" |] ]
        let before = lines.[0 .. afterPlanning - 1]
        let after = lines.[afterPlanning..]
        Array.concat [ before; drawerLines; after ] |> String.concat "\n"

let archive
    (srcContent: string)
    (srcPos: int64)
    (archiveContent: string)
    (srcFile: string)
    (outlinePath: string list)
    (now: DateTime)
    : string * string =
    let subtree = Subtree.extractSubtree srcContent srcPos
    let headlineLine = subtree.Split([| '\n' |]).[0]
    let m = headlineRegex.Match(headlineLine)

    let todoState =
        if m.Success && m.Groups.[2].Success then
            Some m.Groups.[2].Value
        else
            None

    let subtreeHeadlineMatch = Regex.Match(subtree, @"^(\*+) ")

    let currentLevel =
        if subtreeHeadlineMatch.Success then
            subtreeHeadlineMatch.Groups.[1].Value.Length
        else
            1

    let adjustedSubtree = Subtree.adjustLevels subtree (1 - currentLevel)
    let archiveTime = HeadlineEdit.formatInactiveTimestamp now
    let category = System.IO.Path.GetFileNameWithoutExtension(srcFile)
    let olpath = String.concat "/" outlinePath

    let props =
        [ ("ARCHIVE_TIME", archiveTime)
          ("ARCHIVE_FILE", srcFile)
          ("ARCHIVE_OLPATH", olpath)
          ("ARCHIVE_CATEGORY", category) ]
        @ (match todoState with
           | Some s -> [ ("ARCHIVE_TODO", s) ]
           | None -> [])

    let stampedSubtree = addPropertiesToSubtree adjustedSubtree props

    let separator =
        if archiveContent.Length > 0 && not (archiveContent.EndsWith("\n")) then
            "\n"
        else
            ""

    let newArchive = archiveContent + separator + stampedSubtree + "\n"
    let newSrc = Subtree.removeSubtree srcContent srcPos
    newSrc, newArchive
