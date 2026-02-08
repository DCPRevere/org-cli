module BatchTests

open System
open Xunit
open OrgCli.Org

let private now = DateTime(2026, 2, 5, 14, 30, 0)

/// Parse batch JSON and execute against in-memory file contents.
/// Returns (results, final file contents map).
let private executeBatch (json: string) (files: Map<string, string>) =
    BatchMode.executeBatch Types.defaultConfig json files now

[<Fact>]
let ``single todo command produces correct result`` () =
    let content = "* TODO My task\nBody\n"

    let json =
        """{"commands":[{"command":"todo","file":"test.org","identifier":"My task","args":{"state":"DONE"}}]}"""

    let (results, files) = executeBatch json (Map.ofList [ ("test.org", content) ])
    Assert.Equal(1, results.Length)

    match results.[0] with
    | Ok state -> Assert.Equal(Some "DONE", state.Todo)
    | Error _ -> Assert.Fail("Expected Ok")

    Assert.Contains("* DONE My task", files.["test.org"])

[<Fact>]
let ``multi-command batch on same file applies sequentially`` () =
    let content = "* TODO My task\nBody\n"

    let json =
        """{"commands":[
        {"command":"todo","file":"test.org","identifier":"My task","args":{"state":"DONE"}},
        {"command":"tag-add","file":"test.org","identifier":"My task","args":{"tag":"done"}}
    ]}"""

    let (results, files) = executeBatch json (Map.ofList [ ("test.org", content) ])
    Assert.Equal(2, results.Length)
    Assert.True(Result.isOk results.[0])
    Assert.True(Result.isOk results.[1])
    let finalContent = files.["test.org"]
    Assert.Contains("DONE", finalContent)
    Assert.Contains("done", finalContent)

[<Fact>]
let ``batch with org-id identifiers works across mutations`` () =
    let content = "* TODO My task\n:PROPERTIES:\n:ID: abc-123\n:END:\nBody\n"

    let json =
        """{"commands":[
        {"command":"todo","file":"test.org","identifier":"abc-123","args":{"state":"DONE"}},
        {"command":"tag-add","file":"test.org","identifier":"abc-123","args":{"tag":"processed"}}
    ]}"""

    let (results, files) = executeBatch json (Map.ofList [ ("test.org", content) ])
    Assert.True(Result.isOk results.[0])
    Assert.True(Result.isOk results.[1])
    Assert.Contains(":ID: abc-123", files.["test.org"])

[<Fact>]
let ``batch returns per-command error for invalid command`` () =
    let content = "* TODO My task\nBody\n"

    let json =
        """{"commands":[
        {"command":"todo","file":"test.org","identifier":"My task","args":{"state":"DONE"}},
        {"command":"todo","file":"test.org","identifier":"Nonexistent","args":{"state":"DONE"}},
        {"command":"tag-add","file":"test.org","identifier":"My task","args":{"tag":"ok"}}
    ]}"""

    let (results, _) = executeBatch json (Map.ofList [ ("test.org", content) ])
    Assert.Equal(3, results.Length)
    Assert.True(Result.isOk results.[0])
    Assert.True(Result.isError results.[1])
    Assert.True(Result.isOk results.[2])

[<Fact>]
let ``batch with schedule command`` () =
    let content = "* TODO My task\nBody\n"

    let json =
        """{"commands":[{"command":"schedule","file":"test.org","identifier":"My task","args":{"date":"2026-03-01"}}]}"""

    let (results, files) = executeBatch json (Map.ofList [ ("test.org", content) ])
    Assert.True(Result.isOk results.[0])
    Assert.Contains("SCHEDULED:", files.["test.org"])

