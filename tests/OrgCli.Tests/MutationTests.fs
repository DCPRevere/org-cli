module MutationTests

open System
open Xunit
open OrgCli.Org

let private now = DateTime(2026, 2, 5, 14, 30, 0)
let private dayName (d: DateTime) = d.ToString("ddd").Substring(0, 3)

// --- TODO state changes ---

[<Fact>]
let ``setTodoState adds TODO to plain headline`` () =
    let content = "* My headline\nBody\n"
    let result = Mutations.setTodoState Types.defaultConfig content 0L (Some "TODO") now
    Assert.StartsWith("* TODO My headline", result)
    Assert.Contains("Body", result)

[<Fact>]
let ``setTodoState changes TODO to DONE and inserts CLOSED timestamp`` () =
    let content = "* TODO My task\nBody\n"
    let result = Mutations.setTodoState Types.defaultConfig content 0L (Some "DONE") now
    Assert.StartsWith("* DONE My task", result)
    Assert.Contains("CLOSED:", result)
    Assert.Contains("[2026-02-05", result)

[<Fact>]
let ``setTodoState changes DONE to TODO and removes CLOSED`` () =
    let content =
        sprintf "* DONE My task\nCLOSED: [2026-02-05 %s 14:30]\nBody\n" (dayName now)

    let result = Mutations.setTodoState Types.defaultConfig content 0L (Some "TODO") now
    Assert.StartsWith("* TODO My task", result)
    Assert.DoesNotContain("CLOSED:", result)
    Assert.Contains("Body", result)

[<Fact>]
let ``setTodoState on headline with existing planning preserves other planning`` () =
    let content =
        sprintf "* TODO My task\nSCHEDULED: <2026-02-05 %s>\nBody\n" (dayName now)

    let result = Mutations.setTodoState Types.defaultConfig content 0L (Some "DONE") now
    Assert.StartsWith("* DONE My task", result)
    Assert.Contains("SCHEDULED:", result)
    Assert.Contains("CLOSED:", result)

[<Fact>]
let ``setTodoState removes TODO keyword with None`` () =
    let content = "* TODO My task\nBody\n"
    let result = Mutations.setTodoState Types.defaultConfig content 0L None now
    Assert.StartsWith("* My task", result)
    // Headline should not contain TODO (logbook entry will)
    let headlineLine = result.Split('\n').[0]
    Assert.DoesNotContain("TODO", headlineLine)

[<Fact>]
let ``setTodoState preserves rest of file`` () =
    let content = "* TODO First\nBody1\n* Second\nBody2\n"
    let result = Mutations.setTodoState Types.defaultConfig content 0L (Some "DONE") now
    Assert.Contains("* Second", result)
    Assert.Contains("Body2", result)

[<Fact>]
let ``setTodoState preserves tags`` () =
    let content = "* TODO Tagged task :work:urgent:\nBody\n"
    let result = Mutations.setTodoState Types.defaultConfig content 0L (Some "DONE") now
    Assert.Contains(":work:urgent:", result)

// --- Schedule ---

[<Fact>]
let ``setScheduled adds SCHEDULED to headline with no planning`` () =
    let ts =
        { Type = TimestampType.Active
          Date = DateTime(2026, 2, 10)
          HasTime = false
          Repeater = None
          Delay = None
          RangeEnd = None }

    let content = "* TODO My task\nBody\n"
    let result = Mutations.setScheduled Types.defaultConfig content 0L (Some ts) now
    Assert.Contains("SCHEDULED:", result)
    Assert.Contains("<2026-02-10", result)

[<Fact>]
let ``setScheduled adds SCHEDULED to headline with existing DEADLINE`` () =
    let ts =
        { Type = TimestampType.Active
          Date = DateTime(2026, 2, 10)
          HasTime = false
          Repeater = None
          Delay = None
          RangeEnd = None }

    let content =
        sprintf "* TODO My task\nDEADLINE: <2026-02-15 %s>\nBody\n" (dayName (DateTime(2026, 2, 15)))

    let result = Mutations.setScheduled Types.defaultConfig content 0L (Some ts) now
    Assert.Contains("SCHEDULED:", result)
    Assert.Contains("DEADLINE:", result)

[<Fact>]
let ``setScheduled updates existing SCHEDULED`` () =
    let ts =
        { Type = TimestampType.Active
          Date = DateTime(2026, 2, 20)
          HasTime = false
          Repeater = None
          Delay = None
          RangeEnd = None }

    let content =
        sprintf "* TODO My task\nSCHEDULED: <2026-02-10 %s>\nBody\n" (dayName (DateTime(2026, 2, 10)))

    let result = Mutations.setScheduled Types.defaultConfig content 0L (Some ts) now
    Assert.Contains("<2026-02-20", result)
    Assert.DoesNotContain("<2026-02-10", result)

[<Fact>]
let ``setScheduled with None removes SCHEDULED`` () =
    let content =
        sprintf "* TODO My task\nSCHEDULED: <2026-02-10 %s>\nBody\n" (dayName (DateTime(2026, 2, 10)))

    let result = Mutations.setScheduled Types.defaultConfig content 0L None now
    Assert.DoesNotContain("SCHEDULED:", result)
    Assert.Contains("Body", result)

// --- Deadline ---

[<Fact>]
let ``setDeadline adds DEADLINE to headline with no planning`` () =
    let ts =
        { Type = TimestampType.Active
          Date = DateTime(2026, 2, 15)
          HasTime = false
          Repeater = None
          Delay = None
          RangeEnd = None }

    let content = "* TODO My task\nBody\n"
    let result = Mutations.setDeadline Types.defaultConfig content 0L (Some ts) now
    Assert.Contains("DEADLINE:", result)
    Assert.Contains("<2026-02-15", result)

[<Fact>]
let ``setDeadline with None removes DEADLINE`` () =
    let content =
        sprintf "* TODO My task\nDEADLINE: <2026-02-15 %s>\nBody\n" (dayName (DateTime(2026, 2, 15)))

    let result = Mutations.setDeadline Types.defaultConfig content 0L None now
    Assert.DoesNotContain("DEADLINE:", result)

[<Fact>]
let ``set both scheduled and deadline`` () =
    let sched =
        { Type = TimestampType.Active
          Date = DateTime(2026, 2, 10)
          HasTime = false
          Repeater = None
          Delay = None
          RangeEnd = None }

    let dead =
        { Type = TimestampType.Active
          Date = DateTime(2026, 2, 15)
          HasTime = false
          Repeater = None
          Delay = None
          RangeEnd = None }

    let content = "* TODO My task\nBody\n"

    let result =
        content
        |> fun c -> Mutations.setScheduled Types.defaultConfig c 0L (Some sched) now
        |> fun c -> Mutations.setDeadline Types.defaultConfig c 0L (Some dead) now

    Assert.Contains("SCHEDULED:", result)
    Assert.Contains("DEADLINE:", result)

// --- Refile ---

[<Fact>]
let ``refile subtree within same file`` () =
    let content = "* Source\nSource body\n* Target\nTarget body\n"
    let srcPos = 0L
    let tgtPos = content.IndexOf("* Target") |> int64

    let (newSrc, _) =
        Mutations.refile Types.defaultConfig content srcPos content tgtPos true now

    Assert.DoesNotContain("* Source", newSrc.Substring(0, newSrc.IndexOf("* Target")))
    Assert.Contains("** Source", newSrc)
    Assert.Contains("Source body", newSrc)

[<Fact>]
let ``refile subtree to different file`` () =
    let src = "* Source\nSource body\n* Remaining\n"
    let tgt = "* Target\nTarget body\n"
    let (newSrc, newTgt) = Mutations.refile Types.defaultConfig src 0L tgt 0L false now
    Assert.DoesNotContain("Source", newSrc)
    Assert.Contains("* Remaining", newSrc)
    Assert.Contains("** Source", newTgt)
    Assert.Contains("Source body", newTgt)

