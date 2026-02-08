module SchemaTests

open Xunit
open System.Text.Json
open OrgCli.Org

[<Fact>]
let ``schema output is valid JSON`` () =
    let schema = JsonOutput.schema ()
    let doc = JsonDocument.Parse(schema)
    Assert.NotNull(doc)

[<Fact>]
let ``schema includes all command names`` () =
    let schema = JsonOutput.schema ()
    let doc = JsonDocument.Parse(schema)
    let root = doc.RootElement
    let commands = root.GetProperty("commands")

    let names =
        [ for i in 0 .. commands.GetArrayLength() - 1 -> commands.[i].GetProperty("name").GetString() ]

    Assert.Contains("todo", names)
    Assert.Contains("add", names)
    Assert.Contains("agenda", names)
    Assert.Contains("headlines", names)
    Assert.Contains("schedule", names)
    Assert.Contains("deadline", names)
    Assert.Contains("tag", names)
    Assert.Contains("property", names)
    Assert.Contains("note", names)
    Assert.Contains("refile", names)
    Assert.Contains("archive", names)
    Assert.Contains("read", names)
    Assert.Contains("search", names)
    Assert.Contains("clock", names)
    Assert.Contains("links", names)
    Assert.Contains("export", names)
    Assert.Contains("roam", names)
    Assert.Contains("batch", names)
    Assert.Contains("schema", names)

[<Fact>]
let ``schema commands have required fields`` () =
    let schema = JsonOutput.schema ()
    let doc = JsonDocument.Parse(schema)
    let commands = doc.RootElement.GetProperty("commands")

    for i in 0 .. commands.GetArrayLength() - 1 do
        let cmd = commands.[i]
        Assert.True(cmd.TryGetProperty("name") |> fst)
        Assert.True(cmd.TryGetProperty("description") |> fst)
        Assert.True(cmd.TryGetProperty("args") |> fst)

[<Fact>]
let ``schema includes version`` () =
    let schema = JsonOutput.schema ()
    let doc = JsonDocument.Parse(schema)
    Assert.Equal("0.1.0", doc.RootElement.GetProperty("version").GetString())
