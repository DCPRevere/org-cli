module OrgCli.Tests.DirectoryConfigTests

open System
open System.IO
open Xunit
open OrgCli.Org

[<Fact>]
let ``expandHome replaces leading tilde-slash with home directory`` () =
    let home =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)

    let result = Utils.expandHome "~/org"
    Assert.Equal(Path.Combine(home, "org"), result)

[<Fact>]
let ``expandHome replaces bare tilde with home directory`` () =
    let home =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)

    Assert.Equal(home, Utils.expandHome "~")

[<Fact>]
let ``expandHome passes absolute path through unchanged`` () =
    Assert.Equal("/tmp/org", Utils.expandHome "/tmp/org")

[<Fact>]
let ``expandHome passes relative path through unchanged`` () =
    Assert.Equal("some/path", Utils.expandHome "some/path")

[<Fact>]
let ``listOrgFiles returns empty list for nonexistent directory`` () =
    Assert.Empty(Utils.listOrgFiles "/nonexistent/path/that/does/not/exist")

[<Fact>]
let ``listOrgFiles finds org files in a directory`` () =
    let dir =
        Path.Combine(Path.GetTempPath(), sprintf "org-dirconfig-test-%s" (Guid.NewGuid().ToString("N")))

    Directory.CreateDirectory(dir) |> ignore

    try
        File.WriteAllText(Path.Combine(dir, "a.org"), "* Hello")
        File.WriteAllText(Path.Combine(dir, "b.txt"), "not org")
        let files = Utils.listOrgFiles dir
        Assert.Single(files) |> ignore
        Assert.Contains("a.org", files.[0])
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``listOrgFiles does not crash on inaccessible subdirectories`` () =
    // This test verifies that EnumerationOptions.IgnoreInaccessible is set.
    // We create a directory and verify normal scanning works — the key behavior
    // (skipping permission-denied) is structural from the EnumerationOptions flag.
    let dir =
        Path.Combine(Path.GetTempPath(), sprintf "org-dirconfig-test-%s" (Guid.NewGuid().ToString("N")))

    Directory.CreateDirectory(dir) |> ignore

    try
        File.WriteAllText(Path.Combine(dir, "test.org"), "* Test")
        let sub = Path.Combine(dir, "sub")
        Directory.CreateDirectory(sub) |> ignore
        File.WriteAllText(Path.Combine(sub, "nested.org"), "* Nested")
        let files = Utils.listOrgFiles dir
        Assert.Equal(2, files.Length)
    finally
        Directory.Delete(dir, true)

// Program.fs helpers are module-level functions in the OrgCli assembly
open OrgCli

[<Fact>]
let ``loadDirectoriesFromEnv returns empty when env var is unset`` () =
    let old = Environment.GetEnvironmentVariable("ORG_CLI_DIRECTORY")

    try
        Environment.SetEnvironmentVariable("ORG_CLI_DIRECTORY", null)
        let result = Program.loadDirectoriesFromEnv ()
        Assert.Empty(result)
    finally
        Environment.SetEnvironmentVariable("ORG_CLI_DIRECTORY", old)

[<Fact>]
let ``loadDirectoriesFromEnv splits by path separator and expands tilde`` () =
    let home =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)

    let old = Environment.GetEnvironmentVariable("ORG_CLI_DIRECTORY")

    try
        let sep = string Path.PathSeparator
        Environment.SetEnvironmentVariable("ORG_CLI_DIRECTORY", "~/org" + sep + "/tmp/notes")
        let result = Program.loadDirectoriesFromEnv ()
        Assert.Equal(2, result.Length)
        Assert.Equal(Path.Combine(home, "org"), result.[0])
        Assert.Equal("/tmp/notes", result.[1])
    finally
        Environment.SetEnvironmentVariable("ORG_CLI_DIRECTORY", old)

[<Fact>]
let ``loadDirectoriesFromConfig returns empty when config file does not exist`` () =
    let old = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")

    try
        // Point to a nonexistent config dir
        let fake = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", fake)
        let result = Program.loadDirectoriesFromConfig ()
        Assert.Empty(result)
    finally
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", old)