[<Fact>]
let ``refile preserves children`` () =
    let src = "* Parent\nBody\n** Child\nChild body\n* Other\n"
    let tgt = "* Target\n"
    let (newSrc, newTgt) = Mutations.refile Types.defaultConfig src 0L tgt 0L false now
    Assert.DoesNotContain("Parent", newSrc)
    Assert.DoesNotContain("Child", newSrc)
    Assert.Contains("** Parent", newTgt)
    Assert.Contains("*** Child", newTgt)

[<Fact>]
let ``refile adjusts levels to target depth`` () =
    let src = "* Source\n"
    let tgt = "** Deep Target\n"
    let (_, newTgt) = Mutations.refile Types.defaultConfig src 0L tgt 0L false now
    Assert.Contains("*** Source", newTgt)

[<Fact>]
let ``setTodoState on second headline at non-zero position`` () =
    let content = "* First\nBody1\n* TODO Second\nBody2\n"
    let pos = content.IndexOf("* TODO Second") |> int64

    let result =
        Mutations.setTodoState Types.defaultConfig content pos (Some "DONE") now

    Assert.Contains("* First", result)
    Assert.Contains("Body1", result)
    Assert.Contains("* DONE Second", result)
    Assert.Contains("CLOSED:", result)

[<Fact>]
let ``setTodoState TODO to NEXT does not add CLOSED`` () =
    let content = "* TODO My task\nBody\n"
    let result = Mutations.setTodoState Types.defaultConfig content 0L (Some "NEXT") now
    Assert.StartsWith("* NEXT My task", result)
    Assert.DoesNotContain("CLOSED:", result)

[<Fact>]
let ``setTodoState DONE to NEXT removes CLOSED`` () =
    let content =
        sprintf "* DONE My task\nCLOSED: [2026-02-05 %s 14:30]\nBody\n" (dayName now)

    let result = Mutations.setTodoState Types.defaultConfig content 0L (Some "NEXT") now
    Assert.StartsWith("* NEXT My task", result)
    Assert.DoesNotContain("CLOSED:", result)
    Assert.Contains("Body", result)

[<Fact>]
let ``refile source after target in same file`` () =
    let content = "* Target\nTarget body\n* Source\nSource body\n"
    let srcPos = content.IndexOf("* Source") |> int64
    let tgtPos = 0L

    let (result, _) =
        Mutations.refile Types.defaultConfig content srcPos content tgtPos true now

    Assert.Contains("** Source", result)
    Assert.Contains("Source body", result)
    Assert.Contains("* Target", result)
    Assert.Contains("Target body", result)

[<Fact>]
let ``setTodoState preserves property drawer`` () =
    let content = "* TODO My task\n:PROPERTIES:\n:ID: abc123\n:END:\nBody\n"
    let result = Mutations.setTodoState Types.defaultConfig content 0L (Some "DONE") now
    Assert.Contains(":PROPERTIES:", result)
    Assert.Contains(":ID: abc123", result)
    Assert.Contains(":END:", result)
    Assert.Contains("CLOSED:", result)
    Assert.StartsWith("* DONE My task", result)

[<Fact>]
let ``setScheduled preserves property drawer`` () =
    let ts =
        { Type = TimestampType.Active
          Date = DateTime(2026, 2, 10)
          HasTime = false
          Repeater = None
          Delay = None
          RangeEnd = None }

    let content = "* TODO My task\n:PROPERTIES:\n:ID: abc123\n:END:\nBody\n"
    let result = Mutations.setScheduled Types.defaultConfig content 0L (Some ts) now
    Assert.Contains(":PROPERTIES:", result)
    Assert.Contains(":ID: abc123", result)
    Assert.Contains("SCHEDULED:", result)

[<Fact>]
let ``setTodoState does not produce double space without priority`` () =
    let content = "* TODO Simple task\nBody\n"
    let result = Mutations.setTodoState Types.defaultConfig content 0L (Some "DONE") now
    Assert.DoesNotContain("DONE  ", result)
    Assert.Contains("* DONE Simple task", result)

[<Fact>]
let ``setTodoState with priority preserves priority`` () =
    let content = "* TODO [#A] Important task\nBody\n"
    let result = Mutations.setTodoState Types.defaultConfig content 0L (Some "DONE") now
    Assert.Contains("[#A]", result)
    Assert.Contains("* DONE", result)
    Assert.Contains("Important task", result)

[<Fact>]
let ``setScheduled on headline with existing SCHEDULED and property drawer`` () =
    let ts =
        { Type = TimestampType.Active
          Date = DateTime(2026, 2, 20)
          HasTime = false
          Repeater = None
          Delay = None
          RangeEnd = None }

    let content =
        sprintf
            "* TODO My task\nSCHEDULED: <2026-02-10 %s>\n:PROPERTIES:\n:ID: abc123\n:END:\nBody\n"
            (dayName (DateTime(2026, 2, 10)))

    let result = Mutations.setScheduled Types.defaultConfig content 0L (Some ts) now
    Assert.Contains("<2026-02-20", result)
    Assert.DoesNotContain("<2026-02-10", result)
    Assert.Contains(":PROPERTIES:", result)
    Assert.Contains(":ID: abc123", result)

// --- Archive ---

[<Fact>]
let ``archive removes subtree from source`` () =
    let content = "* Task to archive\nBody\n* Remaining\n"
    let (newSrc, _) = Mutations.archive content 0L "" "/path/to/file.org" [] now
    Assert.DoesNotContain("Task to archive", newSrc)
    Assert.Contains("* Remaining", newSrc)

[<Fact>]
let ``archive stamps subtree with archive properties`` () =
    let content = "* TODO Task to archive\nBody\n"

    let (_, newArchive) =
        Mutations.archive content 0L "" "/path/to/file.org" [ "Projects"; "Website" ] now

    Assert.Contains(":PROPERTIES:", newArchive)
    Assert.Contains(":ARCHIVE_TIME:", newArchive)
    Assert.Contains(":ARCHIVE_FILE: /path/to/file.org", newArchive)
    Assert.Contains(":ARCHIVE_OLPATH: Projects/Website", newArchive)
    Assert.Contains(":ARCHIVE_CATEGORY: file", newArchive)
    Assert.Contains(":ARCHIVE_TODO: TODO", newArchive)
    Assert.Contains(":END:", newArchive)

[<Fact>]
let ``archive adjusts subtree to level 1`` () =
    let content = "* Parent\n** Child to archive\nChild body\n"
    let pos = content.IndexOf("** Child") |> int64

    let (_, newArchive) =
        Mutations.archive content pos "" "/path/file.org" [ "Parent" ] now

    Assert.StartsWith("* Child to archive", newArchive)

[<Fact>]
let ``archive omits ARCHIVE_TODO when headline has no state`` () =
    let content = "* Plain headline\nBody\n"
    let (_, newArchive) = Mutations.archive content 0L "" "/path/file.org" [] now
    Assert.DoesNotContain("ARCHIVE_TODO", newArchive)

[<Fact>]
let ``archive appends to existing archive content`` () =
    let content = "* New task\nBody\n"
    let existingArchive = "* Old archived task\nOld body\n"

    let (_, newArchive) =
        Mutations.archive content 0L existingArchive "/path/file.org" [] now

    Assert.Contains("* Old archived task", newArchive)
    Assert.Contains("* New task", newArchive)

[<Fact>]
let ``archive preserves children`` () =
    let content = "* Parent\nBody\n** Child\nChild body\n* Other\n"
    let (newSrc, newArchive) = Mutations.archive content 0L "" "/path/file.org" [] now
    Assert.Contains("** Child", newArchive)
    Assert.Contains("Child body", newArchive)
    Assert.DoesNotContain("Child", newSrc)
    Assert.Contains("* Other", newSrc)

[<Fact>]
let ``archive preserves existing property drawer`` () =
    let content = "* TODO Task\n:PROPERTIES:\n:ID: abc123\n:END:\nBody\n"
    let (_, newArchive) = Mutations.archive content 0L "" "/path/file.org" [] now
    Assert.Contains(":ID: abc123", newArchive)
    Assert.Contains(":ARCHIVE_FILE:", newArchive)
    Assert.Contains(":ARCHIVE_TODO: TODO", newArchive)

