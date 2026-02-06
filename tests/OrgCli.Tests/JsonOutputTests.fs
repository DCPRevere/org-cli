module JsonOutputTests

open System
open System.Text.Json.Nodes
open Xunit
open OrgCli.Org

[<Fact>]
let ``ok wraps data in envelope`` () =
    let result = JsonOutput.ok (JsonValue.Create(42))
    Assert.Equal("""{"ok":true,"data":42}""", result)

[<Fact>]
let ``ok wraps string data in envelope`` () =
    let result = JsonOutput.ok (JsonValue.Create("hello"))
    Assert.Equal("""{"ok":true,"data":"hello"}""", result)

[<Fact>]
let ``ok wraps object data in envelope`` () =
    let dataObj = JsonObject()
    dataObj["key"] <- JsonValue.Create("value")
    let result = JsonOutput.ok dataObj
    Assert.Equal("""{"ok":true,"data":{"key":"value"}}""", result)

[<Fact>]
let ``error produces structured error envelope`` () =
    let err = { Type = CliErrorType.HeadlineNotFound; Message = "Headline not found: foo"; Detail = None }
    let result = JsonOutput.error err
    Assert.Contains("\"ok\":false", result)
    Assert.Contains("\"type\":\"headline_not_found\"", result)
    Assert.Contains("\"message\":\"Headline not found: foo\"", result)

[<Fact>]
let ``error with detail includes detail field`` () =
    let err = { Type = CliErrorType.ParseError; Message = "Parse failed"; Detail = Some "line 5" }
    let result = JsonOutput.error err
    Assert.Contains("\"detail\":\"line 5\"", result)

[<Fact>]
let ``error without detail has null detail`` () =
    let err = { Type = CliErrorType.FileNotFound; Message = "File missing"; Detail = None }
    let result = JsonOutput.error err
    Assert.Contains("\"detail\":null", result)

[<Fact>]
let ``formatHeadlineState produces correct JSON`` () =
    let state : HeadlineEdit.HeadlineState = {
        Pos = 42L
        Id = Some "abc-123"
        Title = "My task"
        Todo = Some "TODO"
        Priority = Some 'A'
        Tags = ["work"; "urgent"]
        Scheduled = Some "<2026-02-10 Tue>"
        Deadline = None
        Closed = None
    }
    let result = JsonOutput.toJsonString (JsonOutput.formatHeadlineState state)
    Assert.Contains("\"pos\":42", result)
    Assert.Contains("\"id\":\"abc-123\"", result)
    Assert.Contains("\"title\":\"My task\"", result)
    Assert.Contains("\"todo\":\"TODO\"", result)
    Assert.Contains("\"priority\":\"A\"", result)
    Assert.Contains("\"tags\":[\"work\",\"urgent\"]", result)
    Assert.Contains("\"scheduled\":\"<2026-02-10 Tue>\"", result)
    Assert.Contains("\"deadline\":null", result)
    Assert.Contains("\"closed\":null", result)

[<Fact>]
let ``formatHeadlineState with null optionals`` () =
    let state : HeadlineEdit.HeadlineState = {
        Pos = 0L
        Id = None
        Title = "Plain"
        Todo = None
        Priority = None
        Tags = []
        Scheduled = None
        Deadline = None
        Closed = None
    }
    let result = JsonOutput.toJsonString (JsonOutput.formatHeadlineState state)
    Assert.Contains("\"id\":null", result)
    Assert.Contains("\"todo\":null", result)
    Assert.Contains("\"priority\":null", result)
    Assert.Contains("\"tags\":[]", result)

[<Fact>]
let ``formatErrorType maps all error types`` () =
    Assert.Equal("headline_not_found", JsonOutput.formatErrorType CliErrorType.HeadlineNotFound)
    Assert.Equal("file_not_found", JsonOutput.formatErrorType CliErrorType.FileNotFound)
    Assert.Equal("parse_error", JsonOutput.formatErrorType CliErrorType.ParseError)
    Assert.Equal("invalid_args", JsonOutput.formatErrorType CliErrorType.InvalidArgs)
    Assert.Equal("internal_error", JsonOutput.formatErrorType CliErrorType.InternalError)

[<Fact>]
let ``formatHeadlineStateDryRun includes dry_run field`` () =
    let state : HeadlineEdit.HeadlineState = {
        Pos = 10L; Id = Some "x"; Title = "Test"; Todo = Some "TODO"
        Priority = None; Tags = []; Scheduled = None; Deadline = None; Closed = None
    }
    let result = JsonOutput.toJsonString (JsonOutput.formatHeadlineStateDryRun state)
    Assert.Contains("\"dry_run\":true", result)
    Assert.Contains("\"pos\":10", result)
    Assert.Contains("\"title\":\"Test\"", result)
    Assert.StartsWith("{", result)
    Assert.EndsWith("}", result)

[<Fact>]
let ``formatBatchResults wraps array in single envelope`` () =
    let state : HeadlineEdit.HeadlineState = {
        Pos = 0L; Id = None; Title = "T"; Todo = Some "TODO"
        Priority = None; Tags = []; Scheduled = None; Deadline = None; Closed = None
    }
    let results = [Ok state; Ok state]
    let result = JsonOutput.formatBatchResults results
    Assert.StartsWith("{\"ok\":true,\"data\":[", result)
    Assert.DoesNotContain("\"ok\":true,\"data\":{\"ok\":", result)

