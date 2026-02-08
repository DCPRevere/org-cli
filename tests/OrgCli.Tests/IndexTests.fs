module OrgCli.Tests.IndexTests

open System
open System.IO
open Xunit
open OrgCli.Index
open OrgCli.Index.IndexDatabase

let private tempDbPath () =
    Path.Combine(Path.GetTempPath(), sprintf "org-index-test-%s.db" (Guid.NewGuid().ToString("N")))

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

// ── Schema tests ──

[<Fact>]
let ``Database enables WAL mode`` () =
    withDb (fun db ->
        let mode = db.ExecuteScalarString("PRAGMA journal_mode")
        Assert.Equal("wal", mode))

[<Fact>]
let ``Database enables foreign keys`` () =
    withDb (fun db ->
        let fk = db.ExecuteScalarInt64("PRAGMA foreign_keys")
        Assert.Equal(1L, fk))

// ── Files table ──

[<Fact>]
let ``InsertFile stores path hash and mtime`` () =
    withDb (fun db ->
        let file =
            { Path = "/home/user/notes.org"
              Hash = "sha256abc"
              Mtime = 1700000000L }

        db.InsertFile(file)
        let result = db.GetFile("/home/user/notes.org")
        Assert.True(result.IsSome)
        let r = result.Value
        Assert.Equal("/home/user/notes.org", r.Path)
        Assert.Equal("sha256abc", r.Hash)
        Assert.Equal(1700000000L, r.Mtime))

[<Fact>]
let ``GetFileHash returns None for unknown file`` () =
    withDb (fun db ->
        let result = db.GetFileHash("/nonexistent.org")
        Assert.True(result.IsNone))

[<Fact>]
let ``GetFileHash returns hash for known file`` () =
    withDb (fun db ->
        db.InsertFile(
            { Path = "/test.org"
              Hash = "deadbeef"
              Mtime = 100L }
        )

        let result = db.GetFileHash("/test.org")
        Assert.Equal(Some "deadbeef", result))

// ── Headlines table ──

[<Fact>]
let ``InsertHeadline stores all fields`` () =
    withDb (fun db ->
        db.InsertFile(
            { Path = "/test.org"
              Hash = "h"
              Mtime = 1L }
        )

        let h =
            { File = "/test.org"
              CharPos = 42L
              Level = 2
              Title = "My headline"
              Todo = Some "TODO"
              Priority = Some "A"
              Scheduled = Some "<2026-02-07 Sat 10:00>"
              ScheduledDt = Some "2026-02-07T10:00"
              Deadline = Some "<2026-02-14 Sat>"
              DeadlineDt = Some "2026-02-14"
              Closed = None
              ClosedDt = None
              Properties = Some """{"ID":"abc-123"}"""
              Body = Some "Some body text here"
              OutlinePath = Some "Projects\x1FBackend" }

        db.InsertHeadline(h)
        let result = db.GetHeadline("/test.org", 42L)
        Assert.True(result.IsSome)
        let r = result.Value
        Assert.Equal("/test.org", r.File)
        Assert.Equal(42L, r.CharPos)
        Assert.Equal(2, r.Level)
        Assert.Equal("My headline", r.Title)
        Assert.Equal(Some "TODO", r.Todo)
        Assert.Equal(Some "A", r.Priority)
        Assert.Equal(Some "<2026-02-07 Sat 10:00>", r.Scheduled)
        Assert.Equal(Some "2026-02-07T10:00", r.ScheduledDt)
        Assert.Equal(Some "<2026-02-14 Sat>", r.Deadline)
        Assert.Equal(Some "2026-02-14", r.DeadlineDt)
        Assert.True(r.Closed.IsNone)
        Assert.True(r.ClosedDt.IsNone)
        Assert.Equal(Some """{"ID":"abc-123"}""", r.Properties)
        Assert.Equal(Some "Some body text here", r.Body)
        Assert.Equal(Some "Projects\x1FBackend", r.OutlinePath))

[<Fact>]
let ``Headlines cascade delete when file deleted`` () =
    withDb (fun db ->
        db.InsertFile(
            { Path = "/test.org"
              Hash = "h"
              Mtime = 1L }
        )

        db.InsertHeadline(
            { File = "/test.org"
              CharPos = 0L
              Level = 1
              Title = "H1"
              Todo = None
              Priority = None
              Scheduled = None
              ScheduledDt = None
              Deadline = None
              DeadlineDt = None
              Closed = None
              ClosedDt = None
              Properties = None
              Body = None
              OutlinePath = None }
        )

        let before = db.GetHeadlines("/test.org")
        Assert.Equal(1, before.Length)
        db.DeleteFile("/test.org")
        let after = db.GetHeadlines("/test.org")
        Assert.Equal(0, after.Length))

