module OrgCli.Index.TaskPresentation

open System
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open OrgCli.Org

let private str (value: string) : JsonNode = JsonValue.Create value
let private num (value: int) : JsonNode = JsonValue.Create value
let private flag (value: bool) : JsonNode = JsonValue.Create value

let private obj (fields: (string * JsonNode) list) : JsonNode =
    let result = JsonObject()

    for key, value in fields do
        result[key] <- value

    result

let private arr values : JsonNode =
    let result = JsonArray()

    for value in values do
        result.Add(value: JsonNode)

    result

let ownText (content: string) (doc: OrgDocument) (h: Headline) =
    let finish =
        doc.Headlines
        |> List.tryFind (fun next -> next.Position > h.Position)
        |> Option.map (fun next -> int next.Position)
        |> Option.defaultValue content.Length

    content.Substring(int h.Position, finish - int h.Position)

let ancestors (doc: OrgDocument) (h: Headline) =
    doc.Headlines
    |> List.takeWhile (fun p -> p.Position < h.Position)
    |> List.fold (fun stack p -> p :: (stack |> List.skipWhile (fun old -> old.Level >= p.Level))) []
    |> List.filter (fun p -> p.Level < h.Level)
    |> List.rev

type private Item =
    { Line: int
      Indent: int
      Parent: int option
      Check: Match
      Text: string }

let private listPattern =
    Regex(@"^(\s*)(?:[-+*]|\d+[.)])\s+(?:\[@\d+\]\s+)?(?:\[([ Xx-])\]\s*)?(.*)$")

let private cookie = Regex(@"\[(?:\d*/\d*|\d*%)\]")

let private items (source: string) =
    let mutable block = false
    let mutable drawer = false
    let mutable stack: (int * int) list = []

    source.Split('\n')
    |> Array.mapi (fun line text ->
        let trimmed = text.Trim()

        if trimmed.StartsWith("#+BEGIN_", StringComparison.OrdinalIgnoreCase) then
            block <- true
        elif trimmed.StartsWith("#+END_", StringComparison.OrdinalIgnoreCase) then
            block <- false
        elif trimmed = ":END:" then
            drawer <- false
        elif Regex.IsMatch(trimmed, @"^:[A-Za-z_]+:$") then
            drawer <- true

        let m = listPattern.Match text

        if line > 0 && not block && not drawer && m.Success then
            let indent = m.Groups[1].Value.Replace("\t", "        ").Length
            stack <- stack |> List.skipWhile (fun (_, depth) -> depth >= indent)
            let parent = stack |> List.tryHead |> Option.map fst
            stack <- (line, indent) :: stack

            Some
                { Line = line
                  Indent = indent
                  Parent = parent
                  Check = m
                  Text = m.Groups[3].Value }
        else
            if trimmed <> "" && not (Char.IsWhiteSpace(text[0])) then
                stack <- []

            None)
    |> Array.choose id
    |> Array.toList

let private checkedItem item = item.Check.Groups[2].Success

let private doneItem item =
    item.Check.Groups[2].Value.ToUpperInvariant() = "X"

let private progress values =
    let boxes = values |> List.filter checkedItem
    boxes |> List.filter doneItem |> List.length, boxes.Length

let private replaceCookie doneCount total (line: string) =
    cookie.Replace(
        line,
        MatchEvaluator(fun m ->
            if m.Value.Contains('/') then
                sprintf "[%d/%d]" doneCount total
            else
                sprintf "[%d%%]" (if total = 0 then 0 else doneCount * 100 / total))
    )

