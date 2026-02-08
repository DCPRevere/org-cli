module RoamComplianceTests

open System
open System.IO
open Xunit
open Microsoft.Data.Sqlite
open OrgCli.Org
open OrgCli.Roam

// Helpers

let private withSyncedFile (orgContent: string) (assertions: Database.OrgRoamDb -> string -> unit) =
    let tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
    Directory.CreateDirectory(tmpDir) |> ignore
    let orgFile = Path.Combine(tmpDir, "test.org")
    let dbPath = Path.Combine(tmpDir, "test.db")

    try
        File.WriteAllText(orgFile, orgContent)
        use db = new Database.OrgRoamDb(dbPath)
        db.Initialize() |> ignore
        Sync.updateFile db tmpDir orgFile
        assertions db dbPath
    finally
        if Directory.Exists(tmpDir) then
            Directory.Delete(tmpDir, true)

let private queryCitations (dbPath: string) =
    use conn = new SqliteConnection(sprintf "Data Source=%s" dbPath)
    conn.Open()
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "SELECT node_id, cite_key, pos FROM citations"
    use reader = cmd.ExecuteReader()

    [ while reader.Read() do
          yield (reader.GetString(0), reader.GetString(1), reader.GetInt32(2)) ]

// ── Discrepancy #1: Body-text citations not extracted ──────────────────────

[<Fact>]
let ``Org-cite in body text populates citations table`` () =
    let content =
        ":PROPERTIES:\n\
         :ID: file-node-1\n\
         :END:\n\
         #+TITLE: Test\n\
         \n\
         Some text with a citation [cite:@smith2020] here.\n"

    withSyncedFile content (fun _db dbPath ->
        let citations = queryCitations dbPath
        Assert.NotEmpty(citations)
        let keys = citations |> List.map (fun (_, k, _) -> k)
        Assert.Contains("smith2020", keys))

[<Fact>]
let ``Multiple org-cite keys in body text are all extracted`` () =
    let content =
        ":PROPERTIES:\n\
         :ID: node-multi-cite\n\
         :END:\n\
         #+TITLE: Multi\n\
         \n\
         See [cite:@jones2021] and also [cite:@doe2019;@lee2022].\n"

    withSyncedFile content (fun _db dbPath ->
        let keys = queryCitations dbPath |> List.map (fun (_, k, _) -> k)
        Assert.Contains("jones2021", keys)
        Assert.Contains("doe2019", keys)
        Assert.Contains("lee2022", keys))

[<Fact>]
let ``Org-cite in headline body populates citations table`` () =
    let content =
        "* Heading\n\
         :PROPERTIES:\n\
         :ID: headline-cite-1\n\
         :END:\n\
         \n\
         Body with [cite:@ref2023] citation.\n"

    withSyncedFile content (fun _db dbPath ->
        let citations = queryCitations dbPath
        Assert.NotEmpty(citations)
        let nodeIds = citations |> List.map (fun (n, _, _) -> n)
        Assert.Contains("headline-cite-1", nodeIds))

// ── Discrepancy #2: ROAM_EXCLUDE too strict ────────────────────────────────

[<Theory>]
[<InlineData("yes")>]
[<InlineData("true")>]
[<InlineData("1")>]
let ``ROAM_EXCLUDE excludes on any non-empty value`` (value: string) =
    let props = Some { Properties = [ { Key = "ROAM_EXCLUDE"; Value = value } ] }
    Assert.True(Types.isRoamExcluded props)

[<Fact>]
let ``ROAM_EXCLUDE with non-t value prevents node from being synced`` () =
    let content =
        ":PROPERTIES:\n\
         :ID: excluded-node\n\
         :ROAM_EXCLUDE: yes\n\
         :END:\n\
         #+TITLE: Excluded\n"

    withSyncedFile content (fun db _dbPath ->
        let node = db.GetNode("excluded-node")
        Assert.True(node.IsNone, "Node with ROAM_EXCLUDE=yes should not be synced"))

// ── Discrepancy #4: Org-ref citation links in body ─────────────────────────

[<Fact>]
let ``Org-ref cite link in body text populates citations table`` () =
    let content =
        ":PROPERTIES:\n\
         :ID: orgref-node-1\n\
         :END:\n\
         #+TITLE: OrgRef\n\
         \n\
         Discussed in cite:martinez2020 and elaborated further.\n"

    withSyncedFile content (fun _db dbPath ->
        let citations = queryCitations dbPath
        Assert.NotEmpty(citations)
        let keys = citations |> List.map (fun (_, k, _) -> k)
        Assert.Contains("martinez2020", keys))

[<Fact>]
let ``Org-ref autocite link in body text populates citations table`` () =
    let content =
        ":PROPERTIES:\n\
         :ID: autocite-node\n\
         :END:\n\
         #+TITLE: AutoCite\n\
         \n\
         As shown autocite:wang2021 the method works.\n"

    withSyncedFile content (fun _db dbPath ->
        let citations = queryCitations dbPath
        Assert.NotEmpty(citations)
        let keys = citations |> List.map (fun (_, k, _) -> k)
        Assert.Contains("wang2021", keys))
