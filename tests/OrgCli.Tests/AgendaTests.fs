module AgendaTests

open System
open Xunit
open OrgCli.Org

let private today = DateTime(2026, 2, 5)
let private tomorrow = today.AddDays(1.0)
let private yesterday = today.AddDays(-1.0)
let private nextWeek = today.AddDays(6.0)

let private dayName (d: DateTime) =
    d.ToString("ddd").Substring(0, 3)

let private parseDoc (content: string) =
    Document.parse content

let private defaultConfig = Types.defaultConfig

let private datedItemsFromContent (content: string) =
    let doc = parseDoc content
    Agenda.collectDatedItemsFromDocs defaultConfig [ ("test.org", doc) ]

let private todoItemsFromContent (content: string) =
    let doc = parseDoc content
    Agenda.collectTodoItemsFromDocs defaultConfig [ ("test.org", doc) ]

[<Fact>]
let ``Headline with SCHEDULED today appears in today view`` () =
    let content = sprintf "* TODO Write tests\nSCHEDULED: <%s %s>\n" (today.ToString("yyyy-MM-dd")) (dayName today)
    let items = datedItemsFromContent content
    let filtered = Agenda.filterByDateRange today tomorrow items
    Assert.Equal(1, filtered.Length)
    Assert.Equal(Agenda.Scheduled, filtered.[0].Type)
    Assert.Equal("Write tests", filtered.[0].Headline.Title)

[<Fact>]
let ``Headline with DEADLINE today appears in today view`` () =
    let content = sprintf "* TODO Submit report\nDEADLINE: <%s %s>\n" (today.ToString("yyyy-MM-dd")) (dayName today)
    let items = datedItemsFromContent content
    let filtered = Agenda.filterByDateRange today tomorrow items
    Assert.Equal(1, filtered.Length)
    Assert.Equal(Agenda.Deadline, filtered.[0].Type)
    Assert.Equal("Submit report", filtered.[0].Headline.Title)

[<Fact>]
let ``Headline with DEADLINE yesterday appears as overdue`` () =
    let content = sprintf "* TODO Overdue task\nDEADLINE: <%s %s>\n" (yesterday.ToString("yyyy-MM-dd")) (dayName yesterday)
    let items = datedItemsFromContent content
    let overdue = Agenda.filterOverdue defaultConfig today items
    Assert.Equal(1, overdue.Length)
    Assert.Equal("Overdue task", overdue.[0].Headline.Title)

[<Fact>]
let ``Headline with DEADLINE next week appears in week view but not today`` () =
    let content = sprintf "* TODO Future task\nDEADLINE: <%s %s>\n" (nextWeek.ToString("yyyy-MM-dd")) (dayName nextWeek)
    let items = datedItemsFromContent content
    let todayItems = Agenda.filterByDateRange today tomorrow items
    Assert.Empty(todayItems)
    let weekItems = Agenda.filterByDateRange today (today.AddDays(7.0)) items
    Assert.Equal(1, weekItems.Length)

[<Fact>]
let ``DONE items excluded from overdue`` () =
    let content = sprintf "* DONE Finished task\nDEADLINE: <%s %s>\n" (yesterday.ToString("yyyy-MM-dd")) (dayName yesterday)
    let items = datedItemsFromContent content
    let overdue = Agenda.filterOverdue defaultConfig today items
    Assert.Empty(overdue)

[<Fact>]
let ``CANCELLED items excluded from overdue`` () =
    let content = sprintf "* CANCELLED Cancelled task\nDEADLINE: <%s %s>\n" (yesterday.ToString("yyyy-MM-dd")) (dayName yesterday)
    let items = datedItemsFromContent content
    let overdue = Agenda.filterOverdue defaultConfig today items
    Assert.Empty(overdue)

[<Fact>]
let ``Tag filter works`` () =
    let content = sprintf "* TODO Tagged task :work:\nSCHEDULED: <%s %s>\n\n* TODO Untagged task\nSCHEDULED: <%s %s>\n"
                    (today.ToString("yyyy-MM-dd")) (dayName today)
                    (today.ToString("yyyy-MM-dd")) (dayName today)
    let items = datedItemsFromContent content
    let filtered = Agenda.filterByTag "work" items
    Assert.Equal(1, filtered.Length)
    Assert.Equal("Tagged task", filtered.[0].Headline.Title)

