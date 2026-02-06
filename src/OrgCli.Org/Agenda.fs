module OrgCli.Org.Agenda

open System
open OrgCli.Org

type AgendaItemType =
    | Scheduled
    | Deadline

type AgendaItem = {
    Type: AgendaItemType
    Date: DateTime
    Headline: Headline
    File: string
}

let private isDoneState (config: OrgConfig) (keyword: string option) : bool =
    match keyword with
    | None -> false
    | Some kw -> Types.isDoneState config.TodoKeywords kw

let private maxRangeExpansion = 366

let private expandTimestamp (ts: Timestamp) (itemType: AgendaItemType) (h: Headline) (file: string) : AgendaItem list =
    match ts.RangeEnd with
    | None -> [{ Type = itemType; Date = ts.Date.Date; Headline = h; File = file }]
    | Some endTs ->
        let mutable d = ts.Date.Date
        let endDate = endTs.Date.Date
        let mutable count = 0
        [ while d <= endDate && count < maxRangeExpansion do
            yield { Type = itemType; Date = d; Headline = h; File = file }
            d <- d.AddDays(1.0)
            count <- count + 1 ]

let private collectDatedItemsCore (_config: OrgConfig) (headlines: (Headline * string) list) : AgendaItem list =
    headlines
    |> List.collect (fun (h, file) ->
        match h.Planning with
        | None -> []
        | Some p ->
            let scheduled =
                p.Scheduled
                |> Option.map (fun ts -> expandTimestamp ts Scheduled h file)
                |> Option.defaultValue []
            let deadline =
                p.Deadline
                |> Option.map (fun ts -> expandTimestamp ts Deadline h file)
                |> Option.defaultValue []
            scheduled @ deadline)

/// Collect headlines with SCHEDULED or DEADLINE planning info.
/// A headline with both produces two items (one per date).
/// Ranges expand to one item per day.
let collectDatedItems (config: OrgConfig) (files: string list) : AgendaItem list =
    let headlines =
        files
        |> List.collect (fun file ->
            let doc = Document.parseFile file
            doc.Headlines |> List.map (fun h -> (h, file)))
    collectDatedItemsCore config headlines

/// Collect headlines with SCHEDULED or DEADLINE from pre-parsed documents.
/// Ranges expand to one item per day.
let collectDatedItemsFromDocs (config: OrgConfig) (docs: (string * OrgDocument) list) : AgendaItem list =
    let headlines =
        docs
        |> List.collect (fun (file, doc) ->
            doc.Headlines |> List.map (fun h -> (h, file)))
    collectDatedItemsCore config headlines

let private collectTodoItemsCore (_config: OrgConfig) (headlines: (Headline * string) list) : AgendaItem list =
    headlines
    |> List.choose (fun (h, file) ->
        match h.TodoKeyword with
        | Some _ ->
            let date =
                h.Planning
                |> Option.bind (fun p ->
                    match p.Scheduled, p.Deadline with
                    | Some ts, _ -> Some ts.Date.Date
                    | _, Some ts -> Some ts.Date.Date
                    | _ -> None)
                |> Option.defaultValue DateTime.MinValue
            Some { Type = Scheduled; Date = date; Headline = h; File = file }
        | None -> None)

/// Collect all headlines with a TODO keyword.
let collectTodoItems (config: OrgConfig) (files: string list) : AgendaItem list =
    let headlines =
        files
        |> List.collect (fun file ->
            let doc = Document.parseFile file
            doc.Headlines |> List.map (fun h -> (h, file)))
    collectTodoItemsCore config headlines

/// Collect all headlines with a TODO keyword from pre-parsed documents.
let collectTodoItemsFromDocs (config: OrgConfig) (docs: (string * OrgDocument) list) : AgendaItem list =
    let headlines =
        docs
        |> List.collect (fun (file, doc) ->
            doc.Headlines |> List.map (fun h -> (h, file)))
    collectTodoItemsCore config headlines

let filterByDateRange (start: DateTime) (end_: DateTime) (items: AgendaItem list) : AgendaItem list =
    items |> List.filter (fun i -> i.Date >= start && i.Date < end_)

let filterByTag (tag: string) (items: AgendaItem list) : AgendaItem list =
    items |> List.filter (fun i -> List.contains tag i.Headline.Tags)

/// Filter overdue deadline items, using config to determine done states.
let filterOverdue (config: OrgConfig) (today: DateTime) (items: AgendaItem list) : AgendaItem list =
    items
    |> List.filter (fun i ->
        i.Type = Deadline
        && i.Date < today
        && not (isDoneState config i.Headline.TodoKeyword))

/// Remove items whose headline is in a done state.
let skipDoneItems (config: OrgConfig) (items: AgendaItem list) : AgendaItem list =
    items |> List.filter (fun i -> not (isDoneState config i.Headline.TodoKeyword))

/// Filter deadline items within the warning window (or already overdue).
let filterDeadlineWarnings (config: OrgConfig) (today: DateTime) (items: AgendaItem list) : AgendaItem list =
    let warningEnd = today.AddDays(float config.DeadlineWarningDays)
    items
    |> List.filter (fun i ->
        i.Type = Deadline && i.Date < warningEnd)
