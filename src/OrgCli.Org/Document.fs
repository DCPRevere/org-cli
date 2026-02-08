module OrgCli.Org.Document

open System
open System.IO
open System.Text.RegularExpressions
open FParsec
open OrgCli.Org

/// Result of parsing a headline with its content
type HeadlineWithContent =
    { Headline: Headline
      RawContent: string
      Links: OrgLink list }

/// Parse the file-level (before first headline) section
let parseFileSection (content: string) : Keyword list * PropertyDrawer option * OrgLink list =
    // Find first headline
    let headlinePattern = Regex(@"^\*+ ", RegexOptions.Multiline)
    let firstHeadlineMatch = headlinePattern.Match(content)

    let fileSectionEnd =
        if firstHeadlineMatch.Success then
            firstHeadlineMatch.Index
        else
            content.Length

    let fileSection = content.Substring(0, fileSectionEnd)

    // Parse keywords
    let keywordPattern = Regex(@"^#\+([^:]+):\s*(.*)$", RegexOptions.Multiline)

    let keywords =
        keywordPattern.Matches(fileSection)
        |> Seq.cast<Match>
        |> Seq.map (fun m ->
            { Key = m.Groups.[1].Value.Trim()
              Value = m.Groups.[2].Value.Trim() })
        |> Seq.toList

    // Parse property drawer at file level
    let propsPattern =
        Regex(@":PROPERTIES:\s*\n([\s\S]*?)\n\s*:END:", RegexOptions.Multiline)

    let propsMatch = propsPattern.Match(fileSection)

    let properties =
        if propsMatch.Success then
            let propsContent = propsMatch.Groups.[1].Value
            let propLinePattern = Regex(@"^\s*:([^:]+):\s*(.*)$", RegexOptions.Multiline)

            let props: Property list =
                propLinePattern.Matches(propsContent)
                |> Seq.cast<Match>
                |> Seq.map (fun m ->
                    { Property.Key = m.Groups.[1].Value.Trim()
                      Value = m.Groups.[2].Value.Trim() })
                |> Seq.toList

            Some { Properties = props }
        else
            None

    // Find links in file section
    let links = Parsers.findAllLinks fileSection

    keywords, properties, links

/// Parse a single headline section with a given headline regex
let parseHeadlineSectionWith (headlinePattern: Regex) (text: string) (startPos: int) : HeadlineWithContent option =

    let lines = text.Split([| '\n' |])

    if lines.Length = 0 then
        None
    else
        let firstLine = lines.[0]
        let headlineMatch = headlinePattern.Match(firstLine)

        if not headlineMatch.Success then
            None
        else
            let level = headlineMatch.Groups.[1].Value.Length

            let todoKeyword =
                let g = headlineMatch.Groups.[2]

                if g.Success && not (String.IsNullOrWhiteSpace g.Value) then
                    Some g.Value
                else
                    None

            let priority =
                let g = headlineMatch.Groups.[4]
                if g.Success then Some(Priority g.Value.[0]) else None

            let title = headlineMatch.Groups.[5].Value.Trim()

            let tags =
                let g = headlineMatch.Groups.[6]

                if g.Success then
                    g.Value.Trim().Trim(':').Split(':')
                    |> Array.filter (not << String.IsNullOrWhiteSpace)
                    |> Array.toList
                else
                    []

            let contentLines = lines |> Array.skip 1
            let rawContent = String.Join("\n", contentLines)

            // Parse planning line if present
            let planning =
                if contentLines.Length > 0 then
                    let firstContentLine = contentLines.[0].Trim()

                    if
                        firstContentLine.StartsWith("SCHEDULED:")
                        || firstContentLine.StartsWith("DEADLINE:")
                        || firstContentLine.StartsWith("CLOSED:")
                    then
                        match Parsers.runParser Parsers.pPlanningLine (firstContentLine + "\n") with
                        | Result.Ok p -> Some p
                        | Result.Error _ -> None
                    else
                        None
                else
                    None

            // Parse property drawer if present
            let properties =
                let propsPattern = Regex(@":PROPERTIES:\s*\n([\s\S]*?)\n\s*:END:")
                let propsMatch = propsPattern.Match(rawContent)

                if propsMatch.Success then
                    let propsContent = propsMatch.Groups.[1].Value
                    let propLinePattern = Regex(@"^\s*:([^:]+):\s*(.*)$", RegexOptions.Multiline)

                    let props: Property list =
                        propLinePattern.Matches(propsContent)
                        |> Seq.cast<Match>
                        |> Seq.map (fun m ->
                            { Property.Key = m.Groups.[1].Value.Trim()
                              Value = m.Groups.[2].Value.Trim() })
                        |> Seq.toList

                    Some { Properties = props }
                else
                    None

            // Find links in content and adjust positions to be absolute in file
            // The content starts after the headline line
            let headlineLineLength = firstLine.Length + 1 // +1 for newline
            let contentOffset = startPos + headlineLineLength

            let links =
                Parsers.findAllLinks rawContent
                |> List.map (fun l ->
                    { l with
                        Position = l.Position + contentOffset })

            Some
                { Headline =
                    { Level = level
                      TodoKeyword = todoKeyword
                      Priority = priority
                      Title = title
                      Tags = tags
                      Planning = planning
                      Properties = properties
                      Position = int64 startPos }
                  RawContent = rawContent
                  Links = links }

