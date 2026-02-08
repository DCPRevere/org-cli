module ConfigTests

open System
open Xunit
open OrgCli.Org

module ConfigTypes =

    [<Fact>]
    let ``defaultConfig has expected TODO keywords`` () =
        let kws = Types.allKeywords Types.defaultTodoKeywords
        Assert.Contains("TODO", kws)
        Assert.Contains("DONE", kws)
        Assert.Contains("NEXT", kws)
        Assert.Contains("WAITING", kws)
        Assert.Contains("CANCELLED", kws)

    [<Fact>]
    let ``isActiveState identifies active states`` () =
        Assert.True(Types.isActiveState Types.defaultTodoKeywords "TODO")
        Assert.True(Types.isActiveState Types.defaultTodoKeywords "NEXT")
        Assert.False(Types.isActiveState Types.defaultTodoKeywords "DONE")
        Assert.False(Types.isActiveState Types.defaultTodoKeywords "CANCELLED")

    [<Fact>]
    let ``isDoneState identifies done states`` () =
        Assert.True(Types.isDoneState Types.defaultTodoKeywords "DONE")
        Assert.True(Types.isDoneState Types.defaultTodoKeywords "CANCELLED")
        Assert.False(Types.isDoneState Types.defaultTodoKeywords "TODO")
        Assert.False(Types.isDoneState Types.defaultTodoKeywords "NEXT")

    [<Fact>]
    let ``defaultConfig priorities are A-C with B default`` () =
        let cfg = Types.defaultConfig
        Assert.Equal('A', cfg.Priorities.Highest)
        Assert.Equal('C', cfg.Priorities.Lowest)
        Assert.Equal('B', cfg.Priorities.Default)

    [<Fact>]
    let ``defaultConfig has tag inheritance enabled`` () =
        Assert.True(Types.defaultConfig.TagInheritance)

    [<Fact>]
    let ``defaultConfig has property inheritance disabled`` () =
        Assert.False(Types.defaultConfig.PropertyInheritance)

    [<Fact>]
    let ``defaultConfig logs into LOGBOOK drawer`` () =
        Assert.Equal(Some "LOGBOOK", Types.defaultConfig.LogIntoDrawer)

    [<Fact>]
    let ``defaultConfig deadline warning is 14 days`` () =
        Assert.Equal(14, Types.defaultConfig.DeadlineWarningDays)

    [<Fact>]
    let ``defaultConfig LogRepeat is LogTime`` () =
        Assert.Equal(LogAction.LogTime, Types.defaultConfig.LogRepeat)

    [<Fact>]
    let ``allKeywords returns both active and done states`` () =
        let custom: TodoKeywordConfig =
            { ActiveStates =
                [ { Keyword = "OPEN"
                    LogOnEnter = LogAction.NoLog
                    LogOnLeave = LogAction.NoLog } ]
              DoneStates =
                [ { Keyword = "CLOSED"
                    LogOnEnter = LogAction.LogTime
                    LogOnLeave = LogAction.NoLog } ] }

        let all = Types.allKeywords custom
        Assert.Equal(2, all.Length)
        Assert.Equal("OPEN", all.[0])
        Assert.Equal("CLOSED", all.[1])