[<Fact>]
let ``archive preserves planning line`` () =
    let content =
        sprintf "* TODO Task\nSCHEDULED: <2026-02-10 %s>\nBody\n" (dayName (DateTime(2026, 2, 10)))

    let (_, newArchive) = Mutations.archive content 0L "" "/path/file.org" [] now
    Assert.Contains("SCHEDULED:", newArchive)
    Assert.Contains(":ARCHIVE_TIME:", newArchive)

[<Fact>]
let ``archive with empty outline path sets empty ARCHIVE_OLPATH`` () =
    let content = "* Top level task\nBody\n"
    let (_, newArchive) = Mutations.archive content 0L "" "/path/file.org" [] now
    Assert.Contains(":ARCHIVE_OLPATH: ", newArchive)

// --- Repeater parsing ---

[<Fact>]
let ``parseRepeater parses standard repeater +1d`` () =
    let result = Mutations.parseRepeater "+1d"
    Assert.Equal(Some(RepeaterType.Standard, 1, 'd'), result)

[<Fact>]
let ``parseRepeater parses from-today repeater .+2w`` () =
    let result = Mutations.parseRepeater ".+2w"
    Assert.Equal(Some(RepeaterType.FromToday, 2, 'w'), result)

[<Fact>]
let ``parseRepeater parses next-future repeater ++1m`` () =
    let result = Mutations.parseRepeater "++1m"
    Assert.Equal(Some(RepeaterType.NextFuture, 1, 'm'), result)

[<Fact>]
let ``parseRepeater returns None for invalid string`` () =
    Assert.Equal(None, Mutations.parseRepeater "foo")

// --- Timestamp shifting ---

[<Fact>]
let ``shiftTimestamp +1d shifts by 1 day from original date`` () =
    let ts =
        { Type = TimestampType.Active
          Date = DateTime(2026, 1, 15)
          HasTime = false
          Repeater = Some "+1d"
          Delay = None
          RangeEnd = None }

    let result = Mutations.shiftTimestamp ts now
    Assert.Equal(DateTime(2026, 1, 16), result.Date)

[<Fact>]
let ``shiftTimestamp .+1d shifts by 1 day from today`` () =
    let ts =
        { Type = TimestampType.Active
          Date = DateTime(2026, 1, 15)
          HasTime = false
          Repeater = Some ".+1d"
          Delay = None
          RangeEnd = None }

    let result = Mutations.shiftTimestamp ts now
    Assert.Equal(DateTime(2026, 2, 6), result.Date)

[<Fact>]
let ``shiftTimestamp ++1d shifts forward until past today`` () =
    let ts =
        { Type = TimestampType.Active
          Date = DateTime(2026, 1, 1)
          HasTime = false
          Repeater = Some "++1d"
          Delay = None
          RangeEnd = None }

    let result = Mutations.shiftTimestamp ts now
    Assert.True(result.Date > now.Date)

[<Fact>]
let ``shiftTimestamp +1w shifts by 7 days`` () =
    let ts =
        { Type = TimestampType.Active
          Date = DateTime(2026, 1, 15)
          HasTime = false
          Repeater = Some "+1w"
          Delay = None
          RangeEnd = None }

    let result = Mutations.shiftTimestamp ts now
    Assert.Equal(DateTime(2026, 1, 22), result.Date)

[<Fact>]
let ``shiftTimestamp +1m shifts by 1 month`` () =
    let ts =
        { Type = TimestampType.Active
          Date = DateTime(2026, 1, 15)
          HasTime = false
          Repeater = Some "+1m"
          Delay = None
          RangeEnd = None }

    let result = Mutations.shiftTimestamp ts now
    Assert.Equal(DateTime(2026, 2, 15), result.Date)

[<Fact>]
let ``shiftTimestamp preserves repeater and delay`` () =
    let ts =
        { Type = TimestampType.Active
          Date = DateTime(2026, 1, 15)
          HasTime = false
          Repeater = Some "+1d"
          Delay = Some "-2d"
          RangeEnd = None }

    let result = Mutations.shiftTimestamp ts now
    Assert.Equal(Some "+1d", result.Repeater)
    Assert.Equal(Some "-2d", result.Delay)

// --- Repeating setTodoState ---

[<Fact>]
let ``setTodoState with repeater preserves TODO state`` () =
    let content =
        sprintf "* TODO Repeating task\nSCHEDULED: <2026-01-15 %s +1d>\nBody\n" (dayName (DateTime(2026, 1, 15)))

    let result = Mutations.setTodoState Types.defaultConfig content 0L (Some "DONE") now
    Assert.StartsWith("* TODO Repeating task", result)
    Assert.DoesNotContain("* DONE", result)

[<Fact>]
let ``setTodoState with repeater does not add CLOSED`` () =
    let content =
        sprintf "* TODO Repeating task\nSCHEDULED: <2026-01-15 %s +1d>\nBody\n" (dayName (DateTime(2026, 1, 15)))

    let result = Mutations.setTodoState Types.defaultConfig content 0L (Some "DONE") now
    Assert.DoesNotContain("CLOSED:", result)

[<Fact>]
let ``setTodoState with repeater shifts scheduled timestamp`` () =
    let content =
        sprintf "* TODO Repeating task\nSCHEDULED: <2026-01-15 %s +1d>\nBody\n" (dayName (DateTime(2026, 1, 15)))

    let result = Mutations.setTodoState Types.defaultConfig content 0L (Some "DONE") now
    Assert.DoesNotContain("<2026-01-15", result)
    Assert.Contains("+1d", result)

[<Fact>]
let ``setTodoState with repeater sets LAST_REPEAT property`` () =
    let content =
        sprintf "* TODO Repeating task\nSCHEDULED: <2026-01-15 %s +1d>\nBody\n" (dayName (DateTime(2026, 1, 15)))

    let result = Mutations.setTodoState Types.defaultConfig content 0L (Some "DONE") now
    Assert.Contains(":LAST_REPEAT:", result)
    Assert.Contains("[2026-02-05", result)

[<Fact>]
let ``setTodoState with repeater adds logbook entry`` () =
    let content =
        sprintf "* TODO Repeating task\nSCHEDULED: <2026-01-15 %s +1d>\nBody\n" (dayName (DateTime(2026, 1, 15)))

    let result = Mutations.setTodoState Types.defaultConfig content 0L (Some "DONE") now
    Assert.Contains(":LOGBOOK:", result)
    Assert.Contains("- State", result)
    Assert.Contains("from \"DONE\"", result)

[<Fact>]
let ``setTodoState with REPEAT_TO_STATE property overrides default state`` () =
    let content =
        sprintf
            "* TODO Repeating task\nSCHEDULED: <2026-01-15 %s +1d>\n:PROPERTIES:\n:REPEAT_TO_STATE: NEXT\n:END:\nBody\n"
            (dayName (DateTime(2026, 1, 15)))

    let result = Mutations.setTodoState Types.defaultConfig content 0L (Some "DONE") now
    Assert.StartsWith("* NEXT Repeating task", result)

[<Fact>]
let ``setTodoState with repeater on DEADLINE works`` () =
    let content =
        sprintf "* TODO Repeating task\nDEADLINE: <2026-01-15 %s +1w>\nBody\n" (dayName (DateTime(2026, 1, 15)))

    let result = Mutations.setTodoState Types.defaultConfig content 0L (Some "DONE") now
    Assert.StartsWith("* TODO Repeating task", result)
    Assert.DoesNotContain("CLOSED:", result)
    Assert.Contains("+1w", result)

[<Fact>]
let ``setTodoState without repeater still works normally`` () =
    let content =
        sprintf "* TODO Normal task\nSCHEDULED: <2026-01-15 %s>\nBody\n" (dayName (DateTime(2026, 1, 15)))

    let result = Mutations.setTodoState Types.defaultConfig content 0L (Some "DONE") now
    Assert.StartsWith("* DONE Normal task", result)
    Assert.Contains("CLOSED:", result)

