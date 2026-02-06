module OrgCli.Org.Search

open System.Text.RegularExpressions

type SearchResult = {
    Headline: Headline option
    File: string
    MatchLine: string
    LineNumber: int
}

let private findContainingHeadline (headlines: Headline list) (charPos: int) : Headline option =
    headlines
    |> List.filter (fun h -> int h.Position <= charPos)
    |> List.tryLast

/// Search pre-parsed documents for a regex pattern, returning results with containing headline context.
/// Returns Error if the pattern is not a valid regex.
let searchDocs (pattern: string) (docs: (string * OrgDocument * string) list) : Result<SearchResult list, string> =
    let regex =
        try Result.Ok (Regex(pattern))
        with :? System.ArgumentException as e -> Result.Error e.Message
    match regex with
    | Result.Error msg -> Result.Error (sprintf "Invalid regex pattern: %s" msg)
    | Result.Ok regex ->
        docs
        |> List.collect (fun (file, doc, content) ->
            let lines = content.Split([|'\n'|])
            let (_, _, results) =
                lines
                |> Array.fold (fun (lineNum, charPos, acc) line ->
                    let nextCharPos = charPos + line.Length + 1
                    if regex.IsMatch(line) then
                        let headline = findContainingHeadline doc.Headlines charPos
                        let result = {
                            Headline = headline
                            File = file
                            MatchLine = line
                            LineNumber = lineNum
                        }
                        (lineNum + 1, nextCharPos, result :: acc)
                    else
                        (lineNum + 1, nextCharPos, acc))
                    (1, 0, [])
            List.rev results)
        |> Result.Ok

/// Search files on disk for a regex pattern.
let search (pattern: string) (files: string list) : Result<SearchResult list, string> =
    let docs =
        files
        |> List.map (fun f ->
            let content = System.IO.File.ReadAllText(f)
            (f, Document.parse content, content))
    searchDocs pattern docs
