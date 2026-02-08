module OrgCli.Roam.NodeOperations

open System
open System.IO
open OrgCli.Org
open OrgCli.Roam

/// Options for creating a new node
type CreateNodeOptions =
    { Title: string
      Id: string option // Auto-generate if None
      Tags: string list
      Aliases: string list
      Refs: string list
      TodoKeyword: string option
      Priority: char option
      Scheduled: DateTime option
      Deadline: DateTime option
      Level: int option // None = file-level node, Some n = headline at level n
      ParentFile: string option // For headline nodes, the file to add to
      Content: string option } // Optional body content

/// Default options
let defaultCreateOptions title =
    { Title = title
      Id = None
      Tags = []
      Aliases = []
      Refs = []
      TodoKeyword = None
      Priority = None
      Scheduled = None
      Deadline = None
      Level = None
      ParentFile = None
      Content = None }

/// Generate a filename from title (slug format)
let generateFilename (roamDirectory: string) (title: string) : string =
    let timestamp = DateTime.Now.ToString("yyyyMMddHHmmss")
    let slug = Utils.slugify title
    let filename = sprintf "%s-%s.org" timestamp slug
    Path.Combine(roamDirectory, filename)

/// Create a new file-level node
let createFileNode (roamDirectory: string) (options: CreateNodeOptions) : string =
    let nodeId = options.Id |> Option.defaultWith Utils.generateId
    let filePath = generateFilename roamDirectory options.Title

    let content =
        Writer.createFileNode nodeId options.Title options.Tags options.Aliases options.Refs

    let fullContent =
        match options.Content with
        | Some c -> content + "\n" + c
        | None -> content

    File.WriteAllText(filePath, fullContent)
    filePath

/// Create a new headline node in an existing file
let createHeadlineNode (filePath: string) (options: CreateNodeOptions) : string =
    let nodeId = options.Id |> Option.defaultWith Utils.generateId
    let level = options.Level |> Option.defaultValue 1

    let scheduled =
        options.Scheduled
        |> Option.map (fun dt ->
            { Type = TimestampType.Active
              Date = dt
              HasTime = false
              Repeater = None
              Delay = None
              RangeEnd = None })

    let deadline =
        options.Deadline
        |> Option.map (fun dt ->
            { Type = TimestampType.Active
              Date = dt
              HasTime = false
              Repeater = None
              Delay = None
              RangeEnd = None })

    let headlineContent =
        Writer.createHeadlineNode
            level
            nodeId
            options.Title
            options.Tags
            options.TodoKeyword
            options.Priority
            options.Aliases
            options.Refs
            scheduled
            deadline

    let fullContent =
        match options.Content with
        | Some c -> headlineContent + "\n" + c + "\n"
        | None -> headlineContent + "\n"

    // Append to file
    File.AppendAllText(filePath, "\n" + fullContent)
    nodeId

/// Add a link from one node to another
let addLink
    (db: Database.OrgRoamDb)
    (sourceFilePath: string)
    (sourceNodeId: string)
    (targetNodeId: string)
    (description: string option)
    : Result<unit, string> =
    let content = File.ReadAllText(sourceFilePath)
    let doc = Document.parseFile sourceFilePath

    let link =
        { LinkType = "id"
          Path = targetNodeId
          Description = description
          SearchOption = None
          Position = 0 }

    let linkText = Writer.formatLink link

    if Types.tryGetId doc.FileProperties = Some sourceNodeId then
        // File-level node - append at end
        File.AppendAllText(sourceFilePath, "\n" + linkText + "\n")
        Ok()
    else
        match
            doc.Headlines
            |> List.tryFind (fun h -> Types.tryGetId h.Properties = Some sourceNodeId)
        with
        | None -> Error(sprintf "Source node %s not found in file %s" sourceNodeId sourceFilePath)
        | Some h ->
            // Insert at end of this headline's subtree (before next sibling)
            let (_, subtreeEnd) = Subtree.getSubtreeRange content h.Position
            let insertPos = subtreeEnd

            let prefix =
                if insertPos > 0 && content.[insertPos - 1] <> '\n' then
                    "\n"
                else
                    ""

            let newContent = content.Insert(insertPos, prefix + linkText + "\n")
            File.WriteAllText(sourceFilePath, newContent)
            Ok()

/// Find the character position of a node within a parsed document.
/// Returns Some 0 for file-level nodes, Some position for headline nodes, None if not found.
let private findNodePosition (doc: OrgDocument) (nodeId: string) : int option =
    if Types.tryGetId doc.FileProperties = Some nodeId then
        Some 0
    else
        doc.Headlines
        |> List.tryFind (fun h -> Types.tryGetId h.Properties = Some nodeId)
        |> Option.map (fun h -> int h.Position)

