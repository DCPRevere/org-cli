namespace OrgCli.Roam

open System

/// Represents a file in the org-roam database
type DbFile = {
    File: string
    Title: string option
    Hash: string
    Atime: DateTime
    Mtime: DateTime
}

/// Represents a node in the org-roam database
type DbNode = {
    Id: string
    File: string
    Level: int
    Pos: int
    Todo: string option
    Priority: string option
    Scheduled: string option
    Deadline: string option
    Title: string
    Properties: string  // Elisp alist format
    Olp: string         // Elisp list format
}

/// Represents an alias in the org-roam database
type DbAlias = {
    NodeId: string
    Alias: string
}

/// Represents a citation in the org-roam database
type DbCitation = {
    NodeId: string
    CiteKey: string
    Pos: int
    Properties: string
}

/// Represents a ref in the org-roam database
type DbRef = {
    NodeId: string
    Ref: string
    Type: string
}

/// Represents a tag in the org-roam database
type DbTag = {
    NodeId: string
    Tag: string
}

/// Represents a link in the org-roam database
type DbLink = {
    Pos: int
    Source: string
    Dest: string
    Type: string
    Properties: string
}

/// A fully populated node with all related data
type RoamNode = {
    Id: string
    File: string
    FileTitle: string option
    FileHash: string option
    FileAtime: DateTime option
    FileMtime: DateTime option
    Level: int
    Point: int
    Todo: string option
    Priority: string option
    Scheduled: string option
    Deadline: string option
    Title: string
    Properties: (string * string) list
    Olp: string list
    Tags: string list
    Aliases: string list
    Refs: string list
}

/// A backlink to a node
type Backlink = {
    SourceNode: RoamNode
    TargetNodeId: string
    Point: int
    Properties: (string * string) list
}

module Domain =
    /// Database version - must match org-roam v20
    let DbVersion = 20

    /// Parse an Elisp alist string into key-value pairs
    /// Format: (("KEY1" . "VALUE1") ("KEY2" . "VALUE2"))
    let parseElispAlist (s: string) : (string * string) list =
        if String.IsNullOrWhiteSpace(s) || s = "nil" then []
        else
            // Simple regex-based parsing
            let pattern = System.Text.RegularExpressions.Regex(@"\(""([^""]*)""\s*\.\s*""([^""]*)""\)")
            pattern.Matches(s)
            |> Seq.cast<System.Text.RegularExpressions.Match>
            |> Seq.map (fun m -> m.Groups.[1].Value, m.Groups.[2].Value)
            |> Seq.toList

    /// Parse an Elisp list string into items
    /// Format: ("item1" "item2" "item3") or (item1 item2)
    let parseElispList (s: string) : string list =
        if String.IsNullOrWhiteSpace(s) || s = "nil" then []
        else
            let trimmed = s.Trim()
            if not (trimmed.StartsWith("(") && trimmed.EndsWith(")")) then []
            else
                let inner = trimmed.Substring(1, trimmed.Length - 2).Trim()
                if String.IsNullOrWhiteSpace(inner) then []
                else
                    // Handle quoted strings
                    let mutable items = []
                    let mutable current = System.Text.StringBuilder()
                    let mutable inQuote = false

                    for c in inner do
                        match c with
                        | '"' when not inQuote ->
                            inQuote <- true
                        | '"' when inQuote ->
                            inQuote <- false
                            items <- current.ToString() :: items
                            current.Clear() |> ignore
                        | ' ' when not inQuote ->
                            if current.Length > 0 then
                                items <- current.ToString() :: items
                                current.Clear() |> ignore
                        | _ ->
                            current.Append(c) |> ignore

                    if current.Length > 0 then
                        items <- current.ToString() :: items

                    List.rev items

    /// Format key-value pairs as an Elisp alist
    let formatElispAlist (pairs: (string * string) list) : string =
        if List.isEmpty pairs then "nil"
        else
            let items =
                pairs
                |> List.map (fun (k, v) ->
                    let escapedK = k.Replace("\\", "\\\\").Replace("\"", "\\\"")
                    let escapedV = v.Replace("\\", "\\\\").Replace("\"", "\\\"")
                    sprintf "(\"%s\" . \"%s\")" escapedK escapedV)
            sprintf "(%s)" (String.Join(" ", items))

    /// Format a string list as an Elisp list
    let formatElispList (items: string list) : string =
        if List.isEmpty items then "nil"
        else
            let escaped =
                items
                |> List.map (fun s ->
                    let e = s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                    sprintf "\"%s\"" e)
            sprintf "(%s)" (String.Join(" ", escaped))

    /// Format key-value pairs as an Elisp plist (property list)
    /// Format: (:key1 value1 :key2 value2)
    /// Values should already be formatted (e.g., quoted strings, lists)
    let formatElispPlist (pairs: (string * string) list) : string =
        if List.isEmpty pairs then "nil"
        else
            let items =
                pairs
                |> List.collect (fun (k, v) -> [k; v])
            sprintf "(%s)" (String.Join(" ", items))
