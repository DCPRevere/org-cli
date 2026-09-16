module OrgCli.Org.Runtime

open System
open System.IO
open System.Text
open System.Threading

/// Injectable filesystem, environment and clock. No test needs the user's home or files.
type IHost =
    abstract CurrentDirectory: string
    abstract HomeDirectory: string
    abstract Now: DateTime
    abstract GetEnvironmentVariable: string -> string
    abstract FileExists: string -> bool
    abstract DirectoryExists: string -> bool
    abstract ReadBytes: string -> byte array
    abstract WriteBytes: string * byte array -> unit
    abstract MoveFile: string * string * bool -> unit
    abstract DeleteFile: string -> unit
    abstract CreateDirectory: string -> unit
    abstract DeleteDirectory: string -> unit
    abstract EnumerateFiles: string -> string list
    abstract LastWriteTime: string -> DateTime
    abstract AcquireLock: string -> IDisposable
    abstract DatabaseConnectionString: string -> string

type PhysicalHost() =
    interface IHost with
        member _.CurrentDirectory = Directory.GetCurrentDirectory()

        member _.HomeDirectory =
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)

        member _.Now = DateTime.Now
        member _.GetEnvironmentVariable name = Environment.GetEnvironmentVariable name
        member _.FileExists p = File.Exists p
        member _.DirectoryExists p = Directory.Exists p
        member _.ReadBytes p = File.ReadAllBytes p

        member _.WriteBytes(p, bytes) =
            use stream = new FileStream(p, FileMode.Create, FileAccess.Write, FileShare.None)
            stream.Write(bytes)
            stream.Flush(true)

        member _.MoveFile(a, b, replace) =
            if File.Exists b && not (OperatingSystem.IsWindows()) then
                File.SetUnixFileMode(a, File.GetUnixFileMode b)

            File.Move(a, b, replace)

        member _.DeleteFile p = File.Delete p
        member _.CreateDirectory p = Directory.CreateDirectory p |> ignore
        member _.DeleteDirectory p = Directory.Delete p

        member _.EnumerateFiles p =
            let options =
                EnumerationOptions(
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = false,
                    AttributesToSkip = FileAttributes.ReparsePoint
                )

            Directory.EnumerateFiles(p, "*", options) |> Seq.toList

        member _.LastWriteTime p = File.GetLastWriteTimeUtc p

        member _.AcquireLock p =
            new FileStream(p, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None) :> IDisposable

        member _.DatabaseConnectionString p =
            sprintf "Data Source=%s;Foreign Keys=True" p

let private local = AsyncLocal<IHost>()
let private physical = PhysicalHost() :> IHost

let host () =
    if isNull (box local.Value) then physical else local.Value

let useHost value =
    let previous = local.Value
    local.Value <- value

    { new IDisposable with
        member _.Dispose() = local.Value <- previous }

let fullPath path =
    Path.GetFullPath(path, (host ()).CurrentDirectory)

let now () = (host ()).Now
let today () = (now ()).Date
let environment name = (host ()).GetEnvironmentVariable name
let home () = (host ()).HomeDirectory
let fileExists path = (host ()).FileExists(fullPath path)

let directoryExists path =
    (host ()).DirectoryExists(fullPath path)

let createDirectory path =
    (host ()).CreateDirectory(fullPath path)

let readBytes path = (host ()).ReadBytes(fullPath path)
let private utf8 = UTF8Encoding(false, true)

let decode (bytes: byte array) =
    let start =
        if bytes.Length >= 3 && bytes.[0..2] = [| 239uy; 187uy; 191uy |] then
            3
        else
            0

    utf8.GetString(bytes, start, bytes.Length - start)

let readText path = readBytes path |> decode

let hash (bytes: byte array) =
    Convert.ToHexString(Security.Cryptography.SHA256.HashData bytes)

type Edit =
    { Path: string
      Before: byte array option
      After: byte array }

let edit path text =
    let p = fullPath path
    let before = if fileExists p then Some(readBytes p) else None
    let encoded = utf8.GetBytes(text: string)

    let after =
        match before with
        | Some b when b.Length >= 3 && b.[0..2] = [| 239uy; 187uy; 191uy |] -> Array.append b.[0..2] encoded
        | _ -> encoded

    { Path = p
      Before = before
      After = after }

