module OrgCli.Org.Headlines

/// Resolve a headline identifier to a byte position.
/// Resolution order: (1) int64 → position, (2) :ID: property match, (3) exact title, (4) error.
let resolveHeadlinePos (content: string) (identifier: string) : Result<int64, CliError> =
    match System.Int64.TryParse(identifier) with
    | true, pos -> Ok pos
    | false, _ ->
        let doc = Document.parse content

        match
            doc.Headlines
            |> List.tryFind (fun h -> Types.tryGetId h.Properties = Some identifier)
        with
        | Some h -> Ok h.Position
        | None ->
            match doc.Headlines |> List.tryFind (fun h -> h.Title = identifier) with
            | Some h -> Ok h.Position
            | None ->
                Error
                    { Type = CliErrorType.HeadlineNotFound
                      Message = sprintf "Headline not found: %s" identifier
                      Detail = None }

type HeadlineMatch =
    { Headline: Headline
      File: string
      OutlinePath: string list }

/// Collect all headlines from pre-parsed documents, including file path and outline path context.
let collectHeadlinesFromDocs (docs: (string * OrgDocument) list) : HeadlineMatch list =
    docs
    |> List.collect (fun (file, doc) ->
        doc.Headlines
        |> List.map (fun h ->
            { Headline = h
              File = file
              OutlinePath = Document.computeOutlinePath doc.Headlines h }))

/// Collect all headlines from files on disk.
let collectHeadlines (files: string list) : HeadlineMatch list =
    let docs = files |> List.map (fun f -> (f, Document.parseFile f))
    collectHeadlinesFromDocs docs

let filterByTodo (state: string) (matches: HeadlineMatch list) : HeadlineMatch list =
    matches |> List.filter (fun m -> m.Headline.TodoKeyword = Some state)

let filterByTag (tag: string) (matches: HeadlineMatch list) : HeadlineMatch list =
    matches |> List.filter (fun m -> List.contains tag m.Headline.Tags)

let filterByLevel (level: int) (matches: HeadlineMatch list) : HeadlineMatch list =
    matches |> List.filter (fun m -> m.Headline.Level = level)

let filterByProperty (key: string) (value: string) (matches: HeadlineMatch list) : HeadlineMatch list =
    matches
    |> List.filter (fun m ->
        match Types.tryGetProperty key m.Headline.Properties with
        | Some v -> v = value
        | None -> false)

let computeInheritedTags (config: OrgConfig) (doc: OrgDocument) (target: Headline) : string list =
    let filetags =
        if config.TagInheritance then
            Types.getFileTags doc.Keywords
        else
            []

    let ancestorTags =
        if config.TagInheritance then
            let idx = doc.Headlines |> List.tryFindIndex (fun h -> h.Position = target.Position)

            match idx with
            | None -> []
            | Some i ->
                let rec collect ci level acc =
                    if ci < 0 || level <= 1 then
                        acc
                    else
                        let h = doc.Headlines.[ci]

                        if h.Level < level then
                            collect (ci - 1) h.Level (h.Tags @ acc)
                        else
                            collect (ci - 1) level acc

                collect (i - 1) target.Level []
        else
            []

    let combined = filetags @ ancestorTags @ target.Tags
    let excluded = Set.ofList config.TagsExcludeFromInheritance

    combined
    |> List.filter (fun t -> not (Set.contains t excluded))
    |> List.distinct

let private alwaysInheritedProperties =
    set [ "CATEGORY"; "ARCHIVE"; "COLUMNS"; "LOGGING" ]

let private shouldInheritProperty (config: OrgConfig) (key: string) : bool =
    let upperKey = key.ToUpperInvariant()

    Set.contains upperKey alwaysInheritedProperties
    || (config.PropertyInheritance
        && (config.InheritProperties.IsEmpty
            || config.InheritProperties
               |> List.exists (fun p -> p.ToUpperInvariant() = upperKey)))

