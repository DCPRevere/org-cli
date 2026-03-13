module OrgCli.Org.Utils

open System
open System.IO
open System.Security.Cryptography
open System.Text.RegularExpressions

/// Generate a UUID in the format org-id uses
let generateId () : string =
    Guid.NewGuid().ToString().ToLowerInvariant()

/// Compute SHA1 hash of file contents (same as org-roam)
let computeFileHash (filePath: string) : string =
    use sha1 = SHA1.Create()
    use stream = File.OpenRead(filePath)
    let hash = sha1.ComputeHash(stream)
    BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant()

/// Compute SHA1 hash of string content
let computeContentHash (content: string) : string =
    use sha1 = SHA1.Create()
    let bytes = System.Text.Encoding.UTF8.GetBytes(content)
    let hash = sha1.ComputeHash(bytes)
    BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant()

/// Slugify a title (same algorithm as org-roam)
let slugify (title: string) : string =
    // Normalize unicode (NFC)
    let normalized = title.Normalize(System.Text.NormalizationForm.FormD)

    // Remove combining diacritical marks
    let withoutDiacritics =
        normalized
        |> Seq.filter (fun c ->
            System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
            <> System.Globalization.UnicodeCategory.NonSpacingMark)
        |> Seq.toArray
        |> String

    // Replace non-alphanumeric with underscore
    let withUnderscores =
        withoutDiacritics
        |> String.collect (fun c -> if Char.IsLetterOrDigit(c) then string c else "_")

    // Collapse multiple underscores
    let collapsed =
        System.Text.RegularExpressions.Regex.Replace(withUnderscores, "_+", "_")

    // Trim leading/trailing underscores and lowercase
    collapsed.Trim('_').ToLowerInvariant()

/// XDG Base Directory paths
let xdgConfigHome () =
    match Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") with
    | null
    | "" -> Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
    | v -> v

let xdgDataHome () =
    match Environment.GetEnvironmentVariable("XDG_DATA_HOME") with
    | null
    | "" -> Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
    | v -> v

let xdgCacheHome () =
    match Environment.GetEnvironmentVariable("XDG_CACHE_HOME") with
    | null
    | "" -> Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache")
    | v -> v

let orgCliConfigDir () =
    Path.Combine(xdgConfigHome (), "org-cli")

let orgCliDataDir () = Path.Combine(xdgDataHome (), "org-cli")

let orgCliCacheDir () =
    Path.Combine(xdgCacheHome (), "org-cli")

let orgCliConfigFile () =
    Path.Combine(orgCliConfigDir (), "config.json")

/// Parse a date string (yyyy-MM-dd) into an Active Timestamp.
let parseDate (s: string) : Timestamp =
    let d = DateTime.ParseExact(s, "yyyy-MM-dd", null)

    { Type = TimestampType.Active
      Date = d
      HasTime = false
      Repeater = None
      Delay = None
      RangeEnd = None }

let private repeaterPattern = Regex(@"^(\.\+|\+\+|\+)(\d+)([hdwmy])$")
let private delayPattern = Regex(@"^-(\d+)([hdwmy])$")

/// Parse a date with optional repeater and delay into an Active Timestamp.
/// Delay may be given with or without leading '-' (e.g. "2d" or "-2d").
/// Returns Error if repeater or delay format is invalid.
let parseDateWithRepeat (date: string) (repeater: string option) (delay: string option) : Result<Timestamp, string> =
    let d = DateTime.ParseExact(date, "yyyy-MM-dd", null)

    // Normalize delay: prepend '-' if not already present
    let normalizedDelay =
        delay |> Option.map (fun dl -> if dl.StartsWith("-") then dl else "-" + dl)

    match repeater with
    | Some r when not (repeaterPattern.IsMatch(r)) ->
        Error(sprintf "Invalid repeater format: '%s'. Expected +N[hdwmy], ++N[hdwmy], or .+N[hdwmy]." r)
    | _ ->
        match normalizedDelay with
        | Some dl when not (delayPattern.IsMatch(dl)) ->
            Error(sprintf "Invalid delay format: '%s'. Expected -N[hdwmy]." dl)
        | _ ->
            Ok
                { Type = TimestampType.Active
                  Date = d
                  HasTime = false
                  Repeater = repeater
                  Delay = normalizedDelay
                  RangeEnd = None }

/// Expand a leading ~/ (or bare ~) to the user's home directory.
let expandHome (path: string) : string =
    let home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)

    if path = "~" then
        home
    elif path.StartsWith("~/") || path.StartsWith("~\\") then
        Path.Combine(home, path.Substring(2))
    else
        path

/// Check if a file is an org file
let isOrgFile (filePath: string) : bool =
    let ext = Path.GetExtension(filePath).ToLowerInvariant()
    ext = ".org" || ext = ".org.gpg" || ext = ".org.age"

/// List all org files in a directory recursively.
/// Silently skips inaccessible subdirectories.
let listOrgFiles (directory: string) : string list =
    if not (Directory.Exists(directory)) then
        []
    else
        let opts =
            EnumerationOptions(RecurseSubdirectories = true, IgnoreInaccessible = true)

        let filter (f: string) =
            let fileName = Path.GetFileName(f)

            not (fileName.StartsWith("."))
            && not (f.Contains("/.git/"))
            && not (f.Contains("\\.git\\"))

        let orgFiles =
            Directory.EnumerateFiles(directory, "*.org", opts) |> Seq.filter filter

        let gpgFiles =
            Directory.EnumerateFiles(directory, "*.org.gpg", opts) |> Seq.filter filter

        let ageFiles =
            Directory.EnumerateFiles(directory, "*.org.age", opts) |> Seq.filter filter

        Seq.concat [ orgFiles; gpgFiles; ageFiles ] |> Seq.toList

/// Get file modification time
let getFileMtime (filePath: string) : DateTime = File.GetLastWriteTimeUtc(filePath)

/// Get file access time
let getFileAtime (filePath: string) : DateTime = File.GetLastAccessTimeUtc(filePath)

/// Format DateTime to ISO8601 (same as org-roam)
let formatIso8601 (dt: DateTime) : string = dt.ToString("yyyy-MM-ddTHH:mm:ss")

/// Parse ISO8601 datetime
let parseIso8601 (s: string) : DateTime option =
    match DateTime.TryParseExact(s, "yyyy-MM-ddTHH:mm:ss", null, System.Globalization.DateTimeStyles.None) with
    | true, dt -> Some dt
    | false, _ -> None
