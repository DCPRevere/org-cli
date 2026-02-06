module OrgCli.Org.Clock

open System
open System.Text.RegularExpressions

let private clockLineRegex = Regex(@"^\s*CLOCK:", RegexOptions.Multiline)
let private headlineRegex = Regex(@"^(\*+) ", RegexOptions.Multiline)

/// Get the range of a headline's own body (from its start to the next headline of any level).
let private getOwnBodyRange (content: string) (pos: int64) : int * int =
    let startIdx = int pos
    let m = headlineRegex.Match(content, startIdx)
    if not m.Success || m.Index <> startIdx then
        startIdx, startIdx
    else
        let lineEnd =
            match content.IndexOf('\n', startIdx) with
            | -1 -> content.Length
            | i -> i + 1
        let nextHeadline =
            headlineRegex.Matches(content, lineEnd)
            |> Seq.cast<Match>
            |> Seq.tryHead
        match nextHeadline with
        | Some nm -> startIdx, nm.Index
        | None -> startIdx, content.Length

/// Collect clock entries from pre-parsed documents.
/// Returns list of (headline, file, clock entries) for headlines that have clock entries.
/// Each headline only reports its own clock entries, not those of child headlines.
let collectClockEntriesFromDocs (docs: (string * OrgDocument * string) list) : (Headline * string * ClockEntry list) list =
    docs
    |> List.collect (fun (file, doc, content) ->
        doc.Headlines
        |> List.choose (fun h ->
            let (sectionStart, sectionEnd) = getOwnBodyRange content h.Position
            let sectionContent = content.Substring(sectionStart, sectionEnd - sectionStart)

            let clockEntries =
                clockLineRegex.Matches(sectionContent)
                |> Seq.cast<Match>
                |> Seq.choose (fun m ->
                    let lineEnd =
                        match sectionContent.IndexOf('\n', m.Index) with
                        | -1 -> sectionContent.Length
                        | i -> i
                    let line = sectionContent.Substring(m.Index, lineEnd - m.Index)
                    match Parsers.runParser Parsers.pClockEntry (line.TrimStart() + "\n") with
                    | Result.Ok entry -> Some entry
                    | Result.Error _ -> None)
                |> Seq.toList

            if List.isEmpty clockEntries then None
            else Some (h, file, clockEntries)))

/// Collect clock entries from files on disk.
let collectClockEntries (files: string list) : (Headline * string * ClockEntry list) list =
    let docs =
        files
        |> List.map (fun f ->
            let content = System.IO.File.ReadAllText(f)
            (f, Document.parse content, content))
    collectClockEntriesFromDocs docs

/// Sum the durations of completed clock entries.
let totalDuration (entries: ClockEntry list) : TimeSpan =
    entries
    |> List.choose (fun e -> e.Duration)
    |> List.fold (fun (acc: TimeSpan) d -> acc.Add(d)) TimeSpan.Zero
