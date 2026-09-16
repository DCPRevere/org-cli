module OrgCli.Index.Snapshot

open System.Text.Json
open OrgCli.Org

[<CLIMutable>]
type Pair = { Key: string; Value: string }

[<CLIMutable>]
type Heading =
    { Level: int
      Position: int64
      Title: string
      Todo: string
      Priority: string
      Tags: string array
      Properties: Pair array
      Scheduled: string
      Deadline: string
      Closed: string }

[<CLIMutable>]
type Link =
    { Kind: string
      Path: string
      Description: string
      Search: string
      Position: int
      Owner: string }

[<CLIMutable>]
type Data =
    { Keywords: Pair array
      Properties: Pair array
      Headlines: Heading array
      Links: Link array }

let private str = Option.toObj

let private props p =
    p
    |> Option.map (fun (d: PropertyDrawer) ->
        d.Properties
        |> List.map (fun p -> { Key = p.Key; Value = p.Value })
        |> List.toArray)
    |> Option.defaultValue [||]

let encode (doc: OrgDocument) =
    let timestamp (f: Planning -> Timestamp option) (p: Planning option) =
        p |> Option.bind f |> Option.map Writer.formatTimestamp |> str

    let data =
        { Keywords =
            doc.Keywords
            |> List.map (fun k -> { Key = k.Key; Value = k.Value })
            |> List.toArray
          Properties = props doc.FileProperties
          Headlines =
            doc.Headlines
            |> List.map (fun h ->
                { Level = h.Level
                  Position = h.Position
                  Title = h.Title
                  Todo = str h.TodoKeyword
                  Priority = h.Priority |> Option.map (fun (Priority c) -> string c) |> str
                  Tags = List.toArray h.Tags
                  Properties = props h.Properties
                  Scheduled = timestamp (fun p -> p.Scheduled) h.Planning
                  Deadline = timestamp (fun p -> p.Deadline) h.Planning
                  Closed = timestamp (fun p -> p.Closed) h.Planning })
            |> List.toArray
          Links =
            doc.Links
            |> List.map (fun (l, owner) ->
                { Kind = l.LinkType
                  Path = l.Path
                  Description = str l.Description
                  Search = str l.SearchOption
                  Position = l.Position
                  Owner = str owner })
            |> List.toArray }

    JsonSerializer.Serialize data

let decode file text : OrgDocument =
    let d = JsonSerializer.Deserialize<Data>(text: string)

    let properties (ps: Pair array) =
        if ps.Length = 0 then
            None
        else
            Some
                { Properties =
                    ps
                    |> Array.map (fun p ->
                        { Property.Key = p.Key
                          Value = p.Value })
                    |> Array.toList }

    let ts s =
        if isNull s then
            None
        else
            match Parsers.runParser Parsers.pTimestampRange s with
            | Ok value -> Some value
            | Error e -> failwith ("Invalid cached timestamp: " + e)

    { FilePath = Some file
      Keywords =
        d.Keywords
        |> Array.map (fun p -> { Keyword.Key = p.Key; Value = p.Value })
        |> Array.toList
      FileProperties = properties d.Properties
      Headlines =
        d.Headlines
        |> Array.map (fun h ->
            { Level = h.Level
              Position = h.Position
              Title = h.Title
              TodoKeyword = Option.ofObj h.Todo
              Priority = Option.ofObj h.Priority |> Option.map (fun s -> Priority s.[0])
              Tags = Array.toList h.Tags
              Properties = properties h.Properties
              Planning =
                if isNull h.Scheduled && isNull h.Deadline && isNull h.Closed then
                    None
                else
                    Some
                        { Scheduled = ts h.Scheduled
                          Deadline = ts h.Deadline
                          Closed = ts h.Closed } })
        |> Array.toList
      Links =
        d.Links
        |> Array.map (fun l ->
            { LinkType = l.Kind
              Path = l.Path
              Description = Option.ofObj l.Description
              SearchOption = Option.ofObj l.Search
              Position = l.Position },
            Option.ofObj l.Owner)
        |> Array.toList }