/// Stage every output before replacing any input. Interrupted replacements retain a recovery record.
let commit (edits: Edit list) =
    let edits = edits |> List.filter (fun e -> e.Before <> Some e.After)

    if not edits.IsEmpty then
        let fs = host ()

        if (edits |> List.map (fun e -> e.Path) |> List.distinct).Length <> edits.Length then
            invalidArg "edits" "Only one final edit per file is allowed"

        let locks = ResizeArray<IDisposable>()
        let operation = Guid.NewGuid().ToString("N")

        let journal =
            Path.Combine(Path.GetDirectoryName edits.Head.Path, ".org-operation-" + operation)

        let createdMarkers = ResizeArray<string>()
        let mutable started = false

        try
            try
                for p in edits |> List.map (fun e -> e.Path) |> List.sort do
                    if fs.DirectoryExists p then
                        raise (IOException("Destination is a directory: " + p))

                    locks.Add(fs.AcquireLock(p + ".org-lock"))

                    if fs.FileExists(p + ".org-pending") then
                        raise (IOException("Pending operation: " + decode (fs.ReadBytes(p + ".org-pending"))))

                for e in edits do
                    let current =
                        if fs.FileExists e.Path then
                            Some(fs.ReadBytes e.Path)
                        else
                            None

                    if current <> e.Before then
                        raise (IOException("Conflict: file changed: " + e.Path))

                fs.CreateDirectory journal

                let manifest =
                    edits
                    |> List.map (fun e ->
                        {| path = e.Path
                           original = e.Before |> Option.map Convert.ToBase64String |> Option.defaultValue null
                           replacement = Convert.ToBase64String e.After
                           stage = e.Path + ".org-stage-" + operation |})

                fs.WriteBytes(
                    Path.Combine(journal, "manifest.json"),
                    utf8.GetBytes(System.Text.Json.JsonSerializer.Serialize manifest)
                )

                for e in edits do
                    createdMarkers.Add(e.Path + ".org-pending")
                    fs.WriteBytes(e.Path + ".org-pending", utf8.GetBytes(Path.Combine(journal, "manifest.json")))

                for e in edits do
                    fs.WriteBytes(e.Path + ".org-stage-" + operation, e.After)

                for e in edits do
                    started <- true
                    fs.MoveFile(e.Path + ".org-stage-" + operation, e.Path, e.Before.IsSome)

                for e in edits do
                    fs.DeleteFile(e.Path + ".org-pending")

                fs.DeleteFile(Path.Combine(journal, "manifest.json"))
                fs.DeleteDirectory journal
            with ex ->
                if started then
                    raise (IOException(sprintf "Commit interrupted; recovery record: %s (%s)" journal ex.Message, ex))
                else
                    for e in edits do
                        let stage = e.Path + ".org-stage-" + operation

                        if fs.FileExists stage then
                            fs.DeleteFile stage

                    for marker in createdMarkers do
                        if fs.FileExists marker then
                            fs.DeleteFile marker

                    if fs.FileExists(Path.Combine(journal, "manifest.json")) then
                        fs.DeleteFile(Path.Combine(journal, "manifest.json"))

                    if fs.DirectoryExists journal then
                        fs.DeleteDirectory journal

                    reraise ()
        finally
            for handle in Seq.rev locks do
                handle.Dispose()

let writeText (path, text) = commit [ edit path text ]

let appendText (path, text) =
    writeText (path, (if fileExists path then readText path else "") + text)

let deleteFile path = (host ()).DeleteFile(fullPath path)
let lastWriteTime path = (host ()).LastWriteTime(fullPath path)

/// Plan from the exact text used by the transform, rather than a newer reread.
let editExpected path expected updated =
    let plan = edit path updated
    let current = plan.Before |> Option.map decode |> Option.defaultValue ""

    if current <> expected then
        raise (IOException("Conflict: file changed while planning: " + plan.Path))

    plan

/// Complete an interrupted operation only when every file is still an original or planned version.
let recover (manifestPath: string) =
    let fs = host ()
    let manifestPath = fullPath manifestPath
    use json = System.Text.Json.JsonDocument.Parse(readText manifestPath)

    let entries =
        json.RootElement.EnumerateArray()
        |> Seq.map (fun e ->
            let path = e.GetProperty("path").GetString()
            let original = e.GetProperty("original")

            let before =
                if original.ValueKind = System.Text.Json.JsonValueKind.Null then
                    None
                else
                    Some(Convert.FromBase64String(original.GetString()))

            let after = Convert.FromBase64String(e.GetProperty("replacement").GetString())
            path, before, after, e.GetProperty("stage").GetString())
        |> Seq.toList

    let locks = ResizeArray<IDisposable>()

    try
        for p, _, _, _ in entries |> List.sortBy (fun (p, _, _, _) -> p) do
            locks.Add(fs.AcquireLock(p + ".org-lock"))

        for p, before, after, _ in entries do
            let current = if fs.FileExists p then Some(fs.ReadBytes p) else None

            if current <> before && current <> Some after then
                raise (IOException("Recovery conflict: " + p))

        for p, _, after, stage in entries do
            if not (fs.FileExists p) || fs.ReadBytes p <> after then
                fs.WriteBytes(stage, after)
                fs.MoveFile(stage, p, fs.FileExists p)
            elif fs.FileExists stage then
                fs.DeleteFile stage

        for p, _, _, _ in entries do
            if fs.FileExists(p + ".org-pending") then
                fs.DeleteFile(p + ".org-pending")

        fs.DeleteFile manifestPath
        let directory = Path.GetDirectoryName manifestPath

        if Path.GetFileName(directory).StartsWith(".org-operation-") then
            fs.DeleteDirectory directory
    finally
        for handle in Seq.rev locks do
            handle.Dispose()