[<Fact>]
let ``Primary key is file plus char_pos`` () =
    withDb (fun db ->
        db.InsertFile(
            { Path = "/test.org"
              Hash = "h"
              Mtime = 1L }
        )

        let mkH pos title =
            { File = "/test.org"
              CharPos = pos
              Level = 1
              Title = title
              Todo = None
              Priority = None
              Scheduled = None
              ScheduledDt = None
              Deadline = None
              DeadlineDt = None
              Closed = None
              ClosedDt = None
              Properties = None
              Body = None
              OutlinePath = None }

        db.InsertHeadline(mkH 0L "First")
        db.InsertHeadline(mkH 100L "Second")
        let all = db.GetHeadlines("/test.org")
        Assert.Equal(2, all.Length))

// ── headline_tags table ──

[<Fact>]
let ``InsertTag stores direct tag`` () =
    withDb (fun db ->
        db.InsertFile(
            { Path = "/test.org"
              Hash = "h"
              Mtime = 1L }
        )

        db.InsertHeadline(
            { File = "/test.org"
              CharPos = 0L
              Level = 1
              Title = "H1"
              Todo = None
              Priority = None
              Scheduled = None
              ScheduledDt = None
              Deadline = None
              DeadlineDt = None
              Closed = None
              ClosedDt = None
              Properties = None
              Body = None
              OutlinePath = None }
        )

        db.InsertTag(
            { File = "/test.org"
              CharPos = 0L
              Tag = "work"
              Inherited = false }
        )

        let tags = db.GetTags("/test.org", 0L)
        Assert.Equal(1, tags.Length)
        Assert.Equal("work", tags.[0].Tag)
        Assert.False(tags.[0].Inherited))

[<Fact>]
let ``InsertTag stores inherited tag`` () =
    withDb (fun db ->
        db.InsertFile(
            { Path = "/test.org"
              Hash = "h"
              Mtime = 1L }
        )

        db.InsertHeadline(
            { File = "/test.org"
              CharPos = 0L
              Level = 1
              Title = "H1"
              Todo = None
              Priority = None
              Scheduled = None
              ScheduledDt = None
              Deadline = None
              DeadlineDt = None
              Closed = None
              ClosedDt = None
              Properties = None
              Body = None
              OutlinePath = None }
        )

        db.InsertTag(
            { File = "/test.org"
              CharPos = 0L
              Tag = "project"
              Inherited = true }
        )

        let tags = db.GetTags("/test.org", 0L)
        Assert.Equal(1, tags.Length)
        Assert.True(tags.[0].Inherited))

[<Fact>]
let ``Tags cascade delete when headline deleted`` () =
    withDb (fun db ->
        db.InsertFile(
            { Path = "/test.org"
              Hash = "h"
              Mtime = 1L }
        )

        db.InsertHeadline(
            { File = "/test.org"
              CharPos = 0L
              Level = 1
              Title = "H1"
              Todo = None
              Priority = None
              Scheduled = None
              ScheduledDt = None
              Deadline = None
              DeadlineDt = None
              Closed = None
              ClosedDt = None
              Properties = None
              Body = None
              OutlinePath = None }
        )

        db.InsertTag(
            { File = "/test.org"
              CharPos = 0L
              Tag = "work"
              Inherited = false }
        )
        // Delete the file (cascades to headlines, then to tags)
        db.DeleteFile("/test.org")
        let tags = db.GetTags("/test.org", 0L)
        Assert.Equal(0, tags.Length))

[<Fact>]
let ``Direct tag wins over inherited via INSERT OR IGNORE`` () =
    withDb (fun db ->
        db.InsertFile(
            { Path = "/test.org"
              Hash = "h"
              Mtime = 1L }
        )

        db.InsertHeadline(
            { File = "/test.org"
              CharPos = 0L
              Level = 1
              Title = "H1"
              Todo = None
              Priority = None
              Scheduled = None
              ScheduledDt = None
              Deadline = None
              DeadlineDt = None
              Closed = None
              ClosedDt = None
              Properties = None
              Body = None
              OutlinePath = None }
        )
        // Insert direct first
        db.InsertTag(
            { File = "/test.org"
              CharPos = 0L
              Tag = "work"
              Inherited = false }
        )
        // Try to insert inherited — should be ignored (same PK)
        db.InsertTagIgnore(
            { File = "/test.org"
              CharPos = 0L
              Tag = "work"
              Inherited = true }
        )

        let tags = db.GetTags("/test.org", 0L)
        Assert.Equal(1, tags.Length)
        Assert.False(tags.[0].Inherited, "Direct tag should win over inherited"))

