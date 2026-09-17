module OrgCli.Index.IndexSync

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions
open OrgCli.Org

let private headlineBoundaryRegex = Regex(@"^\*+ ", RegexOptions.Compiled)

let computeSha256 (content: string) : string =
    use sha256 = SHA256.Create()
    let bytes = Encoding.UTF8.GetBytes(content)
    let hash = sha256.ComputeHash(bytes)
    BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant()

let normalizeTimestamp (ts: Timestamp) : string =
    if ts.HasTime then
        ts.Date.ToString("yyyy-MM-dd") + "T" + ts.Date.ToString("HH:mm")
    else
        ts.Date.ToString("yyyy-MM-dd")

let extractBody (content: string) (pos: int64) : string =
    let section = HeadlineEdit.split content pos
    let body = section.Body
    // HeadlineEdit.split body includes everything until EOF.
    // Truncate at the next headline: one or more '*' followed by a space.
    // This avoids false positives on org markup like *bold*.
    let lines = body.Split([| '\n' |])
    let mutable endIdx = lines.Length

    for i in 0 .. lines.Length - 1 do
        if endIdx = lines.Length && headlineBoundaryRegex.IsMatch(lines.[i]) then
            endIdx <- i

    if endIdx = 0 then
        ""
    else
        String.Join("\n", lines.[0 .. endIdx - 1])

let computeOutlinePathString (headlines: Headline list) (target: Headline) : string =
    let ancestors = Document.computeOutlinePath headlines target
    let components = ancestors @ [ target.Title ]
    String.Join("\x1F", components)

let private isEncryptedFile (path: string) =
    path.EndsWith(".gpg", StringComparison.OrdinalIgnoreCase)
    || path.EndsWith(".age", StringComparison.OrdinalIgnoreCase)

let private serializeProperties (props: PropertyDrawer option) : string option =
    match props with
    | None -> None
    | Some pd ->
        let pairs =
            pd.Properties
            |> List.map (fun p ->
                sprintf
                    "\"%s\":\"%s\""
                    (p.Key.Replace("\\", "\\\\").Replace("\"", "\\\""))
                    (p.Value.Replace("\\", "\\\\").Replace("\"", "\\\"")))

        Some(sprintf "{%s}" (String.Join(",", pairs)))

let private getUnixEpochSeconds (filePath: string) : int64 =
    let mtime = OrgCli.Org.Runtime.lastWriteTime (filePath)
    DateTimeOffset(mtime).ToUnixTimeSeconds()

let private indexFileContent (db: IndexDatabase.OrgIndexDb) (filePath: string) (content: string) =
    let doc = Document.parseWithConfig (Config.load ()) content
    db.StoreDocument(filePath, doc)

    let rootEnd =
        doc.Headlines
        |> List.tryHead
        |> Option.map (fun h -> int h.Position)
        |> Option.defaultValue content.Length

    db.StoreRoot(filePath, Types.tryGetTitle doc.Keywords |> Option.defaultValue "", content.Substring(0, rootEnd))
    let filetags = Types.getFileTags doc.Keywords

    // Insert headlines
    for h in doc.Headlines do
        let outlinePath = computeOutlinePathString doc.Headlines h

        let scheduledRaw =
            h.Planning
            |> Option.bind (fun p -> p.Scheduled)
            |> Option.map Writer.formatTimestamp

        let scheduledDt =
            h.Planning
            |> Option.bind (fun p -> p.Scheduled)
            |> Option.map normalizeTimestamp

        let deadlineRaw =
            h.Planning
            |> Option.bind (fun p -> p.Deadline)
            |> Option.map Writer.formatTimestamp

        let deadlineDt =
            h.Planning |> Option.bind (fun p -> p.Deadline) |> Option.map normalizeTimestamp

        let closedRaw =
            h.Planning
            |> Option.bind (fun p -> p.Closed)
            |> Option.map Writer.formatTimestamp

        let closedDt =
            h.Planning |> Option.bind (fun p -> p.Closed) |> Option.map normalizeTimestamp

        let body = extractBody content h.Position
        let props = serializeProperties h.Properties
        let priority = h.Priority |> Option.map (fun (Priority c) -> string c)
        let customId = Types.tryGetProperty "CUSTOM_ID" h.Properties

        db.InsertHeadline(
            { File = filePath
              CharPos = h.Position
              Level = h.Level
              Title = h.Title
              Todo = h.TodoKeyword
              Priority = priority
              Scheduled = scheduledRaw
              ScheduledDt = scheduledDt
              Deadline = deadlineRaw
              DeadlineDt = deadlineDt
              Closed = closedRaw
              ClosedDt = closedDt
              Properties = props
              Body = if String.IsNullOrWhiteSpace(body) then None else Some body
              OutlinePath = Some outlinePath
              CustomId = customId }
        )

        // Insert direct tags (inherited=0)
        for tag in h.Tags do
            db.InsertTag(
                { File = filePath
                  CharPos = h.Position
                  Tag = tag
                  Inherited = false }
            )

    // Insert inherited tags (filetags + ancestor tags)
    for h in doc.Headlines do
        // Filetags
        for tag in filetags do
            db.InsertTagIgnore(
                { File = filePath
                  CharPos = h.Position
                  Tag = tag
                  Inherited = true }
            )

        // Ancestor headline tags
        let idx = doc.Headlines |> List.tryFindIndex (fun hh -> hh.Position = h.Position)

        match idx with
        | None -> ()
        | Some i ->
            let rec collectAncestorTags ci level acc =
                if ci < 0 || level <= 1 then
                    acc
                else
                    let ancestor = doc.Headlines.[ci]

                    if ancestor.Level < level then
                        collectAncestorTags (ci - 1) ancestor.Level (ancestor.Tags @ acc)
                    else
                        collectAncestorTags (ci - 1) level acc

            let ancestorTags = collectAncestorTags (i - 1) h.Level []

            for tag in ancestorTags do
                db.InsertTagIgnore(
                    { File = filePath
                      CharPos = h.Position
                      Tag = tag
                      Inherited = true }
                )

    // Rebuild FTS for this file
    db.RebuildFtsForFile(filePath)