module ConfigLoading =
    open OrgCli.Org.Config

    [<Fact>]
    let ``parseLogAction returns NoLog for "none"`` () =
        Assert.Equal(Some LogAction.NoLog, parseLogAction "none")

    [<Fact>]
    let ``parseLogAction returns LogTime for "time"`` () =
        Assert.Equal(Some LogAction.LogTime, parseLogAction "time")

    [<Fact>]
    let ``parseLogAction returns LogNote for "note"`` () =
        Assert.Equal(Some LogAction.LogNote, parseLogAction "note")

    [<Fact>]
    let ``parseLogAction returns None for invalid input`` () =
        Assert.Equal(None, parseLogAction "invalid")
        Assert.Equal(None, parseLogAction "")

    [<Fact>]
    let ``parseLogAction is case-insensitive`` () =
        Assert.Equal(Some LogAction.LogTime, parseLogAction "Time")
        Assert.Equal(Some LogAction.LogNote, parseLogAction "NOTE")

    [<Fact>]
    let ``loadFromJson parses complete JSON`` () =
        let json =
            """
        {
          "todoKeywords": {
            "activeStates": [
              {"keyword": "TODO"},
              {"keyword": "NEXT", "logOnEnter": "time"}
            ],
            "doneStates": [
              {"keyword": "DONE", "logOnEnter": "time"}
            ]
          },
          "priorities": {"highest": "A", "lowest": "E", "default": "C"},
          "logDone": "note",
          "logRepeat": "time",
          "logIntoDrawer": "LOGBOOK",
          "tagInheritance": false,
          "deadlineWarningDays": 7,
          "archiveLocation": "%s_archive::"
        }
        """

        let result = loadFromJson json

        match result with
        | Error e -> failwith $"Expected Ok but got Error: {e}"
        | Ok cfg ->
            Assert.Equal(2, cfg.TodoKeywords.ActiveStates.Length)
            Assert.Equal("TODO", cfg.TodoKeywords.ActiveStates.[0].Keyword)
            Assert.Equal(LogAction.NoLog, cfg.TodoKeywords.ActiveStates.[0].LogOnEnter)
            Assert.Equal("NEXT", cfg.TodoKeywords.ActiveStates.[1].Keyword)
            Assert.Equal(LogAction.LogTime, cfg.TodoKeywords.ActiveStates.[1].LogOnEnter)
            Assert.Equal(1, cfg.TodoKeywords.DoneStates.Length)
            Assert.Equal("DONE", cfg.TodoKeywords.DoneStates.[0].Keyword)
            Assert.Equal(LogAction.LogTime, cfg.TodoKeywords.DoneStates.[0].LogOnEnter)
            Assert.Equal('A', cfg.Priorities.Highest)
            Assert.Equal('E', cfg.Priorities.Lowest)
            Assert.Equal('C', cfg.Priorities.Default)
            Assert.Equal(LogAction.LogNote, cfg.LogDone)
            Assert.Equal(LogAction.LogTime, cfg.LogRepeat)
            Assert.Equal(Some "LOGBOOK", cfg.LogIntoDrawer)
            Assert.False(cfg.TagInheritance)
            Assert.Equal(7, cfg.DeadlineWarningDays)
            Assert.Equal(Some "%s_archive::", cfg.ArchiveLocation)

    [<Fact>]
    let ``loadFromJson with partial JSON uses defaults for missing fields`` () =
        let json = """{"logDone": "time", "deadlineWarningDays": 30}"""
        let result = loadFromJson json

        match result with
        | Error e -> failwith $"Expected Ok but got Error: {e}"
        | Ok cfg ->
            Assert.Equal(LogAction.LogTime, cfg.LogDone)
            Assert.Equal(30, cfg.DeadlineWarningDays)
            // Missing fields should be defaults
            Assert.Equal(Types.defaultConfig.TodoKeywords.ActiveStates.Length, cfg.TodoKeywords.ActiveStates.Length)
            Assert.Equal(Types.defaultConfig.Priorities.Highest, cfg.Priorities.Highest)
            Assert.Equal(Types.defaultConfig.LogRepeat, cfg.LogRepeat)
            Assert.Equal(Types.defaultConfig.LogIntoDrawer, cfg.LogIntoDrawer)
            Assert.Equal(Types.defaultConfig.TagInheritance, cfg.TagInheritance)

    [<Fact>]
    let ``loadFromJson with empty object returns defaults`` () =
        let result = loadFromJson "{}"

        match result with
        | Error e -> failwith $"Expected Ok but got Error: {e}"
        | Ok cfg ->
            Assert.Equal(Types.defaultConfig.LogDone, cfg.LogDone)
            Assert.Equal(Types.defaultConfig.DeadlineWarningDays, cfg.DeadlineWarningDays)
            Assert.Equal(Types.defaultConfig.TagInheritance, cfg.TagInheritance)

    [<Fact>]
    let ``loadFromJson with invalid JSON returns Error`` () =
        let result = loadFromJson "not json at all"

        match result with
        | Ok _ -> failwith "Expected Error but got Ok"
        | Error _ -> ()

    [<Fact>]
    let ``loadFromJson parses todoKeywords with logOnLeave`` () =
        let json =
            """
        {
          "todoKeywords": {
            "activeStates": [
              {"keyword": "NEXT", "logOnEnter": "note", "logOnLeave": "time"}
            ],
            "doneStates": []
          }
        }
        """

        match loadFromJson json with
        | Error e -> failwith $"Expected Ok but got Error: {e}"
        | Ok cfg ->
            Assert.Equal(1, cfg.TodoKeywords.ActiveStates.Length)
            let st = cfg.TodoKeywords.ActiveStates.[0]
            Assert.Equal("NEXT", st.Keyword)
            Assert.Equal(LogAction.LogNote, st.LogOnEnter)
            Assert.Equal(LogAction.LogTime, st.LogOnLeave)

    [<Fact>]
    let ``loadFromJson with null logIntoDrawer sets None`` () =
        let json = """{"logIntoDrawer": null}"""

        match loadFromJson json with
        | Error e -> failwith $"Expected Ok but got Error: {e}"
        | Ok cfg -> Assert.Equal(None, cfg.LogIntoDrawer)

    [<Fact>]
    let ``loadFromFile with non-existent path returns Ok defaultConfig`` () =
        let result = loadFromFile "/tmp/org-cli-test-nonexistent-path/config.json"

        match result with
        | Error e -> failwith $"Expected Ok but got Error: {e}"
        | Ok cfg ->
            Assert.Equal(Types.defaultConfig.LogDone, cfg.LogDone)
            Assert.Equal(Types.defaultConfig.DeadlineWarningDays, cfg.DeadlineWarningDays)

    [<Fact>]
    let ``loadFromFile with valid file parses JSON`` () =
        let tmpFile = System.IO.Path.GetTempFileName()

        try
            System.IO.File.WriteAllText(tmpFile, """{"deadlineWarningDays": 21}""")

            match loadFromFile tmpFile with
            | Error e -> failwith $"Expected Ok but got Error: {e}"
            | Ok cfg ->
                Assert.Equal(21, cfg.DeadlineWarningDays)
                Assert.Equal(Types.defaultConfig.LogDone, cfg.LogDone)
        finally
            System.IO.File.Delete(tmpFile)

    [<Fact>]
    let ``loadFromEnv reads ORG_CLI_DEADLINE_WARNING_DAYS`` () =
        let key = "ORG_CLI_DEADLINE_WARNING_DAYS"
        let prev = System.Environment.GetEnvironmentVariable(key)

        try
            System.Environment.SetEnvironmentVariable(key, "21")
            let cfg = loadFromEnv ()
            Assert.Equal(21, cfg.DeadlineWarningDays)
        finally
            System.Environment.SetEnvironmentVariable(key, prev)

    [<Fact>]
    let ``loadFromEnv reads ORG_CLI_LOG_DONE`` () =
        let key = "ORG_CLI_LOG_DONE"
        let prev = System.Environment.GetEnvironmentVariable(key)

        try
            System.Environment.SetEnvironmentVariable(key, "note")
            let cfg = loadFromEnv ()
            Assert.Equal(LogAction.LogNote, cfg.LogDone)
        finally
            System.Environment.SetEnvironmentVariable(key, prev)

    [<Fact>]
    let ``loadFromEnv reads ORG_CLI_TAG_INHERITANCE`` () =
        let key = "ORG_CLI_TAG_INHERITANCE"
        let prev = System.Environment.GetEnvironmentVariable(key)

        try
            System.Environment.SetEnvironmentVariable(key, "false")
            let cfg = loadFromEnv ()
            Assert.False(cfg.TagInheritance)
        finally
            System.Environment.SetEnvironmentVariable(key, prev)

    [<Fact>]
    let ``loadFromEnv reads ORG_CLI_LOG_INTO_DRAWER`` () =
        let key = "ORG_CLI_LOG_INTO_DRAWER"
        let prev = System.Environment.GetEnvironmentVariable(key)

        try
            System.Environment.SetEnvironmentVariable(key, "MYLOG")
            let cfg = loadFromEnv ()
            Assert.Equal(Some "MYLOG", cfg.LogIntoDrawer)
        finally
            System.Environment.SetEnvironmentVariable(key, prev)

    [<Fact>]
    let ``loadFromEnv with empty LOG_INTO_DRAWER sets None`` () =
        let key = "ORG_CLI_LOG_INTO_DRAWER"
        let prev = System.Environment.GetEnvironmentVariable(key)

        try
            System.Environment.SetEnvironmentVariable(key, "")
            let cfg = loadFromEnv ()
            Assert.Equal(None, cfg.LogIntoDrawer)
        finally
            System.Environment.SetEnvironmentVariable(key, prev)