[<Fact>]
let ``Headline with no planning info does not appear in dated items`` () =
    let content = "* TODO Plain task\nSome body text\n"
    let items = datedItemsFromContent content
    Assert.Empty(items)

[<Fact>]
let ``File-level nodes do not appear in agenda`` () =
    let content = ":PROPERTIES:\n:ID: some-id\n:END:\n#+title: A File Node\n\nSome content.\n"
    let items = datedItemsFromContent content
    Assert.Empty(items)

[<Fact>]
let ``collectTodoItems includes all TODO headlines`` () =
    let content = "* TODO First task\n\n* DONE Second task\n\n* Third headline no keyword\n"
    let items = todoItemsFromContent content
    Assert.Equal(2, items.Length)
    let titles = items |> List.map (fun i -> i.Headline.Title)
    Assert.Contains("First task", titles)
    Assert.Contains("Second task", titles)

[<Fact>]
let ``collectTodoItems state filter works`` () =
    let content = "* TODO Active task\n\n* DONE Completed task\n\n* NEXT In progress\n"
    let items = todoItemsFromContent content
    let todoOnly = items |> List.filter (fun i -> i.Headline.TodoKeyword = Some "TODO")
    Assert.Equal(1, todoOnly.Length)
    Assert.Equal("Active task", todoOnly.[0].Headline.Title)

[<Fact>]
let ``Headline with both SCHEDULED and DEADLINE produces two dated items`` () =
    let content = sprintf "* TODO Both dates\nSCHEDULED: <%s %s> DEADLINE: <%s %s>\n"
                    (today.ToString("yyyy-MM-dd")) (dayName today)
                    (tomorrow.ToString("yyyy-MM-dd")) (dayName tomorrow)
    let items = datedItemsFromContent content
    Assert.Equal(2, items.Length)
    let types = items |> List.map (fun i -> i.Type) |> List.sort
    Assert.Contains(Agenda.Scheduled, types)
    Assert.Contains(Agenda.Deadline, types)

// --- Timestamp range expansion ---

[<Fact>]
let ``SCHEDULED range produces one item per day`` () =
    let feb5 = DateTime(2026, 2, 5)
    let feb7 = DateTime(2026, 2, 7)
    let content = sprintf "* TODO Multi-day task\nSCHEDULED: <%s %s>--<%s %s>\n"
                    (feb5.ToString("yyyy-MM-dd")) (dayName feb5)
                    (feb7.ToString("yyyy-MM-dd")) (dayName feb7)
    let items = datedItemsFromContent content
    Assert.Equal(3, items.Length)
    let dates = items |> List.map (fun i -> i.Date) |> List.sort
    Assert.Equal(feb5, dates.[0])
    Assert.Equal(DateTime(2026, 2, 6), dates.[1])
    Assert.Equal(feb7, dates.[2])

[<Fact>]
let ``Range filtered by date range returns correct subset`` () =
    let feb5 = DateTime(2026, 2, 5)
    let feb10 = DateTime(2026, 2, 10)
    let content = sprintf "* TODO Long task\nSCHEDULED: <%s %s>--<%s %s>\n"
                    (feb5.ToString("yyyy-MM-dd")) (dayName feb5)
                    (feb10.ToString("yyyy-MM-dd")) (dayName feb10)
    let items = datedItemsFromContent content
    let filtered = Agenda.filterByDateRange (DateTime(2026, 2, 7)) (DateTime(2026, 2, 9)) items
    Assert.Equal(2, filtered.Length)

[<Fact>]
let ``Same-day range produces one item`` () =
    let content = sprintf "* TODO Same day\nSCHEDULED: <%s %s>--<%s %s>\n"
                    (today.ToString("yyyy-MM-dd")) (dayName today)
                    (today.ToString("yyyy-MM-dd")) (dayName today)
    let items = datedItemsFromContent content
    Assert.Equal(1, items.Length)

