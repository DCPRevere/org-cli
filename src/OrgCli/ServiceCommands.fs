module OrgCli.ServiceCommands

open System
open System.IO
open System.Text
open System.Text.Json
open System.Diagnostics
open System.Net.Http
open OrgCli.Org

[<CLIMutable>]
type Settings =
    { ManagedBy: string
      Directory: string
      Port: int
      Mcp: bool
      ReadOnly: bool }

type Context =
    { Host: Runtime.IHost
      IsLinux: bool
      Command: string list
      Run: string -> string list -> int * string
      Healthy: int -> bool
      Sleep: unit -> unit }

let private marker = "# org-cli managed user service v1"
let private name = "org-cli.service"

let unitTemplate () =
    use stream =
        typeof<Settings>.Assembly.GetManifestResourceStream("OrgCli.org-cli.service")

    use reader = new StreamReader(stream)
    reader.ReadToEnd()

let help () =
    printfn "org service install --directory PATH [--port 8765] [--mcp] [--read-only]"
    printfn "org service status|restart|stop|uninstall"
    printfn "org service run [--service-config PATH]   (used by systemd)"
    printfn "Installs an opt-in Linux user service. Does not enable lingering."
    printfn "Logs: journalctl --user -u org-cli -f"

let private fail message = invalidOp message

let private clean (value: string) =
    if String.IsNullOrWhiteSpace value || value |> Seq.exists Char.IsControl then
        fail "Paths must be nonempty and cannot contain control characters"

    value

/// systemd has its own quoting and specifier expansion; no shell is involved.
let quoteArgument value =
    "\""
    + (clean value).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("%", "%%").Replace("$", "$$")
    + "\""

let private quotePath value =
    "\""
    + (clean value).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("%", "%%")
    + "\""

let private configHome (host: Runtime.IHost) =
    match host.GetEnvironmentVariable "XDG_CONFIG_HOME" with
    | value when not (String.IsNullOrWhiteSpace value) && Path.IsPathRooted value -> clean value
    | _ -> Path.Combine(host.HomeDirectory, ".config")

let settingsPath host =
    Path.Combine(configHome host, "org-cli", "service.json")

let private readSettings (host: Runtime.IHost) file =
    if not (host.FileExists file) then
        fail "Service is not configured. Run org service install --directory PATH first."

    let cfg = JsonSerializer.Deserialize<Settings>(host.ReadBytes file)

    if isNull (box cfg) || cfg.ManagedBy <> "org-cli-service-v1" then
        fail "Unrecognised service configuration; refusing to replace or use it"

    if not (Path.IsPathRooted(clean cfg.Directory)) then
        fail "The configured Org directory is not absolute"

    if cfg.Port < 1 || cfg.Port > 65535 then
        fail "Port must be between 1 and 65535"

    cfg

let private checkedRun ctx args =
    let code, output = ctx.Run "systemctl" ("--user" :: args)

    if code <> 0 then
        fail (
            sprintf
                "systemctl --user %s failed: %s\nInspect journalctl --user -u org-cli. Configuration is retained for diagnosis."
                (String.concat " " args)
                (output.Trim())
        )

    output

let private checkUnitPath ctx unit =
    let paths = checkedRun ctx [ "show"; "--property=UnitPath"; "--value" ]
    let directory = Path.GetDirectoryName(unit: string)

    if not ((" " + paths.Trim() + " ").Contains(" " + directory + " ", StringComparison.Ordinal)) then
        fail
            "XDG_CONFIG_HOME does not match the systemd user manager's unit search path. Use your login session's configuration directory before installing or removing the service."

let private ensureDirectory (host: Runtime.IHost) path =
    let rec create path =
        if not (host.DirectoryExists path) then
            let parent = Path.GetDirectoryName path

            if parent <> path && not (String.IsNullOrEmpty parent) then
                create parent

            host.CreateDirectory path

    create path

let private write (host: Runtime.IHost) (path: string) text =
    ensureDirectory host (Path.GetDirectoryName path)
    let temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp"

    try
        host.WriteBytes(temp, Encoding.UTF8.GetBytes(text: string))
        host.MoveFile(temp, path, true)
    finally
        if host.FileExists temp then
            host.DeleteFile temp

