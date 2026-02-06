module OrgCli.Org.Writer

open System
open System.IO
open System.Text
open OrgCli.Org

/// Format a timestamp to org-mode format
let rec formatTimestamp (ts: Timestamp) : string =
    let openChar, closeChar =
        match ts.Type with
        | TimestampType.Active -> '<', '>'
        | TimestampType.Inactive -> '[', ']'

    let dateStr = ts.Date.ToString("yyyy-MM-dd")
    let dayName = ts.Date.ToString("ddd", System.Globalization.CultureInfo.InvariantCulture)

    let timeStr =
        if ts.HasTime then
            " " + ts.Date.ToString("HH:mm")
        else ""

    let repeaterStr =
        ts.Repeater |> Option.map (fun r -> " " + r) |> Option.defaultValue ""

    let delayStr =
        ts.Delay |> Option.map (fun d -> " " + d) |> Option.defaultValue ""

    let startStr = sprintf "%c%s %s%s%s%s%c" openChar dateStr dayName timeStr repeaterStr delayStr closeChar

    match ts.RangeEnd with
    | None -> startStr
    | Some endTs -> startStr + "--" + formatTimestamp { endTs with RangeEnd = None }

/// Format an org link
let formatLink (link: OrgLink) : string =
    let pathWithSearch =
        match link.SearchOption with
        | Some s -> sprintf "%s::%s" link.Path s
        | None -> link.Path

    let fullPath =
        if link.LinkType = "fuzzy" then pathWithSearch
        else sprintf "%s:%s" link.LinkType pathWithSearch

    match link.Description with
    | Some desc -> sprintf "[[%s][%s]]" fullPath desc
    | None -> sprintf "[[%s]]" fullPath

/// Format a property drawer
let formatPropertyDrawer (props: PropertyDrawer) : string =
    let sb = StringBuilder()
    sb.AppendLine(":PROPERTIES:") |> ignore
    for prop in props.Properties do
        sb.AppendLine(sprintf ":%s: %s" prop.Key prop.Value) |> ignore
    sb.Append(":END:") |> ignore
    sb.ToString()

/// Format a planning line
let formatPlanning (planning: Planning) : string =
    let parts =
        [
            planning.Scheduled |> Option.map (fun ts -> "SCHEDULED: " + formatTimestamp ts)
            planning.Deadline |> Option.map (fun ts -> "DEADLINE: " + formatTimestamp ts)
            planning.Closed |> Option.map (fun ts -> "CLOSED: " + formatTimestamp ts)
        ]
        |> List.choose id

    String.Join(" ", parts)

/// Format a headline
let formatHeadline (h: Headline) : string =
    let sb = StringBuilder()

    // Stars
    sb.Append(String.replicate h.Level "*") |> ignore
    sb.Append(" ") |> ignore

    // TODO keyword
    match h.TodoKeyword with
    | Some todo ->
        sb.Append(todo) |> ignore
        sb.Append(" ") |> ignore
    | None -> ()

    // Priority
    match h.Priority with
    | Some (Priority p) ->
        sb.Append(sprintf "[#%c] " p) |> ignore
    | None -> ()

    // Title
    sb.Append(h.Title) |> ignore

    // Tags
    if not (List.isEmpty h.Tags) then
        sb.Append(" :") |> ignore
        sb.Append(String.Join(":", h.Tags)) |> ignore
        sb.Append(":") |> ignore

    sb.ToString()

/// Format keywords
let formatKeywords (keywords: Keyword list) : string =
    keywords
    |> List.map (fun kw -> sprintf "#+%s: %s" kw.Key kw.Value)
    |> String.concat "\n"

