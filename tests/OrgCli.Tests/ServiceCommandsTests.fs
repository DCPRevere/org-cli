[<Xunit.Collection("ConsoleCapture")>]
module OrgCli.Tests.ServiceCommandsTests

open System
open System.Text.Json
open Xunit
open OrgCli.Org
open OrgCli.ServiceCommands
open OrgCli.Tests.VirtualWorkspaceTests

let options fields =
    fields |> List.map (fun (k, v) -> k, [ v ]) |> Map.ofList

let unitPath = "/home/test/.config/systemd/user/org-cli.service"
let dropinPath = unitPath + ".d/50-org-cli.conf"
let configPath = "/home/test/.config/org-cli/service.json"

let context (h: VirtualHost) =
    let calls = ResizeArray<string * string list>()

    let ctx =
        { Host = h :> Runtime.IHost
          IsLinux = true
          Command = [ "/opt/org/bin/org" ]
          Run =
            fun cmd args ->
                calls.Add(cmd, args)
                let xdg = h.Environment.TryGetValue "XDG_CONFIG_HOME"

                let root =
                    match xdg with
                    | true, value -> value
                    | _ -> "/home/test/.config"

                if args = [ "--user"; "show"; "--property=UnitPath"; "--value" ] then
                    0, root + "/systemd/user /usr/lib/systemd/user\n"
                else
                    0, "active\n"
          Healthy = fun _ -> true
          Sleep = ignore }

    ctx, calls

let install ctx extra =
    execute ctx (options ([ "directory", "/work" ] @ extra)) [ "install" ]

[<Fact>]
let ``standalone service setup records scoped settings without touching notes or enabling linger`` () =
    use h = new VirtualHost()
    h.Put("/work/example.org", "* TODO Keep me\n")
    h.Environment["ORG_API_TOKEN"] <- "do-not-copy-secrets"
    let ctx, calls = context h
    Assert.Equal(0, install ctx [ "mcp", "true"; "read-only", "true"; "port", "9021" ])
    let cfg = JsonSerializer.Deserialize<Settings>(h.Text configPath)
    Assert.Equal("/work", cfg.Directory)
    Assert.Equal(9021, cfg.Port)
    Assert.True(cfg.Mcp && cfg.ReadOnly)
    Assert.Contains("Restart=on-failure", h.Text unitPath)
    Assert.Contains("\"/opt/org/bin/org\" \"service\" \"run\"", h.Text dropinPath)
    Assert.DoesNotContain("do-not-copy-secrets", h.Text configPath)
    Assert.Equal("* TODO Keep me\n", h.Text "/work/example.org")
    Assert.Contains(("systemctl", [ "--user"; "enable"; "org-cli.service" ]), calls)
    Assert.DoesNotContain(calls, fun (cmd, _) -> cmd = "loginctl" || cmd = "sudo")

[<Fact>]
let ``packaged service setup creates only its drop-in and repeat installation restarts with new settings`` () =
    use h = new VirtualHost()
    h.Put("/usr/lib/systemd/user/org-cli.service", unitTemplate ())
    let ctx, calls = context h
    install ctx [] |> ignore
    Assert.False(h.Files.ContainsKey unitPath)
    install ctx [ "port", "9012" ] |> ignore
    Assert.Equal(9012, (JsonSerializer.Deserialize<Settings>(h.Text configPath)).Port)

    Assert.Equal(
        2,
        calls
        |> Seq.filter (fun (_, args) -> args = [ "--user"; "restart"; "org-cli.service" ])
        |> Seq.length
    )

[<Fact>]
let ``custom unit and foreign configuration are preserved before subprocesses or writes`` () =
    for file, text in
        [ unitPath, "[Service]\nExecStart=/my/org\n"
          dropinPath, "# custom"
          configPath, "{}" ] do
        use h = new VirtualHost()
        h.Put(file, text)
        let ctx, calls = context h
        Assert.ThrowsAny<Exception>(fun () -> install ctx [] |> ignore) |> ignore
        Assert.Equal(text, h.Text file)
        Assert.Equal(0, h.Writes)
        Assert.Empty(calls)