let private assertManaged (host: Runtime.IHost) path =
    if
        host.FileExists path
        && not ((Encoding.UTF8.GetString(host.ReadBytes path)).StartsWith(marker + "\n", StringComparison.Ordinal))
    then
        fail (
            "Existing custom service file preserved: "
            + path
            + ". Use systemctl --user edit org-cli or move it aside explicitly."
        )

let private awaitReady ctx port =
    let mutable ready = false
    let mutable attempts = 0
    let mutable consecutive = 0

    while not ready && attempts < 20 do
        ctx.Sleep()
        let active, _ = ctx.Run "systemctl" [ "--user"; "is-active"; "--quiet"; name ]

        consecutive <-
            if active = 0 && ctx.Healthy port then
                consecutive + 1
            else
                0

        ready <- consecutive >= 2
        attempts <- attempts + 1

    if not ready then
        fail "Service did not become ready. Inspect systemctl --user status org-cli and journalctl --user -u org-cli."

    printfn "Org board: http://127.0.0.1:%d/" port

let execute ctx (opts: Map<string, string list>) positional =
    let option key =
        opts.TryFind key |> Option.bind List.tryHead

    let has key = opts.ContainsKey key

    let action =
        match positional with
        | [] -> "help"
        | [ value ] -> value
        | _ -> fail "Expected one service subcommand"

    if action = "help" || has "help" || has "h" then
        help ()
        0
    else
        if not ctx.IsLinux then
            fail "Service management currently supports Linux with systemd. Use org serve on this platform."

        let allowed =
            match action with
            | "install" -> [ "directory"; "d"; "port"; "mcp"; "read-only" ]
            | "run" -> [ "service-config" ]
            | "status"
            | "restart"
            | "stop"
            | "uninstall" -> []
            | _ -> fail "Unknown service subcommand; use org service --help"

        for KeyValue(key, values) in opts do
            if not (List.contains key allowed) || values.Length <> 1 then
                fail ("Unknown, repeated or inapplicable service option: --" + key)

        let host = ctx.Host
        let config = settingsPath host
        let unit = Path.Combine(configHome host, "systemd", "user", name)
        let dropin = unit + ".d/50-org-cli.conf"
        let vendor = "/usr/lib/systemd/user/" + name

        match action with
        | "run" ->
            let file =
                option "service-config"
                |> Option.defaultValue config
                |> fun p -> Path.GetFullPath(p, host.CurrentDirectory)

            let cfg = readSettings host file

            if not (host.DirectoryExists cfg.Directory) then
                fail "The configured Org directory no longer exists"

            let service =
                OrgCli.Index.Application.WorkspaceService(
                    host,
                    cfg.Directory,
                    Path.Combine(cfg.Directory, ".org-index.db"),
                    Config.load (),
                    readOnly = cfg.ReadOnly
                )

            let token =
                host.GetEnvironmentVariable "ORG_API_TOKEN"
                |> Option.ofObj
                |> Option.filter (String.IsNullOrWhiteSpace >> not)

            OrgCli.Server.runHttp service cfg.Port cfg.Mcp token
        | "status" ->
            let code, output = ctx.Run "systemctl" [ "--user"; "status"; "--no-pager"; name ]
            printf "%s" output
            code
        | "stop" ->
            checkedRun ctx [ "stop"; name ] |> ignore
            0
        | "restart" ->
            let cfg = readSettings host config

            if not (host.DirectoryExists cfg.Directory) then
                fail "The configured Org directory no longer exists"

            checkedRun ctx [ "restart"; name ] |> ignore
            awaitReady ctx cfg.Port
            0
        | "uninstall" ->
            // Refuse to remove a manually configured service, or someone else's config.
            assertManaged host unit
            assertManaged host dropin

            if host.FileExists config then
                readSettings host config |> ignore

            if host.FileExists unit || host.FileExists dropin then
                checkUnitPath ctx unit
                checkedRun ctx [ "disable"; "--now"; name ] |> ignore

                for file in [ dropin; unit; config ] do
                    if host.FileExists file then
                        host.DeleteFile file

                checkedRun ctx [ "daemon-reload" ] |> ignore

            printfn "User service configuration removed. Org files, service.env and packaged unit retained."
            0
        | _ ->
            let directory =
                option "directory"
                |> Option.orElse (option "d")
                |> Option.defaultWith (fun () ->
                    fail "Specify --directory PATH; installation never assumes a workspace")
                |> clean
                |> fun p -> Path.GetFullPath(p, host.CurrentDirectory)

            if not (host.DirectoryExists directory) then
                fail "The Org directory must already exist"

            let port =
                match Int32.TryParse(option "port" |> Option.defaultValue "8765") with
                | true, value when value > 0 && value <= 65535 -> value
                | _ -> fail "Port must be between 1 and 65535"

            if has "directory" && has "d" then
                fail "Use either --directory or -d, not both"

            let cfg =
                { ManagedBy = "org-cli-service-v1"
                  Directory = directory
                  Port = port
                  Mcp = has "mcp"
                  ReadOnly = has "read-only" }

            assertManaged host unit
            assertManaged host dropin

            if host.FileExists config then
                readSettings host config |> ignore

            if ctx.Command.IsEmpty then
                fail "Cannot determine executable path"

            let command =
                ctx.Command @ [ "service"; "run"; "--service-config"; config ]
                |> List.map quoteArgument
                |> String.concat " "
            // Preflight the user manager before writing anything. Never enable lingering.
            checkUnitPath ctx unit

            let packaged =
                host.FileExists vendor
                && Encoding.UTF8.GetString(host.ReadBytes vendor) = unitTemplate ()

            if not packaged || host.FileExists unit then
                write host unit (unitTemplate ())

            let env = Path.Combine(configHome host, "org-cli", "service.env")
            write host config (JsonSerializer.Serialize(cfg, JsonSerializerOptions(WriteIndented = true)))

            write
                host
                dropin
                (marker
                 + "\n[Service]\nExecStart=\nExecStart="
                 + command
                 + "\nEnvironment="
                 + quotePath ("XDG_CONFIG_HOME=" + configHome host)
                 + "\nEnvironmentFile=\nEnvironmentFile=-"
                 + quotePath env
                 + "\n")

            checkedRun ctx [ "daemon-reload" ] |> ignore
            checkedRun ctx [ "enable"; name ] |> ignore
            checkedRun ctx [ "restart"; name ] |> ignore
            awaitReady ctx port

            if cfg.Mcp then
                printfn "MCP: http://127.0.0.1:%d/mcp" port

            printfn
                "Starts with your user session. For boot/after-logout operation, explicitly enable lingering with loginctl enable-linger."

            0