[<Fact>]
let ``Single timestamp still produces one item`` () =
    let content = sprintf "* TODO Normal task\nSCHEDULED: <%s %s>\n"
                    (today.ToString("yyyy-MM-dd")) (dayName today)
    let items = datedItemsFromContent content
    Assert.Equal(1, items.Length)

// --- Config-aware agenda tests ---

module ConfigAwareAgenda =
    open OrgCli.Org

    let private today = DateTime(2026, 2, 5)
    let private tomorrow = today.AddDays(1.0)
    let private yesterday = today.AddDays(-1.0)
    let private dayName (d: DateTime) = d.ToString("ddd").Substring(0, 3)
    let private defaultConfig = Types.defaultConfig

    let private customDoneConfig doneKeywords =
        { defaultConfig with
            TodoKeywords = {
                ActiveStates = [{ Keyword = "TODO"; LogOnEnter = LogAction.NoLog; LogOnLeave = LogAction.NoLog }]
                DoneStates = doneKeywords |> List.map (fun kw -> { Keyword = kw; LogOnEnter = LogAction.NoLog; LogOnLeave = LogAction.NoLog })
            }
        }

    [<Fact>]
    let ``filterOverdue uses config done states - DONE not in config means overdue`` () =
        // Config with only CANCELLED as done state, not DONE
        let cfg = customDoneConfig ["CANCELLED"]
        let content = sprintf "* DONE Overdue task\nDEADLINE: <%s %s>\n" (yesterday.ToString("yyyy-MM-dd")) (dayName yesterday)
        let doc = Document.parse content
        let items = Agenda.collectDatedItemsFromDocs cfg [ ("test.org", doc) ]
        // With this config, "DONE" is NOT a done state, so item is overdue
        let overdue = Agenda.filterOverdue cfg today items
        Assert.Equal(1, overdue.Length)

    [<Fact>]
    let ``filterOverdue excludes CANCELLED when it is a config done state`` () =
        let cfg = customDoneConfig ["CANCELLED"]
        let content = sprintf "* CANCELLED Completed task\nDEADLINE: <%s %s>\n" (yesterday.ToString("yyyy-MM-dd")) (dayName yesterday)
        let doc = Document.parse content
        let items = Agenda.collectDatedItemsFromDocs cfg [ ("test.org", doc) ]
        let overdue = Agenda.filterOverdue cfg today items
        Assert.Empty(overdue)

    [<Fact>]
    let ``skipScheduledIfDone filters done scheduled items`` () =
        let content = sprintf "* DONE Finished task\nSCHEDULED: <%s %s>\n\n* TODO Active task\nSCHEDULED: <%s %s>\n"
                        (today.ToString("yyyy-MM-dd")) (dayName today)
                        (today.ToString("yyyy-MM-dd")) (dayName today)
        let doc = Document.parse content
        let items = Agenda.collectDatedItemsFromDocs defaultConfig [ ("test.org", doc) ]
        let filtered = Agenda.skipDoneItems defaultConfig items
        Assert.Equal(1, filtered.Length)
        Assert.Equal("Active task", filtered.[0].Headline.Title)

    [<Fact>]
    let ``skipDoneItems keeps items when headline has no todo keyword`` () =
        let content = sprintf "* A plain headline\nSCHEDULED: <%s %s>\n" (today.ToString("yyyy-MM-dd")) (dayName today)
        let doc = Document.parse content
        let items = Agenda.collectDatedItemsFromDocs defaultConfig [ ("test.org", doc) ]
        let filtered = Agenda.skipDoneItems defaultConfig items
        Assert.Equal(1, filtered.Length)

    [<Fact>]
    let ``DeadlineWarningDays 7 shows deadline within 7 days`` () =
        let cfg7 = { defaultConfig with DeadlineWarningDays = 7 }
        let day6 = today.AddDays(6.0)
        let content = sprintf "* TODO Soon\nDEADLINE: <%s %s>\n" (day6.ToString("yyyy-MM-dd")) (dayName day6)
        let doc = Document.parse content
        let items = Agenda.collectDatedItemsFromDocs cfg7 [ ("test.org", doc) ]
        let warned = Agenda.filterDeadlineWarnings cfg7 today items
        Assert.Equal(1, warned.Length)

    [<Fact>]
    let ``DeadlineWarningDays 7 hides deadline beyond 7 days`` () =
        let cfg7 = { defaultConfig with DeadlineWarningDays = 7 }
        let day10 = today.AddDays(10.0)
        let content = sprintf "* TODO Far away\nDEADLINE: <%s %s>\n" (day10.ToString("yyyy-MM-dd")) (dayName day10)
        let doc = Document.parse content
        let items = Agenda.collectDatedItemsFromDocs cfg7 [ ("test.org", doc) ]
        let warned = Agenda.filterDeadlineWarnings cfg7 today items
        Assert.Empty(warned)

    [<Fact>]
    let ``DeadlineWarningDays 14 shows deadline at 10 days`` () =
        let cfg14 = { defaultConfig with DeadlineWarningDays = 14 }
        let day10 = today.AddDays(10.0)
        let content = sprintf "* TODO Medium range\nDEADLINE: <%s %s>\n" (day10.ToString("yyyy-MM-dd")) (dayName day10)
        let doc = Document.parse content
        let items = Agenda.collectDatedItemsFromDocs cfg14 [ ("test.org", doc) ]
        let warned = Agenda.filterDeadlineWarnings cfg14 today items
        Assert.Equal(1, warned.Length)

    [<Fact>]
    let ``filterDeadlineWarnings includes overdue deadlines`` () =
        let content = sprintf "* TODO Past due\nDEADLINE: <%s %s>\n" (yesterday.ToString("yyyy-MM-dd")) (dayName yesterday)
        let doc = Document.parse content
        let items = Agenda.collectDatedItemsFromDocs defaultConfig [ ("test.org", doc) ]
        let warned = Agenda.filterDeadlineWarnings defaultConfig today items
        Assert.Equal(1, warned.Length)

    [<Fact>]
    let ``filterDeadlineWarnings ignores scheduled items`` () =
        let day3 = today.AddDays(3.0)
        let content = sprintf "* TODO Scheduled only\nSCHEDULED: <%s %s>\n" (day3.ToString("yyyy-MM-dd")) (dayName day3)
        let doc = Document.parse content
        let items = Agenda.collectDatedItemsFromDocs defaultConfig [ ("test.org", doc) ]
        let warned = Agenda.filterDeadlineWarnings defaultConfig today items
        Assert.Empty(warned)

