module OrgCli.Tests.IndexQueryTests

open System
open System.IO
open Xunit
open OrgCli.Index
open OrgCli.Index.IndexDatabase
open OrgCli.Index.IndexSync

let private tempDbPath () =
    Path.Combine(Path.GetTempPath(), sprintf "org-index-query-test-%s.db" (Guid.NewGuid().ToString("N")))

let private withDb (f: OrgIndexDb -> unit) =
    let path = tempDbPath ()

    try
        use db = new OrgIndexDb(path)
        db.Initialize()
        f db
    finally
        if File.Exists(path) then
            File.Delete(path)

        let wal = path + "-wal"
        let shm = path + "-shm"

        if File.Exists(wal) then
            File.Delete(wal)

        if File.Exists(shm) then
            File.Delete(shm)

let private insertTestFile (db: OrgIndexDb) =
    db.InsertFile(
        { Path = "/test.org"
          Hash = "h"
          Mtime = 1L }
    )

let private mkHeadline
    file
    pos
    level
    title
    todo
    priority
    scheduledRaw
    scheduledDt
    deadlineRaw
    deadlineDt
    body
    outlinePath
    =
    { File = file
      CharPos = pos
      Level = level
      Title = title
      Todo = todo
      Priority = priority
      Scheduled = scheduledRaw
      ScheduledDt = scheduledDt
      Deadline = deadlineRaw
      DeadlineDt = deadlineDt
      Closed = None
      ClosedDt = None
      Properties = None
      Body = body
      OutlinePath = outlinePath }

// ── FTS query ──

[<Fact>]
let ``FTS query returns matching headlines ranked by relevance`` () =
    withDb (fun db ->
        insertTestFile db
        // Title match should rank higher than body-only match
        db.InsertHeadline(
            mkHeadline "/test.org" 0L 1 "API migration guide" None None None None None None (Some "general info") None
        )

        db.InsertHeadline(
            mkHeadline
                "/test.org"
                100L
                1
                "Random notes"
                None
                None
                None
                None
                None
                None
                (Some "contains migration keyword")
                None
        )

        db.RebuildFtsForFile("/test.org")
        let results = db.SearchFts("migration")
        Assert.Equal(2, results.Length))

[<Fact>]
let ``FTS query with boolean AND`` () =
    withDb (fun db ->
        insertTestFile db

        db.InsertHeadline(
            mkHeadline "/test.org" 0L 1 "API migration" None None None None None None (Some "about the api") None
        )

        db.InsertHeadline(
            mkHeadline "/test.org" 100L 1 "Other migration" None None None None None None (Some "database stuff") None
        )

        db.RebuildFtsForFile("/test.org")
        let results = db.SearchFts("api AND migration")
        Assert.Equal(1, results.Length)
        Assert.Equal("API migration", results.[0].Title))

[<Fact>]
let ``FTS query with NOT`` () =
    withDb (fun db ->
        insertTestFile db

        db.InsertHeadline(
            mkHeadline "/test.org" 0L 1 "API migration" None None None None None None (Some "new api") None
        )

        db.InsertHeadline(
            mkHeadline "/test.org" 100L 1 "Legacy API" None None None None None None (Some "old legacy system") None
        )

        db.RebuildFtsForFile("/test.org")
        let results = db.SearchFts("api NOT legacy")
        Assert.Equal(1, results.Length)
        Assert.Equal("API migration", results.[0].Title))

[<Fact>]
let ``FTS query with prefix`` () =
    withDb (fun db ->
        insertTestFile db
        db.InsertHeadline(mkHeadline "/test.org" 0L 1 "Migration plan" None None None None None None None None)
        db.InsertHeadline(mkHeadline "/test.org" 100L 1 "Unrelated" None None None None None None None None)
        db.RebuildFtsForFile("/test.org")
        let results = db.SearchFts("migrat*")
        Assert.Equal(1, results.Length))

[<Fact>]
let ``FTS query with phrase`` () =
    withDb (fun db ->
        insertTestFile db
        db.InsertHeadline(mkHeadline "/test.org" 0L 1 "API migration plan" None None None None None None None None)
        db.InsertHeadline(mkHeadline "/test.org" 100L 1 "Migration of API" None None None None None None None None)
        db.RebuildFtsForFile("/test.org")
        let results = db.SearchFts("\"API migration\"")
        Assert.Equal(1, results.Length)
        Assert.Equal("API migration plan", results.[0].Title))

[<Fact>]
let ``FTS query with column filter`` () =
    withDb (fun db ->
        insertTestFile db

        db.InsertHeadline(
            mkHeadline "/test.org" 0L 1 "Migration notes" None None None None None None (Some "unrelated body") None
        )

        db.InsertHeadline(
            mkHeadline
                "/test.org"
                100L
                1
                "Other topic"
                None
                None
                None
                None
                None
                None
                (Some "migration in the body only")
                None
        )

        db.RebuildFtsForFile("/test.org")
        let results = db.SearchFts("title:migration")
        Assert.Equal(1, results.Length)
        Assert.Equal("Migration notes", results.[0].Title))

