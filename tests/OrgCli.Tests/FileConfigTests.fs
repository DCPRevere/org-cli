module FileConfigTests

open Xunit
open OrgCli.Org

module ParseTodoLine =

    [<Fact>]
    let ``pipe separates active from done states`` () =
        let cfg = FileConfig.parseTodoLine "TODO WAIT | DONE CANCELLED"
        Assert.Equal(2, cfg.ActiveStates.Length)
        Assert.Equal(2, cfg.DoneStates.Length)
        Assert.Equal("TODO", cfg.ActiveStates.[0].Keyword)
        Assert.Equal("WAIT", cfg.ActiveStates.[1].Keyword)
        Assert.Equal("DONE", cfg.DoneStates.[0].Keyword)
        Assert.Equal("CANCELLED", cfg.DoneStates.[1].Keyword)

    [<Fact>]
    let ``all states default to NoLog when no parens`` () =
        let cfg = FileConfig.parseTodoLine "TODO WAIT | DONE CANCELLED"
        for d in cfg.ActiveStates @ cfg.DoneStates do
            Assert.Equal(LogAction.NoLog, d.LogOnEnter)
            Assert.Equal(LogAction.NoLog, d.LogOnLeave)

    [<Fact>]
    let ``keyword logging indicators are parsed correctly`` () =
        let cfg = FileConfig.parseTodoLine "TODO(t) WAIT(w@/!) | DONE(d!) CANCELLED(c@)"
        // TODO(t) - fast key only, no logging
        Assert.Equal(LogAction.NoLog, cfg.ActiveStates.[0].LogOnEnter)
        Assert.Equal(LogAction.NoLog, cfg.ActiveStates.[0].LogOnLeave)
        // WAIT(w@/!) - @ before / = LogNote enter, ! after / = LogTime leave
        Assert.Equal(LogAction.LogNote, cfg.ActiveStates.[1].LogOnEnter)
        Assert.Equal(LogAction.LogTime, cfg.ActiveStates.[1].LogOnLeave)
        // DONE(d!) - ! with no / = LogTime enter
        Assert.Equal(LogAction.LogTime, cfg.DoneStates.[0].LogOnEnter)
        Assert.Equal(LogAction.NoLog, cfg.DoneStates.[0].LogOnLeave)
        // CANCELLED(c@) - @ with no / = LogNote enter
        Assert.Equal(LogAction.LogNote, cfg.DoneStates.[1].LogOnEnter)
        Assert.Equal(LogAction.NoLog, cfg.DoneStates.[1].LogOnLeave)

    [<Fact>]
    let ``no pipe makes last word sole done state`` () =
        let cfg = FileConfig.parseTodoLine "TODO DOING DONE"
        Assert.Equal(2, cfg.ActiveStates.Length)
        Assert.Equal(1, cfg.DoneStates.Length)
        Assert.Equal("TODO", cfg.ActiveStates.[0].Keyword)
        Assert.Equal("DOING", cfg.ActiveStates.[1].Keyword)
        Assert.Equal("DONE", cfg.DoneStates.[0].Keyword)

    [<Fact>]
    let ``minimal pipe case`` () =
        let cfg = FileConfig.parseTodoLine "TODO | DONE"
        Assert.Equal(1, cfg.ActiveStates.Length)
        Assert.Equal(1, cfg.DoneStates.Length)
        Assert.Equal("TODO", cfg.ActiveStates.[0].Keyword)
        Assert.Equal("DONE", cfg.DoneStates.[0].Keyword)

    [<Fact>]
    let ``logging with only slash and bang after fast key`` () =
        // (w/!) means no enter logging, leave = LogTime
        let cfg = FileConfig.parseTodoLine "WAIT(w/!) | DONE"
        Assert.Equal(LogAction.NoLog, cfg.ActiveStates.[0].LogOnEnter)
        Assert.Equal(LogAction.LogTime, cfg.ActiveStates.[0].LogOnLeave)

    [<Fact>]
    let ``logging with at and slash but nothing after`` () =
        // (w@/) means enter = LogNote, leave = NoLog
        let cfg = FileConfig.parseTodoLine "WAIT(w@/) | DONE"
        Assert.Equal(LogAction.LogNote, cfg.ActiveStates.[0].LogOnEnter)
        Assert.Equal(LogAction.NoLog, cfg.ActiveStates.[0].LogOnLeave)

