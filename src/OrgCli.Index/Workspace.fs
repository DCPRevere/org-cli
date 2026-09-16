module OrgCli.Index.Workspace

open OrgCli.Org

/// One owned index for all core queries and optional extensions.
let documents dbPath files =
    use db = new IndexDatabase.OrgIndexDb(dbPath)
    db.Initialize()
    IndexSync.syncFiles db files false
    let selected = files |> List.map Runtime.fullPath |> Set.ofList
    db.GetDocuments() |> List.filter (fun (file, _) -> Set.contains file selected)

let private resolveCore refresh dbPath files (identifier: string) =
    use db = new IndexDatabase.OrgIndexDb(dbPath)
    db.Initialize()

    if refresh then
        IndexSync.syncFiles db files false

    let matches =
        if identifier.StartsWith("id:") then
            db.FindIdentity("id", identifier.Substring 3)
        elif identifier.StartsWith("custom:") then
            db.FindIdentity("custom", identifier.Substring 7)
        else
            match db.FindIdentity("id", identifier) with
            | [] -> db.FindIdentity("custom", identifier)
            | matches -> matches

    let selected = files |> List.map Runtime.fullPath |> Set.ofList
    let matches = matches |> List.filter (fun (file, _) -> Set.contains file selected)

    match matches with
    | [ (file, pos) ] -> Ok(file, pos)
    | [] ->
        Error
            { Type = CliErrorType.HeadlineNotFound
              Message = "No entry found: " + identifier
              Detail = None }
    | _ ->
        Error
            { Type = CliErrorType.InvalidArgs
              Message = "Ambiguous identity; specify a file: " + identifier
              Detail = None }

let resolve dbPath files identifier =
    resolveCore true dbPath files identifier

/// Resolve against an index already maintained by the workspace service.
let resolveIndexed dbPath files identifier =
    resolveCore false dbPath files identifier

/// File roots are addressable for read/append but are not fabricated headlines.
let isFileRoot (doc: OrgDocument) (identifier: string) =
    let id =
        if identifier.StartsWith("id:") then
            identifier.Substring 3
        else
            identifier

    Types.tryGetId doc.FileProperties = Some id

let appendRoot (content: string) text =
    let doc = Document.parse content

    let pos =
        doc.Headlines
        |> List.tryHead
        |> Option.map (fun h -> int h.Position)
        |> Option.defaultValue content.Length

    let before = content.Substring(0, pos)
    let separator = if before.EndsWith("\n") || before = "" then "" else "\n"
    content.Insert(pos, separator + text + "\n")