[<Fact>]
let ``missing manager or unsupported options do not write service configuration`` () =
    use h = new VirtualHost()
    let ctx, _ = context h

    for opts in
        [ options []
          options [ "directory", "/missing" ]
          options [ "directory", "/work"; "port", "0" ]
          options [ "directory", "/work"; "dry-run", "true" ] ] do
        Assert.ThrowsAny<Exception>(fun () -> execute ctx opts [ "install" ] |> ignore)
        |> ignore

    Assert.ThrowsAny<Exception>(fun () -> install { ctx with IsLinux = false } [] |> ignore)
    |> ignore

    Assert.ThrowsAny<Exception>(fun () ->
        install
            { ctx with
                Run = fun _ _ -> 1, "No user bus" }
            []
        |> ignore)
    |> ignore

    Assert.Equal(0, h.Writes)

[<Fact>]
let ``uninstall disables before removing owned files but preserves environment credentials and workspace`` () =
    use h = new VirtualHost()
    let ctx, calls = context h
    install ctx [] |> ignore
    h.Put("/home/test/.config/org-cli/service.env", "ORG_API_TOKEN=keep-me")
    h.Put("/work/a.org", "* Notes")
    // Removal must work even if the old workspace has been deleted.
    (h :> Runtime.IHost).DeleteDirectory "/work"
    Assert.Equal(0, execute ctx Map.empty [ "uninstall" ])

    for path in [ unitPath; dropinPath; configPath ] do
        Assert.False(h.Files.ContainsKey path)

    Assert.Equal("ORG_API_TOKEN=keep-me", h.Text "/home/test/.config/org-cli/service.env")
    Assert.Equal("* Notes", h.Text "/work/a.org")
    Assert.Contains(("systemctl", [ "--user"; "disable"; "--now"; "org-cli.service" ]), calls)

[<Fact>]
let ``failed start is reported and failed disable does not delete diagnostic configuration`` () =
    use h = new VirtualHost()
    let ctx, _ = context h

    let ex =
        Assert.Throws<InvalidOperationException>(fun () -> install { ctx with Healthy = fun _ -> false } [] |> ignore)

    Assert.Contains("did not become ready", ex.Message)

    let bad =
        { ctx with
            Run = fun _ _ -> 1, "Unavailable" }

    Assert.Throws<InvalidOperationException>(fun () -> execute bad Map.empty [ "uninstall" ] |> ignore)
    |> ignore

    Assert.True(h.Files.ContainsKey configPath)
    Assert.True(h.Files.ContainsKey dropinPath)

[<Fact>]
let ``systemd quoting handles spaces dollars specifiers and custom XDG configuration`` () =
    use h = new VirtualHost()
    h.Environment["XDG_CONFIG_HOME"] <- "/home/test/custom %h $config"
    let ctx, _ = context h

    let ctx =
        { ctx with
            Command = [ "/opt/My App $bin %h/org" ] }

    install ctx [] |> ignore

    let dropin =
        h.Text "/home/test/custom %h $config/systemd/user/org-cli.service.d/50-org-cli.conf"

    Assert.Contains("\"/opt/My App $$bin %%h/org\"", dropin)
    Assert.Contains("Environment=\"XDG_CONFIG_HOME=/home/test/custom %%h $config\"", dropin)
    Assert.Contains("EnvironmentFile=-\"/home/test/custom %%h $config/org-cli/service.env\"", dropin)

    Assert.Throws<InvalidOperationException>(fun () -> quoteArgument "bad\npath" |> ignore)
    |> ignore

[<Fact>]
let ``status forwards systemctl exit status and read-only controls do not create files`` () =
    use h = new VirtualHost()
    let ctx, calls = context h

    Assert.Equal(
        3,
        execute
            { ctx with
                Run = fun _ _ -> 3, "inactive\n" }
            Map.empty
            [ "status" ]
    )

    Assert.Equal(0, execute ctx Map.empty [ "stop" ])
    Assert.Equal(0, h.Writes)
    Assert.Single(calls) |> ignore
