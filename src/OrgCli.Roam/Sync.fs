module OrgCli.Roam.Sync

open System
open System.IO
open System.Text.RegularExpressions
open OrgCli.Org
open OrgCli.Roam

/// Parse a ROAM_REFS value and extract ref type and value
let parseRef (refStr: string) : (string * string) option =
    if String.IsNullOrWhiteSpace(refStr) then
        None
    elif refStr.StartsWith("@") then
        // Citation key: @citeKey -> store without @
        Some("cite", refStr.Substring(1))
    elif refStr.StartsWith("[cite:") then
        // Org-cite format: [cite:@key]
        let key = refStr.TrimStart('[').TrimEnd(']').Replace("cite:@", "")
        Some("cite", key)
    else
        // URL or other link type: https://example.com -> type="https", path="//example.com"
        match refStr.IndexOf(':') with
        | -1 -> None
        | idx ->
            let linkType = refStr.Substring(0, idx)
            let path = refStr.Substring(idx + 1) // Keep the // prefix
            Some(linkType, path)

/// Match org-cite bracket citations: [cite:@key], [cite/style:@key1;@key2]
let private orgCiteRe = Regex(@"\[cite(?:/\w+)?:([^\]]+)\]", RegexOptions.Compiled)

/// Extract @key references from within an org-cite match
let private citeKeyRe = Regex(@"@([\w][\w:./-]*)", RegexOptions.Compiled)

/// Match org-ref citation patterns: cite:key, autocite:key, Textcite:key, etc.
/// Negative lookbehind prevents matching inside [cite:...] brackets
let private orgRefCiteRe =
    Regex(@"(?<!\[)\b\w*[Cc]ite\w*:([\w][\w:./-]*)", RegexOptions.Compiled)

/// Find all citations in text, returning (cite_key, char_position) pairs
let findCitationsInText (text: string) : (string * int) list =
    let results = ResizeArray<string * int>()

    for m in orgCiteRe.Matches(text) do
        let inner = m.Groups.[1]

        for km in citeKeyRe.Matches(inner.Value) do
            results.Add(km.Groups.[1].Value, inner.Index + km.Index)

    for m in orgRefCiteRe.Matches(text) do
        results.Add(m.Groups.[1].Value, m.Index)

    results |> Seq.toList

/// Update the database for a single file
let updateFile (db: Database.OrgRoamDb) (roamDirectory: string) (filePath: string) =
    let fullPath = Path.GetFullPath(filePath)

    // Compute file hash
    let contentHash = Utils.computeFileHash fullPath

    // Check if file needs updating
    let existingHash = db.GetFileHash(fullPath)

    if existingHash = Some contentHash then
        () // File unchanged
    else
        // Clear existing data for this file
        db.ClearFile(fullPath)

        // Parse the file
        let doc = Document.parseFile fullPath

        // Get file metadata
        let fileTitle = Types.tryGetTitle doc.Keywords
        let atime = Utils.getFileAtime fullPath
        let mtime = Utils.getFileMtime fullPath

        // Insert file record
        db.InsertFile(
            { File = fullPath
              Title = fileTitle
              Hash = contentHash
              Atime = atime
              Mtime = mtime }
        )

        // Track inserted nodes for citation attribution: (nodeId, startPos)
        let insertedNodes = ResizeArray<string * int>()

        // Get file-level node if present
        let fileNodeId = Types.tryGetId doc.FileProperties
        let isFileNode = fileNodeId.IsSome && not (Types.isRoamExcluded doc.FileProperties)

        match fileNodeId with
        | Some nodeId when isFileNode ->
            let title =
                fileTitle |> Option.defaultValue (Path.GetFileNameWithoutExtension(fullPath))

            let fileTags = Types.getFileTags doc.Keywords

            // Build properties as elisp alist
            let props =
                doc.FileProperties
                |> Option.map (fun pd -> pd.Properties |> List.map (fun p -> p.Key, p.Value))
                |> Option.defaultValue []

            db.InsertNode(
                { Id = nodeId
                  File = fullPath
                  Level = 0
                  Pos = 1
                  Todo = None
                  Priority = None
                  Scheduled = None
                  Deadline = None
                  Title = title
                  Properties = Domain.formatElispAlist props
                  Olp = "nil" }
            )

            insertedNodes.Add(nodeId, 0)

            // Insert tags
            for tag in fileTags do
                db.InsertTag({ NodeId = nodeId; Tag = tag })

            // Insert aliases
            let aliases = Types.getRoamAliases doc.FileProperties

            for alias in aliases do
                db.InsertAlias({ NodeId = nodeId; Alias = alias })

            // Insert refs
            let refs = Types.getRoamRefs doc.FileProperties

            for refStr in refs do
                match parseRef refStr with
                | Some(refType, refPath) ->
                    db.InsertRef(
                        { NodeId = nodeId
                          Ref = refPath
                          Type = refType }
                    )
                | None -> ()
        | _ -> ()

        // Process headline nodes
        for headline in doc.Headlines do
            let headlineId = Types.tryGetId headline.Properties
            let isExcluded = Types.isRoamExcluded headline.Properties

            match headlineId with
            | Some nodeId when not isExcluded ->
                // Compute outline path
                let olp = Document.computeOutlinePath doc.Headlines headline

                // Build properties
                let props =
                    headline.Properties
                    |> Option.map (fun pd -> pd.Properties |> List.map (fun p -> p.Key, p.Value))
                    |> Option.defaultValue []

                // Format scheduled/deadline
                let scheduled =
                    headline.Planning
                    |> Option.bind (fun p -> p.Scheduled)
                    |> Option.map (fun ts -> Utils.formatIso8601 ts.Date)

                let deadline =
                    headline.Planning
                    |> Option.bind (fun p -> p.Deadline)
                    |> Option.map (fun ts -> Utils.formatIso8601 ts.Date)

                let priority = headline.Priority |> Option.map (fun (Priority c) -> string c)

                db.InsertNode(
                    { Id = nodeId
                      File = fullPath
                      Level = headline.Level
                      Pos = int headline.Position
                      Todo = headline.TodoKeyword
                      Priority = priority
                      Scheduled = scheduled
                      Deadline = deadline
                      Title = headline.Title
                      Properties = Domain.formatElispAlist props
                      Olp = Domain.formatElispList olp }
                )

                insertedNodes.Add(nodeId, int headline.Position)

                // Insert tags (include inherited tags from headline)
                for tag in headline.Tags do
                    db.InsertTag({ NodeId = nodeId; Tag = tag })

                // Insert aliases
                let aliases = Types.getRoamAliases headline.Properties

                for alias in aliases do
                    db.InsertAlias({ NodeId = nodeId; Alias = alias })

                // Insert refs
                let refs = Types.getRoamRefs headline.Properties

                for refStr in refs do
                    match parseRef refStr with
                    | Some(refType, refPath) ->
                        db.InsertRef(
                            { NodeId = nodeId
                              Ref = refPath
                              Type = refType }
                        )
                    | None -> ()
            | _ -> ()

        // Process links
        for (link, sourceNodeId) in doc.Links do
            match sourceNodeId with
            | None -> () // Link not in a node with ID
            | Some srcId ->
                // Compute outline path at link position
                let outlinePath = Document.computeOutlinePathAtPosition doc.Headlines link.Position

                // Build link properties (matching org-roam format)
                let linkProps =
                    if List.isEmpty outlinePath then
                        []
                    else
                        [ (":outline", Domain.formatElispList outlinePath) ]

                let propsWithSearch =
                    match link.SearchOption with
                    | Some s -> (":search-option", "\"" + s + "\"") :: linkProps
                    | None -> linkProps

                db.InsertLink(
                    { Pos = link.Position
                      Source = srcId
                      Dest = link.Path
                      Type = link.LinkType
                      Properties = Domain.formatElispPlist propsWithSearch }
                )

        // Process citations from body text
        let content = File.ReadAllText(fullPath)
        let citations = findCitationsInText content

        for (citeKey, pos) in citations do
            let containingNode =
                insertedNodes
                |> Seq.filter (fun (_, startPos) -> startPos <= pos)
                |> Seq.sortByDescending (fun (_, startPos) -> startPos)
                |> Seq.tryHead

            match containingNode with
            | Some(nodeId, _) ->
                db.InsertCitation(
                    { NodeId = nodeId
                      CiteKey = citeKey
                      Pos = pos
                      Properties = "nil" }
                )
            | None -> ()