// --- Logbook ---

[<Fact>]
let ``setTodoState creates logbook entry on state change`` () =
    let content = "* TODO My task\nBody\n"
    let result = Mutations.setTodoState Types.defaultConfig content 0L (Some "DONE") now
    Assert.Contains(":LOGBOOK:", result)
    Assert.Contains("- State \"DONE\"", result)
    Assert.Contains("from \"TODO\"", result)

[<Fact>]
let ``setTodoState inserts logbook entry into existing logbook`` () =
    let content = "* TODO My task\n:LOGBOOK:\n- Old entry\n:END:\nBody\n"
    let result = Mutations.setTodoState Types.defaultConfig content 0L (Some "DONE") now
    Assert.Contains(":LOGBOOK:", result)
    Assert.Contains("- State \"DONE\"", result)
    Assert.Contains("- Old entry", result)
    // New entry should be before old entry
    let logIdx = result.IndexOf("- State \"DONE\"")
    let oldIdx = result.IndexOf("- Old entry")
    Assert.True(logIdx < oldIdx)

[<Fact>]
let ``logbook placed after property drawer`` () =
    let content = "* TODO My task\n:PROPERTIES:\n:ID: abc123\n:END:\nBody\n"
    let result = Mutations.setTodoState Types.defaultConfig content 0L (Some "DONE") now
    let propsEnd = result.IndexOf(":END:")
    let logbookStart = result.IndexOf(":LOGBOOK:")
    Assert.True(logbookStart > propsEnd)

[<Fact>]
let ``multiple state changes accumulate logbook entries`` () =
    // Use a config with LogOnEnter for TODO so both transitions log
    let cfg =
        { Types.defaultConfig with
            TodoKeywords =
                { ActiveStates =
                    [ { Keyword = "TODO"
                        LogOnEnter = LogAction.LogTime
                        LogOnLeave = LogAction.NoLog }
                      { Keyword = "NEXT"
                        LogOnEnter = LogAction.NoLog
                        LogOnLeave = LogAction.NoLog }
                      { Keyword = "WAITING"
                        LogOnEnter = LogAction.NoLog
                        LogOnLeave = LogAction.NoLog }
                      { Keyword = "HOLD"
                        LogOnEnter = LogAction.NoLog
                        LogOnLeave = LogAction.NoLog }
                      { Keyword = "SOMEDAY"
                        LogOnEnter = LogAction.NoLog
                        LogOnLeave = LogAction.NoLog }
                      { Keyword = "PROJECT"
                        LogOnEnter = LogAction.NoLog
                        LogOnLeave = LogAction.NoLog } ]
                  DoneStates =
                    [ { Keyword = "DONE"
                        LogOnEnter = LogAction.LogTime
                        LogOnLeave = LogAction.NoLog }
                      { Keyword = "CANCELLED"
                        LogOnEnter = LogAction.NoLog
                        LogOnLeave = LogAction.NoLog }
                      { Keyword = "CANCELED"
                        LogOnEnter = LogAction.NoLog
                        LogOnLeave = LogAction.NoLog } ] } }

    let content = "* TODO My task\nBody\n"
    let result1 = Mutations.setTodoState cfg content 0L (Some "DONE") now
    let result2 = Mutations.setTodoState cfg result1 0L (Some "TODO") now
    Assert.Contains("- State \"TODO\"", result2)
    Assert.Contains("- State \"DONE\"", result2)

[<Fact>]
let ``setTodoState with state removal does not log by default`` () =
    let content = "* TODO My task\nBody\n"
    let result = Mutations.setTodoState Types.defaultConfig content 0L None now
    Assert.DoesNotContain(":LOGBOOK:", result)

[<Fact>]
let ``repeater creates logbook entry about the repeat`` () =
    let content =
        sprintf "* TODO Repeating task\nSCHEDULED: <2026-01-15 %s +1d>\nBody\n" (dayName (DateTime(2026, 1, 15)))

    let result = Mutations.setTodoState Types.defaultConfig content 0L (Some "DONE") now
    Assert.Contains(":LOGBOOK:", result)
    Assert.Contains("- State \"TODO\"", result)
    Assert.Contains("from \"DONE\"", result)

// --- Set property ---

[<Fact>]
let ``setProperty adds property to headline with no drawer`` () =
    let content = "* TODO My task\nBody\n"
    let result = Mutations.setProperty content 0L "EFFORT" "1:00"
    Assert.Contains(":PROPERTIES:", result)
    Assert.Contains(":EFFORT: 1:00", result)
    Assert.Contains(":END:", result)

[<Fact>]
let ``setProperty adds property to existing drawer`` () =
    let content = "* TODO My task\n:PROPERTIES:\n:ID: abc\n:END:\nBody\n"
    let result = Mutations.setProperty content 0L "EFFORT" "1:00"
    Assert.Contains(":EFFORT: 1:00", result)
    Assert.Contains(":ID: abc", result)

[<Fact>]
let ``setProperty updates existing property`` () =
    let content = "* TODO My task\n:PROPERTIES:\n:EFFORT: 0:30\n:END:\nBody\n"
    let result = Mutations.setProperty content 0L "EFFORT" "1:00"
    Assert.Contains(":EFFORT: 1:00", result)
    Assert.DoesNotContain("0:30", result)

// --- Remove property ---

[<Fact>]
let ``removeProperty removes property from drawer`` () =
    let content = "* TODO My task\n:PROPERTIES:\n:ID: abc\n:EFFORT: 1:00\n:END:\nBody\n"
    let result = Mutations.removeProperty content 0L "EFFORT"
    Assert.DoesNotContain("EFFORT", result)
    Assert.Contains(":ID: abc", result)

[<Fact>]
let ``removeProperty removes empty drawer`` () =
    let content = "* TODO My task\n:PROPERTIES:\n:EFFORT: 1:00\n:END:\nBody\n"
    let result = Mutations.removeProperty content 0L "EFFORT"
    Assert.DoesNotContain(":PROPERTIES:", result)

[<Fact>]
let ``removeProperty on nonexistent property is no-op`` () =
    let content = "* TODO My task\n:PROPERTIES:\n:ID: abc\n:END:\nBody\n"
    let result = Mutations.removeProperty content 0L "NONEXISTENT"
    Assert.Contains(":ID: abc", result)

// --- Add tag ---

[<Fact>]
let ``addTag adds tag to headline with no tags`` () =
    let content = "* TODO My task\nBody\n"
    let result = Mutations.addTag content 0L "work"
    Assert.Contains(":work:", result)

[<Fact>]
let ``addTag adds tag to headline with existing tags`` () =
    let content = "* TODO My task :personal:\nBody\n"
    let result = Mutations.addTag content 0L "work"
    Assert.Contains("personal", result)
    Assert.Contains("work", result)

[<Fact>]
let ``addTag does not duplicate existing tag`` () =
    let content = "* TODO My task :work:\nBody\n"
    let result = Mutations.addTag content 0L "work"
    Assert.Equal(content, result)

// --- Remove tag ---

[<Fact>]
let ``removeTag removes tag from headline`` () =
    let content = "* TODO My task :work:personal:\nBody\n"
    let result = Mutations.removeTag content 0L "work"
    Assert.DoesNotContain("work", result)
    Assert.Contains("personal", result)

[<Fact>]
let ``removeTag removes tag markers when last tag removed`` () =
    let content = "* TODO My task :work:\nBody\n"
    let result = Mutations.removeTag content 0L "work"
    let headlineLine = result.Split('\n').[0]
    Assert.Equal("* TODO My task", headlineLine)

[<Fact>]
let ``removeTag on nonexistent tag is no-op`` () =
    let content = "* TODO My task :work:\nBody\n"
    let result = Mutations.removeTag content 0L "missing"
    Assert.Equal(content, result)

// --- Set priority ---