[<Fact>]
let ``Tag query by exact match`` () =
    withDb (fun db ->
        db.InsertFile(
            { Path = "/test.org"
              Hash = "h"
              Mtime = 1L }
        )

        let mkH pos title =
            { File = "/test.org"
              CharPos = pos
              Level = 1
              Title = title
              Todo = None
              Priority = None
              Scheduled = None
              ScheduledDt = None
              Deadline = None
              DeadlineDt = None
              Closed = None
              ClosedDt = None
              Properties = None
              Body = None
              OutlinePath = None }

        db.InsertHeadline(mkH 0L "H1")
        db.InsertHeadline(mkH 100L "H2")
        db.InsertHeadline(mkH 200L "H3")

        db.InsertTag(
            { File = "/test.org"
              CharPos = 0L
              Tag = "work"
              Inherited = false }
        )

        db.InsertTag(
            { File = "/test.org"
              CharPos = 100L
              Tag = "homework"
              Inherited = false }
        )

        db.InsertTag(
            { File = "/test.org"
              CharPos = 200L
              Tag = "work"
              Inherited = false }
        )

        let results = db.GetTagsByName("work")
        Assert.Equal(2, results.Length)
        Assert.True(results |> List.forall (fun t -> t.Tag = "work")))

// ── FTS5 ──

[<Fact>]
let ``FTS insert and MATCH query returns results`` () =
    withDb (fun db ->
        db.InsertFile(
            { Path = "/test.org"
              Hash = "h"
              Mtime = 1L }
        )

        db.InsertHeadline(
            { File = "/test.org"
              CharPos = 0L
              Level = 1
              Title = "API migration plan"
              Todo = None
              Priority = None
              Scheduled = None
              ScheduledDt = None
              Deadline = None
              DeadlineDt = None
              Closed = None
              ClosedDt = None
              Properties = None
              Body = Some "Migrate the legacy API to the new framework"
              OutlinePath = None }
        )

        db.RebuildFtsForFile("/test.org")
        let results = db.SearchFts("migration")
        Assert.True(results.Length > 0)
        Assert.Equal("API migration plan", results.[0].Title))

[<Fact>]
let ``FTS delete command removes entries`` () =
    withDb (fun db ->
        db.InsertFile(
            { Path = "/test.org"
              Hash = "h"
              Mtime = 1L }
        )

        db.InsertHeadline(
            { File = "/test.org"
              CharPos = 0L
              Level = 1
              Title = "API migration plan"
              Todo = None
              Priority = None
              Scheduled = None
              ScheduledDt = None
              Deadline = None
              DeadlineDt = None
              Closed = None
              ClosedDt = None
              Properties = None
              Body = Some "Migrate the legacy API"
              OutlinePath = None }
        )

        db.RebuildFtsForFile("/test.org")
        let before = db.SearchFts("migration")
        Assert.True(before.Length > 0)
        db.DeleteFtsForFile("/test.org")
        let after = db.SearchFts("migration")
        Assert.Equal(0, after.Length))

[<Fact>]
let ``FTS snippet returns context for body match`` () =
    withDb (fun db ->
        db.InsertFile(
            { Path = "/test.org"
              Hash = "h"
              Mtime = 1L }
        )

        db.InsertHeadline(
            { File = "/test.org"
              CharPos = 0L
              Level = 1
              Title = "Project notes"
              Todo = None
              Priority = None
              Scheduled = None
              ScheduledDt = None
              Deadline = None
              DeadlineDt = None
              Closed = None
              ClosedDt = None
              Properties = None
              Body = Some "The migration of the API was completed successfully last week"
              OutlinePath = None }
        )

        db.RebuildFtsForFile("/test.org")
        let results = db.SearchFts("migration")
        Assert.True(results.Length > 0)
        Assert.True(results.[0].Context.IsSome, "Should have snippet context")
        Assert.Contains("migration", results.[0].Context.Value))

[<Fact>]
let ``FTS snippet returns context for title match`` () =
    withDb (fun db ->
        db.InsertFile(
            { Path = "/test.org"
              Hash = "h"
              Mtime = 1L }
        )

        db.InsertHeadline(
            { File = "/test.org"
              CharPos = 0L
              Level = 1
              Title = "API migration roadmap"
              Todo = None
              Priority = None
              Scheduled = None
              ScheduledDt = None
              Deadline = None
              DeadlineDt = None
              Closed = None
              ClosedDt = None
              Properties = None
              Body = Some "Just some text"
              OutlinePath = None }
        )

        db.RebuildFtsForFile("/test.org")
        let results = db.SearchFts("migration")
        Assert.True(results.Length > 0)
        Assert.True(results.[0].Context.IsSome))

