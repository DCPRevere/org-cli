namespace OrgCli.Org

open System

/// Character offset (0-based) from the start of the file content string.
/// Must point to the first `*` of a headline for HeadlineEdit.split.
type CharPos = int64

/// Org-mode timestamp types
[<RequireQualifiedAccess>]
type TimestampType =
    | Active // <2024-01-15 Mon>
    | Inactive // [2024-01-15 Mon]

/// Represents an org-mode timestamp
type Timestamp =
    { Type: TimestampType
      Date: DateTime
      HasTime: bool
      Repeater: string option // e.g., "+1w", ".+1d"
      Delay: string option // e.g., "-2d"
      RangeEnd: Timestamp option } // end of <start>--<end> range, or None

/// Represents an org-mode link
type OrgLink =
    { LinkType: string // "id", "roam", "https", "file", etc.
      Path: string // The target (ID, URL, filename, etc.)
      Description: string option
      SearchOption: string option // Text after :: in path
      Position: int } // Character position in file

/// Planning information (SCHEDULED, DEADLINE, CLOSED)
type Planning =
    { Scheduled: Timestamp option
      Deadline: Timestamp option
      Closed: Timestamp option }

/// A property from a property drawer
type Property = { Key: string; Value: string }

/// Represents the properties block
type PropertyDrawer = { Properties: Property list }

/// A file-level keyword (#+KEY: value)
type Keyword = { Key: string; Value: string }

/// Priority values A-Z
type Priority = Priority of char

/// Represents an org headline
type Headline =
    { Level: int
      TodoKeyword: string option
      Priority: Priority option
      Title: string
      Tags: string list
      Planning: Planning option
      Properties: PropertyDrawer option
      Position: CharPos }

/// An element that can appear in an org document
[<RequireQualifiedAccess>]
type OrgElement =
    | Keyword of Keyword
    | PropertyDrawer of PropertyDrawer
    | Headline of Headline
    | Paragraph of string
    | Link of OrgLink
    | Other of string

/// Represents a parsed org document
type OrgDocument =
    {
        FilePath: string option
        Keywords: Keyword list
        FileProperties: PropertyDrawer option
        Headlines: Headline list
        /// All links found in the document with their containing headline ID
        Links: (OrgLink * string option) list // (link, containing node ID)
    }

type ClockEntry =
    { Start: Timestamp
      End: Timestamp option
      Duration: TimeSpan option }

[<RequireQualifiedAccess>]
type RepeaterType =
    | Standard // +1w
    | FromToday // .+1d
    | NextFuture // ++1m

type ResolvedLink =
    { Link: OrgLink
      TargetFile: string option
      TargetHeadline: string option
      TargetPos: CharPos option }

[<RequireQualifiedAccess>]
type CliErrorType =
    | HeadlineNotFound
    | FileNotFound
    | ParseError
    | InvalidArgs
    | InternalError

type CliError =
    { Type: CliErrorType
      Message: string
      Detail: string option }

type TagDef = { Name: string; FastKey: char option }

[<RequireQualifiedAccess>]
type TagGroup =
    | Regular of TagDef list
    | MutuallyExclusive of TagDef list

[<RequireQualifiedAccess>]
type LogAction =
    | NoLog
    | LogTime
    | LogNote

type TodoKeywordDef =
    { Keyword: string
      LogOnEnter: LogAction
      LogOnLeave: LogAction }

type TodoKeywordConfig =
    { ActiveStates: TodoKeywordDef list
      DoneStates: TodoKeywordDef list }

type PriorityConfig =
    { Highest: char
      Lowest: char
      Default: char }

type OrgConfig =
    { TodoKeywords: TodoKeywordConfig
      Priorities: PriorityConfig
      LogDone: LogAction
      LogRepeat: LogAction
      LogReschedule: LogAction
      LogRedeadline: LogAction
      LogRefile: LogAction
      LogIntoDrawer: string option
      TagInheritance: bool
      InheritTags: string list option
      TagsExcludeFromInheritance: string list
      PropertyInheritance: bool
      InheritProperties: string list
      DeadlineWarningDays: int
      ArchiveLocation: string option }