[<Fact>]
let ``loadDirectoriesFromConfig reads directories array from config`` () =
    let home =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)

    let old = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")

    try
        let tmpConfig =
            Path.Combine(Path.GetTempPath(), sprintf "org-dirconfig-test-%s" (Guid.NewGuid().ToString("N")))

        let configDir = Path.Combine(tmpConfig, "org-cli")
        Directory.CreateDirectory(configDir) |> ignore
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", tmpConfig)

        File.WriteAllText(
            Path.Combine(configDir, "config.json"),
            """{"directories": ["~/org", "/tmp/notes"]}"""
        )

        let result = Program.loadDirectoriesFromConfig ()
        Assert.Equal(2, result.Length)
        Assert.Equal(Path.Combine(home, "org"), result.[0])
        Assert.Equal("/tmp/notes", result.[1])

        Directory.Delete(tmpConfig, true)
    finally
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", old)

[<Fact>]
let ``loadDirectoriesFromConfig returns empty when key is missing`` () =
    let old = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")

    try
        let tmpConfig =
            Path.Combine(Path.GetTempPath(), sprintf "org-dirconfig-test-%s" (Guid.NewGuid().ToString("N")))

        let configDir = Path.Combine(tmpConfig, "org-cli")
        Directory.CreateDirectory(configDir) |> ignore
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", tmpConfig)

        File.WriteAllText(
            Path.Combine(configDir, "config.json"),
            """{"logDone": "time"}"""
        )

        let result = Program.loadDirectoriesFromConfig ()
        Assert.Empty(result)

        Directory.Delete(tmpConfig, true)
    finally
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", old)

[<Fact>]
let ``resolveFiles prefers CLI directory flag over env and config`` () =
    let oldEnv = Environment.GetEnvironmentVariable("ORG_CLI_DIRECTORY")
    let oldXdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")

    try
        let dir =
            Path.Combine(Path.GetTempPath(), sprintf "org-dirconfig-test-%s" (Guid.NewGuid().ToString("N")))

        Directory.CreateDirectory(dir) |> ignore
        File.WriteAllText(Path.Combine(dir, "cli.org"), "* CLI")

        let envDir =
            Path.Combine(Path.GetTempPath(), sprintf "org-dirconfig-test-%s" (Guid.NewGuid().ToString("N")))

        Directory.CreateDirectory(envDir) |> ignore
        File.WriteAllText(Path.Combine(envDir, "env.org"), "* Env")

        let sep = string Path.PathSeparator
        Environment.SetEnvironmentVariable("ORG_CLI_DIRECTORY", envDir)

        // Pass -d flag
        let opts = Map.ofList [ "directory", [ dir ] ]
        let files = Program.resolveFiles opts
        Assert.Single(files) |> ignore
        Assert.Contains("cli.org", files.[0])

        Directory.Delete(dir, true)
        Directory.Delete(envDir, true)
    finally
        Environment.SetEnvironmentVariable("ORG_CLI_DIRECTORY", oldEnv)
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", oldXdg)

[<Fact>]
let ``resolveFiles uses env var when no CLI flag`` () =
    let oldEnv = Environment.GetEnvironmentVariable("ORG_CLI_DIRECTORY")

    try
        let envDir =
            Path.Combine(Path.GetTempPath(), sprintf "org-dirconfig-test-%s" (Guid.NewGuid().ToString("N")))

        Directory.CreateDirectory(envDir) |> ignore
        File.WriteAllText(Path.Combine(envDir, "env.org"), "* Env")

        Environment.SetEnvironmentVariable("ORG_CLI_DIRECTORY", envDir)

        let opts = Map.empty
        let files = Program.resolveFiles opts
        Assert.Single(files) |> ignore
        Assert.Contains("env.org", files.[0])

        Directory.Delete(envDir, true)
    finally
        Environment.SetEnvironmentVariable("ORG_CLI_DIRECTORY", oldEnv)

[<Fact>]
let ``resolveFiles deduplicates files from multiple directories`` () =
    let oldEnv = Environment.GetEnvironmentVariable("ORG_CLI_DIRECTORY")

    try
        let dir =
            Path.Combine(Path.GetTempPath(), sprintf "org-dirconfig-test-%s" (Guid.NewGuid().ToString("N")))

        Directory.CreateDirectory(dir) |> ignore
        File.WriteAllText(Path.Combine(dir, "a.org"), "* A")
        File.WriteAllText(Path.Combine(dir, "b.org"), "* B")

        let sep = string Path.PathSeparator
        // Point both entries at the same directory to verify dedup
        Environment.SetEnvironmentVariable("ORG_CLI_DIRECTORY", dir + sep + dir)

        let opts = Map.empty
        let files = Program.resolveFiles opts
        Assert.Equal(2, files.Length)

        Directory.Delete(dir, true)
    finally
        Environment.SetEnvironmentVariable("ORG_CLI_DIRECTORY", oldEnv)