// ── outline_path ──

[<Fact>]
let ``outline_path uses unit separator`` () =
    withDb (fun db ->
        db.InsertFile(
            { Path = "/test.org"
              Hash = "h"
              Mtime = 1L }
        )

        let path = "Projects\x1FBackend\x1FAPI cleanup"

        db.InsertHeadline(
            { File = "/test.org"
              CharPos = 0L
              Level = 3
              Title = "API cleanup"
              Todo = None
              Priority = None
              Scheduled = None
              ScheduledDt = None
              Deadline = None
              DeadlineDt = None
              Closed = None
              ClosedDt = None
              Properties = None
              Body = None
              OutlinePath = Some path }
        )

        let result = db.GetHeadline("/test.org", 0L)
        Assert.True(result.IsSome)
        Assert.Equal(Some path, result.Value.OutlinePath)
        Assert.Contains("\x1F", result.Value.OutlinePath.Value))

[<Fact>]
let ``outline_path prefix query matches children`` () =
    withDb (fun db ->
        db.InsertFile(
            { Path = "/test.org"
              Hash = "h"
              Mtime = 1L }
        )

        let mkH pos title path =
            { File = "/test.org"
              CharPos = pos
              Level = 1
              Title = title
              Todo = None
              Priority = None
              Scheduled = None
              ScheduledDt = None
              Deadline = None
              DeadlineDt = None
              Closed = None
              ClosedDt = None
              Properties = None
              Body = None
              OutlinePath = Some path }

        db.InsertHeadline(mkH 0L "A" "A")
        db.InsertHeadline(mkH 10L "B" ("A\x1FB"))
        db.InsertHeadline(mkH 20L "C" ("A\x1FB\x1FC"))
        let prefix = "A\x1F"
        let results = db.QueryHeadlines(outlinePathPrefix = "A")
        Assert.True(results.Length >= 2, sprintf "Expected at least 2 children, got %d" results.Length))

[<Fact>]
let ``outline_path with slash in title does not match false prefix`` () =
    withDb (fun db ->
        db.InsertFile(
            { Path = "/test.org"
              Hash = "h"
              Mtime = 1L }
        )

        let mkH pos title path =
            { File = "/test.org"
              CharPos = pos
              Level = 1
              Title = title
              Todo = None
              Priority = None
              Scheduled = None
              ScheduledDt = None
              Deadline = None
              DeadlineDt = None
              Closed = None
              ClosedDt = None
              Properties = None
              Body = None
              OutlinePath = Some path }
        // Title "B/C" contains a slash — should not be confused with hierarchy
        db.InsertHeadline(mkH 0L "B/C" ("A\x1FB/C"))
        // Query for "A\x1FB\x1F%" should NOT match "A\x1FB/C"
        let results = db.QueryHeadlines(outlinePathPrefix = "A\x1FB")
        Assert.Equal(0, results.Length))

// ── Timestamp normalization ──

[<Fact>]
let ``Timed timestamp normalizes to yyyy-MM-ddTHH:mm`` () =
    let ts: OrgCli.Org.Timestamp =
        { Type = OrgCli.Org.TimestampType.Active
          Date = DateTime(2026, 2, 7, 10, 0, 0)
          HasTime = true
          Repeater = None
          Delay = None
          RangeEnd = None }

    let result = IndexSync.normalizeTimestamp ts
    Assert.Equal("2026-02-07T10:00", result)

[<Fact>]
let ``All-day timestamp normalizes to yyyy-MM-dd`` () =
    let ts: OrgCli.Org.Timestamp =
        { Type = OrgCli.Org.TimestampType.Active
          Date = DateTime(2026, 2, 7)
          HasTime = false
          Repeater = None
          Delay = None
          RangeEnd = None }

    let result = IndexSync.normalizeTimestamp ts
    Assert.Equal("2026-02-07", result)

[<Fact>]
let ``All-day sorts before timed on same date`` () =
    // "2026-02-07" < "2026-02-07T00:00" lexicographically
    let allDay = "2026-02-07"
    let timed = "2026-02-07T00:00"
    Assert.True(String.Compare(allDay, timed, StringComparison.Ordinal) < 0)

[<Fact>]
let ``Normalized datetimes sort correctly across days and times`` () =
    let values =
        [ "2026-02-06T23:59"
          "2026-02-07"
          "2026-02-07T00:00"
          "2026-02-07T10:30"
          "2026-02-08" ]

    let sorted = values |> List.sort
    Assert.Equal<string list>(values, sorted)