[<Fact>]
let ``batch with property-set command`` () =
    let content = "* My task\nBody\n"

    let json =
        """{"commands":[{"command":"property-set","file":"test.org","identifier":"My task","args":{"key":"EFFORT","value":"1:00"}}]}"""

    let (results, files) = executeBatch json (Map.ofList [ ("test.org", content) ])
    Assert.True(Result.isOk results.[0])
    Assert.Contains(":EFFORT: 1:00", files.["test.org"])

[<Fact>]
let ``batch across multiple files`` () =
    let content1 = "* TODO Task A\nBody\n"
    let content2 = "* TODO Task B\nBody\n"

    let json =
        """{"commands":[
        {"command":"todo","file":"a.org","identifier":"Task A","args":{"state":"DONE"}},
        {"command":"todo","file":"b.org","identifier":"Task B","args":{"state":"DONE"}}
    ]}"""

    let files = Map.ofList [ ("a.org", content1); ("b.org", content2) ]
    let (results, newFiles) = executeBatch json files
    Assert.True(Result.isOk results.[0])
    Assert.True(Result.isOk results.[1])
    Assert.Contains("DONE", newFiles.["a.org"])
    Assert.Contains("DONE", newFiles.["b.org"])

// --- Missing test cases per docs/tasks.org ---

[<Fact>]
let ``batch with invalid JSON returns parse error`` () =
    let badJson = "not json at all {"

    Assert.ThrowsAny<System.Text.Json.JsonException>(fun () ->
        executeBatch badJson (Map.ofList [ ("test.org", "* Task\n") ]) |> ignore)
    |> ignore

[<Fact>]
let ``batch with missing required fields returns error`` () =
    // Missing "file" field
    let json =
        """{"commands":[{"command":"todo","identifier":"Task","args":{"state":"DONE"}}]}"""

    Assert.ThrowsAny<Exception>(fun () -> executeBatch json (Map.ofList [ ("test.org", "* TODO Task\n") ]) |> ignore)
    |> ignore

[<Fact>]
let ``batch with empty commands array returns empty results`` () =
    let json = """{"commands":[]}"""
    let (results, _) = executeBatch json (Map.ofList [ ("test.org", "* Task\n") ])
    Assert.Empty(results)

[<Fact>]
let ``batch partial failure: some succeed some fail, successes are preserved`` () =
    let content = "* TODO Good task\n:PROPERTIES:\n:ID: good-id\n:END:\nBody\n"

    let json =
        """{"commands":[
        {"command":"todo","file":"test.org","identifier":"Good task","args":{"state":"DONE"}},
        {"command":"todo","file":"test.org","identifier":"Does not exist","args":{"state":"DONE"}},
        {"command":"tag-add","file":"test.org","identifier":"Good task","args":{"tag":"processed"}}
    ]}"""

    let (results, files) = executeBatch json (Map.ofList [ ("test.org", content) ])
    Assert.Equal(3, results.Length)
    Assert.True(Result.isOk results.[0], "First command should succeed")
    Assert.True(Result.isError results.[1], "Second command should fail (nonexistent headline)")
    Assert.True(Result.isOk results.[2], "Third command should succeed")
    // The successful mutations should still be applied
    Assert.Contains("DONE", files.["test.org"])
    Assert.Contains("processed", files.["test.org"])

[<Fact>]
let ``batch with 50 commands completes`` () =
    let content = "* TODO Task\n:PROPERTIES:\n:ID: task-id\n:END:\nBody\n"

    let commands =
        [ 1..50 ]
        |> List.map (fun i ->
            sprintf """{"command":"tag-add","file":"test.org","identifier":"Task","args":{"tag":"tag%d"}}""" i)
        |> String.concat ","

    let json = sprintf """{"commands":[%s]}""" commands
    let (results, files) = executeBatch json (Map.ofList [ ("test.org", content) ])
    Assert.Equal(50, results.Length)
    Assert.True(results |> List.forall Result.isOk)
    // Verify some tags present in final content
    Assert.Contains("tag1", files.["test.org"])
    Assert.Contains("tag50", files.["test.org"])
