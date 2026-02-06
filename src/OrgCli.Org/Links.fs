module OrgCli.Org.Links

open System.IO

let private unresolved (link: OrgLink) : ResolvedLink =
    { Link = link; TargetFile = None; TargetHeadline = None; TargetPos = None }

let private findHeadlineById (id: string) (docs: (string * OrgDocument) list) : ResolvedLink option =
    docs
    |> List.tryPick (fun (file, doc) ->
        doc.Headlines
        |> List.tryFind (fun h -> Types.tryGetId h.Properties = Some id)
        |> Option.map (fun h ->
            { Link = { LinkType = "id"; Path = id; Description = None; SearchOption = None; Position = 0 }
              TargetFile = Some file
              TargetHeadline = Some h.Title
              TargetPos = Some h.Position }))

let private findHeadlineByTitle (title: string) (file: string) (doc: OrgDocument) : (string * int64) option =
    doc.Headlines
    |> List.tryFind (fun h -> h.Title = title)
    |> Option.map (fun h -> (h.Title, h.Position))

let private findHeadlineByCustomId (customId: string) (file: string) (doc: OrgDocument) : (string * int64) option =
    doc.Headlines
    |> List.tryFind (fun h -> Types.tryGetProperty "CUSTOM_ID" h.Properties = Some customId)
    |> Option.map (fun h -> (h.Title, h.Position))

/// Fuzzy headline search: uses substring match, matching org-mode's ::search link resolution.
let private findHeadlineContaining (text: string) (doc: OrgDocument) : (string * int64) option =
    doc.Headlines
    |> List.tryFind (fun h -> h.Title.Contains(text))
    |> Option.map (fun h -> (h.Title, h.Position))

let private resolveFilePath (path: string) (currentFile: string) : string =
    let dir = Path.GetDirectoryName(currentFile)
    if dir = null || dir = "" then path
    else Path.Combine(dir, path) |> fun p -> p.Replace("\\", "/")

let private resolveSearchOption (search: string) (file: string) (doc: OrgDocument) : (string * int64) option =
    if search.StartsWith("*") then
        findHeadlineByTitle (search.Substring(1)) file doc
    elif search.StartsWith("#") then
        findHeadlineByCustomId (search.Substring(1)) file doc
    else
        findHeadlineContaining search doc

let resolveLink (link: OrgLink) (currentFile: string) (docs: (string * OrgDocument) list) (abbreviations: Map<string, string>) : ResolvedLink =
    match link.LinkType with
    | "id" ->
        findHeadlineById link.Path docs
        |> Option.map (fun r -> { r with Link = link })
        |> Option.defaultValue (unresolved link)

    | "file" ->
        let targetPath = resolveFilePath link.Path currentFile
        let targetDoc = docs |> List.tryFind (fun (f, _) -> f = targetPath)
        match targetDoc, link.SearchOption with
        | None, _ ->
            { (unresolved link) with TargetFile = Some targetPath }
        | Some (f, _), None ->
            { (unresolved link) with TargetFile = Some f }
        | Some (f, doc), Some search ->
            match resolveSearchOption search f doc with
            | Some (title, pos) ->
                { Link = link; TargetFile = Some f; TargetHeadline = Some title; TargetPos = Some pos }
            | None ->
                { (unresolved link) with TargetFile = Some f }

    | "fuzzy" ->
        if link.Path.StartsWith("*") then
            let title = link.Path.Substring(1)
            let currentDoc = docs |> List.tryFind (fun (f, _) -> f = currentFile)
            match currentDoc with
            | Some (f, doc) ->
                match findHeadlineByTitle title f doc with
                | Some (t, pos) ->
                    { Link = link; TargetFile = Some f; TargetHeadline = Some t; TargetPos = Some pos }
                | None -> unresolved link
            | None -> unresolved link
        elif link.Path.StartsWith("#") then
            let customId = link.Path.Substring(1)
            let currentDoc = docs |> List.tryFind (fun (f, _) -> f = currentFile)
            match currentDoc with
            | Some (f, doc) ->
                match findHeadlineByCustomId customId f doc with
                | Some (t, pos) ->
                    { Link = link; TargetFile = Some f; TargetHeadline = Some t; TargetPos = Some pos }
                | None -> unresolved link
            | None -> unresolved link
        else
            // Search for headline by title in current file, then all files
            let currentDoc = docs |> List.tryFind (fun (f, _) -> f = currentFile)
            let inCurrent =
                currentDoc
                |> Option.bind (fun (f, doc) -> findHeadlineByTitle link.Path f doc |> Option.map (fun (t, p) -> (f, t, p)))
            match inCurrent with
            | Some (f, t, pos) ->
                { Link = link; TargetFile = Some f; TargetHeadline = Some t; TargetPos = Some pos }
            | None ->
                let inAny =
                    docs
                    |> List.tryPick (fun (f, doc) ->
                        findHeadlineByTitle link.Path f doc
                        |> Option.map (fun (t, p) -> (f, t, p)))
                match inAny with
                | Some (f, t, pos) ->
                    { Link = link; TargetFile = Some f; TargetHeadline = Some t; TargetPos = Some pos }
                | None -> unresolved link

    | "https" | "http" ->
        unresolved link

    | _ ->
        match Map.tryFind link.LinkType abbreviations with
        | Some template ->
            let url =
                if template.Contains("%s") then template.Replace("%s", link.Path)
                else template + link.Path
            { (unresolved link) with TargetFile = Some url }
        | None ->
            unresolved link

let private extractLinkAbbreviations (doc: OrgDocument) : Map<string, string> =
    doc.Keywords
    |> List.choose (fun kw ->
        if kw.Key = "LINK" then
            let parts = kw.Value.Trim().Split([| ' '; '\t' |], 2)
            if parts.Length = 2 then Some (parts.[0], parts.[1].Trim())
            else None
        else None)
    |> Map.ofList

let resolveLinksInFile (file: string) (docs: (string * OrgDocument) list) : ResolvedLink list =
    let doc = docs |> List.tryFind (fun (f, _) -> f = file)
    match doc with
    | None -> []
    | Some (_, d) ->
        let abbrevs = extractLinkAbbreviations d
        d.Links
        |> List.map (fun (link, _) -> resolveLink link file docs abbrevs)