/// Create a new file-level node content
let createFileNode (id: string) (title: string) (tags: string list) (aliases: string list) (refs: string list) : string =
    let sb = StringBuilder()

    // Property drawer
    sb.AppendLine(":PROPERTIES:") |> ignore
    sb.AppendLine(sprintf ":ID: %s" id) |> ignore
    if not (List.isEmpty aliases) then
        let aliasStr = aliases |> List.map (fun a -> if a.Contains(" ") then sprintf "\"%s\"" a else a) |> String.concat " "
        sb.AppendLine(sprintf ":ROAM_ALIASES: %s" aliasStr) |> ignore
    if not (List.isEmpty refs) then
        let refStr = refs |> List.map (fun r -> if r.Contains(" ") then sprintf "\"%s\"" r else r) |> String.concat " "
        sb.AppendLine(sprintf ":ROAM_REFS: %s" refStr) |> ignore
    sb.AppendLine(":END:") |> ignore

    // Title
    sb.AppendLine(sprintf "#+title: %s" title) |> ignore

    // Filetags
    if not (List.isEmpty tags) then
        sb.AppendLine(sprintf "#+filetags: :%s:" (String.Join(":", tags))) |> ignore

    sb.ToString()

/// Create a headline node content
let createHeadlineNode
    (level: int)
    (id: string)
    (title: string)
    (tags: string list)
    (todoKeyword: string option)
    (priority: char option)
    (aliases: string list)
    (refs: string list)
    (scheduled: Timestamp option)
    (deadline: Timestamp option) : string =

    let sb = StringBuilder()

    // Headline
    sb.Append(String.replicate level "*") |> ignore
    sb.Append(" ") |> ignore

    match todoKeyword with
    | Some todo ->
        sb.Append(todo) |> ignore
        sb.Append(" ") |> ignore
    | None -> ()

    match priority with
    | Some p ->
        sb.Append(sprintf "[#%c] " p) |> ignore
    | None -> ()

    sb.Append(title) |> ignore

    if not (List.isEmpty tags) then
        sb.Append(" :") |> ignore
        sb.Append(String.Join(":", tags)) |> ignore
        sb.Append(":") |> ignore

    sb.AppendLine() |> ignore

    // Planning
    let planningParts =
        [
            scheduled |> Option.map (fun ts -> "SCHEDULED: " + formatTimestamp ts)
            deadline |> Option.map (fun ts -> "DEADLINE: " + formatTimestamp ts)
        ]
        |> List.choose (fun x -> x)

    if not (List.isEmpty planningParts) then
        sb.AppendLine(String.Join(" ", planningParts)) |> ignore

    // Property drawer
    sb.AppendLine(":PROPERTIES:") |> ignore
    sb.AppendLine(sprintf ":ID: %s" id) |> ignore
    if not (List.isEmpty aliases) then
        let aliasStr = aliases |> List.map (fun a -> if a.Contains(" ") then sprintf "\"%s\"" a else a) |> String.concat " "
        sb.AppendLine(sprintf ":ROAM_ALIASES: %s" aliasStr) |> ignore
    if not (List.isEmpty refs) then
        let refStr = refs |> List.map (fun r -> if r.Contains(" ") then sprintf "\"%s\"" r else r) |> String.concat " "
        sb.AppendLine(sprintf ":ROAM_REFS: %s" refStr) |> ignore
    sb.AppendLine(":END:") |> ignore

    sb.ToString()

/// Insert a link at a position in file content
let insertLink (content: string) (position: int) (link: OrgLink) : string =
    let linkText = formatLink link
    content.Insert(position, linkText)

let private propsDrawerRegex = System.Text.RegularExpressions.Regex(@"^:PROPERTIES:\s*$", System.Text.RegularExpressions.RegexOptions.Multiline)
let private endDrawerRegex = System.Text.RegularExpressions.Regex(@"^:END:\s*$", System.Text.RegularExpressions.RegexOptions.Multiline)