// --- Parameterized time-boundary tests ---

module TimeBoundaryTests =
    open OrgCli.Org
    open System.Globalization

    let private dn (d: DateTime) = d.ToString("ddd", CultureInfo.InvariantCulture)

    let private makeScheduled (d: DateTime) =
        sprintf "* TODO Task\nSCHEDULED: <%s %s>\n" (d.ToString("yyyy-MM-dd")) (dn d)

    let private makeDeadline (d: DateTime) =
        sprintf "* TODO Task\nDEADLINE: <%s %s>\n" (d.ToString("yyyy-MM-dd")) (dn d)

    let private datedItems cfg content =
        let doc = Document.parse content
        Agenda.collectDatedItemsFromDocs cfg [ ("test.org", doc) ]

    // Month boundary: deadline on Jan 31, viewing from Feb 1
    [<Fact>]
    let ``overdue across month boundary: Jan 31 deadline viewed from Feb 1`` () =
        let jan31 = DateTime(2026, 1, 31)
        let feb1 = DateTime(2026, 2, 1)
        let content = makeDeadline jan31
        let items = datedItems Types.defaultConfig content
        let overdue = Agenda.filterOverdue Types.defaultConfig feb1 items
        Assert.Equal(1, overdue.Length)

    // Year boundary: deadline on Dec 31, viewing from Jan 1
    [<Fact>]
    let ``overdue across year boundary: Dec 31 deadline viewed from Jan 1`` () =
        let dec31 = DateTime(2025, 12, 31)
        let jan1 = DateTime(2026, 1, 1)
        let content = makeDeadline dec31
        let items = datedItems Types.defaultConfig content
        let overdue = Agenda.filterOverdue Types.defaultConfig jan1 items
        Assert.Equal(1, overdue.Length)

    // Leap year: Feb 29 exists in 2028
    [<Fact>]
    let ``scheduled on leap day Feb 29 appears correctly`` () =
        let feb29 = DateTime(2028, 2, 29)
        let content = makeScheduled feb29
        let items = datedItems Types.defaultConfig content
        let filtered = Agenda.filterByDateRange feb29 (feb29.AddDays(1.0)) items
        Assert.Equal(1, filtered.Length)
        Assert.Equal(feb29, filtered.[0].Date)

    // Deadline warning across month boundary
    [<Fact>]
    let ``deadline warning spans month boundary: warning on Jan 28 for Feb 3 deadline`` () =
        let jan28 = DateTime(2026, 1, 28)
        let feb3 = DateTime(2026, 2, 3)
        let cfg = { Types.defaultConfig with DeadlineWarningDays = 7 }
        let content = makeDeadline feb3
        let items = datedItems cfg content
        let warned = Agenda.filterDeadlineWarnings cfg jan28 items
        Assert.Equal(1, warned.Length)

    // Deadline warning does NOT fire when just beyond window
    [<Fact>]
    let ``deadline warning does not fire at day boundary + 1`` () =
        let today = DateTime(2026, 2, 5)
        let deadline = today.AddDays(8.0)
        let cfg = { Types.defaultConfig with DeadlineWarningDays = 7 }
        let content = makeDeadline deadline
        let items = datedItems cfg content
        let warned = Agenda.filterDeadlineWarnings cfg today items
        Assert.Empty(warned)

    // Repeater shift across month boundary: Jan 31 +1m -> Feb 28 (not Feb 31)
    [<Fact>]
    let ``repeater +1m from Jan 31 shifts to Feb 28`` () =
        let jan31 = DateTime(2026, 1, 31)
        let ts : Timestamp = {
            Type = TimestampType.Active
            Date = jan31
            HasTime = false
            Repeater = Some "+1m"
            Delay = None
            RangeEnd = None
        }
        let now = DateTime(2026, 2, 1)
        let shifted = Mutations.shiftTimestamp ts now
        Assert.Equal(DateTime(2026, 2, 28), shifted.Date)

    // Repeater shift across year boundary: Dec 15 +1m -> Jan 15
    [<Fact>]
    let ``repeater +1m from Dec 15 shifts to Jan 15 next year`` () =
        let dec15 = DateTime(2025, 12, 15)
        let ts : Timestamp = {
            Type = TimestampType.Active
            Date = dec15
            HasTime = false
            Repeater = Some "+1m"
            Delay = None
            RangeEnd = None
        }
        let now = DateTime(2026, 1, 1)
        let shifted = Mutations.shiftTimestamp ts now
        Assert.Equal(DateTime(2026, 1, 15), shifted.Date)

    // Repeater .+ (from today) on leap year boundary
    [<Fact>]
    let ``repeater from-today +1d on Feb 28 leap year shifts to Feb 29`` () =
        let feb28 = DateTime(2028, 2, 28)
        let ts : Timestamp = {
            Type = TimestampType.Active
            Date = feb28
            HasTime = false
            Repeater = Some ".+1d"
            Delay = None
            RangeEnd = None
        }
        let now = DateTime(2028, 2, 28)
        let shifted = Mutations.shiftTimestamp ts now
        // .+ adds from today (Feb 28) + 1 day = Feb 29 (leap year)
        Assert.Equal(DateTime(2028, 2, 29), shifted.Date)

    // ++ (next future) repeater skips past today
    [<Fact>]
    let ``repeater next-future +1w skips past today`` () =
        let jan1 = DateTime(2026, 1, 1)
        let ts : Timestamp = {
            Type = TimestampType.Active
            Date = jan1
            HasTime = false
            Repeater = Some "++1w"
            Delay = None
            RangeEnd = None
        }
        let now = DateTime(2026, 1, 20)
        let shifted = Mutations.shiftTimestamp ts now
        // Should be first Thursday (Jan 1 is Thu) after Jan 20 = Jan 22
        Assert.True(shifted.Date > now)