let private runProcess command args =
    let info =
        ProcessStartInfo(command, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true)

    for arg in args do
        info.ArgumentList.Add arg

    use child = new Process(StartInfo = info)

    if not (child.Start()) then
        fail ("Cannot start " + command)

    let stdout = child.StandardOutput.ReadToEndAsync()
    let stderr = child.StandardError.ReadToEndAsync()

    if not (child.WaitForExit(30000)) then
        child.Kill(true)
        fail (command + " timed out")

    child.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult()

let run opts positional =
    try
        let child = Environment.ProcessPath

        let command =
            if String.IsNullOrEmpty child then
                []
            elif Path.GetFileNameWithoutExtension(child) = "dotnet" then
                [ child; typeof<Settings>.Assembly.Location ]
            else
                [ child ]

        use client = new HttpClient(Timeout = TimeSpan.FromSeconds 1.0)

        let healthy port =
            try
                use response =
                    client.GetAsync(sprintf "http://127.0.0.1:%d/health" port).GetAwaiter().GetResult()

                response.IsSuccessStatusCode
                || response.StatusCode = Net.HttpStatusCode.Unauthorized
            with _ ->
                false

        execute
            { Host = Runtime.host ()
              IsLinux = OperatingSystem.IsLinux()
              Command = command
              Run = runProcess
              Healthy = healthy
              Sleep = fun () -> Threading.Thread.Sleep 500 }
            opts
            positional
    with ex ->
        eprintfn "Service setup: %s" ex.Message
        1