let resolveProperty (config: OrgConfig) (doc: OrgDocument) (target: Headline) (key: string) : string option =
    match Types.tryGetProperty key target.Properties with
    | Some v -> Some v
    | None when shouldInheritProperty config key ->
        let idx = doc.Headlines |> List.tryFindIndex (fun h -> h.Position = target.Position)

        let fromAncestors =
            match idx with
            | None -> None
            | Some i ->
                let rec search ci level =
                    if ci < 0 || level <= 1 then
                        None
                    else
                        let h = doc.Headlines.[ci]

                        if h.Level < level then
                            match Types.tryGetProperty key h.Properties with
                            | Some v -> Some v
                            | None -> search (ci - 1) h.Level
                        else
                            search (ci - 1) level

                search (i - 1) target.Level

        match fromAncestors with
        | Some _ -> fromAncestors
        | None ->
            match Types.tryGetProperty key doc.FileProperties with
            | Some v -> Some v
            | None ->
                let upperKey = key.ToUpperInvariant()

                let fromDirectKeyword =
                    doc.Keywords
                    |> List.tryFind (fun kw -> kw.Key.ToUpperInvariant() = upperKey)
                    |> Option.map (fun kw -> kw.Value)

                match fromDirectKeyword with
                | Some v -> Some v
                | None ->
                    doc.Keywords
                    |> List.tryFind (fun kw ->
                        kw.Key.ToUpperInvariant() = "PROPERTY"
                        && kw.Value.StartsWith(key, System.StringComparison.OrdinalIgnoreCase))
                    |> Option.map (fun kw ->
                        let spaceIdx = kw.Value.IndexOf(' ')

                        if spaceIdx >= 0 then
                            kw.Value.Substring(spaceIdx + 1).Trim()
                        else
                            "")
                    |> Option.bind (fun v -> if System.String.IsNullOrWhiteSpace v then None else Some v)
    | None -> None

let filterByTagWithInheritance
    (config: OrgConfig)
    (docs: (string * OrgDocument) list)
    (tag: string)
    (matches: HeadlineMatch list)
    : HeadlineMatch list =
    let docMap = docs |> Map.ofList

    matches
    |> List.filter (fun m ->
        match Map.tryFind m.File docMap with
        | None -> List.contains tag m.Headline.Tags
        | Some doc ->
            let allTags = computeInheritedTags config doc m.Headline
            List.contains tag allTags)

let private formatTagList (tags: string list) : string =
    if List.isEmpty tags then
        ""
    else
        sprintf ":%s:" (System.String.Join(":", tags))

let resolveVirtualProperty
    (config: OrgConfig)
    (doc: OrgDocument)
    (h: Headline)
    (key: string)
    (file: string option)
    : string option =
    match key.ToUpperInvariant() with
    | "ITEM" -> Some h.Title
    | "TODO" -> h.TodoKeyword
    | "PRIORITY" -> h.Priority |> Option.map (fun (Priority c) -> string c)
    | "LEVEL" -> Some(string h.Level)
    | "TAGS" -> let t = formatTagList h.Tags in if t = "" then None else Some t
    | "ALLTAGS" ->
        let all = computeInheritedTags config doc h
        let t = formatTagList all
        if t = "" then None else Some t
    | "FILE" -> file
    | "CATEGORY" ->
        resolveProperty config doc h "CATEGORY"
        |> Option.orElseWith (fun () ->
            doc.Keywords
            |> List.tryFind (fun kw -> kw.Key.ToUpperInvariant() = "CATEGORY")
            |> Option.map (fun kw -> kw.Value))
        |> Option.orElseWith (fun () -> file |> Option.map (fun f -> System.IO.Path.GetFileNameWithoutExtension(f)))
    | "SCHEDULED" ->
        h.Planning
        |> Option.bind (fun p -> p.Scheduled)
        |> Option.map Writer.formatTimestamp
    | "DEADLINE" ->
        h.Planning
        |> Option.bind (fun p -> p.Deadline)
        |> Option.map Writer.formatTimestamp
    | "CLOSED" ->
        h.Planning
        |> Option.bind (fun p -> p.Closed)
        |> Option.map Writer.formatTimestamp
    | _ -> resolveProperty config doc h key