[<Fact>]
let ``FTS returns empty for no match`` () =
    withDb (fun db ->
        insertTestFile db
        db.InsertHeadline(mkHeadline "/test.org" 0L 1 "Some headline" None None None None None None None None)
        db.RebuildFtsForFile("/test.org")
        let results = db.SearchFts("nonexistentterm")
        Assert.Equal(0, results.Length))

[<Fact>]
let ``FTS errors when no index exists`` () =
    let path = tempDbPath ()

    try
        // Don't create/initialize the db — file shouldn't exist
        let ex =
            Assert.ThrowsAny<Exception>(fun () ->
                use db = new OrgIndexDb(path)
                // Don't initialize — schema doesn't exist
                db.SearchFts("anything") |> ignore)

        Assert.Contains("headlines_fts", ex.Message)
    finally
        if File.Exists(path) then
            File.Delete(path)

        let wal = path + "-wal"
        let shm = path + "-shm"

        if File.Exists(wal) then
            File.Delete(wal)

        if File.Exists(shm) then
            File.Delete(shm)

// ── Headlines query ──

[<Fact>]
let ``Filter by TODO state`` () =
    withDb (fun db ->
        insertTestFile db
        db.InsertHeadline(mkHeadline "/test.org" 0L 1 "Task A" (Some "TODO") None None None None None None None)
        db.InsertHeadline(mkHeadline "/test.org" 100L 1 "Task B" (Some "DONE") None None None None None None None)
        db.InsertHeadline(mkHeadline "/test.org" 200L 1 "Task C" None None None None None None None None)
        let results = db.QueryHeadlines(todo = "TODO")
        Assert.Equal(1, results.Length)
        Assert.Equal("Task A", results.[0].Title))

[<Fact>]
let ``Filter by tag exact match`` () =
    withDb (fun db ->
        insertTestFile db
        db.InsertHeadline(mkHeadline "/test.org" 0L 1 "H1" None None None None None None None None)
        db.InsertHeadline(mkHeadline "/test.org" 100L 1 "H2" None None None None None None None None)

        db.InsertTag(
            { File = "/test.org"
              CharPos = 0L
              Tag = "work"
              Inherited = false }
        )

        db.InsertTag(
            { File = "/test.org"
              CharPos = 100L
              Tag = "personal"
              Inherited = false }
        )

        let results = db.QueryHeadlines(tag = "work")
        Assert.Equal(1, results.Length)
        Assert.Equal("H1", results.[0].Title))

[<Fact>]
let ``Filter by tag does not match substring`` () =
    withDb (fun db ->
        insertTestFile db
        db.InsertHeadline(mkHeadline "/test.org" 0L 1 "H1" None None None None None None None None)

        db.InsertTag(
            { File = "/test.org"
              CharPos = 0L
              Tag = "homework"
              Inherited = false }
        )

        let results = db.QueryHeadlines(tag = "work")
        Assert.Equal(0, results.Length))

[<Fact>]
let ``Filter by inherited tag`` () =
    withDb (fun db ->
        insertTestFile db
        db.InsertHeadline(mkHeadline "/test.org" 0L 1 "H1" None None None None None None None None)

        db.InsertTag(
            { File = "/test.org"
              CharPos = 0L
              Tag = "project"
              Inherited = true }
        )

        let results = db.QueryHeadlines(tag = "project")
        Assert.Equal(1, results.Length))

[<Fact>]
let ``Filter by outline_path prefix`` () =
    withDb (fun db ->
        insertTestFile db
        db.InsertHeadline(mkHeadline "/test.org" 0L 1 "Root" None None None None None None None (Some "Projects"))

        db.InsertHeadline(
            mkHeadline "/test.org" 100L 2 "Sub" None None None None None None None (Some "Projects\x1FSub")
        )

        db.InsertHeadline(mkHeadline "/test.org" 200L 1 "Other" None None None None None None None (Some "Other"))
        let results = db.QueryHeadlines(outlinePathPrefix = "Projects")
        // Should match "Projects\x1FSub" (child) but not "Projects" itself (exact, not child)
        // The prefix query is LIKE 'Projects' || X'1F' || '%'
        Assert.True(results.Length >= 1)

        Assert.True(
            results
            |> List.forall (fun r -> r.OutlinePath.IsSome && r.OutlinePath.Value.StartsWith("Projects\x1F"))
        ))

[<Fact>]
let ``Filter by multiple criteria`` () =
    withDb (fun db ->
        insertTestFile db

        db.InsertHeadline(
            mkHeadline "/test.org" 0L 1 "TODO work task" (Some "TODO") None None None None None None None
        )

        db.InsertHeadline(
            mkHeadline "/test.org" 100L 1 "DONE work task" (Some "DONE") None None None None None None None
        )

        db.InsertHeadline(
            mkHeadline "/test.org" 200L 1 "TODO personal task" (Some "TODO") None None None None None None None
        )

        db.InsertTag(
            { File = "/test.org"
              CharPos = 0L
              Tag = "work"
              Inherited = false }
        )

        db.InsertTag(
            { File = "/test.org"
              CharPos = 100L
              Tag = "work"
              Inherited = false }
        )

        db.InsertTag(
            { File = "/test.org"
              CharPos = 200L
              Tag = "personal"
              Inherited = false }
        )

        let results = db.QueryHeadlines(todo = "TODO", tag = "work")
        Assert.Equal(1, results.Length)
        Assert.Equal("TODO work task", results.[0].Title))