/// Add an alias to a node
let addAlias (filePath: string) (nodeId: string) (alias: string) : Result<unit, string> =
    let content = File.ReadAllText(filePath)
    let doc = Document.parseFile filePath

    match findNodePosition doc nodeId with
    | None -> Error(sprintf "Node %s not found in file %s" nodeId filePath)
    | Some nodePosition ->
        let newContent =
            Writer.addToMultiValueProperty content nodePosition "ROAM_ALIASES" alias

        File.WriteAllText(filePath, newContent)
        Ok()

/// Remove an alias from a node
let removeAlias (filePath: string) (nodeId: string) (alias: string) : Result<unit, string> =
    let content = File.ReadAllText(filePath)
    let doc = Document.parseFile filePath

    match findNodePosition doc nodeId with
    | None -> Error(sprintf "Node %s not found in file %s" nodeId filePath)
    | Some nodePosition ->
        let newContent =
            Writer.removeFromMultiValueProperty content nodePosition "ROAM_ALIASES" alias

        File.WriteAllText(filePath, newContent)
        Ok()

/// Add a tag to a node
let addTag (filePath: string) (nodeId: string) (tag: string) : Result<unit, string> =
    let content = File.ReadAllText(filePath)
    let doc = Document.parseFile filePath

    // Check if file-level node
    if Types.tryGetId doc.FileProperties = Some nodeId then
        // Add to filetags keyword
        let existingTags = Types.getFileTags doc.Keywords

        if not (List.contains tag existingTags) then
            let newTags = tag :: existingTags
            let tagString = sprintf ":%s:" (String.Join(":", newTags))

            // Find and update or add filetags keyword
            let lines = content.Split([| '\n' |])

            let hasFiletags =
                lines
                |> Array.exists (fun l -> l.TrimStart().ToLower().StartsWith("#+filetags:"))

            let newContent =
                if hasFiletags then
                    lines
                    |> Array.map (fun l ->
                        if l.TrimStart().ToLower().StartsWith("#+filetags:") then
                            sprintf "#+filetags: %s" tagString
                        else
                            l)
                    |> String.concat "\n"
                else
                    // Add after title
                    let titleIdx =
                        lines
                        |> Array.tryFindIndex (fun l -> l.TrimStart().ToLower().StartsWith("#+title:"))

                    match titleIdx with
                    | Some idx ->
                        let before = lines.[..idx]
                        let after = lines.[(idx + 1) ..]

                        String.concat
                            "\n"
                            [| String.concat "\n" before
                               sprintf "#+filetags: %s" tagString
                               String.concat "\n" after |]
                    | None -> content + sprintf "\n#+filetags: %s" tagString

            File.WriteAllText(filePath, newContent)

        Ok()
    else
        // Headline node - add to headline tags
        match
            doc.Headlines
            |> List.tryFind (fun h -> Types.tryGetId h.Properties = Some nodeId)
        with
        | None -> Error(sprintf "Node %s not found in file %s" nodeId filePath)
        | Some h ->
            let existingTags =
                HeadlineEdit.parseTags (content.Substring(int h.Position).Split([| '\n' |]).[0])

            if not (List.contains tag existingTags) then
                let newTags = existingTags @ [ tag ]
                let section = HeadlineEdit.split content h.Position
                let newHeadlineLine = HeadlineEdit.replaceTags section.HeadlineLine newTags

                let newContent =
                    HeadlineEdit.reassemble
                        { section with
                            HeadlineLine = newHeadlineLine }

                File.WriteAllText(filePath, newContent)

            Ok()

/// Add a ref to a node
let addRef (filePath: string) (nodeId: string) (ref: string) : Result<unit, string> =
    let content = File.ReadAllText(filePath)
    let doc = Document.parseFile filePath

    match findNodePosition doc nodeId with
    | None -> Error(sprintf "Node %s not found in file %s" nodeId filePath)
    | Some nodePosition ->
        let newContent = Writer.addToMultiValueProperty content nodePosition "ROAM_REFS" ref
        File.WriteAllText(filePath, newContent)
        Ok()

/// Remove a ref from a node
let removeRef (filePath: string) (nodeId: string) (ref: string) : Result<unit, string> =
    let content = File.ReadAllText(filePath)
    let doc = Document.parseFile filePath

    match findNodePosition doc nodeId with
    | None -> Error(sprintf "Node %s not found in file %s" nodeId filePath)
    | Some nodePosition ->
        let newContent =
            Writer.removeFromMultiValueProperty content nodePosition "ROAM_REFS" ref

        File.WriteAllText(filePath, newContent)
        Ok()

/// Delete a node (either remove headline or delete file)
let deleteNode (db: Database.OrgRoamDb) (nodeId: string) : Result<unit, string> =
    match db.GetNode(nodeId) with
    | None -> Error(sprintf "Node %s not found" nodeId)
    | Some node ->
        if node.Level = 0 then
            // File-level node - delete the file
            if File.Exists(node.File) then
                File.Delete(node.File)
        else
            // Headline node - remove the entire subtree
            let content = File.ReadAllText(node.File)
            let newContent = Subtree.removeSubtree content (int64 node.Pos)
            File.WriteAllText(node.File, newContent)

        Ok()
