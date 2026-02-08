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
    let mtime = File.GetLastWriteTimeUtc(filePath)
    DateTimeOffset(mtime).ToUnixTimeSeconds()

let private indexFileContent (db: IndexDatabase.OrgIndexDb) (filePath: string) (content: string) =
    let doc = Document.parse content
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
              OutlinePath = Some outlinePath }
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
    if not (File.Exists(filePath)) then
        db.ExecuteInTransaction(fun () ->
            db.DeleteFtsForFile(filePath)
            db.DeleteFile(filePath))
    elif isEncryptedFile filePath then
        ()
    else
        let content =
            try
                Some(File.ReadAllText(filePath))
            with _ ->
                None

        match content with
        | None -> ()
        | Some text ->
            let hash = computeSha256 text
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
let syncFileIncremental (db: IndexDatabase.OrgIndexDb) (filePath: string) : unit =
    if isEncryptedFile filePath then
        ()
    else
        let content =
            try
                Some(File.ReadAllText(filePath))
            with _ ->
                None

        match content with
        | None -> ()
        | Some text ->
            let hash = computeSha256 text
            let mtime = getUnixEpochSeconds filePath
            let existingFile = db.GetFile(filePath)

            let needsReindex =
                match existingFile with
                | None -> true
                | Some ef ->
                    if ef.Mtime = mtime then
                        false
                    elif ef.Hash = hash then
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

let syncDirectory (db: IndexDatabase.OrgIndexDb) (directory: string) : unit =
    let orgFiles =
        Utils.listOrgFiles directory |> List.filter (fun f -> not (isEncryptedFile f))

    for filePath in orgFiles do
        try
            syncFileIncremental db filePath
        with _ ->
            () // skip files that fail to parse; index the rest

    // Remove entries for files that no longer exist on disk
    let indexedFiles = db.GetAllFiles()
    let staleFiles = indexedFiles |> List.filter (fun f -> not (File.Exists(f.Path)))

    if not staleFiles.IsEmpty then
        db.ExecuteInTransaction(fun () ->
            for f in staleFiles do
                db.DeleteFtsForFile(f.Path)
                db.DeleteFile(f.Path))

let syncDirectoryForce (db: IndexDatabase.OrgIndexDb) (directory: string) : unit =
    let orgFiles =
        Utils.listOrgFiles directory |> List.filter (fun f -> not (isEncryptedFile f))

    for filePath in orgFiles do
        let content =
            try
                Some(File.ReadAllText(filePath))
            with _ ->
                None

        match content with
        | None -> ()
        | Some text ->
            let hash = computeSha256 text
            let mtime = getUnixEpochSeconds filePath

            try
                db.ExecuteInTransaction(fun () ->
                    db.DeleteFtsForFile(filePath)
                    db.DeleteHeadlines(filePath)

                    db.InsertFile(
                        { Path = filePath
                          Hash = hash
                          Mtime = mtime }
                    )

                    indexFileContent db filePath text)
            with _ ->
                ()

    let indexedFiles = db.GetAllFiles()
    let staleFiles = indexedFiles |> List.filter (fun f -> not (File.Exists(f.Path)))

    if not staleFiles.IsEmpty then
        db.ExecuteInTransaction(fun () ->
            for f in staleFiles do
                db.DeleteFtsForFile(f.Path)
                db.DeleteFile(f.Path))