module ParseStartupOptions =

    [<Fact>]
    let ``logdone and logrepeat`` () =
        let opts = FileConfig.parseStartupOptions "logdone logrepeat"
        Assert.Equal(Some LogAction.LogTime, opts.LogDone)
        Assert.Equal(Some LogAction.LogTime, opts.LogRepeat)

    [<Fact>]
    let ``nologdone and lognoterepeat`` () =
        let opts = FileConfig.parseStartupOptions "nologdone lognoterepeat"
        Assert.Equal(Some LogAction.NoLog, opts.LogDone)
        Assert.Equal(Some LogAction.LogNote, opts.LogRepeat)

    [<Fact>]
    let ``lognotedone`` () =
        let opts = FileConfig.parseStartupOptions "lognotedone"
        Assert.Equal(Some LogAction.LogNote, opts.LogDone)
        Assert.Equal(None, opts.LogRepeat)

    [<Fact>]
    let ``unknown words are ignored`` () =
        let opts = FileConfig.parseStartupOptions "overview indent"
        Assert.Equal(None, opts.LogDone)
        Assert.Equal(None, opts.LogRepeat)

    [<Fact>]
    let ``nologrepeat`` () =
        let opts = FileConfig.parseStartupOptions "nologrepeat"
        Assert.Equal(None, opts.LogDone)
        Assert.Equal(Some LogAction.NoLog, opts.LogRepeat)

module ParsePriorities =

    [<Fact>]
    let ``three single chars`` () =
        let result = FileConfig.parsePriorities "A E C"
        Assert.True(result.IsSome)
        let p = result.Value
        Assert.Equal('A', p.Highest)
        Assert.Equal('E', p.Lowest)
        Assert.Equal('C', p.Default)

    [<Fact>]
    let ``malformed input returns None`` () =
        Assert.True((FileConfig.parsePriorities "bad input").IsNone)

    [<Fact>]
    let ``too few tokens returns None`` () =
        Assert.True((FileConfig.parsePriorities "A B").IsNone)

    [<Fact>]
    let ``multi-char tokens returns None`` () =
        Assert.True((FileConfig.parsePriorities "AB CD EF").IsNone)

module MergeFileConfig =

    [<Fact>]
    let ``todo keywords from file replace base config`` () =
        let baseConfig = Types.defaultConfig
        let keywords = [
            { Key = "TODO"; Value = "OPEN INPROGRESS | CLOSED" }
        ]
        let merged = FileConfig.mergeFileConfig baseConfig keywords
        let allKws = Types.allKeywords merged.TodoKeywords
        Assert.Equal(3, allKws.Length)
        Assert.Equal("OPEN", allKws.[0])
        Assert.Equal("INPROGRESS", allKws.[1])
        Assert.Equal("CLOSED", allKws.[2])

    [<Fact>]
    let ``seq_todo is treated same as todo`` () =
        let baseConfig = Types.defaultConfig
        let keywords = [
            { Key = "SEQ_TODO"; Value = "ALPHA | OMEGA" }
        ]
        let merged = FileConfig.mergeFileConfig baseConfig keywords
        Assert.Equal(1, merged.TodoKeywords.ActiveStates.Length)
        Assert.Equal("ALPHA", merged.TodoKeywords.ActiveStates.[0].Keyword)
        Assert.Equal(1, merged.TodoKeywords.DoneStates.Length)
        Assert.Equal("OMEGA", merged.TodoKeywords.DoneStates.[0].Keyword)

    [<Fact>]
    let ``startup logdone overrides base config`` () =
        let baseConfig = { Types.defaultConfig with LogDone = LogAction.NoLog }
        let keywords = [
            { Key = "STARTUP"; Value = "logdone" }
        ]
        let merged = FileConfig.mergeFileConfig baseConfig keywords
        Assert.Equal(LogAction.LogTime, merged.LogDone)

    [<Fact>]
    let ``priorities from file override base config`` () =
        let baseConfig = Types.defaultConfig
        let keywords = [
            { Key = "PRIORITIES"; Value = "A Z M" }
        ]
        let merged = FileConfig.mergeFileConfig baseConfig keywords
        Assert.Equal('A', merged.Priorities.Highest)
        Assert.Equal('Z', merged.Priorities.Lowest)
        Assert.Equal('M', merged.Priorities.Default)

    [<Fact>]
    let ``archive location from file`` () =
        let baseConfig = Types.defaultConfig
        let keywords = [
            { Key = "ARCHIVE"; Value = "archive/%s_archive::" }
        ]
        let merged = FileConfig.mergeFileConfig baseConfig keywords
        Assert.Equal(Some "archive/%s_archive::", merged.ArchiveLocation)

    [<Fact>]
    let ``no relevant keywords returns base config unchanged`` () =
        let baseConfig = Types.defaultConfig
        let keywords = [
            { Key = "TITLE"; Value = "My Document" }
            { Key = "AUTHOR"; Value = "Test" }
        ]
        let merged = FileConfig.mergeFileConfig baseConfig keywords
        Assert.Equal(baseConfig.TodoKeywords.ActiveStates.Length, merged.TodoKeywords.ActiveStates.Length)
        Assert.Equal(baseConfig.Priorities, merged.Priorities)
        Assert.Equal(baseConfig.LogDone, merged.LogDone)
        Assert.Equal(baseConfig.ArchiveLocation, merged.ArchiveLocation)

    [<Fact>]
    let ``multiple todo lines are combined`` () =
        let baseConfig = Types.defaultConfig
        let keywords = [
            { Key = "TODO"; Value = "TODO | DONE" }
            { Key = "TODO"; Value = "OPEN | CLOSED" }
        ]
        let merged = FileConfig.mergeFileConfig baseConfig keywords
        let allKws = Types.allKeywords merged.TodoKeywords
        Assert.Equal(4, allKws.Length)
        Assert.Contains("TODO", allKws)
        Assert.Contains("DONE", allKws)
        Assert.Contains("OPEN", allKws)
        Assert.Contains("CLOSED", allKws)

    [<Fact>]
    let ``startup logrepeat overrides base config`` () =
        let baseConfig = Types.defaultConfig
        let keywords = [
            { Key = "STARTUP"; Value = "lognoterepeat" }
        ]
        let merged = FileConfig.mergeFileConfig baseConfig keywords
        Assert.Equal(LogAction.LogNote, merged.LogRepeat)

