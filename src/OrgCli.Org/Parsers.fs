module OrgCli.Org.Parsers

open System
open FParsec
open OrgCli.Org

// Helper to run a parser and get result or error
let runParser p str =
    match run p str with
    | Success(result, _, _) -> Result.Ok result
    | Failure(errorMsg, _, _) -> Result.Error errorMsg

// Basic character parsers
let ws = spaces
let ws1 = spaces1
let restOfLine = restOfLine true
let eol = skipNewline <|> eof

// Timestamp parsing
let pTimestampType =
    (pchar '<' >>% TimestampType.Active) <|> (pchar '[' >>% TimestampType.Inactive)

let pTimestampClose tsType =
    match tsType with
    | TimestampType.Active -> pchar '>'
    | TimestampType.Inactive -> pchar ']'

let pDate =
    pipe3 (pint32 .>> pchar '-') (pint32 .>> pchar '-') pint32 (fun y m d -> DateTime(y, m, d))

let pDayName =
    choice
        [ pstring "Mon"
          pstring "Tue"
          pstring "Wed"
          pstring "Thu"
          pstring "Fri"
          pstring "Sat"
          pstring "Sun" ]

let pTime = pipe2 (pint32 .>> pchar ':') pint32 (fun h m -> TimeSpan(h, m, 0))

let pRepeater =
    many1Chars (anyOf "+.") .>>. many1Chars (digit <|> letter)
    |>> fun (prefix, value) -> prefix + value

let pDelay =
    pstring "-" .>>. many1Chars (digit <|> letter)
    |>> fun (prefix, value) -> prefix + value

let pTimestamp: Parser<Timestamp, unit> =
    parse {
        let! tsType = pTimestampType
        let! date = pDate
        let! _ = opt (attempt (ws1 >>. pDayName))
        let! timeOpt = opt (attempt (ws1 >>. pTime))
        let! repeaterOpt = opt (attempt (ws1 >>. pRepeater))
        let! delayOpt = opt (attempt (ws1 >>. pDelay))
        let! _ = pTimestampClose tsType

        let finalDate =
            match timeOpt with
            | Some ts -> date.Add(ts)
            | None -> date

        return
            { Type = tsType
              Date = finalDate
              HasTime = Option.isSome timeOpt
              Repeater = repeaterOpt
              Delay = delayOpt
              RangeEnd = None }
    }

let pTimestampRange: Parser<Timestamp, unit> =
    pTimestamp .>>. opt (attempt (pstring "--" >>. pTimestamp))
    |>> fun (start, endOpt) ->
        match endOpt with
        | None -> start
        | Some endTs ->
            { start with
                RangeEnd = Some { endTs with RangeEnd = None } }

// Link parsing
// Format: [[type:path][description]] or [[type:path]] or [[path]]
let private pLinkDescription: Parser<string, unit> =
    pstring "][" >>. manyCharsTill anyChar (pstring "]]")

/// Parse a bracket link, returning position 0 (actual position set by findAllLinksWithPositions)
let pBracketLink: Parser<OrgLink, unit> =
    parse {
        let! _ = pstring "[["
        let! fullPath = manyCharsTill anyChar (lookAhead (pstring "]]" <|> pstring "]["))
        let! descOpt = opt (attempt pLinkDescription)
        let! _ = if descOpt.IsNone then pstring "]]" >>% () else preturn ()

        // Parse the path into type and actual path
        // Could be "id:uuid", "https://url", "roam:title", or just "path"
        let linkType, path, searchOpt =
            // Check for search option (::)
            let pathWithoutSearch, searchOption =
                match fullPath.LastIndexOf("::") with
                | -1 -> fullPath, None
                | idx -> fullPath.Substring(0, idx), Some(fullPath.Substring(idx + 2))

            match pathWithoutSearch.IndexOf(':') with
            | -1 -> "fuzzy", pathWithoutSearch, searchOption
            | idx ->
                let t = pathWithoutSearch.Substring(0, idx)
                let p = pathWithoutSearch.Substring(idx + 1)
                t, p, searchOption // Keep path as-is, including // prefix

        return
            { LinkType = linkType
              Path = path
              Description = descOpt
              SearchOption = searchOpt
              Position = 0 // Set by caller
            }
    }

/// Find all links in a string with their character positions
let findAllLinks (text: string) : OrgLink list =
    let rec findLinks (startIdx: int) acc =
        match text.IndexOf("[[", startIdx) with
        | -1 -> List.rev acc
        | idx ->
            // Find matching ]]
            let endIdx = text.IndexOf("]]", idx)

            if endIdx = -1 then
                List.rev acc
            else
                let linkText = text.Substring(idx, endIdx - idx + 2)

                match runParser pBracketLink linkText with
                | Result.Ok link ->
                    // Set the actual character position
                    let linkWithPos = { link with Position = idx }
                    findLinks (endIdx + 2) (linkWithPos :: acc)
                | Result.Error _ -> findLinks (endIdx + 2) acc

    findLinks 0 []