/// Changes only checked list items and existing statistics cookies in this heading's own text.
let toggleCheckbox (content: string) doc (h: Headline) line checkedValue =
    let source = ownText content doc h
    let parsed = items source

    let target =
        parsed
        |> List.tryFind (fun item -> item.Line = line && checkedItem item)
        |> Option.defaultWith (fun () -> invalidArg "line" "This line is not an editable checkbox")

    if Regex.IsMatch(source, @"(?im)^\s*#\+ATTR_ORG:.*:radio\s+t") then
        invalidArg "line" "Radio checklists must be edited in the Org file"

    let descendants =
        parsed
        |> List.skipWhile (fun item -> item.Line <= target.Line)
        |> List.takeWhile (fun item -> item.Indent > target.Indent)

    let ancestorLines =
        let rec loop parent =
            match parent with
            | None -> []
            | Some n -> n :: loop (parsed |> List.find (fun item -> item.Line = n)).Parent

        loop target.Parent

    if
        checkedValue
        && (Types.tryGetProperty "ORDERED" h.Properties
            |> Option.exists (fun value -> value <> "nil" && value <> "false"))
    then
        if
            parsed
            |> List.exists (fun item ->
                item.Line < line
                && not (List.contains item.Line ancestorLines)
                && checkedItem item
                && not (doneItem item))
        then
            invalidArg "line" "This ORDERED checklist must be completed in sequence"

    let lines = source.Split('\n')

    let setState (item: Item) value =
        let index = item.Check.Groups[2].Index
        lines[item.Line] <- lines[item.Line].Remove(index, 1).Insert(index, value)

    for item in target :: descendants do
        if checkedItem item then
            setState item (if checkedValue then "X" else " ")

    for parentLine in ancestorLines do
        let current = items (String.concat "\n" lines)
        let parent = current |> List.find (fun item -> item.Line = parentLine)

        let children =
            current
            |> List.filter (fun item -> item.Parent = Some parentLine && checkedItem item)

        if checkedItem parent && not children.IsEmpty then
            setState
                parent
                (if children |> List.forall doneItem then
                     "X"
                 elif children |> List.forall (fun child -> child.Check.Groups[2].Value = " ") then
                     " "
                 else
                     "-")

    let updated = items (String.concat "\n" lines)

    for item in updated do
        let doneCount, total =
            updated |> List.filter (fun child -> child.Parent = Some item.Line) |> progress

        if total > 0 then
            lines[item.Line] <- replaceCookie doneCount total lines[item.Line]

    let data =
        Types.tryGetProperty "COOKIE_DATA" h.Properties
        |> Option.defaultValue "checkbox"

    if not (data.Split(' ') |> Array.contains "todo") then
        let counted =
            if data.Contains("recursive") then
                updated
            else
                updated |> List.filter (fun item -> item.Parent.IsNone)

        let doneCount, total = progress counted
        lines[0] <- replaceCookie doneCount total lines[0]

    let after = String.concat "\n" lines

    content.Substring(0, int h.Position)
    + after
    + content.Substring(int h.Position + source.Length)