[<Fact>]
let ``setPriority adds priority to headline`` () =
    let content = "* TODO My task\nBody\n"
    let result = Mutations.setPriority content 0L (Some 'A')
    Assert.Contains("[#A]", result)

[<Fact>]
let ``setPriority changes existing priority`` () =
    let content = "* TODO [#B] My task\nBody\n"
    let result = Mutations.setPriority content 0L (Some 'A')
    Assert.Contains("[#A]", result)
    Assert.DoesNotContain("[#B]", result)

[<Fact>]
let ``setPriority removes priority with None`` () =
    let content = "* TODO [#A] My task\nBody\n"
    let result = Mutations.setPriority content 0L None
    Assert.DoesNotContain("[#A]", result)
    Assert.StartsWith("* TODO My task", result)

[<Fact>]
let ``setPriority preserves tags`` () =
    let content = "* TODO My task :work:\nBody\n"
    let result = Mutations.setPriority content 0L (Some 'A')
    Assert.Contains("[#A]", result)
    Assert.Contains(":work:", result)

// --- Clock in ---

[<Fact>]
let ``clockIn inserts clock entry into logbook`` () =
    let content = "* TODO My task\nBody\n"
    let result = Mutations.clockIn content 0L now
    Assert.Contains(":LOGBOOK:", result)
    Assert.Contains("CLOCK: [2026-02-05", result)

[<Fact>]
let ``clockIn creates logbook after property drawer`` () =
    let content = "* TODO My task\n:PROPERTIES:\n:ID: abc\n:END:\nBody\n"
    let result = Mutations.clockIn content 0L now
    let propsEnd = result.IndexOf(":ID: abc")
    let clockStart = result.IndexOf("CLOCK:")
    Assert.True(clockStart > propsEnd)

// --- Clock out ---

[<Fact>]
let ``clockOut closes open clock entry`` () =
    let clockOutTime = DateTime(2026, 2, 5, 14, 30, 0)

    let content =
        sprintf "* TODO My task\n:LOGBOOK:\nCLOCK: [2026-02-05 %s 14:00]\n:END:\nBody\n" (dayName now)

    let result = Mutations.clockOut content 0L clockOutTime
    Assert.Contains("--[2026-02-05", result)
    Assert.Contains("=> ", result)
    Assert.Contains("0:30", result)

[<Fact>]
let ``clockOut with no open entry is no-op`` () =
    let content =
        sprintf
            "* TODO My task\n:LOGBOOK:\nCLOCK: [2026-02-05 %s 14:00]--[2026-02-05 %s 14:30] =>  0:30\n:END:\nBody\n"
            (dayName now)
            (dayName now)

    let result = Mutations.clockOut content 0L now
    Assert.Equal(content, result)

[<Fact>]
let ``clockOut with no logbook is no-op`` () =
    let content = "* TODO My task\nBody\n"
    let result = Mutations.clockOut content 0L now
    Assert.Equal(content, result)

// --- Add note ---

[<Fact>]
let ``addNote inserts note into logbook`` () =
    let content = "* TODO My task\nBody\n"
    let result = Mutations.addNote content 0L "This is a note" now
    Assert.Contains(":LOGBOOK:", result)
    Assert.Contains("Note taken on", result)
    Assert.Contains("This is a note", result)

[<Fact>]
let ``addNote inserts into existing logbook as first entry`` () =
    let content = "* TODO My task\n:LOGBOOK:\n- Old entry\n:END:\nBody\n"
    let result = Mutations.addNote content 0L "New note" now
    let noteIdx = result.IndexOf("New note")
    let oldIdx = result.IndexOf("Old entry")
    Assert.True(noteIdx < oldIdx)

// --- Add headline ---

[<Fact>]
let ``addHeadline appends at end of file`` () =
    let content = "* Existing\nBody\n"
    let result = Mutations.addHeadline content "New task" 1 None None [] None None
    Assert.Contains("* Existing", result)
    Assert.Contains("* New task", result)

[<Fact>]
let ``addHeadline with todo and tags`` () =
    let result =
        Mutations.addHeadline "" "My task" 1 (Some "TODO") (Some 'A') [ "work"; "urgent" ] None None

    Assert.Contains("* TODO [#A] My task :work:urgent:", result)

[<Fact>]
let ``addHeadline with scheduling`` () =
    let ts =
        { Type = TimestampType.Active
          Date = DateTime(2026, 2, 10)
          HasTime = false
          Repeater = None
          Delay = None
          RangeEnd = None }

    let result =
        Mutations.addHeadline "" "My task" 1 (Some "TODO") None [] (Some ts) None

    Assert.Contains("* TODO My task", result)
    Assert.Contains("SCHEDULED:", result)

[<Fact>]
let ``addHeadlineUnder inserts as child`` () =
    let content = "* Parent\nBody\n"

    let result =
        Mutations.addHeadlineUnder content 0L "Child" (Some "TODO") None [] None None

    Assert.Contains("** TODO Child", result)
    Assert.Contains("* Parent", result)

[<Fact>]
let ``addHeadlineUnder preserves parent body`` () =
    let content = "* Parent\nBody\n* Other\n"
    let result = Mutations.addHeadlineUnder content 0L "Child" None None [] None None
    Assert.Contains("Body", result)
    Assert.Contains("** Child", result)
    Assert.Contains("* Other", result)

// --- extractState tests ---

[<Fact>]
let ``extractState returns correct title todo tags from simple headline`` () =
    let content = "* TODO My task :work:urgent:\nBody\n"
    let state = HeadlineEdit.extractState content 0L
    Assert.Equal("My task", state.Title)
    Assert.Equal(Some "TODO", state.Todo)
    Assert.Equal<string list>([ "work"; "urgent" ], state.Tags)
    Assert.Equal(0L, state.Pos)

[<Fact>]
let ``extractState returns org-id from property drawer`` () =
    let content = "* My headline\n:PROPERTIES:\n:ID: abc-123\n:END:\nBody\n"
    let state = HeadlineEdit.extractState content 0L
    Assert.Equal(Some "abc-123", state.Id)
    Assert.Equal("My headline", state.Title)

[<Fact>]
let ``extractState returns scheduled and deadline from planning line`` () =
    let content =
        sprintf
            "* TODO My task\nSCHEDULED: <2026-02-10 %s> DEADLINE: <2026-02-15 %s>\nBody\n"
            (DateTime(2026, 2, 10).ToString("ddd").Substring(0, 3))
            (DateTime(2026, 2, 15).ToString("ddd").Substring(0, 3))

    let state = HeadlineEdit.extractState content 0L
    Assert.True(state.Scheduled.IsSome)
    Assert.True(state.Deadline.IsSome)
    Assert.True(state.Closed.IsNone)

[<Fact>]
let ``extractState returns priority`` () =
    let content = "* TODO [#A] Important :work:\nBody\n"
    let state = HeadlineEdit.extractState content 0L
    Assert.Equal(Some 'A', state.Priority)

[<Fact>]
let ``extractState after setTodoState returns new state`` () =
    let content = "* TODO My task\nBody\n"

    let newContent =
        Mutations.setTodoState Types.defaultConfig content 0L (Some "DONE") now

    let state = HeadlineEdit.extractState newContent 0L
    Assert.Equal(Some "DONE", state.Todo)
    Assert.True(state.Closed.IsSome)

[<Fact>]
let ``extractState after addTag returns new tags`` () =
    let content = "* My task\nBody\n"
    let newContent = Mutations.addTag content 0L "work"
    let state = HeadlineEdit.extractState newContent 0L
    Assert.Contains("work", state.Tags)

[<Fact>]
let ``extractState returns None for missing optional fields`` () =
    let content = "* Plain headline\nBody\n"
    let state = HeadlineEdit.extractState content 0L
    Assert.Equal("Plain headline", state.Title)
    Assert.Equal(None, state.Todo)
    Assert.Equal(None, state.Priority)
    Assert.Equal(None, state.Id)
    Assert.Equal(None, state.Scheduled)
    Assert.Equal(None, state.Deadline)
    Assert.Equal(None, state.Closed)
    Assert.Empty(state.Tags)