// Property drawer parsing
let pPropertyKey = pchar ':' >>. many1Chars (noneOf ":\n\r") .>> pchar ':'

let pPropertyValue = ws >>. restOfLine

let pProperty: Parser<Property, unit> =
    pipe2 pPropertyKey pPropertyValue (fun k v -> { Key = k.Trim(); Value = v.Trim() })

let pPropertiesStart = pstring ":PROPERTIES:" .>> eol
let pPropertiesEnd = ws >>. pstring ":END:" .>> optional eol

let pPropertyDrawer: Parser<PropertyDrawer, unit> =
    parse {
        let! _ = ws >>. pPropertiesStart
        let! props = manyTill (ws >>. pProperty) (lookAhead (ws >>. pPropertiesEnd))
        let! _ = ws >>. pPropertiesEnd
        return { Properties = props }
    }

// Keyword parsing (#+KEY: value)
let pKeyword: Parser<Keyword, unit> =
    parse {
        let! _ = pstring "#+"
        let! key = many1Chars (noneOf ":\n\r")
        let! _ = pchar ':'
        let! _ = ws
        let! value = restOfLine

        return
            { Key = key.Trim()
              Value = value.Trim() }
    }

// Headline parsing
let pStars: Parser<int, unit> = many1Chars (pchar '*') |>> String.length

let pTodoKeywordFrom (keywords: string list) : Parser<string, unit> =
    keywords |> List.sortByDescending String.length |> List.map pstring |> choice

let pTodoKeyword: Parser<string, unit> =
    pTodoKeywordFrom (Types.allKeywords Types.defaultTodoKeywords)

let pPriority: Parser<Priority, unit> =
    pstring "[#" >>. anyOf "ABCDEFGHIJKLMNOPQRSTUVWXYZ" .>> pchar ']' |>> Priority

let pTags: Parser<string list, unit> =
    pchar ':' >>. sepBy1 (many1Chars (noneOf ":\n\r ")) (pchar ':') .>> pchar ':'

let pHeadlineTitle =
    manyCharsTill anyChar (lookAhead ((ws >>. pTags .>> eol |>> ignore) <|> eol))

let pHeadline: Parser<Headline, unit> =
    parse {
        let! pos = getPosition
        let! level = pStars
        let! _ = ws1
        let! todoOpt = opt (attempt (pTodoKeyword .>> ws1))
        let! priorityOpt = opt (attempt (pPriority .>> ws1))
        let! title = pHeadlineTitle
        let! tags = opt (attempt (ws >>. pTags))
        let! _ = eol

        return
            { Level = level
              TodoKeyword = todoOpt
              Priority = priorityOpt
              Title = title.Trim()
              Tags = tags |> Option.defaultValue []
              Planning = None // Parsed separately
              Properties = None // Parsed separately
              Position = pos.Index }
    }

// Planning line parsing (SCHEDULED: <timestamp> DEADLINE: <timestamp>)
let pPlanningKeyword =
    choice
        [ pstring "SCHEDULED:" >>% "SCHEDULED"
          pstring "DEADLINE:" >>% "DEADLINE"
          pstring "CLOSED:" >>% "CLOSED" ]

let pPlanningItem = pPlanningKeyword .>> ws .>>. pTimestampRange

let pPlanningLine: Parser<Planning, unit> =
    parse {
        let! _ = ws
        let! first = pPlanningItem
        let! rest = many (attempt (ws1 >>. pPlanningItem))
        let! _ = eol
        let items = first :: rest

        let scheduled =
            items |> List.tryFind (fun (k, _) -> k = "SCHEDULED") |> Option.map snd

        let deadline =
            items |> List.tryFind (fun (k, _) -> k = "DEADLINE") |> Option.map snd

        let closed = items |> List.tryFind (fun (k, _) -> k = "CLOSED") |> Option.map snd

        return
            { Scheduled = scheduled
              Deadline = deadline
              Closed = closed }
    }

// Clock entry parsing
// CLOCK: [start]--[end] => H:MM
// CLOCK: [start]
let pClockDuration: Parser<TimeSpan, unit> =
    pint32 .>> pchar ':' .>>. pint32 |>> fun (h, m) -> TimeSpan(h, m, 0)

let pClockEntry: Parser<ClockEntry, unit> =
    parse {
        let! _ = ws >>. pstring "CLOCK:" >>. ws
        let! startTs = pTimestamp
        let! endPart = opt (attempt (pstring "--" >>. pTimestamp))
        let! duration = opt (attempt (ws >>. pstring "=>" >>. ws >>. pClockDuration))
        let! _ = eol

        return
            { Start = startTs
              End = endPart
              Duration = duration }
    }