/// Sync a single file. Always re-indexes (used for post-mutation auto-sync
/// where the caller knows the file changed).
let syncFile (db: IndexDatabase.OrgIndexDb) (filePath: string) : unit =
    let filePath = Runtime.fullPath filePath

    if not (OrgCli.Org.Runtime.fileExists (filePath)) then
        db.ExecuteInTransaction(fun () ->
            db.DeleteFtsForFile(filePath)
            db.DeleteFile(filePath))
    elif isEncryptedFile filePath then
        ()
    else
        let content = Some(OrgCli.Org.Runtime.readText (filePath))

        match content with
        | None -> ()
        | Some text ->
            let hash =
                computeSha256 ("projection-v4\n" + sprintf "%A" (Config.load ()) + "\n" + text)

            let mtime = getUnixEpochSeconds filePath

            db.ExecuteInTransaction(fun () ->
                db.DeleteFtsForFile(filePath)
                db.DeleteHeadlines(filePath)

                db.InsertFile(
                    { Path = filePath
                      Hash = hash
                      Mtime = mtime }
                )

                indexFileContent db filePath text)

/// Incremental sync for a single file during directory scan.
/// Uses mtime/hash checks to skip unchanged files.
let private syncIncremental fingerprint (db: IndexDatabase.OrgIndexDb) existingFile (filePath: string) : unit =
    let filePath = Runtime.fullPath filePath

    if isEncryptedFile filePath then
        ()
    else
        let content = Some(OrgCli.Org.Runtime.readText (filePath))

        match content with
        | None -> ()
        | Some text ->
            let hash = computeSha256 (fingerprint + text)

            let mtime = getUnixEpochSeconds filePath

            let needsReindex =
                match existingFile with
                | None -> true
                | Some ef ->
                    if ef.Hash = hash then
                        if ef.Mtime <> mtime then
                            db.UpdateFileMtime(filePath, mtime)

                        false
                    else
                        true

            if needsReindex then
                db.ExecuteInTransaction(fun () ->
                    db.DeleteFtsForFile(filePath)
                    db.DeleteHeadlines(filePath)

                    db.InsertFile(
                        { Path = filePath
                          Hash = hash
                          Mtime = mtime }
                    )

                    indexFileContent db filePath text)

let private fingerprint () =
    "projection-v4\n" + sprintf "%A" (Config.load ()) + "\n"

let syncFileIncremental (db: IndexDatabase.OrgIndexDb) (filePath: string) =
    let path = Runtime.fullPath filePath
    syncIncremental (fingerprint ()) db (db.GetFile path) path

/// Remove missing files, then transactionally replace each changed file projection.
let syncFiles (db: IndexDatabase.OrgIndexDb) (files: string list) (force: bool) =
    let files = files |> List.map Runtime.fullPath |> List.distinct |> List.sort

    let existing = db.GetAllFiles() |> List.map (fun f -> f.Path, f) |> Map.ofList
    let prefix = fingerprint ()

    db.ExecuteInTransaction(fun () ->
        for KeyValue(_, f) in existing do
            if not (Runtime.fileExists f.Path) then
                db.DeleteFtsForFile f.Path
                db.DeleteFile f.Path)

    for file in files do
        if force then
            syncFile db file
        else
            syncIncremental prefix db (existing.TryFind file) file

let syncDirectory (db: IndexDatabase.OrgIndexDb) (directory: string) =
    if not (Runtime.directoryExists directory) then
        invalidArg "directory" ("Directory does not exist: " + directory)

    syncFiles db (Utils.listOrgFiles directory) false

let syncDirectoryForce (db: IndexDatabase.OrgIndexDb) (directory: string) =
    if not (Runtime.directoryExists directory) then
        invalidArg "directory" ("Directory does not exist: " + directory)

    syncFiles db (Utils.listOrgFiles directory) true