// --- parsePlanningParts with range ---

[<Fact>]
let ``parsePlanningParts captures range timestamp`` () =
    let line = "SCHEDULED: <2026-02-05 Thu>--<2026-02-10 Tue>"
    let parts = HeadlineEdit.parsePlanningParts line
    Assert.True(Map.containsKey "SCHEDULED" parts)
    let value = Map.find "SCHEDULED" parts
    Assert.Contains("--", value)
    Assert.Contains("<2026-02-05", value)
    Assert.Contains("<2026-02-10", value)

// --- shiftTimestamp with RangeEnd ---

[<Fact>]
let ``shiftTimestamp shifts both start and RangeEnd by same delta`` () =
    let ts: Timestamp =
        { Type = TimestampType.Active
          Date = DateTime(2026, 1, 15)
          HasTime = false
          Repeater = Some "+1d"
          Delay = None
          RangeEnd =
            Some
                { Type = TimestampType.Active
                  Date = DateTime(2026, 1, 20)
                  HasTime = false
                  Repeater = None
                  Delay = None
                  RangeEnd = None } }

    let result = Mutations.shiftTimestamp ts now
    Assert.Equal(DateTime(2026, 1, 16), result.Date)
    Assert.True(result.RangeEnd.IsSome)
    Assert.Equal(DateTime(2026, 1, 21), result.RangeEnd.Value.Date)

// --- Round-trip: range preserved through mutation ---

[<Fact>]
let ``addTag preserves range in planning line`` () =
    let content =
        "* TODO My task\nSCHEDULED: <2026-02-05 Thu>--<2026-02-10 Tue>\nBody\n"

    let result = Mutations.addTag content 0L "work"
    Assert.Contains(":work:", result)
    Assert.Contains("<2026-02-05 Thu>--<2026-02-10 Tue>", result)

// --- Config-aware mutation tests ---

module ConfigAwareMutations =
    open OrgCli.Org

    let private now = DateTime(2026, 2, 5, 14, 30, 0)
    let private dayName (d: DateTime) = d.ToString("ddd").Substring(0, 3)
    let private defaultConfig = Types.defaultConfig

    let private noLogConfig =
        { defaultConfig with
            LogDone = LogAction.NoLog }

    let private logTimeConfig =
        { defaultConfig with
            LogDone = LogAction.LogTime }

    let private logNoteConfig =
        { defaultConfig with
            LogDone = LogAction.LogNote }

    [<Fact>]
    let ``setTodoState with LogDone=NoLog does not add CLOSED or logbook`` () =
        let content = "* TODO My task\nBody\n"
        let result = Mutations.setTodoState noLogConfig content 0L (Some "DONE") now
        Assert.StartsWith("* DONE My task", result)
        Assert.DoesNotContain("CLOSED:", result)
        Assert.DoesNotContain(":LOGBOOK:", result)

    [<Fact>]
    let ``setTodoState with LogDone=LogTime adds CLOSED and logbook`` () =
        let content = "* TODO My task\nBody\n"
        let result = Mutations.setTodoState logTimeConfig content 0L (Some "DONE") now
        Assert.StartsWith("* DONE My task", result)
        Assert.Contains("CLOSED:", result)
        Assert.Contains(":LOGBOOK:", result)

    [<Fact>]
    let ``setTodoState with LogDone=LogNote adds CLOSED and logbook`` () =
        let content = "* TODO My task\nBody\n"
        let result = Mutations.setTodoState logNoteConfig content 0L (Some "DONE") now
        Assert.StartsWith("* DONE My task", result)
        Assert.Contains("CLOSED:", result)
        Assert.Contains(":LOGBOOK:", result)

    [<Fact>]
    let ``setTodoState with per-keyword LogOnEnter=LogTime creates logbook`` () =
        let cfg =
            { defaultConfig with
                TodoKeywords =
                    { ActiveStates =
                        [ { Keyword = "TODO"
                            LogOnEnter = LogAction.NoLog
                            LogOnLeave = LogAction.NoLog } ]
                      DoneStates =
                        [ { Keyword = "DONE"
                            LogOnEnter = LogAction.LogTime
                            LogOnLeave = LogAction.NoLog } ] }
                LogDone = LogAction.NoLog }

        let content = "* TODO My task\nBody\n"
        let result = Mutations.setTodoState cfg content 0L (Some "DONE") now
        Assert.Contains(":LOGBOOK:", result)
        Assert.Contains("CLOSED:", result)

    [<Fact>]
    let ``setTodoState LogDone=NoLog going from active to active no logbook`` () =
        let content = "* TODO My task\nBody\n"
        let result = Mutations.setTodoState noLogConfig content 0L (Some "NEXT") now
        Assert.StartsWith("* NEXT My task", result)
        Assert.DoesNotContain("CLOSED:", result)
        Assert.DoesNotContain(":LOGBOOK:", result)

    [<Fact>]
    let ``repeater with LogRepeat=NoLog resets state but no logbook`` () =
        let noLogRepeatCfg =
            { defaultConfig with
                LogRepeat = LogAction.NoLog }

        let content =
            sprintf "* TODO Repeating task\nSCHEDULED: <2026-01-15 %s +1d>\nBody\n" (dayName (DateTime(2026, 1, 15)))

        let result = Mutations.setTodoState noLogRepeatCfg content 0L (Some "DONE") now
        Assert.StartsWith("* TODO Repeating task", result)
        Assert.DoesNotContain(":LOGBOOK:", result)
        Assert.Contains("+1d", result)

    [<Fact>]
    let ``repeater with LogRepeat=LogTime adds logbook entry`` () =
        let logRepeatCfg =
            { defaultConfig with
                LogRepeat = LogAction.LogTime }

        let content =
            sprintf "* TODO Repeating task\nSCHEDULED: <2026-01-15 %s +1d>\nBody\n" (dayName (DateTime(2026, 1, 15)))

        let result = Mutations.setTodoState logRepeatCfg content 0L (Some "DONE") now
        Assert.StartsWith("* TODO Repeating task", result)
        Assert.Contains(":LOGBOOK:", result)

    [<Fact>]
    let ``LOGGING property nil suppresses all logging`` () =
        let content = "* TODO My task\n:PROPERTIES:\n:LOGGING: nil\n:END:\nBody\n"
        let result = Mutations.setTodoState logTimeConfig content 0L (Some "DONE") now
        Assert.StartsWith("* DONE My task", result)
        Assert.DoesNotContain("- State", result)

    [<Fact>]
    let ``file-level STARTUP logdone triggers CLOSED and logbook`` () =
        let content = "#+STARTUP: logdone\n* TODO My task\nBody\n"
        let doc = Document.parse content
        let fileCfg = FileConfig.mergeFileConfig defaultConfig doc.Keywords
        let pos = content.IndexOf("* TODO") |> int64
        let result = Mutations.setTodoState fileCfg content pos (Some "DONE") now
        Assert.Contains("CLOSED:", result)
        Assert.Contains(":LOGBOOK:", result)

    [<Fact>]
    let ``file-level STARTUP nologdone suppresses logging even with LogTime base`` () =
        let content = "#+STARTUP: nologdone\n* TODO My task\nBody\n"
        let doc = Document.parse content
        let fileCfg = FileConfig.mergeFileConfig logTimeConfig doc.Keywords
        let pos = content.IndexOf("* TODO") |> int64
        let result = Mutations.setTodoState fileCfg content pos (Some "DONE") now
        Assert.DoesNotContain("CLOSED:", result)
        Assert.DoesNotContain(":LOGBOOK:", result)

    [<Fact>]
    let ``file-level TODO keywords with per-keyword logging`` () =
        let content = "#+TODO: TODO(t) | DONE(d!)\n* TODO My task\nBody\n"
        let doc = Document.parse content
        let fileCfg = FileConfig.mergeFileConfig defaultConfig doc.Keywords
        let pos = content.IndexOf("* TODO") |> int64
        let result = Mutations.setTodoState fileCfg content pos (Some "DONE") now
        Assert.Contains("CLOSED:", result)
        Assert.Contains(":LOGBOOK:", result)

    [<Fact>]
    let ``LOGGING nil on parent headline suppresses child logging`` () =
        let content =
            "* Parent :project:\n:PROPERTIES:\n:LOGGING: nil\n:END:\n** TODO Child task\nBody\n"

        let pos = content.IndexOf("** TODO") |> int64
        let result = Mutations.setTodoState logTimeConfig content pos (Some "DONE") now
        Assert.StartsWith("* Parent", result)
        Assert.Contains("** DONE Child task", result)
        Assert.DoesNotContain("- State", result)
        Assert.DoesNotContain("CLOSED:", result)

    [<Fact>]
    let ``LOGGING nil on grandparent suppresses grandchild logging`` () =
        let content =
            "* Grandparent\n:PROPERTIES:\n:LOGGING: nil\n:END:\n** Parent\n*** TODO Grandchild\nBody\n"

        let pos = content.IndexOf("*** TODO") |> int64
        let result = Mutations.setTodoState logTimeConfig content pos (Some "DONE") now
        Assert.Contains("*** DONE Grandchild", result)
        Assert.DoesNotContain("- State", result)

    [<Fact>]
    let ``own LOGGING property takes precedence over parent`` () =
        let content =
            "* Parent\n:PROPERTIES:\n:LOGGING: nil\n:END:\n** TODO Child\n:PROPERTIES:\n:LOGGING: logdone\n:END:\nBody\n"

        let pos = content.IndexOf("** TODO") |> int64
        let result = Mutations.setTodoState logTimeConfig content pos (Some "DONE") now
        Assert.Contains("** DONE Child", result)
        // Own LOGGING is not "nil", so logging should proceed per config
        Assert.Contains(":LOGBOOK:", result)

    [<Fact>]
    let ``setTodoState config with isDoneState using config keywords`` () =
        // Custom done states: only "CLOSED" is done, "DONE" is not
        let cfg =
            { defaultConfig with
                TodoKeywords =
                    { ActiveStates =
                        [ { Keyword = "TODO"
                            LogOnEnter = LogAction.NoLog
                            LogOnLeave = LogAction.NoLog }
                          { Keyword = "DONE"
                            LogOnEnter = LogAction.NoLog
                            LogOnLeave = LogAction.NoLog } ]
                      DoneStates =
                        [ { Keyword = "CLOSED"
                            LogOnEnter = LogAction.LogTime
                            LogOnLeave = LogAction.NoLog } ] }
                LogDone = LogAction.LogTime }

        let content = "* TODO My task\nBody\n"
        // Transitioning to "DONE" which is active in this config — no CLOSED timestamp
        let result = Mutations.setTodoState cfg content 0L (Some "DONE") now
        Assert.DoesNotContain("CLOSED:", result)