let details content (doc: OrgDocument) (h: Headline) cfg reference =
    let source = ownText content doc h
    let parents = ancestors doc h

    let descendants =
        doc.Headlines
        |> List.skipWhile (fun child -> child.Position <= h.Position)
        |> List.takeWhile (fun child -> child.Level > h.Level)

    let mutable shallowest = Int32.MaxValue

    let children =
        descendants
        |> List.filter (fun child ->
            if child.Level <= shallowest then
                shallowest <- child.Level
                true
            else
                false)

    let hasChildren =
        doc.Headlines
        |> List.pairwise
        |> List.choose (fun (heading, next) ->
            if next.Level > heading.Level then
                Some heading.Position
            else
                None)
        |> Set.ofList

    let node (heading: Headline) =
        obj
            [ "ref", str (reference heading)
              "title", str heading.Title
              "state", str (heading.TodoKeyword |> Option.defaultValue "")
              "level", num heading.Level
              "has_children", flag (hasChildren.Contains heading.Position) ]

    let properties (drawer: PropertyDrawer option) =
        drawer |> Option.map (fun p -> p.Properties) |> Option.defaultValue []

    let local = properties h.Properties

    let keys =
        (local
         @ (parents |> List.collect (fun parent -> properties parent.Properties))
         @ properties doc.FileProperties
         |> List.map (fun p -> p.Key))
        @ (doc.Keywords
           |> List.choose (fun keyword ->
               if keyword.Key.ToUpperInvariant() = "PROPERTY" then
                   keyword.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries) |> Array.tryHead
               else
                   Some keyword.Key))
        |> List.distinctBy (fun key -> key.ToUpperInvariant())

    let inspector =
        keys
        |> List.choose (fun key ->
            Headlines.resolveProperty cfg doc h key
            |> Option.map (fun value ->
                let explicit =
                    local
                    |> List.exists (fun p -> p.Key.Equals(key, StringComparison.OrdinalIgnoreCase))

                let origin =
                    if explicit then
                        "This heading"
                    else
                        parents
                        |> List.rev
                        |> List.tryFind (fun parent -> Types.tryGetProperty key parent.Properties |> Option.isSome)
                        |> Option.map (fun parent -> parent.Title)
                        |> Option.defaultValue "File settings"

                obj
                    [ "key", str key
                      "value", str value
                      "inherited", flag (not explicit)
                      "source", str origin ]))

    let section = HeadlineEdit.split source 0L

    let mutable inBlock = false

    let clocks =
        source.Split('\n')
        |> Array.choose (fun line ->
            let trimmed = line.Trim()

            if trimmed.StartsWith("#+BEGIN_", StringComparison.OrdinalIgnoreCase) then
                inBlock <- true
            elif trimmed.StartsWith("#+END_", StringComparison.OrdinalIgnoreCase) then
                inBlock <- false

            if not inBlock && trimmed.StartsWith("CLOCK:") then
                try
                    match Parsers.runParser Parsers.pClockEntry (trimmed + "\n") with
                    | Ok entry -> Some entry
                    | Error _ -> None
                with _ ->
                    None
            else
                None)
        |> Array.toList

    let minutes (entry: ClockEntry) =
        entry.Duration
        |> Option.orElseWith (fun () -> entry.End |> Option.map (fun finish -> finish.Date - entry.Start.Date))

    let closedMinutes =
        clocks
        |> List.filter (fun entry -> entry.End.IsSome)
        |> List.sumBy (fun entry ->
            minutes entry
            |> Option.map (fun span -> int span.TotalMinutes)
            |> Option.defaultValue 0)

    let taskChildren = children |> List.filter (fun child -> child.TodoKeyword.IsSome)
    let boxes = items source

    let checkedCount, count =
        boxes |> List.filter (fun item -> item.Parent.IsNone) |> progress

    obj
        [ "own_source", str source
          "parents", parents |> Seq.map node |> arr
          "children", children |> Seq.map node |> arr
          "child_tasks_done",
          num (
              taskChildren
              |> List.filter (fun child -> Agenda.isDoneState cfg child.TodoKeyword)
              |> List.length
          )
          "child_tasks_total", num taskChildren.Length
          "checkbox_done", num checkedCount
          "checkbox_total", num count
          "checkboxes",
          boxes
          |> List.filter checkedItem
          |> Seq.map (fun item ->
              obj
                  [ "line", num item.Line
                    "state", str item.Check.Groups[2].Value
                    "text", str item.Text
                    "indent", num item.Indent ])
          |> arr
          "properties", inspector |> arr
          "closed",
          (h.Planning
           |> Option.bind (fun planning -> planning.Closed)
           |> Option.map (Writer.formatTimestamp >> str)
           |> Option.defaultValue null)
          "clock_total_minutes", num closedMinutes
          "clocks",
          clocks
          |> Seq.map (fun entry ->
              obj
                  [ "start", str (Writer.formatTimestamp entry.Start)
                    "end",
                    entry.End
                    |> Option.map (Writer.formatTimestamp >> str)
                    |> Option.defaultValue null
                    "minutes",
                    minutes entry
                    |> Option.map (fun span -> num (int span.TotalMinutes))
                    |> Option.defaultValue null
                    "running_minutes",
                    (if entry.End.IsNone then
                         num (max 0 (int ((Runtime.now () - entry.Start.Date).TotalMinutes)))
                     else
                         null) ])
          |> arr
          "history", str (section.LogbookDrawer |> Option.defaultValue "") ]