/// Sync the database with files in the roam directory.
/// Returns a list of (file, error message) for files that failed to process.
let sync (db: Database.OrgRoamDb) (roamDirectory: string) (force: bool) : (string * string) list =
    match db.Initialize() with
    | Error msg -> [ ("database", msg) ]
    | Ok() ->
        if force then
            db.ClearAll()

        let roamDir = Path.GetFullPath(roamDirectory)
        let orgFiles = Utils.listOrgFiles roamDir

        // Get current files in database
        let existingFiles = db.GetAllFileHashes()

        // Find files to remove (in DB but not on disk)
        let filesToRemove =
            existingFiles
            |> Map.toList
            |> List.filter (fun (f, _) -> not (List.contains f orgFiles))
            |> List.map fst

        // Remove deleted files
        for file in filesToRemove do
            db.ClearFile(file)

        // Update modified files, collecting errors
        let errors = ResizeArray<string * string>()

        for file in orgFiles do
            try
                updateFile db roamDir file
            with ex ->
                eprintfn "Error processing %s: %s" file ex.Message
                errors.Add(file, ex.Message)

        errors |> Seq.toList

/// Get all nodes from the database
let getAllNodes (db: Database.OrgRoamDb) : RoamNode list =
    db.GetAllNodes() |> List.choose (fun n -> db.PopulateNode(n.Id))

/// Get a node by ID
let getNode (db: Database.OrgRoamDb) (nodeId: string) : RoamNode option = db.PopulateNode(nodeId)

/// Get backlinks to a node
let getBacklinks (db: Database.OrgRoamDb) (nodeId: string) : Backlink list =
    db.GetLinksTo(nodeId)
    |> List.choose (fun link ->
        match db.PopulateNode(link.Source) with
        | None -> None
        | Some sourceNode ->
            Some
                { SourceNode = sourceNode
                  TargetNodeId = nodeId
                  Point = link.Pos
                  Properties = Domain.parseElispAlist link.Properties })

/// Find node by title or alias
let findNodeByTitleOrAlias (db: Database.OrgRoamDb) (searchTerm: string) : RoamNode option =
    match db.FindNodeByTitleOrAlias(searchTerm) with
    | None -> None
    | Some node -> db.PopulateNode(node.Id)

/// Find nodes by tag
let findNodesByTag (db: Database.OrgRoamDb) (tag: string) : RoamNode list =
    db.FindNodesByTag(tag) |> List.choose (fun n -> db.PopulateNode(n.Id))