// --- Reschedule/Redeadline/Refile logging ---

module RescheduleRedeadlineRefileLogging =
    open OrgCli.Org

    let private logRescheduleConfig =
        { Types.defaultConfig with
            LogReschedule = LogAction.LogTime }

    let private logRedeadlineConfig =
        { Types.defaultConfig with
            LogRedeadline = LogAction.LogTime }

    let private logRefileConfig =
        { Types.defaultConfig with
            LogRefile = LogAction.LogTime }

    [<Fact>]
    let ``setScheduled with logReschedule logs old schedule timestamp`` () =
        let dn = dayName now

        let content =
            sprintf "* TODO My task\nSCHEDULED: <2026-02-01 %s>\nBody\n" (dayName (DateTime(2026, 2, 1)))

        let newTs =
            { Type = TimestampType.Active
              Date = DateTime(2026, 3, 1)
              HasTime = false
              Repeater = None
              Delay = None
              RangeEnd = None }

        let result = Mutations.setScheduled logRescheduleConfig content 0L (Some newTs) now
        Assert.Contains("SCHEDULED:", result)
        Assert.Contains("2026-03-01", result)
        Assert.Contains("Rescheduled from", result)
        Assert.Contains("2026-02-01", result)

    [<Fact>]
    let ``setScheduled without logReschedule does not log`` () =
        let content =
            sprintf "* TODO My task\nSCHEDULED: <2026-02-01 %s>\nBody\n" (dayName (DateTime(2026, 2, 1)))

        let newTs =
            { Type = TimestampType.Active
              Date = DateTime(2026, 3, 1)
              HasTime = false
              Repeater = None
              Delay = None
              RangeEnd = None }

        let result = Mutations.setScheduled Types.defaultConfig content 0L (Some newTs) now
        Assert.Contains("SCHEDULED:", result)
        Assert.DoesNotContain("Rescheduled", result)

    [<Fact>]
    let ``setDeadline with logRedeadline logs old deadline timestamp`` () =
        let content =
            sprintf "* TODO My task\nDEADLINE: <2026-02-01 %s>\nBody\n" (dayName (DateTime(2026, 2, 1)))

        let newTs =
            { Type = TimestampType.Active
              Date = DateTime(2026, 3, 1)
              HasTime = false
              Repeater = None
              Delay = None
              RangeEnd = None }

        let result = Mutations.setDeadline logRedeadlineConfig content 0L (Some newTs) now
        Assert.Contains("DEADLINE:", result)
        Assert.Contains("2026-03-01", result)
        Assert.Contains("New deadline from", result)
        Assert.Contains("2026-02-01", result)

    [<Fact>]
    let ``setDeadline without logRedeadline does not log`` () =
        let content =
            sprintf "* TODO My task\nDEADLINE: <2026-02-01 %s>\nBody\n" (dayName (DateTime(2026, 2, 1)))

        let newTs =
            { Type = TimestampType.Active
              Date = DateTime(2026, 3, 1)
              HasTime = false
              Repeater = None
              Delay = None
              RangeEnd = None }

        let result = Mutations.setDeadline Types.defaultConfig content 0L (Some newTs) now
        Assert.Contains("DEADLINE:", result)
        Assert.DoesNotContain("New deadline", result)

    [<Fact>]
    let ``refile with logRefile logs refile note`` () =
        let srcContent = "* TODO Task A\nBody A\n* Target\nBody Target\n"
        let srcPos = 0L
        let tgtPos = srcContent.IndexOf("* Target") |> int64

        let (_, newTgt) =
            Mutations.refile logRefileConfig srcContent srcPos srcContent tgtPos false now

        Assert.Contains("Refiled on", newTgt)

    [<Fact>]
    let ``refile without logRefile does not log`` () =
        let srcContent = "* TODO Task A\nBody A\n"
        let tgtContent = "* Target\nBody Target\n"

        let (_, newTgt) =
            Mutations.refile Types.defaultConfig srcContent 0L tgtContent 0L false now

        Assert.DoesNotContain("Refiled", newTgt)

    [<Fact>]
    let ``parseStartupOptions recognizes logreschedule`` () =
        let opts = FileConfig.parseStartupOptions "logreschedule logredeadline logrefile"
        Assert.Equal(Some LogAction.LogTime, opts.LogReschedule)
        Assert.Equal(Some LogAction.LogTime, opts.LogRedeadline)
        Assert.Equal(Some LogAction.LogTime, opts.LogRefile)

    [<Fact>]
    let ``parseStartupOptions recognizes nologreschedule`` () =
        let opts =
            FileConfig.parseStartupOptions "nologreschedule nologredeadline nologrefile"

        Assert.Equal(Some LogAction.NoLog, opts.LogReschedule)
        Assert.Equal(Some LogAction.NoLog, opts.LogRedeadline)
        Assert.Equal(Some LogAction.NoLog, opts.LogRefile)

// --- Tag definitions and mutual exclusion ---