module DynamicKeywordParsing =

    [<Fact>]
    let ``Document.parse recognizes custom TODO keywords from file header`` () =
        let content = "#+TODO: OPEN INPROGRESS | CLOSED\n* OPEN My task\n* INPROGRESS Another\n* CLOSED Done task\n"
        let doc = Document.parse content
        Assert.Equal(3, doc.Headlines.Length)
        Assert.Equal(Some "OPEN", doc.Headlines.[0].TodoKeyword)
        Assert.Equal(Some "INPROGRESS", doc.Headlines.[1].TodoKeyword)
        Assert.Equal(Some "CLOSED", doc.Headlines.[2].TodoKeyword)

    [<Fact>]
    let ``Document.parse still works with default keywords when no #+TODO`` () =
        let content = "* TODO My task\n* DONE Finished\n"
        let doc = Document.parse content
        Assert.Equal(2, doc.Headlines.Length)
        Assert.Equal(Some "TODO", doc.Headlines.[0].TodoKeyword)
        Assert.Equal(Some "DONE", doc.Headlines.[1].TodoKeyword)

    [<Fact>]
    let ``Document.parse does not treat default keywords as TODO when custom keywords defined`` () =
        let content = "#+TODO: OPEN | CLOSED\n* TODO Not a keyword anymore\n"
        let doc = Document.parse content
        Assert.Equal(1, doc.Headlines.Length)
        Assert.Equal(None, doc.Headlines.[0].TodoKeyword)
        Assert.Equal("TODO Not a keyword anymore", doc.Headlines.[0].Title)

    [<Fact>]
    let ``Document.parse handles multiple #+TODO lines`` () =
        let content = "#+TODO: TODO | DONE\n#+TODO: OPEN | CLOSED\n* TODO Task\n* OPEN Another\n"
        let doc = Document.parse content
        Assert.Equal(Some "TODO", doc.Headlines.[0].TodoKeyword)
        Assert.Equal(Some "OPEN", doc.Headlines.[1].TodoKeyword)

    [<Fact>]
    let ``Document.parse handles #+SEQ_TODO`` () =
        let content = "#+SEQ_TODO: ALPHA BETA | GAMMA\n* ALPHA First\n* GAMMA Last\n"
        let doc = Document.parse content
        Assert.Equal(Some "ALPHA", doc.Headlines.[0].TodoKeyword)
        Assert.Equal(Some "GAMMA", doc.Headlines.[1].TodoKeyword)

    [<Fact>]
    let ``HeadlineEdit uses dynamic keywords for getState`` () =
        let keywords = ["OPEN"; "CLOSED"]
        let line = "* OPEN My task"
        let state = HeadlineEdit.getStateWith keywords line
        Assert.Equal(Some "OPEN", state)

    [<Fact>]
    let ``HeadlineEdit uses dynamic keywords for replaceKeyword`` () =
        let keywords = ["OPEN"; "CLOSED"]
        let line = "* OPEN My task"
        let result = HeadlineEdit.replaceKeywordWith keywords line (Some "CLOSED")
        Assert.Equal("* CLOSED My task", result)

    [<Fact>]
    let ``HeadlineEdit dynamic getState returns None for unknown keyword`` () =
        let keywords = ["OPEN"; "CLOSED"]
        let line = "* TODO My task"
        let state = HeadlineEdit.getStateWith keywords line
        Assert.Equal(None, state)

    [<Fact>]
    let ``Document.parse with keyword logging parens strips them from keyword names`` () =
        let content = "#+TODO: TODO(t) | DONE(d!)\n* TODO My task\n"
        let doc = Document.parse content
        Assert.Equal(Some "TODO", doc.Headlines.[0].TodoKeyword)