/// Find the start of the real :PROPERTIES: drawer at or after nodePosition,
/// skipping occurrences inside source blocks.
let private findPropertiesDrawer (content: string) (nodePosition: int) : (int * int) option =
    let blockRanges = Document.computeBlockRanges content
    let mutable m = propsDrawerRegex.Match(content, nodePosition)
    let mutable found = None
    while m.Success && found.IsNone do
        let isInsideBlock = blockRanges |> List.exists (fun (s, e) -> m.Index >= s && m.Index < e)
        if not isInsideBlock then
            let endMatch = endDrawerRegex.Match(content, m.Index + m.Length)
            if endMatch.Success then
                let isEndInsideBlock = blockRanges |> List.exists (fun (s, e) -> endMatch.Index >= s && endMatch.Index < e)
                if not isEndInsideBlock then
                    found <- Some (m.Index, endMatch.Index)
        m <- m.NextMatch()
    found

/// Add a property to an existing property drawer in content
let addProperty (content: string) (nodePosition: int) (key: string) (value: string) : string =
    match findPropertiesDrawer content nodePosition with
    | None -> content
    | Some (propsStart, propsEnd) ->
        let drawerContent = content.Substring(propsStart, propsEnd - propsStart)
        let propPattern = sprintf ":%s:" (key.ToUpper())

        if drawerContent.Contains(propPattern) then
            let propLineStart = content.IndexOf(propPattern, propsStart)
            let propLineEnd = content.IndexOf('\n', propLineStart)
            let newLine = sprintf ":%s: %s" key value
            content.Remove(propLineStart, propLineEnd - propLineStart).Insert(propLineStart, newLine)
        else
            let newLine = sprintf ":%s: %s\n" key value
            content.Insert(propsEnd, newLine)

/// Add a value to a multi-value property (like ROAM_ALIASES)
let addToMultiValueProperty (content: string) (nodePosition: int) (key: string) (value: string) : string =
    match findPropertiesDrawer content nodePosition with
    | None -> content
    | Some (propsStart, propsEnd) ->
        let propPattern = sprintf ":%s:" (key.ToUpper())
        let propLineStart = content.IndexOf(propPattern, propsStart)

        let valueFormatted = if value.Contains(" ") then sprintf "\"%s\"" value else value

        if propLineStart < 0 || propLineStart >= propsEnd then
            let newLine = sprintf ":%s: %s\n" key valueFormatted
            content.Insert(propsEnd, newLine)
        else
            let propLineEnd = content.IndexOf('\n', propLineStart)
            let currentLine = content.Substring(propLineStart, propLineEnd - propLineStart)
            let newLine = currentLine + " " + valueFormatted
            content.Remove(propLineStart, propLineEnd - propLineStart).Insert(propLineStart, newLine)

/// Remove a value from a multi-value property
let removeFromMultiValueProperty (content: string) (nodePosition: int) (key: string) (value: string) : string =
    match findPropertiesDrawer content nodePosition with
    | None -> content
    | Some (propsStart, propsEnd) ->
        let propPattern = sprintf ":%s:" (key.ToUpper())
        let propLineStart = content.IndexOf(propPattern, propsStart)
        if propLineStart < 0 || propLineStart >= propsEnd then content
        else
            let propLineEnd = content.IndexOf('\n', propLineStart)
            let currentLine = content.Substring(propLineStart, propLineEnd - propLineStart)

            let colonIdx = currentLine.IndexOf(':', 1)
            if colonIdx < 0 then content
            else
                let valuesPart = currentLine.Substring(colonIdx + 1).Trim()
                let values = Types.splitQuotedString valuesPart

                let newValues = values |> List.filter (fun v -> v <> value)

                if List.isEmpty newValues then
                    content.Remove(propLineStart, propLineEnd - propLineStart + 1)
                else
                    let newValueStr =
                        newValues
                        |> List.map (fun v -> if v.Contains(" ") then sprintf "\"%s\"" v else v)
                        |> String.concat " "
                    let newLine = sprintf ":%s: %s" key newValueStr
                    content.Remove(propLineStart, propLineEnd - propLineStart).Insert(propLineStart, newLine)