/// Compute (start, end) index ranges for #+BEGIN_xxx ... #+END_xxx blocks.
let computeBlockRanges (content: string) : (int * int) list =
    let beginPattern =
        Regex(@"^#\+BEGIN_\w+", RegexOptions.Multiline ||| RegexOptions.IgnoreCase)

    let endPattern =
        Regex(@"^#\+END_\w+", RegexOptions.Multiline ||| RegexOptions.IgnoreCase)

    let begins = beginPattern.Matches(content) |> Seq.cast<Match> |> Seq.toList
    let ends = endPattern.Matches(content) |> Seq.cast<Match> |> Seq.toList
    let mutable endCandidates = ends

    begins
    |> List.choose (fun b ->
        match endCandidates |> List.tryFind (fun e -> e.Index > b.Index) with
        | Some e ->
            endCandidates <- endCandidates |> List.filter (fun c -> c.Index > e.Index)
            Some(b.Index, e.Index + e.Length)
        | None -> None)

let private isInsideBlock (blockRanges: (int * int) list) (pos: int) : bool =
    blockRanges |> List.exists (fun (s, e) -> pos > s && pos < e)

/// Split document into headline sections
let splitIntoSections (content: string) : (int * string) list =
    let headlinePattern = Regex(@"^(\*+ )", RegexOptions.Multiline)
    let blockRanges = computeBlockRanges content

    let matches =
        headlinePattern.Matches(content)
        |> Seq.cast<Match>
        |> Seq.filter (fun m -> not (isInsideBlock blockRanges m.Index))
        |> Seq.toList

    match matches with
    | [] -> []
    | _ ->
        matches
        |> List.mapi (fun i m ->
            let startIdx = m.Index

            let endIdx =
                if i + 1 < matches.Length then
                    matches.[i + 1].Index
                else
                    content.Length

            startIdx, content.Substring(startIdx, endIdx - startIdx).TrimEnd())
        |> List.filter (fun (_, s) -> not (String.IsNullOrWhiteSpace s))