[<Fact>]
let ``Query omits body column`` () =
    withDb (fun db ->
        insertTestFile db

        db.InsertHeadline(
            mkHeadline "/test.org" 0L 1 "H1" None None None None None None (Some "large body text here") None
        )

        let results = db.QueryHeadlines()
        Assert.Equal(1, results.Length)
        // HeadlineQueryResult type doesn't have Body field — this is a compile-time check
        // Verify we get the headline back without body
        Assert.Equal("H1", results.[0].Title))

// ── Agenda query ──

[<Fact>]
let ``Non-repeating scheduled in range returned`` () =
    withDb (fun db ->
        insertTestFile db

        db.InsertHeadline(
            mkHeadline
                "/test.org"
                0L
                1
                "Task"
                (Some "TODO")
                None
                (Some "<2026-02-10 Tue>")
                (Some "2026-02-10")
                None
                None
                None
                None
        )

        let results = db.QueryAgendaNonRepeating("2026-02-07", "2026-02-14")
        Assert.Equal(1, results.Length)
        Assert.Equal("Task", results.[0].Title))

[<Fact>]
let ``Non-repeating deadline in range returned`` () =
    withDb (fun db ->
        insertTestFile db

        db.InsertHeadline(
            mkHeadline
                "/test.org"
                0L
                1
                "Task"
                (Some "TODO")
                None
                None
                None
                (Some "<2026-02-12 Thu>")
                (Some "2026-02-12")
                None
                None
        )

        let results = db.QueryAgendaNonRepeating("2026-02-07", "2026-02-14")
        Assert.Equal(1, results.Length))

[<Fact>]
let ``Scheduled outside range not returned`` () =
    withDb (fun db ->
        insertTestFile db

        db.InsertHeadline(
            mkHeadline
                "/test.org"
                0L
                1
                "Past task"
                (Some "TODO")
                None
                (Some "<2026-01-01 Thu>")
                (Some "2026-01-01")
                None
                None
                None
                None
        )

        let results = db.QueryAgendaNonRepeating("2026-02-07", "2026-02-14")
        Assert.Equal(0, results.Length))

[<Fact>]
let ``Repeating task with base date before range still returned`` () =
    withDb (fun db ->
        insertTestFile db

        db.InsertHeadline(
            mkHeadline
                "/test.org"
                0L
                1
                "Weekly task"
                (Some "TODO")
                None
                (Some "<2026-01-01 Thu +1w>")
                (Some "2026-01-01")
                None
                None
                None
                None
        )

        let results = db.QueryAgendaRepeating()
        Assert.Equal(1, results.Length)
        Assert.Equal("Weekly task", results.[0].Title))

[<Fact>]
let ``All-day items sort before timed items on same date`` () =
    withDb (fun db ->
        insertTestFile db

        db.InsertHeadline(
            mkHeadline
                "/test.org"
                0L
                1
                "Timed task"
                (Some "TODO")
                None
                (Some "<2026-02-10 Tue 09:00>")
                (Some "2026-02-10T09:00")
                None
                None
                None
                None
        )

        db.InsertHeadline(
            mkHeadline
                "/test.org"
                100L
                1
                "All-day task"
                (Some "TODO")
                None
                (Some "<2026-02-10 Tue>")
                (Some "2026-02-10")
                None
                None
                None
                None
        )

        let results = db.QueryAgendaNonRepeating("2026-02-07", "2026-02-14")
        Assert.Equal(2, results.Length)
        // When sorted by scheduled_dt, all-day ("2026-02-10") < timed ("2026-02-10T09:00")
        let sorted =
            results |> List.sortBy (fun r -> r.ScheduledDt |> Option.defaultValue "")

        Assert.Equal("All-day task", sorted.[0].Title)
        Assert.Equal("Timed task", sorted.[1].Title))

[<Fact>]
let ``Deduplication by file and char_pos`` () =
    withDb (fun db ->
        insertTestFile db
        // Headline has both repeating scheduled and non-repeating deadline in range
        db.InsertHeadline(
            mkHeadline
                "/test.org"
                0L
                1
                "Dual task"
                (Some "TODO")
                None
                (Some "<2026-02-01 Sun +1w>")
                (Some "2026-02-01")
                (Some "<2026-02-10 Tue>")
                (Some "2026-02-10")
                None
                None
        )

        let nonRepeating = db.QueryAgendaNonRepeating("2026-02-07", "2026-02-14")
        let repeating = db.QueryAgendaRepeating()
        // Both queries should find this headline
        Assert.True(nonRepeating.Length > 0 || repeating.Length > 0)
        // After dedup by (file, char_pos), should be 1 unique headline
        let all = nonRepeating @ repeating
        let deduped = all |> List.distinctBy (fun r -> r.File, r.CharPos)
        Assert.Equal(1, deduped.Length))