[<Fact>]
let ``formatBatchResults formats errors without envelope`` () =
    let err = { Type = CliErrorType.HeadlineNotFound; Message = "Not found"; Detail = None }
    let state : HeadlineEdit.HeadlineState = {
        Pos = 0L; Id = None; Title = "T"; Todo = None
        Priority = None; Tags = []; Scheduled = None; Deadline = None; Closed = None
    }
    let results = [Ok state; Error err]
    let result = JsonOutput.formatBatchResults results
    Assert.Contains("\"ok\":false", result)
    let okTrueCount = System.Text.RegularExpressions.Regex.Matches(result, "\"ok\":true").Count
    Assert.Equal(2, okTrueCount)

[<Fact>]
let ``Utils.parseDate produces Active timestamp from yyyy-MM-dd`` () =
    let ts = Utils.parseDate "2026-03-15"
    Assert.Equal(DateTime(2026, 3, 15), ts.Date)
    Assert.Equal(TimestampType.Active, ts.Type)
    Assert.False(ts.HasTime)
    Assert.True(Option.isNone ts.Repeater)
    Assert.True(Option.isNone ts.Delay)

[<Fact>]
let ``JSON properly escapes special characters in strings`` () =
    let state : HeadlineEdit.HeadlineState = {
        Pos = 0L; Id = None; Title = "Task with \"quotes\" and\nnewline"
        Todo = None; Priority = None; Tags = []; Scheduled = None; Deadline = None; Closed = None
    }
    let result = JsonOutput.toJsonString (JsonOutput.formatHeadlineState state)
    Assert.Contains("\\\"quotes\\\"", result)
    Assert.Contains("\\n", result)

// ============================================================
// Structural JSON assertions (tasks.org: Use structural JSON assertions)
// These parse JSON and check structure rather than matching strings.
// ============================================================

[<Fact>]
let ``structural: ok envelope has correct shape`` () =
    let result = JsonOutput.ok (JsonValue.Create(42))
    let node = JsonNode.Parse(result)
    Assert.True(node["ok"].GetValue<bool>())
    Assert.Equal(42, node["data"].GetValue<int>())

[<Fact>]
let ``structural: error envelope has correct shape`` () =
    let err = { Type = CliErrorType.HeadlineNotFound; Message = "Not found"; Detail = Some "ctx" }
    let result = JsonOutput.error err
    let node = JsonNode.Parse(result)
    Assert.False(node["ok"].GetValue<bool>())
    let errNode = node["error"]
    Assert.Equal("headline_not_found", errNode["type"].GetValue<string>())
    Assert.Equal("Not found", errNode["message"].GetValue<string>())
    Assert.Equal("ctx", errNode["detail"].GetValue<string>())

[<Fact>]
let ``structural: formatHeadlineState fields are correct types`` () =
    let state : HeadlineEdit.HeadlineState = {
        Pos = 42L; Id = Some "abc-123"; Title = "My task"; Todo = Some "TODO"
        Priority = Some 'A'; Tags = ["work"; "urgent"]
        Scheduled = Some "<2026-02-10 Tue>"; Deadline = None; Closed = None
    }
    let json = JsonOutput.toJsonString (JsonOutput.formatHeadlineState state)
    let node = JsonNode.Parse(json)
    Assert.Equal(42, node["pos"].GetValue<int>())
    Assert.Equal("abc-123", node["id"].GetValue<string>())
    Assert.Equal("My task", node["title"].GetValue<string>())
    Assert.Equal("TODO", node["todo"].GetValue<string>())
    Assert.Equal("A", node["priority"].GetValue<string>())
    let tags = node["tags"].AsArray()
    Assert.Equal(2, tags.Count)
    Assert.Equal("work", tags.[0].GetValue<string>())
    Assert.Equal("urgent", tags.[1].GetValue<string>())
    Assert.Equal("<2026-02-10 Tue>", node["scheduled"].GetValue<string>())
    Assert.Null(node["deadline"])
    Assert.Null(node["closed"])

[<Fact>]
let ``structural: batch results array has correct shape`` () =
    let state : HeadlineEdit.HeadlineState = {
        Pos = 0L; Id = None; Title = "T"; Todo = Some "TODO"
        Priority = None; Tags = []; Scheduled = None; Deadline = None; Closed = None
    }
    let err = { Type = CliErrorType.HeadlineNotFound; Message = "Not found"; Detail = None }
    let results = [Ok state; Error err]
    let json = JsonOutput.formatBatchResults results
    let node = JsonNode.Parse(json)
    Assert.True(node["ok"].GetValue<bool>())
    let data = node["data"].AsArray()
    Assert.Equal(2, data.Count)
    // First result: ok
    let first = data.[0]
    Assert.True(first["ok"].GetValue<bool>())
    let firstData = first["data"]
    Assert.Equal("T", firstData["title"].GetValue<string>())
    // Second result: error
    let second = data.[1]
    Assert.False(second["ok"].GetValue<bool>())
    let secondErr = second["error"]
    Assert.Equal("headline_not_found", secondErr["type"].GetValue<string>())