module TagDefinitionTests =
    open OrgCli.Org

    [<Fact>]
    let ``parseTagsLine parses simple tag list`` () =
        let result = FileConfig.parseTagsLine "@work @home @laptop"
        Assert.Equal(1, result.Length)

        match result.[0] with
        | TagGroup.Regular tags ->
            Assert.Equal(3, tags.Length)
            Assert.Equal("@work", tags.[0].Name)
        | _ -> Assert.Fail("Expected Regular")

    [<Fact>]
    let ``parseTagsLine parses mutual exclusion group`` () =
        let result = FileConfig.parseTagsLine "{ @work @home @laptop }"
        Assert.Equal(1, result.Length)

        match result.[0] with
        | TagGroup.MutuallyExclusive tags ->
            Assert.Equal(3, tags.Length)
            Assert.Equal("@work", tags.[0].Name)
        | _ -> Assert.Fail("Expected MutuallyExclusive")

    [<Fact>]
    let ``parseTagsLine parses tag with fast-select key`` () =
        let result = FileConfig.parseTagsLine "@work(w) @home(h)"

        match result.[0] with
        | TagGroup.Regular tags ->
            Assert.Equal(Some 'w', tags.[0].FastKey)
            Assert.Equal(Some 'h', tags.[1].FastKey)
        | _ -> Assert.Fail("Expected Regular")

    [<Fact>]
    let ``parseTagsLine parses mixed regular and exclusive groups`` () =
        let result = FileConfig.parseTagsLine "@urgent { @work @home } @errand"
        Assert.Equal(3, result.Length)

        match result.[0] with
        | TagGroup.Regular tags -> Assert.Equal(1, tags.Length)
        | _ -> Assert.Fail("Expected Regular for @urgent")

        match result.[1] with
        | TagGroup.MutuallyExclusive tags -> Assert.Equal(2, tags.Length)
        | _ -> Assert.Fail("Expected MutuallyExclusive for @work/@home")

        match result.[2] with
        | TagGroup.Regular tags -> Assert.Equal(1, tags.Length)
        | _ -> Assert.Fail("Expected Regular for @errand")

    [<Fact>]
    let ``addTag with mutual exclusion removes conflicting tags`` () =
        let content = "* My task                                         :@work:\nBody\n"

        let tagDefs =
            [ TagGroup.MutuallyExclusive [ { Name = "@work"; FastKey = None }; { Name = "@home"; FastKey = None } ] ]

        let result = Mutations.addTagWithExclusion content 0L "@home" tagDefs
        Assert.Contains(":@home:", result)
        Assert.DoesNotContain(":@work:", result)

    [<Fact>]
    let ``addTag with mutual exclusion does not affect unrelated tags`` () =
        let content =
            "* My task                                         :@work:urgent:\nBody\n"

        let tagDefs =
            [ TagGroup.MutuallyExclusive [ { Name = "@work"; FastKey = None }; { Name = "@home"; FastKey = None } ] ]

        let result = Mutations.addTagWithExclusion content 0L "@home" tagDefs
        Assert.Contains(":@home:", result)
        Assert.Contains(":urgent:", result)
        Assert.DoesNotContain(":@work:", result)

    [<Fact>]
    let ``addTag without exclusion group works normally`` () =
        let content = "* My task                                         :existing:\nBody\n"
        let tagDefs = [ TagGroup.Regular [ { Name = "@work"; FastKey = None } ] ]
        let result = Mutations.addTagWithExclusion content 0L "newtag" tagDefs
        Assert.Contains(":existing:", result)
        Assert.Contains(":newtag:", result)

module CustomKeywordMutations =

    let private now = DateTime(2026, 2, 5, 14, 30, 0)

    let private customConfig =
        { Types.defaultConfig with
            TodoKeywords =
                { ActiveStates =
                    [ { Keyword = "OPEN"
                        LogOnEnter = LogAction.NoLog
                        LogOnLeave = LogAction.NoLog } ]
                  DoneStates =
                    [ { Keyword = "CLOSED"
                        LogOnEnter = LogAction.NoLog
                        LogOnLeave = LogAction.NoLog } ] } }

    [<Fact>]
    let ``setTodoState recognizes custom keyword OPEN`` () =
        let content = "#+TODO: OPEN | CLOSED\n* My task\nBody\n"
        let result = Mutations.setTodoState customConfig content 22L (Some "OPEN") now
        Assert.Contains("* OPEN My task", result)

    [<Fact>]
    let ``setTodoState transitions custom OPEN to CLOSED with CLOSED timestamp`` () =
        let content = "#+TODO: OPEN | CLOSED\n* OPEN My task\nBody\n"
        let result = Mutations.setTodoState customConfig content 22L (Some "CLOSED") now
        Assert.Contains("* CLOSED My task", result)
        Assert.Contains("CLOSED:", result)

    [<Fact>]
    let ``addTag works with custom keyword headline`` () =
        let content = "#+TODO: OPEN | CLOSED\n* OPEN My task\nBody\n"
        let result = Mutations.addTag content 22L "work"
        Assert.Contains("* OPEN My task", result)
        Assert.Contains(":work:", result)

    [<Fact>]
    let ``setPriority works with custom keyword headline`` () =
        let content = "#+TODO: OPEN | CLOSED\n* OPEN My task\nBody\n"
        let result = Mutations.setPriority content 22L (Some 'A')
        Assert.Contains("* OPEN [#A] My task", result)

    [<Fact>]
    let ``extractStateWith recognizes custom keywords`` () =
        let content = "#+TODO: OPEN | CLOSED\n* OPEN My task :tag:\nBody\n"
        let state = HeadlineEdit.extractStateWith [ "OPEN"; "CLOSED" ] content 22L
        Assert.Equal(Some "OPEN", state.Todo)
        Assert.Equal("My task", state.Title)
        Assert.Equal<string list>([ "tag" ], state.Tags)

// --- Hyphenated tag parsing ---

[<Fact>]
let ``addTag with hyphenated tag round-trips through parse`` () =
    let content = "* TODO Task :existing:\nBody\n"
    let result = Mutations.addTag content 0L "my-tag"
    let doc = Document.parse result
    Assert.Contains("my-tag", doc.Headlines.[0].Tags)
    Assert.Equal("Task", doc.Headlines.[0].Title)

[<Fact>]
let ``addTag with hyphenated tag round-trips through extractState`` () =
    let content = "* TODO Task\nBody\n"
    let result = Mutations.addTag content 0L "in-progress"
    let state = HeadlineEdit.extractState result 0L
    Assert.Contains("in-progress", state.Tags)
    Assert.Equal("Task", state.Title)

[<Fact>]
let ``Document parse recognizes hyphenated tags`` () =
    let content = "* Headline :work-item:high-priority:\n"
    let doc = Document.parse content
    Assert.Equal("Headline", doc.Headlines.[0].Title)
    Assert.Equal<string list>([ "work-item"; "high-priority" ], doc.Headlines.[0].Tags)

[<Fact>]
let ``removeTag removes hyphenated tag`` () =
    let content = "* Headline :keep:remove-me:\n"
    let result = Mutations.removeTag content 0L "remove-me"
    let doc = Document.parse result
    Assert.Contains("keep", doc.Headlines.[0].Tags)
    Assert.DoesNotContain("remove-me", doc.Headlines.[0].Tags)

// --- Refile without target headline ---

[<Fact>]
let ``appendSubtree appends at level 1`` () =
    let subtree = "* Source headline\nSource body\n"
    let tgt = "* Existing headline\nExisting body\n"
    let result = Subtree.appendSubtree tgt subtree
    let doc = Document.parse result
    let source = doc.Headlines |> List.find (fun h -> h.Title = "Source headline")
    Assert.Equal(1, source.Level)
    Assert.Equal(2, doc.Headlines.Length)

[<Fact>]
let ``appendSubtree adjusts deeper subtree to level 1`` () =
    let subtree = "** Deep headline\nBody\n"
    let tgt = "* Existing\n"
    let result = Subtree.appendSubtree tgt subtree
    let doc = Document.parse result
    let deep = doc.Headlines |> List.find (fun h -> h.Title = "Deep headline")
    Assert.Equal(1, deep.Level)