module ConfigOverlay =
    open OrgCli.Org.Config

    [<Fact>]
    let ``env var set to default value overrides file config`` () =
        // File config sets LogDone=NoLog. Env sets ORG_CLI_LOG_DONE=time (the default).
        // The env var should win, producing LogTime — not NoLog.
        let key = "ORG_CLI_LOG_DONE"
        let prev = System.Environment.GetEnvironmentVariable(key)

        try
            System.Environment.SetEnvironmentVariable(key, "time")

            let fileCfg =
                { Types.defaultConfig with
                    LogDone = LogAction.NoLog }

            let result = overlayEnv fileCfg
            Assert.Equal(LogAction.LogTime, result.LogDone)
        finally
            System.Environment.SetEnvironmentVariable(key, prev)

    [<Fact>]
    let ``env var not set preserves file config`` () =
        let key = "ORG_CLI_LOG_DONE"
        let prev = System.Environment.GetEnvironmentVariable(key)

        try
            System.Environment.SetEnvironmentVariable(key, null)

            let fileCfg =
                { Types.defaultConfig with
                    LogDone = LogAction.NoLog }

            let result = overlayEnv fileCfg
            Assert.Equal(LogAction.NoLog, result.LogDone)
        finally
            System.Environment.SetEnvironmentVariable(key, prev)

    [<Fact>]
    let ``env DeadlineWarningDays set to default overrides file config`` () =
        let key = "ORG_CLI_DEADLINE_WARNING_DAYS"
        let prev = System.Environment.GetEnvironmentVariable(key)

        try
            System.Environment.SetEnvironmentVariable(key, "14")

            let fileCfg =
                { Types.defaultConfig with
                    DeadlineWarningDays = 30 }

            let result = overlayEnv fileCfg
            Assert.Equal(14, result.DeadlineWarningDays)
        finally
            System.Environment.SetEnvironmentVariable(key, prev)

    [<Fact>]
    let ``env TagInheritance set to default overrides file config`` () =
        let key = "ORG_CLI_TAG_INHERITANCE"
        let prev = System.Environment.GetEnvironmentVariable(key)

        try
            System.Environment.SetEnvironmentVariable(key, "true")

            let fileCfg =
                { Types.defaultConfig with
                    TagInheritance = false }

            let result = overlayEnv fileCfg
            Assert.True(result.TagInheritance)
        finally
            System.Environment.SetEnvironmentVariable(key, prev)