module Types =
    /// Splits a string respecting quoted segments
    /// e.g., "foo \"bar baz\" qux" -> ["foo"; "bar baz"; "qux"]
    let splitQuotedString (s: string) : string list =
        let rec parse (chars: char list) (current: char list) (inQuote: bool) (acc: string list) =
            match chars with
            | [] ->
                let final = current |> List.rev |> Array.ofList |> String

                if String.IsNullOrWhiteSpace(final) then
                    List.rev acc
                else
                    List.rev (final :: acc)
            | '\\' :: '"' :: rest when inQuote ->
                // Escaped quote inside quoted string - treat as literal quote
                parse rest ('"' :: current) true acc
            | '"' :: rest when not inQuote ->
                // Start quote
                parse rest current true acc
            | '"' :: rest when inQuote ->
                // End quote - emit current
                let word = current |> List.rev |> Array.ofList |> String
                parse rest [] false (word :: acc)
            | ' ' :: rest when not inQuote ->
                // Space outside quote - emit current if non-empty
                let word = current |> List.rev |> Array.ofList |> String

                if String.IsNullOrWhiteSpace(word) then
                    parse rest [] false acc
                else
                    parse rest [] false (word :: acc)
            | c :: rest -> parse rest (c :: current) inQuote acc

        parse (List.ofSeq s) [] false []

    /// Get the ID property from a property drawer
    let tryGetId (props: PropertyDrawer option) =
        props
        |> Option.bind (fun pd ->
            pd.Properties
            |> List.tryFind (fun p -> p.Key.ToUpperInvariant() = "ID")
            |> Option.map (fun p -> p.Value))

    /// Get a property value by key
    let tryGetProperty (key: string) (props: PropertyDrawer option) =
        props
        |> Option.bind (fun pd ->
            pd.Properties
            |> List.tryFind (fun p -> p.Key.ToUpperInvariant() = key.ToUpperInvariant())
            |> Option.map (fun p -> p.Value))

    /// Get ROAM_ALIASES as a list
    let getRoamAliases (props: PropertyDrawer option) =
        tryGetProperty "ROAM_ALIASES" props
        |> Option.map splitQuotedString
        |> Option.defaultValue []

    /// Get ROAM_REFS as a list
    let getRoamRefs (props: PropertyDrawer option) =
        tryGetProperty "ROAM_REFS" props
        |> Option.map splitQuotedString
        |> Option.defaultValue []

    /// Check if node should be excluded from org-roam
    let isRoamExcluded (props: PropertyDrawer option) =
        tryGetProperty "ROAM_EXCLUDE" props
        |> Option.map (fun v -> not (String.IsNullOrWhiteSpace(v)))
        |> Option.defaultValue false

    /// Get the title keyword from document keywords
    let tryGetTitle (keywords: Keyword list) =
        keywords
        |> List.tryFind (fun k -> k.Key.ToUpperInvariant() = "TITLE")
        |> Option.map (fun k -> k.Value)

    /// Get filetags from document keywords
    let getFileTags (keywords: Keyword list) =
        keywords
        |> List.tryFind (fun k -> k.Key.ToUpperInvariant() = "FILETAGS")
        |> Option.map (fun k -> k.Value.Split([| ':' |], StringSplitOptions.RemoveEmptyEntries) |> Array.toList)
        |> Option.defaultValue []

    let private defKeyword kw =
        { Keyword = kw
          LogOnEnter = LogAction.NoLog
          LogOnLeave = LogAction.NoLog }

    let defaultTodoKeywords: TodoKeywordConfig =
        { ActiveStates =
            [ defKeyword "TODO"
              defKeyword "NEXT"
              defKeyword "WAITING"
              defKeyword "HOLD"
              defKeyword "SOMEDAY"
              defKeyword "PROJECT" ]
          DoneStates = [ defKeyword "DONE"; defKeyword "CANCELLED"; defKeyword "CANCELED" ] }

    let defaultConfig: OrgConfig =
        { TodoKeywords = defaultTodoKeywords
          Priorities =
            { Highest = 'A'
              Lowest = 'C'
              Default = 'B' }
          LogDone = LogAction.LogTime
          LogRepeat = LogAction.LogTime
          LogReschedule = LogAction.NoLog
          LogRedeadline = LogAction.NoLog
          LogRefile = LogAction.NoLog
          LogIntoDrawer = Some "LOGBOOK"
          TagInheritance = true
          InheritTags = None
          TagsExcludeFromInheritance = []
          PropertyInheritance = false
          InheritProperties = []
          DeadlineWarningDays = 14
          ArchiveLocation = None }

    let allKeywords (config: TodoKeywordConfig) : string list =
        (config.ActiveStates |> List.map (fun d -> d.Keyword))
        @ (config.DoneStates |> List.map (fun d -> d.Keyword))

    let isActiveState (config: TodoKeywordConfig) (kw: string) : bool =
        config.ActiveStates |> List.exists (fun d -> d.Keyword = kw)

    let isDoneState (config: TodoKeywordConfig) (kw: string) : bool =
        config.DoneStates |> List.exists (fun d -> d.Keyword = kw)

    let buildHeadlineRegex (keywords: string list) : System.Text.RegularExpressions.Regex =
        let escaped = keywords |> List.map System.Text.RegularExpressions.Regex.Escape
        let kwPattern = String.Join("|", escaped)

        let pattern =
            sprintf @"^(\*+)\s+(%s)?\s*(\[#([A-Z])\])?\s*(.+?)(\s+:[\w@:\-]+:)?\s*$" kwPattern

        System.Text.RegularExpressions.Regex(pattern)

    let defaultHeadlineRegex = buildHeadlineRegex (allKeywords defaultTodoKeywords)