let private buildDocumentFromSections
    (keywords: Keyword list)
    (fileProperties: PropertyDrawer option)
    (fileLinks: OrgLink list)
    (headlineRegex: Regex)
    (content: string)
    : OrgDocument =
    let sections = splitIntoSections content

    let headlinesWithContent =
        sections
        |> List.choose (fun (pos, text) -> parseHeadlineSectionWith headlineRegex text pos)

    let headlines = headlinesWithContent |> List.map (fun h -> h.Headline)

    let allLinks =
        let fileNodeId = Types.tryGetId fileProperties
        let fileLevelLinks = fileLinks |> List.map (fun l -> l, fileNodeId)

        let headlineLinks =
            headlinesWithContent
            |> List.collect (fun hwc ->
                let nodeId = Types.tryGetId hwc.Headline.Properties
                hwc.Links |> List.map (fun l -> l, nodeId))

        fileLevelLinks @ headlineLinks

    { FilePath = None
      Keywords = keywords
      FileProperties = fileProperties
      Headlines = headlines
      Links = allLinks }

/// Parse with explicit config (uses config's TODO keywords)
let parseWithConfig (config: OrgConfig) (content: string) : OrgDocument =
    let keywords, fileProperties, fileLinks = parseFileSection content
    let effectiveConfig = FileConfig.mergeFileConfig config keywords
    let todoKws = Types.allKeywords effectiveConfig.TodoKeywords
    let regex = Types.buildHeadlineRegex todoKws
    buildDocumentFromSections keywords fileProperties fileLinks regex content

/// Parse a complete org document (auto-detects #+TODO: keywords)
let parse (content: string) : OrgDocument =
    parseWithConfig Types.defaultConfig content

/// Parse an org file from disk
let parseFile (filePath: string) : OrgDocument =
    let content = File.ReadAllText(filePath)
    let doc = parse content
    { doc with FilePath = Some filePath }

/// Get all nodes (file-level + headlines with IDs) from a document
let getNodes (doc: OrgDocument) : (string * Headline option * PropertyDrawer option) list =
    // File-level node (if it has an ID)
    let fileNode =
        match Types.tryGetId doc.FileProperties with
        | Some id ->
            // Create a pseudo-headline for file-level node
            let fileHeadline =
                { Level = 0
                  TodoKeyword = None
                  Priority = None
                  Title = Types.tryGetTitle doc.Keywords |> Option.defaultValue ""
                  Tags = Types.getFileTags doc.Keywords
                  Planning = None
                  Properties = doc.FileProperties
                  Position = 1L }

            [ (id, Some fileHeadline, doc.FileProperties) ]
        | None -> []

    // Headline nodes
    let headlineNodes =
        doc.Headlines
        |> List.choose (fun h ->
            match Types.tryGetId h.Properties with
            | Some id -> Some(id, Some h, h.Properties)
            | None -> None)

    fileNode @ headlineNodes

/// Compute the outline path for a headline
let computeOutlinePath (headlines: Headline list) (targetHeadline: Headline) : string list =
    // Find all ancestors of the target headline
    let targetIdx =
        headlines |> List.tryFindIndex (fun h -> h.Position = targetHeadline.Position)

    match targetIdx with
    | None -> []
    | Some idx ->
        let targetLevel = targetHeadline.Level

        // Walk backwards to find ancestors
        let rec findAncestors currentIdx currentLevel acc =
            if currentIdx < 0 || currentLevel <= 0 then
                acc
            else
                let h = headlines.[currentIdx]

                if h.Level < currentLevel then
                    findAncestors (currentIdx - 1) h.Level (h.Title :: acc)
                else
                    findAncestors (currentIdx - 1) currentLevel acc

        findAncestors (idx - 1) targetLevel []

/// Compute the outline path at a given character position (for link context)
/// Returns the path including the headline containing the position
let computeOutlinePathAtPosition (headlines: Headline list) (position: int) : string list =
    // Find the headline that contains this position
    let containingHeadline =
        headlines |> List.filter (fun h -> int h.Position <= position) |> List.tryLast

    match containingHeadline with
    | None -> []
    | Some headline ->
        // Get ancestors plus this headline
        let ancestors = computeOutlinePath headlines headline
        ancestors @ [ headline.Title ]
